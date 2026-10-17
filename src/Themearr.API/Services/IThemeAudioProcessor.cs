namespace Themearr.API.Services;

public sealed record AudioProcessingResult(long Bytes, double DurationSeconds, string Fingerprint);

public interface IThemeAudioProcessor
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    Task<AudioProcessingResult> ProcessAsync(string inputPath, string outputPath,
        AudioProcessingOptions options, Action<string> progress, CancellationToken ct);
}
