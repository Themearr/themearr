using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class UpdateService(Database db, IConfiguration config)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentQueue<string> _logs = new();
    private volatile bool _inProgress;
    private volatile bool _finished;
    private string _error = "";

    private string GithubRepo =>
        Environment.GetEnvironmentVariable("GITHUB_REPO")
        ?? config["Themearr:GithubRepo"]
        ?? "Themearr/themearr";

    private string CurrentVersion()
    {
        var env = Environment.GetEnvironmentVariable("APP_VERSION")?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        var versionFile = Environment.GetEnvironmentVariable("THEMEARR_VERSION_FILE")
            ?? config["Themearr:VersionFile"] ?? "/opt/themearr/VERSION";
        if (File.Exists(versionFile))
        {
            var val = File.ReadAllText(versionFile).Trim();
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return "dev";
    }

    private string _cachedLatest    = "";
    private string _cachedCheckError = "";
    private DateTime _cacheExpiresAt = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public async Task<object> GetVersionInfoAsync()
    {
        var current = NormaliseSemver(CurrentVersion());

        await _cacheLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow < _cacheExpiresAt)
                return BuildVersionResponse(current, _cachedLatest, _cachedCheckError);

            var latest     = "";
            var checkError = "";
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                http.DefaultRequestHeaders.Add("User-Agent", $"Themearr/{current.TrimStart('v')}");

                var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrEmpty(token))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await http.GetAsync($"https://api.github.com/repos/{GithubRepo}/releases/latest");
                var body     = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var doc = JsonDocument.Parse(body);
                    latest  = NormaliseSemver(doc.RootElement.GetProperty("tag_name").GetString() ?? "");
                }
                else
                {
                    string detail;
                    try   { detail = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString() ?? body; }
                    catch { detail = body.Length > 200 ? body[..200] : body; }
                    checkError = $"GitHub API error {(int)response.StatusCode}: {detail}";
                }
            }
            catch (Exception ex) { checkError = ex.Message; }

            _cachedLatest     = latest;
            _cachedCheckError = checkError;
            // On success cache for 1 hour; on error cache for 5 minutes to avoid hammering GitHub
            _cacheExpiresAt   = string.IsNullOrEmpty(checkError)
                ? DateTime.UtcNow.AddHours(1)
                : DateTime.UtcNow.AddMinutes(5);

            return BuildVersionResponse(current, latest, checkError);
        }
        finally { _cacheLock.Release(); }
    }

    private object BuildVersionResponse(string current, string latest, string checkError) => new
    {
        current,
        latest,
        updateAvailable = IsUpdateAvailable(current, latest),
        updating        = _inProgress,
        updateError     = _error,
        checkError,
        repo            = GithubRepo,
    };

    public async Task<bool> StartAsync()
    {
        if (_inProgress) return false;
        if (!await _lock.WaitAsync(0)) return false;

        _inProgress = true;
        _finished   = false;
        _error      = "";
        while (_logs.TryDequeue(out _)) { }
        // Clear any previous persisted completion so stale state doesn't interfere
        db.SetSetting("last_update_completed_at", "");
        db.SetSetting("last_update_error", "");

        _ = Task.Run(RunAsync).ContinueWith(_ => _lock.Release());
        return true;
    }

    public object GetStatus()
    {
        var finished   = _finished;
        var inProgress = _inProgress;
        var error      = _error;

        // The service restarts itself after an update, wiping in-memory state.
        // Check the database so the frontend can detect completion after restart.
        if (!inProgress && !finished)
        {
            var ts = db.GetSetting("last_update_completed_at");
            if (!string.IsNullOrEmpty(ts) && long.TryParse(ts, out var unix))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix;
                if (age < 300) // within 5 minutes
                {
                    finished = true;
                    error    = db.GetSetting("last_update_error");
                }
            }
        }

        return new { inProgress, finished, error, logs = _logs.ToArray() };
    }

    private async Task RunAsync()
    {
        var cmd = UpdaterCommand();
        AddLog($"Starting update command: {cmd}");
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", $"-c \"{cmd}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            while (!proc.StandardOutput.EndOfStream)
                AddLog(await proc.StandardOutput.ReadLineAsync() ?? "");
            await proc.WaitForExitAsync();
            AddLog($"Update command exited with code {proc.ExitCode}");
            if (proc.ExitCode != 0)
                _error = $"Update command exited with code {proc.ExitCode}";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            AddLog($"Update failed: {ex.Message}");
        }
        finally
        {
            _finished   = true;
            _inProgress = false;
            // Persist completion to DB so the frontend can detect it after the
            // service restarts (in-memory state is wiped on restart).
            db.SetSetting("last_update_completed_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            db.SetSetting("last_update_error", _error);
        }
    }

    private string UpdaterCommand()
    {
        var env = Environment.GetEnvironmentVariable("THEMEARR_UPDATER_CMD")?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;

        var helper = "/usr/local/bin/themearr-update";
        if (File.Exists(helper))
            return IsRoot() ? helper : $"sudo {helper}";

        // deploy.sh handles its own restart via systemd-run --no-block, so we
        // must NOT append "systemctl restart" here — that would kill this process
        // (exit 143) before the command is recorded as finished.
        var deployUrl = "https://raw.githubusercontent.com/Themearr/themearr/main/deploy.sh";
        return IsRoot()
            ? $"curl -fsSL {deployUrl} | bash"
            : $"curl -fsSL {deployUrl} | sudo bash";
    }

    private static bool IsRoot()
    {
        // Check EUID env var (may be set explicitly), then fall back to whoami
        var euid = Environment.GetEnvironmentVariable("EUID");
        if (euid == "0") return true;
        try
        {
            var psi = new ProcessStartInfo("id", "-u")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return output == "0";
        }
        catch { return false; }
    }

    private static string NormaliseSemver(string value)
    {
        var m = Regex.Match(value.Trim(), @"v?(\d+)\.(\d+)\.(\d+)");
        return m.Success ? $"v{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}" : value.Trim();
    }

    private static bool IsUpdateAvailable(string current, string latest)
    {
        if (string.IsNullOrEmpty(current) || current.ToLower() is "dev" or "unknown") return false;
        var cv = ParseSemver(current);
        var lv = ParseSemver(latest);
        if (cv.HasValue && lv.HasValue)
        {
            var (cm, cmi, cp) = cv.Value;
            var (lm, lmi, lp) = lv.Value;
            return lm > cm || (lm == cm && lmi > cmi) || (lm == cm && lmi == cmi && lp > cp);
        }
        return !string.IsNullOrEmpty(latest) && current != latest;
    }

    private static (int major, int minor, int patch)? ParseSemver(string value)
    {
        var m = Regex.Match(value.Trim(), @"v?(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    private void AddLog(string msg) => _logs.Enqueue(msg.TrimEnd());
}
