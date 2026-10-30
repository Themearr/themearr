namespace Themearr.API.Services;

public sealed record DownloadStartResult(bool Started, Guid? AttemptId);

public sealed record DownloadStatusDto(bool InProgress, bool Finished, string? Error,
    string[] Logs, Guid? AttemptId, bool Known, string State, Guid? CurrentAttemptId = null);
