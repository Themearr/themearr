namespace Themearr.API.Services.Health;

/// <summary>
/// The slice of <see cref="AutoDownloadService"/> that the health check needs.
/// Keeping it narrow means the check can be unit-tested without constructing a
/// BackgroundService, a service provider, or a timer.
/// </summary>
public interface IDownloadWorkerStatus
{
    /// <summary>When the worker's loop began (before its warm-up delay); null if it
    /// has not started yet. Lets the check tell "started but never ticked" (dead in
    /// warm-up) from "just started".</summary>
    DateTime? StartedAt      { get; }
    DateTime? LastTickAt     { get; }
    string    LastTickResult { get; }
}
