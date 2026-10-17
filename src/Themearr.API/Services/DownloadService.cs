using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(
    IThemeAudioProvider provider, Database db, IHttpClientFactory httpClientFactory,
    IConfiguration config, ILogger<DownloadService> log,
    // Optional so the existing five-argument test constructions keep compiling, and
    // null-safe because the refresh is best-effort anyway. DI supplies the real
    // PlexService (registered in Program.cs) without a registration change.
    PlexService? plex = null, IThemeAudioProcessor? audioProcessor = null) : Health.IQuotaStatus
{
    private sealed record JobState(bool InProgress, bool Finished, string? Error, DateTime StartedAtUtc = default, Guid Generation = default, string? Folder = null, long FolderRevision = 0, string Phase = "pending");
    private readonly ConcurrentDictionary<string, JobState>          _jobs    = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _jobLogs = new();

    // Serializes the whole theme landing (write → promote → cleanup) per canonical
    // folder. Job single-flight is keyed {mediaType}:{id} (see JobKey), and a movie and
    // a show sharing one folder are two DISTINCT jobs mutating the same theme.* files:
    // unserialized, each one's name-dependent cleanup can delete the other's freshly
    // landed theme, and both share one in-flight part name (#48 concurrency review).
    // Entries are never removed — bounded by the number of distinct media folders, and
    // an idle SemaphoreSlim is tiny. Instance state suffices: DownloadService is a DI
    // singleton (Program.cs), so every download in the process crosses this map.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folderGates = new(StringComparer.Ordinal);

    private const int MaxLogLines = 300;
    private readonly object _admissionLock = new();
    private readonly Dictionary<string, long> _folderRevisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyFolders = new(StringComparer.Ordinal);

    // After a provider quota-exhaustion (HTTP 429) we pause downloads until this time
    // so the auto-download loop stops hammering the API. Volatile single-writer state.
    private volatile int _quotaCooldownUntilUnix;

    private TimeSpan QuotaCooldown
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("THEMEARR_QUOTA_COOLDOWN_MINUTES")
                      ?? config["Themearr:QuotaCooldownMinutes"];
            return int.TryParse(raw, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : TimeSpan.FromHours(1);
        }
    }

    // Hard ceiling on a single download. A stalled CDN connection (silent TCP drop)
    // can leave the response-stream read hanging forever; without this bound the job
    // stays "in progress" and wedges the auto-download loop until a restart.
    private TimeSpan DownloadTimeout
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("THEMEARR_DOWNLOAD_TIMEOUT_SECONDS")
                      ?? config["Themearr:DownloadTimeoutSeconds"];
            return int.TryParse(raw, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(15);
        }
    }

    // True while a quota cooldown is active; `untilUtc` is the (UTC) resume time.
    public bool IsQuotaCoolingDown(out DateTime untilUtc)
    {
        var until = _quotaCooldownUntilUnix;
        untilUtc = until == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(until).UtcDateTime;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < until;
    }

    // Job state is namespaced by media type: movie and show ids come from the same
    // MediaFolderId hash space, so a show and a movie pointing at the same folder would
    // otherwise share (and clobber) one job entry.
    private static string JobKey(string mediaType, string id) => $"{mediaType}:{id}";

    // pickedBy/matchScore are the re-check's provenance: "auto" means the matcher chose
    // the video (both auto-download workers and MoviesController.AutoDownload — a human
    // may have clicked the button, but did not pick the result), "manual" means a human
    // picked it (the movies/shows page download and download-url endpoints). mediaType
    // and pickedBy are deliberately NOT defaulted: pickedBy is the fact the re-check's
    // whole safety property rests on, and a default of "auto" would let a forgotten call
    // site compile and silently record the value that permits automatic replacement — the
    // one value it must never get by accident. matchScore stays optional; "no score" is a
    // legitimate state for a manual pick, not an oversight.
    public bool Start(string id, string youtubeUrl, string mediaType, string pickedBy, int? matchScore = null, RecheckDownloadExpectation? recheck = null)
        => StartAttempt(id, youtubeUrl, mediaType, pickedBy, matchScore, recheck).Started;

    public DownloadStartResult StartAttempt(string id, string youtubeUrl, string mediaType, string pickedBy, int? matchScore = null, RecheckDownloadExpectation? recheck = null)
    {
        var key = JobKey(mediaType, id);
        // Serialize admission through URL parsing and reservation. Requests cannot both pass
        // the admission check and spend provider quota on the same item.
        JobState attempt;
        string url;
        lock (_admissionLock)
        {
            if (_jobs.TryGetValue(key, out var existing) && existing.InProgress
                && existing.StartedAtUtc > DateTime.UtcNow - (DownloadTimeout + WatchdogGrace))
                return new(false, null);

            if (recheck != null && !db.IsRecheckCurrent(id, mediaType, recheck.HistoryId)) return new(false, null);
            url = NormaliseYoutubeUrl(youtubeUrl.Trim());
            var logs = new ConcurrentQueue<string>();
            _jobLogs[key] = logs;
            var rawFolder = db.GetThemeFolder(id, mediaType);
            var folder = string.IsNullOrEmpty(rawFolder) ? null : ThemeFiles.CanonicalFolder(rawFolder);
            attempt = new JobState(true, false, null, DateTime.UtcNow, Guid.NewGuid(), folder,
                folder == null ? 0 : _folderRevisions.GetValueOrDefault(folder));
            _jobs[key] = attempt;
        }
        _ = Task.Run(() => RunAsync(id, url, mediaType, pickedBy, matchScore, attempt, recheck));
        return new(true, attempt.Generation);
    }

    // Admission and final publication already share this lock. Deletion must join it,
    // and invalidate jobs admitted before deletion even while their provider is parked.
    public bool DeleteTheme(string id, string mediaType)
    {
        lock (_admissionLock)
        {
            var rawFolder = db.GetThemeFolder(id, mediaType);
            if (string.IsNullOrEmpty(rawFolder)) return false;
            var folder = ThemeFiles.CanonicalFolder(rawFolder);
            ValidateFolderRoots(folder);
            _folderRevisions[folder] = _folderRevisions.GetValueOrDefault(folder) + 1;
            try
            {
                var deleted = db.DeleteThemesInFolder(folder, () => Directory.Exists(folder) && ThemeFiles.DeleteThemes(folder));
                _dirtyFolders.Remove(folder);
                return deleted;
            }
            catch
            {
                // SQLite cannot roll back a file deletion. Keep invalidation and retry
                // status reconciliation instead of allowing older bytes to resurrect it.
                _dirtyFolders.Add(folder);
                throw;
            }
        }
    }

    public bool ReconcileTheme(string id, string mediaType)
    {
        lock (_admissionLock)
        {
            var rawFolder = db.GetThemeFolder(id, mediaType);
            if (string.IsNullOrEmpty(rawFolder)) return false;
            var folder = ThemeFiles.CanonicalFolder(rawFolder);
            return db.ReconcileThemeStatus(id, mediaType, () => ThemeFiles.HasUsableTheme(folder));
        }
    }

    public void ReconcileDirtyFolders()
    {
        lock (_admissionLock)
        {
            foreach (var folder in _dirtyFolders.ToArray())
            {
                try
                {
                    ValidateFolderRoots(folder);
                    db.ReconcileThemeFolder(folder, () => ThemeFiles.HasUsableTheme(folder));
                    _dirtyFolders.Remove(folder);
                }
                catch (Exception ex)
                {
                    log.LogWarning("Theme status reconciliation failed for {Folder}: {Detail}",
                        LogSanitizer.Clean(folder), LogSanitizer.Clean(ex.Message));
                }
            }
        }
    }

    private void ValidateFolderRoots(string folder)
    {
        var roots = db.GetLibraryPaths();
        if (roots.Count > 0 && !ThemeFiles.IsWithinRoots(folder, roots))
            throw new UnauthorizedAccessException("Refusing to mutate a theme outside the configured library roots.");
    }

    private bool OwnsFolderRevision(JobState attempt, string folder) =>
        attempt.Folder == folder && attempt.FolderRevision == _folderRevisions.GetValueOrDefault(folder);

    // Defense-in-depth beyond the per-job timeout: if a job somehow stays "in progress"
    // past the timeout plus a grace margin (e.g. a backend that ignores cancellation),
    // stop counting it as blocking so a single pathological download can't wedge the
    // auto-download loop until a restart.
    private TimeSpan WatchdogGrace
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("THEMEARR_DOWNLOAD_WATCHDOG_GRACE_SECONDS")
                      ?? config["Themearr:DownloadWatchdogGraceSeconds"];
            return int.TryParse(raw, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(30);
        }
    }

    public bool IsAnyInProgress()
    {
        var staleBefore = DateTime.UtcNow - (DownloadTimeout + WatchdogGrace);
        return _jobs.Values.Any(j => j.InProgress && j.StartedAtUtc > staleBefore);
    }

    // Single gate for provider-bound downloads, shared by the manual/UI endpoints AND
    // the auto-download loop so they behave identically: blocks when the provider is
    // unconfigured or while a 429 quota cooldown is active (so a manual retry doesn't
    // just burn another billed request). Direct (non-provider) URLs are never gated.
    public string? DownloadBlockedReason(bool isProviderUrl)
    {
        if (!isProviderUrl) return null;
        if (provider.CheckConfiguration() is { } notReady) return notReady;
        if (IsQuotaCoolingDown(out var until))
            return $"{Health.QuotaMessages.CooldownUntil(until)}. Try again later.";
        return null;
    }

    // True if this URL would be handled by the theme-audio provider (a YouTube URL)
    // rather than fetched directly. Used to decide whether a provider readiness
    // check applies before starting a download.
    public static bool IsProviderUrl(string url) => ExtractVideoId(url) != null;

    public object GetStatus(string id, string mediaType = "movie")
    {
        var status = GetAttemptStatus(id, mediaType, null);
        // Existing in-process callers reflect these lowercase anonymous properties.
        return new { inProgress = status.InProgress, finished = status.Finished, error = status.Error,
            logs = status.Logs, attemptId = status.AttemptId, known = status.Known,
            state = status.State, currentAttemptId = status.CurrentAttemptId };
    }

    public DownloadStatusDto GetAttemptStatus(string id, string mediaType, Guid? requestedAttempt)
    {
        lock (_admissionLock)
        {
            var key = JobKey(mediaType, id);
            if (!_jobs.TryGetValue(key, out var state))
                return new(false, false, null, [], null, false, "unknown");
            if (requestedAttempt is { } requested && requested != state.Generation)
                return new(false, false, null, [], requested, true, "superseded", state.Generation);

            _jobLogs.TryGetValue(key, out var logQueue);
            var lines = logQueue?.ToArray() ?? [];
            if (lines.Length > 50) lines = lines[^50..];
            return new(state.InProgress, state.Finished, state.Error, lines, state.Generation, true,
                state.Finished ? (state.Error == null ? "succeeded" : "failed") : state.Phase);
        }
    }

    // Takes the namespaced job key (see JobKey), not a bare media id.
    private void AddLog(string jobKey, string message, JobState? attempt = null)
    {
        lock (_admissionLock)
        {
            if (attempt != null && (!_jobs.TryGetValue(jobKey, out var active) || active.Generation != attempt.Generation)) return;
            if (!_jobLogs.TryGetValue(jobKey, out var logQueue)) return;
            logQueue.Enqueue(message);
            while (logQueue.Count > MaxLogLines)
                logQueue.TryDequeue(out _);
        }
    }

    private async Task RunAsync(string id, string url, string mediaType, string pickedBy, int? matchScore, JobState attempt, RecheckDownloadExpectation? recheck)
    {
        var key = JobKey(mediaType, id);
        void Progress(string message) => AddLog(key, message, attempt);
        void Complete(string? error)
        {
            lock (_admissionLock)
            {
                // Running changes the record snapshot; generation remains its ownership token.
                if (_jobs.TryGetValue(key, out var active) && active.Generation == attempt.Generation)
                    _jobs[key] = active with { InProgress = false, Finished = true, Error = error };
            }
        }
        try
        {
            var item = (mediaType == "show" ? db.GetShow(id) : db.GetMovie(id))
                ?? throw new KeyNotFoundException($"{mediaType} not found: {id}");

            var folder = ThemeFiles.CanonicalFolder(item["folderName"]?.ToString()
                ?? throw new InvalidOperationException($"{mediaType} has no folder path"));

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                throw new ArgumentException("Invalid URL");

            var videoId = ExtractVideoId(url);

            string? themeTitle = null;

            // Confine writes to the configured library roots so a malicious/compromised
            // Plex server can't redirect a theme write to an arbitrary directory (e.g.
            // /opt/themearr). Enforced only when roots are configured, to preserve
            // behaviour on installs that rely solely on direct path resolution.
            var roots = db.GetLibraryPaths();
            if (roots.Count > 0 && !ThemeFiles.IsWithinRoots(folder, roots))
                throw new UnauthorizedAccessException(
                    $"Refusing to write outside the configured library roots: \"{folder}\".");

            // The download lands under a neutral in-flight name and is promoted to its
            // container-correct final name in ONE atomic move — see PromoteThemePart
            // (issue #48). Opt-in processing is staged before this same publication;
            // the default keeps the provider's original bytes and container. This service owns promotion for
            // BOTH branches so there is exactly one namer and the cleanup below always
            // knows the real landed name.
            var partPath = ThemeFiles.ThemePartPath(folder);
            var processedPath = Path.Combine(folder, ".themearr-audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            var processing = db.GetAudioProcessingOptions();

            // Fail fast with an actionable message if the folder isn't writable — the
            // common Proxmox/LXC case where the themearr service user lacks permission
            // on a bind-mounted media folder. Without this the download fails opaquely
            // for every movie and the auto-loop just silently cools each one down.
            if (!ThemeFiles.IsDirectoryWritable(folder))
                throw new UnauthorizedAccessException(
                    $"Cannot write to \"{folder}\". The themearr service user needs write permission on " +
                    $"this {mediaType} folder — on Proxmox/LXC, add the themearr user to your media group.");

            // Bound the whole download (incl. the response-stream read, which
            // HttpClient.Timeout does NOT cover once streaming) so a stalled
            // connection can't hang the job forever.
            using var cts = new CancellationTokenSource(DownloadTimeout);
            var token = cts.Token;

            // One landing per folder at a time, on top of per-job single-flight: a movie
            // and a show can share one folder under two job keys (see JobKey), and two
            // unserialized landings share the part name and can cross-delete each
            // other's landed theme via the name-dependent cleanup (#48 concurrency
            // review). Canonicalized the same way IsWithinRoots canonicalizes.
            var gate = _folderGates.GetOrAdd(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                Progress("[themearr] Another download is writing to this folder — waiting for it to finish.");
                await gate.WaitAsync(token);
            }
            try
            {
                lock (_admissionLock)
                {
                    if (!_jobs.TryGetValue(key, out var active) || active.Generation != attempt.Generation) return;
                    _jobs[key] = active with { Phase = "running" };
                    if (!OwnsFolderRevision(attempt, folder))
                    {
                        Complete("Download cancelled because the theme was deleted after this attempt started.");
                        return;
                    }
                }
                if (recheck != null && !db.IsRecheckCurrent(id, mediaType, recheck.HistoryId))
                {
                    Complete("Replacement cancelled because the theme or its re-check decision changed.");
                    return;
                }
                if (videoId != null)
                {
                    // YouTube URL — delegate to the configured theme-audio provider.
                    themeTitle = await provider.DownloadAsync(videoId, partPath, Progress, token);
                }
                else
                {
                    // Non-YouTube URL — download directly
                    Progress("[themearr] Downloading from URL…");

                    using var dlResp = await FetchFollowingSafeRedirectsAsync(url, token);

                    if (!dlResp.IsSuccessStatusCode)
                    {
                        var errBody = await dlResp.Content.ReadAsStringAsync(token);
                        var snippet = errBody.Length > 300 ? errBody[..300] : errBody;
                        throw new InvalidOperationException($"Download failed ({(int)dlResp.StatusCode}): {snippet}");
                    }

                    // Atomic within the part name too, so a failed or empty download
                    // never clobbers a previously-good theme.
                    await ThemeFiles.WriteAtomicAsync(
                        await dlResp.Content.ReadAsStreamAsync(token), partPath, StreamLimits.MaxThemeBytes, token);
                }

                if (processing.Enabled)
                {
                    if (audioProcessor == null) throw new InvalidOperationException("Audio processing is unavailable. Install ffmpeg and ffprobe before enabling it.");
                    await audioProcessor.ProcessAsync(partPath, processedPath, processing, Progress, token);
                    var bytes = new FileInfo(processedPath).Length;
                    using (var staged = File.OpenRead(processedPath))
                    {
                        var header = new byte[16]; var read = staged.Read(header);
                        if (bytes is <= 0 or > StreamLimits.MaxThemeBytes || ThemeFiles.SniffedThemeExtension(header.AsSpan(0, read)) != ".mp3")
                            throw new InvalidOperationException("Processor did not produce valid MP3 audio.");
                    }
                    File.Move(processedPath, partPath, overwrite: true);
                }

                lock (_admissionLock)
                {
                    // Some providers ignore cancellation. Their late bytes must never
                    // replace the old theme, or commit history for an expired generation.
                    token.ThrowIfCancellationRequested();
                    if (!_jobs.TryGetValue(key, out var active) || active.Generation != attempt.Generation) return;
                    if (!OwnsFolderRevision(attempt, folder))
                    {
                        Complete("Download cancelled because the theme was deleted after this attempt started.");
                        return;
                    }
                    ValidateFolderRoots(folder);
                    void PublishFiles()
                    {
                        token.ThrowIfCancellationRequested();
                        var outputPath = ThemeFiles.PromoteThemePart(folder);

                        // Remove stale alternate-extension theme files (e.g. an old theme.m4a) now
                        // that the new theme file is safely in place — never before the download.
                        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
                            if (!string.Equals(f, outputPath, StringComparison.Ordinal)
                                && Path.GetExtension(f) is not (".part" or ".ytdl"))
                                try { File.Delete(f); } catch { /* best effort */ }
                    }

                    var title = item["title"]?.ToString() ?? "";
                    var year  = item["year"] is int y ? y : (int?)null;
                    if (recheck != null)
                    {
                        if (!db.TryCommitRecheckReplacement(id, mediaType, recheck.HistoryId,
                                title, year, themeTitle, url, pickedBy, matchScore, PublishFiles))
                        {
                            Complete("Replacement cancelled because the theme or its re-check decision changed.");
                            return;
                        }
                    }
                    else
                    {
                        try
                        {
                            db.CommitDownloadedTheme(id, mediaType, title, year, themeTitle, url, pickedBy, matchScore, PublishFiles);
                        }
                        catch { _dirtyFolders.Add(folder); throw; }
                    }
                }
            }
            finally
            {
                try { File.Delete(partPath); } catch { /* best effort */ }
                try { File.Delete(processedPath); } catch { /* best effort */ }
                gate.Release();
            }

            Progress("[themearr] Download complete.");

            // Ask Plex to refresh this item so the theme starts playing without a manual
            // "Refresh Metadata" (issue #45) — partial scan catches movie folders on most
            // installs, but a show's theme stays invisible until the show is refreshed.
            // Runs before the job flips to finished so the outcome lands in this job's log.
            await TryPlexRefreshAsync(item, key, attempt);

            Complete(null);
        }
        catch (QuotaExceededException ex)
        {
            var until = DateTimeOffset.UtcNow.Add(QuotaCooldown);
            _quotaCooldownUntilUnix = (int)until.ToUnixTimeSeconds();
            log.LogWarning("RapidAPI quota exhausted — pausing downloads until {Until:o}. {Detail}",
                until.UtcDateTime, ex.Message);
            Progress($"[themearr] RapidAPI quota exhausted — pausing downloads until {until.UtcDateTime:HH:mm} UTC.");
            Complete(ex.Message);
        }
        catch (OperationCanceledException)
        {
            var msg = $"Download timed out after {DownloadTimeout.TotalSeconds:0}s and was aborted.";
            log.LogWarning("Download for {JobKey} timed out after {Timeout}", LogSanitizer.Clean(key), DownloadTimeout);
            Progress($"[themearr] {msg}");
            Complete(msg);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download failed for {JobKey}", LogSanitizer.Clean(key));
            Complete(ex.Message);
        }
    }

    // Never throws. The theme is already on disk when this runs, so a refresh failure
    // must stay a logged warning — if it escaped, RunAsync's outer catch would report a
    // successfully landed theme as a failed download.
    private async Task TryPlexRefreshAsync(Dictionary<string, object?> item, string jobKey, JobState attempt)
    {
        if (plex == null) return;
        try
        {
            var refreshed = await plex.RefreshItemMetadataAsync(
                item.GetValueOrDefault("source")?.ToString(),
                item.GetValueOrDefault("sourceRef")?.ToString(),
                msg => AddLog(jobKey, msg, attempt));
            if (refreshed)
                AddLog(jobKey, "[themearr] Asked Plex to refresh the item's metadata.", attempt);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Plex metadata refresh failed for {JobKey}", LogSanitizer.Clean(jobKey));
            AddLog(jobKey, "[themearr] Plex metadata refresh failed — refresh the item in Plex manually if the theme doesn't play.", attempt);
        }
    }

    // Fetches `url`, following redirects manually so EVERY hop is re-validated against
    // the SSRF guard. A 3xx to an internal address (169.254.x, 10.x, …) is the classic
    // bypass of an initial-host-only check; the download-url endpoint validates the
    // first host, and this closes the redirect gap. Uses the "no-redirect" client.
    private async Task<HttpResponseMessage> FetchFollowingSafeRedirectsAsync(string url, CancellationToken ct)
    {
        const int MaxRedirects = 5;
        var http = httpClientFactory.CreateClient("no-redirect");
        http.Timeout = Timeout.InfiniteTimeSpan; // the CTS bounds the whole operation

        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var resp = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                resp.Dispose();
                if (hop >= MaxRedirects)
                    throw new InvalidOperationException("Too many redirects while downloading.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme is not ("http" or "https") || HostGuard.IsPrivateOrLoopback(next.Host))
                    throw new InvalidOperationException(
                        "Refusing to follow a redirect to a private, loopback, or non-http(s) address.");
                current = next;
                continue;
            }
            return resp;
        }
    }

    // Single source of truth for YouTube URL parsing. Returns null for non-YouTube URLs.
    private static string? ExtractVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];

        if (host is "youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var v = System.Web.HttpUtility.ParseQueryString(uri.Query)["v"]?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        if (host is "youtu.be")
        {
            var videoId = uri.AbsolutePath.Trim('/');
            return string.IsNullOrEmpty(videoId) ? null : videoId;
        }
        return null;
    }

    private static string NormaliseYoutubeUrl(string url)
    {
        var videoId = ExtractVideoId(url);
        return videoId == null ? url : $"https://www.youtube.com/watch?v={videoId}";
    }
}
