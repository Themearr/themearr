using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

public sealed class AudioProcessingCheck(Database db, FfmpegThemeAudioProcessor processor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.GetAudioProcessingOptions().Enabled) return HealthCheckResult.Healthy("Audio processing is disabled.");
        return await processor.IsAvailableAsync(cancellationToken)
            ? HealthCheckResult.Healthy("ffmpeg and ffprobe are available.")
            : HealthCheckResult.Unhealthy("Audio processing is enabled but ffmpeg or ffprobe is unavailable. Install ffmpeg before enabling processing.");
    }
}
