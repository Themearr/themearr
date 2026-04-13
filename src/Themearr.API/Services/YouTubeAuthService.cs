using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class YouTubeAuthService(Database db, ILogger<YouTubeAuthService> log)
{
    public enum FlowState { Idle, WaitingForUser, Completed, Failed }

    private sealed record State(FlowState Status, string? DeviceUrl, string? UserCode, string? Error);
    private volatile State _state = new(FlowState.Idle, null, null, null);
    private CancellationTokenSource? _cts;

    // Marker file — present only after a successful OAuth2 auth
    private string MarkerPath => Path.Combine(db.DataDir, "youtube-oauth-configured");
    public bool IsAuthenticated => File.Exists(MarkerPath);

    public object GetStatus() => new
    {
        authenticated = IsAuthenticated,
        flowState     = _state.Status.ToString().ToLower(),
        deviceUrl     = _state.DeviceUrl,
        userCode      = _state.UserCode,
        error         = _state.Error,
    };

    public void StartFlow()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _state = new(FlowState.Idle, null, null, null);
        _ = Task.Run(() => RunAuthAsync(_cts.Token));
    }

    private async Task RunAuthAsync(CancellationToken ct)
    {
        try
        {
            // --skip-download so yt-dlp authenticates then exits without fetching formats
            var psi = new ProcessStartInfo
            {
                FileName               = "yt-dlp",
                Arguments              = "--username oauth2 --password \"\" --skip-download --no-playlist https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };

            using var proc = Process.Start(psi)!;

            string? pendingUrl  = null;
            string? pendingCode = null;

            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    log.LogInformation("[oauth2] {Line}", line);

                    // yt-dlp outputs something like:
                    //   "To continue, open https://www.google.com/device and enter code XXXX-XXXX"
                    // or across two lines. Accumulate both parts.
                    var urlMatch  = Regex.Match(line, @"https://\S+device\S*|https://accounts\.google\.\S+", RegexOptions.IgnoreCase);
                    var codeMatch = Regex.Match(line, @"(?:code|enter)[:\s]+([A-Z0-9]{4}-[A-Z0-9]{4})", RegexOptions.IgnoreCase);

                    if (urlMatch.Success)  pendingUrl  = urlMatch.Value.Trim('.', ',');
                    if (codeMatch.Success) pendingCode = codeMatch.Groups[1].Value;

                    // Also handle bare XXXX-XXXX on its own line
                    if (!codeMatch.Success)
                    {
                        var bare = Regex.Match(line.Trim(), @"^([A-Z0-9]{4}-[A-Z0-9]{4})$");
                        if (bare.Success) pendingCode = bare.Groups[1].Value;
                    }

                    if (pendingUrl != null && pendingCode != null)
                        _state = new(FlowState.WaitingForUser, pendingUrl, pendingCode, null);
                    else if (pendingUrl != null && _state.Status == FlowState.Idle)
                        _state = new(FlowState.WaitingForUser, pendingUrl, null, null);
                }
            }, ct);

            await Task.Run(() => proc.WaitForExit(), CancellationToken.None);
            await stderrTask;

            if (proc.ExitCode == 0)
            {
                Directory.CreateDirectory(db.DataDir);
                File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));
                _state = new(FlowState.Completed, null, null, null);
                log.LogInformation("YouTube OAuth2 authentication completed successfully");
            }
            else if (!ct.IsCancellationRequested)
            {
                _state = new(FlowState.Failed, null, null, "Authentication failed — please try again.");
            }
        }
        catch (OperationCanceledException)
        {
            _state = new(FlowState.Idle, null, null, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "YouTube OAuth2 flow error");
            _state = new(FlowState.Failed, null, null, ex.Message);
        }
    }

    public void Revoke()
    {
        _cts?.Cancel();
        _state = new(FlowState.Idle, null, null, null);

        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);

        // Clear yt-dlp's cached OAuth2 token
        var cacheDir = Path.Combine(db.DataDir, ".cache", "yt-dlp");
        if (Directory.Exists(cacheDir))
        {
            foreach (var f in Directory.EnumerateFiles(cacheDir, "*.json"))
                try { File.Delete(f); } catch { /* ignore */ }
        }
    }
}
