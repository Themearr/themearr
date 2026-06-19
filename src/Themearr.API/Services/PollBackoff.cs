namespace Themearr.API.Services;

/// <summary>
/// Backoff schedule for polling the RapidAPI conversion endpoint while a transcode
/// is "processing". Each poll is a billed request, so we back off exponentially
/// (1s, 2s, 4s, 8s, …) capped at 15s rather than hammering at 1 Hz and burning the
/// free-tier quota — which would trip the 429 circuit breaker.
/// </summary>
public static class PollBackoff
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(15);

    public static TimeSpan ForAttempt(int attempt)
    {
        if (attempt <= 1) return TimeSpan.FromSeconds(1);
        // 2^(attempt-1) seconds; once the exponent reaches the cap, stop computing
        // powers (also avoids overflow for large attempt counts).
        var exponent = attempt - 1;
        if (exponent >= 4) return Cap;        // 2^4 = 16s > 15s cap
        return TimeSpan.FromSeconds(1 << exponent);
    }
}
