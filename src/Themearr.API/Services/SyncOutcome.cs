namespace Themearr.API.Services;

/// <summary>
/// Wording for a finished library sync, shown as the last result on the
/// System → Tasks tab.
/// </summary>
public static class SyncOutcome
{
    /// <summary>
    /// Describes how a sync ended. A failure is deliberately described in fixed
    /// text: the raw error can carry a Plex server URL or token, and this string is
    /// rendered in the browser. The detail belongs in the application log.
    /// </summary>
    public static string Describe(string error, int synced) =>
        string.IsNullOrEmpty(error)
            ? $"{synced} movie{(synced == 1 ? "" : "s")} synced"
            : "sync failed — see the application log";
}
