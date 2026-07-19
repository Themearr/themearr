namespace Themearr.API.Services.Health;

/// <summary>
/// The slice of <see cref="AutoDownloadService"/> that the health check needs.
/// Keeping it narrow means the check can be unit-tested without constructing a
/// BackgroundService, a service provider, or a timer.
/// </summary>
public interface IDownloadWorkerStatus
{
    DateTime? LastTickAt     { get; }
    string    LastTickResult { get; }
}
