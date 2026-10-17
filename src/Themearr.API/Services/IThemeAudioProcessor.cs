namespace Themearr.API.Services;

public sealed record AudioProcessingResult(long Bytes, double DurationSeconds, string Fingerprint);

public interface IThemeAudioProcessor
{
    Task<AudioProcessingResult> ProcessAsync(string inputPath, string outputPath,
        AudioProcessingOptions options, Action<string> progress, CancellationToken ct);
}
