namespace Themearr.API.Services.Health;

/// <summary>
/// The slice of <see cref="DownloadService"/> the health check needs. Narrow so the
/// check can be tested without constructing the full download pipeline.
/// </summary>
public interface IQuotaStatus
{
    /// <summary>
    /// True while downloads are paused after a quota rejection. <paramref name="untilUtc"/>
    /// is only meaningful when this returns true — callers must not read it otherwise, as it
    /// may hold a stale timestamp from a cooldown that has already lapsed.
    /// </summary>
    bool IsQuotaCoolingDown(out DateTime untilUtc);
}

/// <summary>
/// Shared wording for the quota-cooldown message, used by both DownloadService (which
/// blocks a download attempt with it) and RapidApiCheck (which reports it passively)
/// so the two can't independently drift. Each caller still appends/wraps its own
/// call-to-action around this core fragment as fits its context.
/// </summary>
public static class QuotaMessages
{
    public static string CooldownUntil(DateTime untilUtc) =>
        $"RapidAPI quota is exhausted — downloads are paused until {untilUtc:HH:mm} UTC";
}
