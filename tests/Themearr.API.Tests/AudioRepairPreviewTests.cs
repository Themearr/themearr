using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

internal sealed class RepairTestFixture : IDisposable
{
    internal static readonly byte[] Original = [0x49,0x44,0x33,9,9,9,9,9];
    internal static readonly byte[] Processed = [0x49,0x44,0x33,1,2];
    internal sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    internal sealed class Provider : IThemeAudioProvider
    {
        public int Calls;
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string id,string path,Action<string> progress,CancellationToken ct) { Calls++; File.WriteAllBytes(path,[0x49,0x44,0x33,8,8,8]); return Task.FromResult<string?>("New download"); }
    }
    internal sealed class Processor : IThemeAudioProcessor
    {
        public int Calls; public bool Hold; public bool Fail; public bool Invalid;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AudioProcessingResult> ProcessAsync(string input,string output,AudioProcessingOptions options,Action<string> progress,CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Assert.StartsWith(".themearr-audio-backup-",Path.GetFileName(input)); Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(ct);
            if (Fail) throw new IOException("controlled encoder failure");
            var bytes=Invalid ? new byte[] {0,0,0,24,(byte)'f',(byte)'t',(byte)'y',(byte)'p'} : Processed;
            File.WriteAllBytes(output,bytes); return new(bytes.Length,30,Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public readonly TempDir Dir = new(); public readonly Database Db; public readonly string Folder; public readonly string Id;
    public readonly Processor Audio = new(); public readonly Provider Source = new(); public readonly Clock Time = new();
    public readonly DownloadService Downloads; public readonly AudioRepairService Repair;
    public readonly AudioProcessingOptions Options = new(true,30,false,192);
    public RepairTestFixture(bool shared = true)
    {
        Db=new(Dir.File("test.db"));Db.Init();Folder=Dir.File("Media");Directory.CreateDirectory(Folder);Id=MediaFolderId.For(Folder);
        Db.SetLibraryPaths([Dir.Path]);Db.UpsertMovies([new(Folder,"radarr","1","Item",2020,Folder)]);
        if(shared)Db.UpsertShows([new(Folder,"plex","server:1","Item",2020,Folder,false)]);
        File.WriteAllBytes(Path.Combine(Folder,"theme.mp3"),Original);
        Downloads=new(Source,Db,new Factory(),new ConfigurationBuilder().Build(),NullLogger<DownloadService>.Instance);
        Repair=NewService();
    }
    public AudioRepairService NewService() => new(Db,Downloads,Audio,NullLogger<AudioRepairService>.Instance,clock:Time);
    public Task<AudioRepairPreview> Preview(string media="all",bool ignored=false,IReadOnlyList<AudioRepairSelection>? items=null) => Repair.PreviewAsync(new(media,Options,items,ignored));
    public async Task<AudioRepairStatus> Finish(Guid? run)
    {
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(true) { var status=Repair.GetStatus(run);if(status.State is "completed" or "failed" or "cancelled" or "interrupted")return status;await Task.Delay(10,cts.Token); }
    }
    public object? Scalar(string sql)
    {
        using var conn=new SqliteConnection($"Data Source={Dir.File("test.db")}");conn.Open();using var cmd=conn.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteScalar();
    }
    public void Dispose() { Audio.Release.TrySetResult();Repair.Dispose();Dir.Dispose(); }
}

public class AudioRepairPreviewTests
{
    [Fact]
    public async Task Preview_is_read_only_network_free_and_deduplicates_movie_show_aliases()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();var before=Directory.GetFiles(f.Folder).ToDictionary(x=>x,File.ReadAllBytes);
        var preview=await f.Preview();var item=Assert.Single(preview.Items);Assert.Equal(2,item.Aliases.Count);Assert.Equal(RepairTestFixture.Original.Length,item.BeforeBytes);
        Assert.Equal(0,f.Audio.Calls);Assert.Equal(0,f.Source.Calls);Assert.Equal(before.Keys,Directory.GetFiles(f.Folder));Assert.Equal(before[Path.Combine(f.Folder,"theme.mp3")],File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(RepairTestFixture.Original)),item.InputFingerprint);
    }
    [Fact]
    public async Task Ignored_shared_alias_excludes_the_file_by_default_and_explicit_opt_in_requires_selection()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();f.Db.SetMovieIgnored(f.Id,true);
        Assert.Empty((await f.Preview()).Items);var included=Assert.Single((await f.Preview(ignored:true)).Items);Assert.True(included.RequiresExplicitSelection);
        Assert.Equal(1L,f.Scalar("SELECT ignored FROM movies LIMIT 1"));
    }
    [Fact]
    public async Task Manual_or_kept_choices_are_visible_but_require_explicit_selection()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();f.Db.AddThemeHistory(f.Id,"Item",2020,"Chosen","url","movie","manual",100);
        f.Scalar("UPDATE movies SET recheck_state='kept';");
        var item=Assert.Single((await f.Preview(items:[new("movie",f.Id)])).Items);Assert.True(item.RequiresExplicitSelection);
        Assert.Single(f.Db.GetThemeHistory());Assert.Equal("kept",f.Scalar("SELECT recheck_state FROM movies LIMIT 1"));
    }
    [Fact]
    public async Task Preview_rejects_invalid_scope_or_item_ids_and_excludes_outside_roots()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Preview("../../elsewhere"));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Preview(items:[new("movie","../../elsewhere")]));
        f.Db.SetLibraryPaths([f.Dir.File("OtherRoot")]);var preview=await f.Preview();Assert.Empty(preview.Items);Assert.NotEmpty(preview.Exclusions);
    }
    [Fact]
    public async Task Preview_excludes_symbolic_links_and_ambiguous_multiple_themes()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();File.WriteAllBytes(Path.Combine(f.Folder,"theme.m4a"),RepairTestFixture.Original);
        Assert.Empty((await f.Preview()).Items);File.Delete(Path.Combine(f.Folder,"theme.m4a"));File.Delete(Path.Combine(f.Folder,"theme.mp3"));
        File.WriteAllBytes(f.Dir.File("outside.mp3"),RepairTestFixture.Original);File.CreateSymbolicLink(Path.Combine(f.Folder,"theme.mp3"),f.Dir.File("outside.mp3"));
        Assert.Empty((await f.Preview()).Items);
    }
    [Fact]
    public async Task Expired_or_replaced_or_options_changed_previews_cannot_start()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();var old=await f.Preview();var current=await f.Preview();
        Assert.False(f.Repair.StartApply(old.PreviewId,[old.Items[0].Id]).Started);
        f.Time.Now=f.Time.Now.AddMinutes(16);Assert.False(f.Repair.StartApply(current.PreviewId,[current.Items[0].Id]).Started);
        var fresh=await f.Preview();f.Db.SetAudioProcessingOptions(new(false,60));Assert.False(f.Repair.StartApply(fresh.PreviewId,[fresh.Items[0].Id]).Started);
        Assert.Equal(0,f.Audio.Calls);Assert.Empty(Directory.GetFiles(f.Folder,".themearr-audio-backup-*"));
    }
}
