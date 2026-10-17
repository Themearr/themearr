using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Themearr.API.Services;

public sealed class FfmpegThemeAudioProcessor(IConfiguration config) : IThemeAudioProcessor
{
    private readonly SemaphoreSlim _encoder = new(1, 1);
    private string Ffmpeg => config["Themearr:FfmpegPath"] ?? "ffmpeg";
    private string Ffprobe => config["Themearr:FfprobePath"] ?? "ffprobe";
    // Restrict demuxers too: a local playlist could otherwise ask file:// to open
    // a second file. Neither probing nor decoding may follow network playlists.
    private const string Formats = "mp3,wav,mov,aac,flac,ogg,matroska,aiff,asf";
    private static readonly string[] LocalInput = ["-protocol_whitelist", "file,pipe", "-format_whitelist", Formats];
    private sealed record Probe(double Duration, string Codec, int SampleRate, int Channels);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var tool in new[] { Ffmpeg, Ffprobe })
                if ((await AudioProcessRunner.RunAsync(tool, ["-version"], TimeSpan.FromSeconds(3), ct)).ExitCode != 0) return false;
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException) { return false; }
    }

    public async Task<AudioProcessingResult> ProcessAsync(string inputPath, string outputPath, AudioProcessingOptions options, Action<string> progress, CancellationToken ct)
    {
        if (!options.Enabled) throw new ArgumentException("Audio processing is disabled.");
        if (options.ValidationError() is { } error) throw new ArgumentException(error);
        var input = Path.GetFullPath(inputPath); var output = Path.GetFullPath(outputPath);
        if (input == output) throw new ArgumentException("Processing output must be a separate staged file.");
        var size = new FileInfo(input).Length;
        if (size is <= 0 or > StreamLimits.MaxThemeBytes) throw new InvalidOperationException("Audio input is empty or exceeds the size limit.");
        var seconds = int.TryParse(config["Themearr:AudioProcessingTimeoutSeconds"], out var value) ? Math.Clamp(value, 1, 120) : 120;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await _encoder.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Audio processing exceeded its time budget while waiting for the encoder."); }
        try
        {
            var source = await ProbeAsync(input, deadline.Token);
            var trim = options.TrimSeconds is { } duration ? $"atrim=duration={duration},asetpts=PTS-STARTPTS," : "";
            var filter = trim.TrimEnd(',');
            if (options.Normalize)
            {
                progress("[themearr] Measuring audio loudness.");
                var measured = await RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "info", ..LocalInput, "-i", input,
                    "-map", "0:a:0", "-af", trim + "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"], deadline.Token);
                var start = measured.Stderr.LastIndexOf('{'); var end = measured.Stderr.LastIndexOf('}');
                if (start < 0 || end <= start) throw new InvalidOperationException("Audio loudness measurement was invalid.");
                using var json = JsonDocument.Parse(measured.Stderr[start..(end + 1)]);
                string Number(string name)
                {
                    var raw = json.RootElement.GetProperty(name).GetString();
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        throw new InvalidOperationException("Cannot normalize silent or invalid audio.");
                    return number.ToString("R", CultureInfo.InvariantCulture);
                }
                filter = trim + $"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={Number("input_i")}:measured_TP={Number("input_tp")}:measured_LRA={Number("input_lra")}:measured_thresh={Number("input_thresh")}:offset={Number("target_offset")}:linear=true";
            }
            progress("[themearr] Encoding theme audio.");
            List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-xerror", ..LocalInput, "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1"];
            if (filter.Length > 0) args.AddRange(["-af", filter]);
            args.AddRange(["-ar", "44100", "-ac", "2", "-c:a", "libmp3lame", "-b:a", $"{options.BitrateKbps}k", "-fs", (StreamLimits.MaxThemeBytes + 1).ToString(CultureInfo.InvariantCulture), "-f", "mp3", "-y", output]);
            await RunAsync(Ffmpeg, args, deadline.Token);
            var bytes = new FileInfo(output).Length;
            if (bytes is <= 0 or > StreamLimits.MaxThemeBytes) throw new InvalidOperationException("Processed audio is empty or exceeds the size limit.");
            var result = await ProbeAsync(output, deadline.Token);
            var expected = Math.Min(source.Duration, options.TrimSeconds ?? source.Duration);
            if (result.Codec != "mp3" || result.SampleRate != 44100 || result.Channels != 2 || Math.Abs(result.Duration - expected) > 0.2)
                throw new InvalidOperationException("Processed audio failed format or duration validation.");
            await RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", ..LocalInput, "-i", output, "-map", "0:a:0", "-f", "null", "-"], deadline.Token);
            using var stream = File.OpenRead(output);
            return new(bytes, result.Duration, Convert.ToHexString(await SHA256.HashDataAsync(stream, deadline.Token)));
        }
        catch (Exception ex)
        {
            try { File.Delete(output); } catch (IOException) { }
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException("Audio processing exceeded its time budget.", ex);
            throw;
        }
        finally { _encoder.Release(); }
    }
    private static async Task<AudioProcessOutput> RunAsync(string tool, IEnumerable<string> args, CancellationToken ct)
    {
        var result = await AudioProcessRunner.RunAsync(tool, args, TimeSpan.FromSeconds(120), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Audio tool failed (exit {result.ExitCode}). Check the source audio and ffmpeg installation.");
        return result;
    }
    private async Task<Probe> ProbeAsync(string path, CancellationToken ct)
    {
        var result = await RunAsync(Ffprobe, ["-v", "error", ..LocalInput, "-show_entries", "stream=codec_type,codec_name,sample_rate,channels:format=duration", "-of", "json", path], ct);
        using var json = JsonDocument.Parse(result.Stdout);
        var stream = json.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        if (stream.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Source contains no audio stream.");
        if (!double.TryParse(json.RootElement.GetProperty("format").GetProperty("duration").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("Audio duration is invalid.");
        return new(duration, stream.GetProperty("codec_name").GetString() ?? "", int.Parse(stream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture), stream.GetProperty("channels").GetInt32());
    }
}
