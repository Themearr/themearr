using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class UpdateService(Database db, IConfiguration config, ILogger<UpdateService> log)
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

    public async Task<object> GetVersionInfoAsync()
    {
        var current = NormaliseSemver(CurrentVersion());
        var latest = "";
        var checkError = "";
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("User-Agent", "themearr");
            var resp = await http.GetStringAsync($"https://api.github.com/repos/{GithubRepo}/releases/latest");
            var doc  = JsonDocument.Parse(resp);
            latest   = NormaliseSemver(doc.RootElement.GetProperty("tag_name").GetString() ?? "");
        }
        catch (Exception ex) { checkError = ex.Message; }

        return new
        {
            current,
            latest,
            updateAvailable = IsUpdateAvailable(current, latest),
            updating        = _inProgress,
            updateError     = _error,
            checkError,
            repo            = GithubRepo,
        };
    }

    public async Task<bool> StartAsync()
    {
        if (_inProgress) return false;
        if (!await _lock.WaitAsync(0)) return false;

        _inProgress = true;
        _finished   = false;
        _error      = "";
        while (_logs.TryDequeue(out _)) { }

        _ = Task.Run(RunAsync).ContinueWith(_ => _lock.Release());
        return true;
    }

    public object GetStatus() => new
    {
        inProgress = _inProgress,
        finished   = _finished,
        error      = _error,
        logs       = _logs.ToArray(),
    };

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
        }
    }

    private string UpdaterCommand()
    {
        var env = Environment.GetEnvironmentVariable("THEMEARR_UPDATER_CMD")?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;

        var helper = "/usr/local/bin/themearr-update";
        if (File.Exists(helper))
            return Environment.GetEnvironmentVariable("EUID") == "0" ? helper : $"sudo {helper}";

        var deployUrl = "https://raw.githubusercontent.com/Themearr/themearr/main/deploy.sh";
        var isRoot = Environment.GetEnvironmentVariable("EUID") == "0";
        return isRoot
            ? $"TMP_DEPLOY=/tmp/themearr-deploy.sh && curl -fsSL {deployUrl} -o \"$TMP_DEPLOY\" && bash \"$TMP_DEPLOY\" && systemctl restart themearr"
            : $"TMP_DEPLOY=/tmp/themearr-deploy.sh && curl -fsSL {deployUrl} -o \"$TMP_DEPLOY\" && sudo bash \"$TMP_DEPLOY\" && sudo systemctl restart themearr";
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
