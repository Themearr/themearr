using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class AudioProcessingHealthTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_tools_only_fail_health_when_processing_is_enabled(bool enabled)
    {
        using var dir = new TempDir(); var db = new Database(dir.File("test.db")); db.Init(); db.SetAudioProcessingOptions(new(enabled));
        var processor = new FfmpegThemeAudioProcessor(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Themearr:FfmpegPath"] = dir.File("absent"), ["Themearr:FfprobePath"] = dir.File("absent") }).Build());
        var status = await new AudioProcessingCheck(db,processor).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(enabled ? HealthStatus.Unhealthy : HealthStatus.Healthy,status.Status);
    }
    [Fact]
    public async Task Available_tools_are_reported_healthy()
    {
        using var dir = new TempDir(); var db = new Database(dir.File("test.db")); db.Init(); db.SetAudioProcessingOptions(new(true));
        var status = await new AudioProcessingCheck(db,FfmpegProcessTests.Processor()).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy,status.Status);
    }
}
