namespace Themearr.API.Services;

/// <summary>
/// Neutralizes user-controlled values before they are written to a log so they can't
/// forge or split log lines (CWE-117 log injection). Strips CR and LF — the newline
/// characters an attacker would use to inject a fabricated log entry.
/// </summary>
public static class LogSanitizer
{
    public static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("\r", "").Replace("\n", "");
}
