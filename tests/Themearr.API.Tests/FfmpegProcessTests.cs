using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class FfmpegProcessTests
{
    internal static string Encoder => Environment.GetEnvironmentVariable("THEMEARR_TEST_FFMPEG") ?? "ffmpeg";
    internal static string Probe => Environment.GetEnvironmentVariable("THEMEARR_TEST_FFPROBE") ?? "ffprobe";
    internal static FfmpegThemeAudioProcessor Processor() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?> { ["Themearr:FfmpegPath"] = Encoder, ["Themearr:FfprobePath"] = Probe }).Build());
    internal static void Wave(string path, int seconds = 35, bool silence = false)
    {
        using var writer = new BinaryWriter(File.Create(path)); var samples = seconds * 44100;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write(silence ? (short)0 : (short)(Math.Sin(2 * Math.PI * 440 * i / 44100) * 6000));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_encoder_produces_thirty_seconds_of_decodable_stereo_mp3(bool normalize)
    {
        using var dir = new TempDir(); var input = dir.File("input ' $(literal).wav"); var output = dir.File("processed.mp3"); Wave(input);
        var result = await Processor().ProcessAsync(input,output,new(true,30,normalize,192),_ => {},CancellationToken.None);
        Assert.InRange(result.DurationSeconds,29.9,30.2); Assert.InRange(result.Bytes,650000,800000); Assert.Equal(64,result.Fingerprint.Length);
        var probe = await AudioProcessRunner.RunAsync(Probe,["-v","error","-show_streams","-of","json",output],TimeSpan.FromSeconds(5),CancellationToken.None);
        using var json = JsonDocument.Parse(probe.Stdout); var stream = json.RootElement.GetProperty("streams")[0];
        Assert.Equal("mp3",stream.GetProperty("codec_name").GetString()); Assert.Equal("44100",stream.GetProperty("sample_rate").GetString()); Assert.Equal(2,stream.GetProperty("channels").GetInt32());
        if (normalize)
        {
            var measure = await AudioProcessRunner.RunAsync(Encoder,["-hide_banner","-i",output,"-af","loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json","-f","null","-"],TimeSpan.FromSeconds(10),CancellationToken.None);
            // FFmpeg can append output statistics after the loudnorm JSON report.
            var start = measure.Stderr.LastIndexOf('{'); var end = measure.Stderr.LastIndexOf('}');
            Assert.True(start >= 0 && end > start, measure.Stderr);
            using var loudness = JsonDocument.Parse(measure.Stderr[start..(end + 1)]);
            Assert.InRange(double.Parse(loudness.RootElement.GetProperty("input_i").GetString()!,System.Globalization.CultureInfo.InvariantCulture),-17,-15);
        }
    }
    [Fact]
    public async Task Disabled_dependencies_are_optional_but_enabled_unavailable_tools_fail()
    {
        using var dir = new TempDir(); var processor = new FfmpegThemeAudioProcessor(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Themearr:FfmpegPath"] = dir.File("absent"), ["Themearr:FfprobePath"] = dir.File("absent") }).Build());
        Assert.False(await processor.IsAvailableAsync()); Wave(dir.File("input.wav"),5);
        await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessAsync(dir.File("input.wav"),dir.File("out.mp3"),new(true),_ => {},CancellationToken.None));
        Assert.False(File.Exists(dir.File("out.mp3")));
    }
    [Fact]
    public async Task Silent_normalization_and_malformed_audio_fail_without_altering_source()
    {
        using var dir = new TempDir(); var input = dir.File("silent.wav"); Wave(input,5,true); var original = File.ReadAllBytes(input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(input,dir.File("out.mp3"),new(true,5,true),_ => {},CancellationToken.None));
        Assert.Equal(original,File.ReadAllBytes(input)); Assert.False(File.Exists(dir.File("out.mp3")));
        File.WriteAllText(input,"not audio");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(input,dir.File("out.mp3"),new(true),_ => {},CancellationToken.None));
    }
    [Fact]
    public async Task Playlists_cannot_follow_network_or_other_local_sources()
    {
        using var dir = new TempDir(); using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); listener.Start();
        var accepted = listener.AcceptTcpClientAsync(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(dir.File("playlist"),$"#EXTM3U\n#EXTINF:30,\nhttp://127.0.0.1:{port}/secret\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(dir.File("playlist"),dir.File("out.mp3"),new(true),_ => {},CancellationToken.None));
        Assert.False(accepted.IsCompleted); Assert.False(File.Exists(dir.File("out.mp3")));
    }
    [Fact]
    public async Task Oversized_input_is_rejected_before_any_encoder_work()
    {
        using var dir = new TempDir(); using (var file = File.Create(dir.File("large"))) file.SetLength(StreamLimits.MaxThemeBytes + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(dir.File("large"),dir.File("out.mp3"),new(true),_ => {},CancellationToken.None));
        Assert.False(File.Exists(dir.File("out.mp3")));
    }
    [Fact]
    public async Task Runner_drains_both_pipes_with_bounded_retention_and_preserves_literal_arguments()
    {
        using var dir = new TempDir(); var literal = "'\";$(touch NEVER);\\n";
        var result = await AudioProcessRunner.RunAsync("python3",["-c","import os,sys;os.write(1,b'x'*200000);os.write(2,b'y'*200000);sys.exit(7)",literal],TimeSpan.FromSeconds(5),CancellationToken.None);
        Assert.Equal(7,result.ExitCode); Assert.Equal(65536,result.Stdout.Length); Assert.Equal(65536,result.Stderr.Length);
        var echo = await AudioProcessRunner.RunAsync("python3",["-c","import sys;print(sys.argv[1],end='')",literal],TimeSpan.FromSeconds(5),CancellationToken.None);
        Assert.Equal(literal,echo.Stdout); Assert.False(File.Exists(dir.File("NEVER")));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Runner_kills_process_tree_on_timeout_or_cancellation(bool cancel)
    {
        using var dir = new TempDir(); using var cts = new CancellationTokenSource(); var pidFile = dir.File("child.pid");
        var command = "import subprocess,sys,time; p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);open(sys.argv[1],'w').write(str(p.pid));time.sleep(60)";
        var run = AudioProcessRunner.RunAsync("python3",["-c",command,pidFile],TimeSpan.FromSeconds(2),cts.Token);
        if (cancel)
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidFile)) await Task.Delay(10,ready.Token);
            cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => run);
        var pid = int.Parse(File.ReadAllText(pidFile));
        try { using var child = Process.GetProcessById(pid); Assert.True(child.HasExited || child.WaitForExit(3000)); }
        catch (ArgumentException) { }
    }
    [Theory]
    [InlineData("oversize")] [InlineData("noaudio")] [InlineData("nan")] [InlineData("badjson")]
    public async Task Controlled_tool_failures_reject_unverified_output(string mode)
    {
        using var dir = new TempDir(); var tool = dir.File("fake-audio-tool"); var input = dir.File("input"); var output = dir.File("out.mp3"); File.WriteAllText(input,"source");
        File.WriteAllText(tool,"""
            #!/usr/bin/env python3
            import sys,json
            mode=MODE_VALUE
            a=sys.argv[1:]
            if '-show_entries' in a:
                streams=[] if mode=='noaudio' else [{'codec_type':'audio','codec_name':'mp3','sample_rate':'44100','channels':2}]
                print(json.dumps({'streams':streams,'format':{'duration':'30' if a[-1].endswith('.mp3') else '35'}}))
            elif '-af' in a and 'print_format=json' in a[a.index('-af')+1]:
                if mode=='badjson':sys.stderr.write('invalid measurement')
                else:sys.stderr.write(json.dumps({'input_i':'NaN','input_tp':'-5','input_lra':'1','input_thresh':'-30','target_offset':'0'}))
            elif a[-1]=='-':sys.stderr.write('Peak level dB: -3.0')
            else:
                with open(a[-1],'wb') as f:
                    if mode=='oversize':f.truncate(104857601)
                    else:f.write(b'ID3test')
            """.Replace("MODE_VALUE",JsonSerializer.Serialize(mode)));
        File.SetUnixFileMode(tool,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var processor = new FfmpegThemeAudioProcessor(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Themearr:FfmpegPath"] = tool, ["Themearr:FfprobePath"] = tool }).Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(input,output,new(true,30,mode is "nan" or "badjson"),_ => {},CancellationToken.None));
        Assert.False(File.Exists(output)); Assert.Equal("source",File.ReadAllText(input));
    }

    [Fact]
    public async Task One_encoder_serializes_measurement_and_encoding_across_files()
    {
        using var dir = new TempDir(); Wave(dir.File("input.wav"),5); var processor = Processor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => processor.ProcessAsync(dir.File("input.wav"),dir.File("first.mp3"),new(true,5),_ => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release encoder"); },CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = processor.ProcessAsync(dir.File("input.wav"),dir.File("second.mp3"),new(true,5),_ => secondEntered.TrySetResult(),CancellationToken.None);
        try { Assert.NotSame(secondEntered.Task,await Task.WhenAny(secondEntered.Task,Task.Delay(300))); }
        finally { release.Set(); await Task.WhenAll(first,second); }
    }

    [Fact]
    public async Task Silent_audio_without_normalization_is_rejected_before_publication()
    {
        using var dir = new TempDir(); var input = dir.File("silent.wav"); Wave(input,5,true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(input,dir.File("out.mp3"),new(true,5,false),_ => {},CancellationToken.None));
        Assert.False(File.Exists(dir.File("out.mp3"))); Assert.True(File.Exists(input));
    }

}
