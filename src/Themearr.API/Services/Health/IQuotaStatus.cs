namespace Themearr.API.Services.Health;

/// <summary>
/// The slice of <see cref="DownloadService"/> the health check needs. Narrow so the
/// check can be tested without constructing the full download pipeline.
/// </summary>
public interface IQuotaStatus
{
    bool IsQuotaCoolingDown(out DateTime untilUtc);
}
