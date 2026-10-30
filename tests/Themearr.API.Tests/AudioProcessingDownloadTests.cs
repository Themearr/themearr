using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class AudioProcessingDownloadTests
{
    private static readonly byte[] Original = [0x49, 0x44, 0x33, 8];
    private static readonly byte[] Delivered = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 9];
    private static readonly byte[] Processed = [0x49, 0x44, 0x33, 1, 2];
    private sealed class BytesProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string id, string path, Action<string> progress, CancellationToken ct) { File.WriteAllBytes(path, Delivered); return Task.FromResult<string?>("Theme"); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Delivered) });
    }
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(new Handler()); }
    private sealed class Processor : IThemeAudioProcessor
    {
        public int Calls;
        public bool Fail;
        public bool Invalid;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AudioProcessingResult> ProcessAsync(string input, string output, AudioProcessingOptions options, Action<string> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Assert.Equal(Delivered, File.ReadAllBytes(input)); Assert.Equal(30, options.TrimSeconds);
            Assert.StartsWith(".themearr-audio-", Path.GetFileName(output)); Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            if (Fail) throw new IOException("encoder failed");
            var bytes = Invalid ? Delivered : Processed; File.WriteAllBytes(output, bytes);
            return new(bytes.Length, 30, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Dir = new(); public readonly Database Db; public readonly string Folder; public readonly string Id;
        public readonly Processor Processor = new(); public readonly DownloadService Service;
        public Fixture(bool enabled, bool inject = true)
        {
            Db = new(Dir.File("test.db")); Db.Init(); Folder = Dir.File("Media"); Directory.CreateDirectory(Folder); Id = MediaFolderId.For(Folder);
            Db.SetLibraryPaths([Dir.Path]); Db.UpsertMovies([new(Folder,"plex","s:1","Item",2020,Folder)]); Db.UpsertShows([new(Folder,"plex","s:1","Item",2020,Folder,false)]);
            File.WriteAllBytes(Path.Combine(Folder,"theme.mp3"), Original); Db.SetAudioProcessingOptions(new(enabled,30,false,192));
            Service = new(new BytesProvider(), Db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance, audioProcessor: inject ? Processor : null);
        }
        public async Task<DownloadStatusDto> Finish(string media)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true) { var status = Service.GetAttemptStatus(Id,media,null); if (status.Finished) return status; await Task.Delay(10,cts.Token); }
        }
        public void Dispose() { Processor.Release.TrySetResult(); Dir.Dispose(); }
    }
    [Theory]
    [InlineData("movie",true)] [InlineData("show",true)] [InlineData("movie",false)] [InlineData("show",false)]
    public async Task Both_sources_and_media_process_only_when_opted_in(string media, bool provider)
    {
        using var f = new Fixture(true); f.Processor.Release.TrySetResult();
        Assert.True(f.Service.Start(f.Id,provider ? "https://www.youtube.com/watch?v=abcdefghijk" : "https://93.184.216.34/theme",media,"manual"));
        var status = await f.Finish(media); Assert.Null(status.Error); Assert.Equal(1,f.Processor.Calls);
        Assert.Equal(Processed,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3"))); Assert.False(File.Exists(Path.Combine(f.Folder,"theme.m4a")));
        Assert.Empty(Directory.GetFiles(f.Folder,".themearr-audio-*"));
    }
    [Fact]
    public async Task Disabled_processing_preserves_provider_bytes_and_never_invokes_encoder()
    {
        using var f = new Fixture(false); Assert.True(f.Service.Start(f.Id,"https://www.youtube.com/watch?v=abcdefghijk","movie","manual"));
        Assert.Null((await f.Finish("movie")).Error); Assert.Equal(0,f.Processor.Calls);
        Assert.Equal(Delivered,File.ReadAllBytes(Path.Combine(f.Folder,"theme.m4a")));
    }
    [Theory]
    [InlineData("failure")] [InlineData("invalid")] [InlineData("unavailable")]
    public async Task Processing_errors_preserve_good_original_and_selection_history(string mode)
    {
        using var f = new Fixture(true,mode != "unavailable"); f.Processor.Fail = mode == "failure"; f.Processor.Invalid = mode == "invalid"; f.Processor.Release.TrySetResult();
        Assert.True(f.Service.Start(f.Id,"https://www.youtube.com/watch?v=abcdefghijk","movie","manual"));
        Assert.NotNull((await f.Finish("movie")).Error); Assert.Equal(Original,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));
        Assert.Empty(f.Db.GetThemeHistory()); Assert.Empty(Directory.GetFiles(f.Folder,".themearr-audio-*"));
    }
    [Fact]
    public async Task Deletion_during_processing_cannot_publish_processed_bytes()
    {
        using var f = new Fixture(true); Assert.True(f.Service.Start(f.Id,"https://www.youtube.com/watch?v=abcdefghijk","show","manual"));
        await f.Processor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Service.DeleteTheme(f.Id,"show"); f.Processor.Release.TrySetResult();
        Assert.NotNull((await f.Finish("show")).Error); Assert.False(ThemeFiles.HasUsableTheme(f.Folder)); Assert.Empty(f.Db.GetThemeHistory());
    }
}
