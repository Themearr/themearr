using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Fetches theme audio through the youtube-mp36 RapidAPI: poll dl?id=… until the
/// server has finished transcoding, then download the resulting MP3 immediately
/// while the link is fresh.
/// </summary>
public class RapidApiThemeAudioProvider(
    Database db,
    IHttpClientFactory httpClientFactory,
    ILogger<RapidApiThemeAudioProvider> log) : IThemeAudioProvider
{
    public string? CheckConfiguration()
    {
        var hasKey  = !string.IsNullOrWhiteSpace(db.GetSetting("rapidapi_key", ""));
        var hasUser = !string.IsNullOrWhiteSpace(db.GetSetting("rapidapi_username", ""));
        if (hasKey && hasUser) return null;

        var missing = (hasKey, hasUser) switch
        {
            (false, false) => "RapidAPI key and username are",
            (false, true)  => "RapidAPI key is",
            _              => "RapidAPI username is",
        };
        return $"Theme downloads are disabled: {missing} not set. " +
               "Add your youtube-mp36 credentials under Settings → RapidAPI.";
    }

    public async Task<string?> DownloadAsync(
        string videoId, string outputPath, Action<string> progress, CancellationToken ct = default)
    {
        var configError = CheckConfiguration();
        if (configError != null)
            throw new ProviderNotConfiguredException(configError);

        var apiKey   = db.GetSetting("rapidapi_key", "");
        var username = db.GetSetting("rapidapi_username", "");

        var usernameMd5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(username))).ToLower();

        progress($"[themearr] Fetching download link for video {videoId}…");
        log.LogInformation("Fetching RapidAPI download link for {VideoId}", videoId);

        var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        var attempt = 0;
        // Cap retries on CDN 4xx to avoid burning RapidAPI quota when a bad
        // video (private/age-gated) returns links that 403 every time.
        const int MaxCdnRetries = 3;
        var cdnRetries = 0;

        string? themeTitle = null;

        while (true)
        {
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("RapidAPI timed out waiting for processing to complete.");

            attempt++;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://youtube-mp36.p.rapidapi.com/dl?id={Uri.EscapeDataString(videoId)}");
            req.Headers.Add("X-RapidAPI-Key", apiKey);
            req.Headers.Add("X-RapidAPI-Host", "youtube-mp36.p.rapidapi.com");

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 429 == free-tier quota exhausted: distinguish it so callers can
                // back off / circuit-break instead of retrying every movie.
                if ((int)resp.StatusCode == 429)
                    throw new QuotaExceededException($"RapidAPI quota exceeded (HTTP 429): {body}");
                throw new InvalidOperationException($"RapidAPI error ({(int)resp.StatusCode}): {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

            if (status == "processing")
            {
                progress($"[themearr] Processing… (attempt {attempt})");
                await Task.Delay(1000, ct);
                continue;
            }

            if (status != "ok")
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : body;
                throw new InvalidOperationException($"RapidAPI error (status={status}): {msg}");
            }

            var link = root.TryGetProperty("link", out var lnk) ? lnk.GetString() : null;
            if (string.IsNullOrEmpty(link))
                throw new InvalidOperationException($"RapidAPI returned ok but missing link: {body}");

            themeTitle = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            progress("[themearr] Got download link. Downloading immediately…");

            // Download immediately while the link is fresh, with whitelist headers
            using var dlReq = new HttpRequestMessage(HttpMethod.Get, link);
            dlReq.Headers.TryAddWithoutValidation("User-Agent", $"Mozilla/5.0 {username}");
            dlReq.Headers.Add("X-RUN", usernameMd5);
            using var dlResp = await http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!dlResp.IsSuccessStatusCode)
            {
                cdnRetries++;
                if (cdnRetries > MaxCdnRetries)
                    throw new InvalidOperationException($"CDN download kept failing after {MaxCdnRetries} retries (last status {(int)dlResp.StatusCode}). Giving up to preserve RapidAPI quota.");
                // Exponential backoff: 2s, 4s, 8s
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, cdnRetries));
                progress($"[themearr] Link 4xx ({(int)dlResp.StatusCode}), retry {cdnRetries}/{MaxCdnRetries} in {backoff.TotalSeconds:0}s…");
                await Task.Delay(backoff, ct);
                continue;
            }

            try
            {
                await using var fileStream = File.Create(outputPath);
                await StreamLimits.CopyWithLimitAsync(
                    await dlResp.Content.ReadAsStreamAsync(ct), fileStream, StreamLimits.MaxThemeBytes, ct);
                await fileStream.FlushAsync(ct);
            }
            catch
            {
                // Don't leave a truncated theme.mp3 behind on size-limit/IO failure.
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort */ }
                throw;
            }
            break;
        }

        return themeTitle;
    }
}
