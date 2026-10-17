using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class AuditReplacementOwnershipTests
{
    private const string Url = "https://www.youtube.com/watch?v=abcdefghijk";
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private sealed class Search : YoutubeService
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<List<Dictionary<string, object?>>> SearchAsync(string query, int maxResults = 8, string? title = null, int? year = null)
        { Entered.TrySetResult(); await Release.Task; return [new() { ["bestMatch"] = true, ["videoId"] = "bcdefghijkl", ["title"] = "Dune Theme", ["score"] = 80 }]; }
    }
    private sealed class Provider : IThemeAudioProvider
    {
        public int Calls;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task; File.WriteAllBytes(outputPath, [0x49, 0x44, 0x33, 8, 8, 8]); return "replacement theme"; }
    }
    private static (Database db, string id) Seed(TempDir dir, string mediaType)
    {
        var db = new Database(dir.File("test.db")); db.Init(); db.MarkSetupComplete(); db.SetSetting("auto_download", "true");
        var folder = dir.File("Dune"); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [1, 2, 3]);
        var id = MediaFolderId.For(folder);
        if (mediaType == "movie") { db.UpsertMovies([new(folder, "plex", id, "Dune", 2021, folder)]); db.SetMovieStatus(id, "downloaded"); }
        else { db.UpsertShows([new(folder, "plex", id, "Dune", 2021, folder, false)]); db.SetShowStatus(id, "downloaded"); }
        db.AddThemeHistory(id, "Dune", 2021, "old theme", Url, mediaType, "auto", 40); db.SetRecheck(id, mediaType, "queued", "old verdict");
        return (db, id);
    }
    private static DownloadService Download(Database db, Provider provider) => new(provider, db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
    private static MoviesController Movies(Database db, YoutubeService search, DownloadService download) => new(db, search, download, new PosterUrlSigner(new byte[32]), null!, NullLogger<MoviesController>.Instance);
    private static ShowsController Shows(Database db, YoutubeService search, DownloadService download) => new(db, search, download, new PosterUrlSigner(new byte[32]), NullLogger<ShowsController>.Instance);
    private static object? Prop(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
    private static async Task WaitIdle(DownloadService download, string id, string mediaType)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (Prop(download.GetStatus(id, mediaType), "inProgress") is true) await Task.Delay(10, timeout.Token); }
    [Theory]
    [InlineData("movie", "search", "keep")]
    [InlineData("show", "search", "keep")]
    [InlineData("movie", "provider", "keep")]
    [InlineData("show", "provider", "keep")]
    [InlineData("movie", "search", "ignore")]
    [InlineData("show", "search", "ignore")]
    [InlineData("movie", "provider", "ignore")]
    [InlineData("show", "provider", "ignore")]
    [InlineData("movie", "search", "delete")]
    [InlineData("show", "search", "delete")]
    [InlineData("movie", "provider", "delete")]
    [InlineData("show", "provider", "delete")]
    [InlineData("movie", "search", "history")]
    [InlineData("show", "search", "history")]
    [InlineData("movie", "provider", "history")]
    [InlineData("show", "provider", "history")]
    public async Task Worker_replacement_preserves_newer_decisions_during_search_and_provider(string mediaType, string phase, string action)
    {
        using var dir = new TempDir(); var (db, id) = Seed(dir, mediaType); var search = new Search(); var provider = new Provider(); var download = Download(db, provider);
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton<YoutubeService>(search).BuildServiceProvider();
        Task tick;
        if (mediaType == "movie")
        {
            var worker = new AutoDownloadService(services, download, provider, NullLogger<AutoDownloadService>.Instance);
            tick = (Task)typeof(AutoDownloadService).GetMethod("TryAutoDownloadOne", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, null)!;
        }
        else tick = new ShowAutoDownloadService(services, download, provider, NullLogger<ShowAutoDownloadService>.Instance).TryDownloadOnceAsync(CancellationToken.None);
        try
        {
            await search.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (phase == "provider") { search.Release.TrySetResult(); await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            if (action == "keep") { if (mediaType == "movie") Movies(db, search, download).RecheckKeep(id); else Shows(db, search, download).RecheckKeep(id); }
            if (action == "ignore") { if (mediaType == "movie") Movies(db, search, download).IgnoreMovie(id); else Shows(db, search, download).IgnoreShow(id); }
            if (action == "delete") { if (mediaType == "movie") Movies(db, search, download).DeleteTheme(id); else Shows(db, search, download).DeleteTheme(id); }
            if (action == "history") db.AddThemeHistory(id, "Dune", 2021, "newer theme", Url, mediaType, "manual", 70);
        }
        finally { search.Release.TrySetResult(); await tick; provider.Release.TrySetResult(); await WaitIdle(download, id, mediaType); }
        Assert.Equal(action == "history" ? 2 : 1, db.GetThemeHistory().Count);
        if (action == "delete") Assert.False(File.Exists(dir.File("Dune/theme.mp3")));
        else Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dir.File("Dune/theme.mp3")));
        if (action == "keep") Assert.Equal("kept", db.GetRecheckCandidates(mediaType).Single()["recheckState"]);
        if (phase == "search") Assert.Equal(0, provider.Calls);
        Assert.False(File.Exists(ThemeFiles.ThemePartPath(dir.File("Dune"))));
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Successful_recheck_replacement_still_publishes_files_history_and_resolution(string mediaType)
    {
        using var dir = new TempDir(); var (db, id) = Seed(dir, mediaType); var search = new Search(); search.Release.TrySetResult(); var provider = new Provider(); provider.Release.TrySetResult(); var download = Download(db, provider);
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton<YoutubeService>(search).BuildServiceProvider();
        if (mediaType == "movie")
        {
            var worker = new AutoDownloadService(services, download, provider, NullLogger<AutoDownloadService>.Instance);
            await (Task)typeof(AutoDownloadService).GetMethod("TryAutoDownloadOne", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, null)!;
        }
        else await new ShowAutoDownloadService(services, download, provider, NullLogger<ShowAutoDownloadService>.Instance).TryDownloadOnceAsync(CancellationToken.None);
        await WaitIdle(download, id, mediaType);
        Assert.Equal(2, db.GetThemeHistory().Count); Assert.Equal("replacement theme", db.GetLatestThemeProvenance(id, mediaType)!.ThemeTitle);
        Assert.Equal(new byte[] { 0x49, 0x44, 0x33, 8, 8, 8 }, File.ReadAllBytes(dir.File("Dune/theme.mp3"))); Assert.Empty(db.GetFlaggedItems()); Assert.Null(Prop(download.GetStatus(id, mediaType), "error"));
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Explicit_download_still_replaces_a_kept_theme(string mediaType)
    {
        using var dir = new TempDir(); var (db, id) = Seed(dir, mediaType); db.SetRecheck(id, mediaType, "kept", "manual choice");
        var provider = new Provider(); provider.Release.TrySetResult(); var download = Download(db, provider);
        Assert.True(download.Start(id, Url, mediaType, "manual")); await WaitIdle(download, id, mediaType);
        Assert.Equal(2, db.GetThemeHistory().Count); Assert.Equal("manual", db.GetLatestThemeProvenance(id, mediaType)!.PickedBy); Assert.Null(db.GetRecheckCandidates(mediaType).Single()["recheckState"]);
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Keep_cannot_interleave_between_validation_and_file_history_publication(string mediaType)
    {
        using var dir = new TempDir(); var (db, id) = Seed(dir, mediaType);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var commit = Task.Run(() => db.TryCommitRecheckReplacement(id, mediaType, db.GetLatestThemeProvenance(id, mediaType)!.HistoryId,
            "Dune", 2021, "replacement theme", Url, "auto", 80, () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); File.WriteAllBytes(dir.File("Dune/theme.mp3"), [8, 8, 8]); }));
        Task? keep = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var keepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            keep = Task.Run(() => { keepEntered.TrySetResult(); db.SetRecheck(id, mediaType, "kept", "user decision"); });
            await keepEntered.Task; await Task.Delay(150); Assert.False(keep.IsCompleted);
        }
        finally { release.Set(); Assert.True(await commit); if (keep != null) await keep; }
        Assert.Equal(2, db.GetThemeHistory().Count); Assert.Equal("kept", db.GetRecheckCandidates(mediaType).Single()["recheckState"]);
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Delete_reserves_the_writer_before_its_filesystem_mutation(string mediaType)
    {
        using var dir = new TempDir(); var (db, id) = Seed(dir, mediaType); var search = new Search(); var provider = new Provider(); var download = Download(db, provider);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var commit = Task.Run(() => db.TryCommitRecheckReplacement(id, mediaType, db.GetLatestThemeProvenance(id, mediaType)!.HistoryId,
            "Dune", 2021, "replacement theme", Url, "auto", 80, () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); File.WriteAllBytes(dir.File("Dune/theme.mp3"), [8, 8, 8]); }));
        Task<IActionResult>? deletion = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            deletion = Task.Run(() => { deleteEntered.TrySetResult(); return mediaType == "movie" ? Movies(db, search, download).DeleteTheme(id) : Shows(db, search, download).DeleteTheme(id); });
            await deleteEntered.Task; await Task.Delay(150); Assert.False(deletion.IsCompleted); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dir.File("Dune/theme.mp3")));
        }
        finally { release.Set(); Assert.True(await commit); if (deletion != null) await deletion; }
        Assert.False(File.Exists(dir.File("Dune/theme.mp3"))); Assert.Empty(db.GetRecheckCandidates(mediaType));
    }
    [Theory]
    [InlineData("movie", "download")]
    [InlineData("show", "download")]
    [InlineData("movie", "auto")]
    [InlineData("show", "auto")]
    [InlineData("movie", "url")]
    [InlineData("show", "url")]
    public async Task Controllers_conflict_when_a_different_download_already_owns_the_item(string mediaType, string route)
    {
        using var dir = new TempDir(); var (db, id) = Seed(dir, mediaType); var provider = new Provider(); var search = new Search(); search.Release.TrySetResult(); var download = Download(db, provider);
        Assert.True(download.Start(id, Url, mediaType, "manual")); await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            IActionResult response;
            if (mediaType == "movie") { var controller = Movies(db, search, download); response = route switch { "download" => controller.Download(new(id, "bcdefghijkl")), "url" => controller.DownloadUrl(new(id, "https://8.8.8.8/theme.mp3")), _ => await controller.AutoDownload(id) }; }
            else { var controller = Shows(db, search, download); response = route switch { "download" => controller.Download(id, new("bcdefghijkl")), "url" => controller.DownloadUrl(id, new("https://8.8.8.8/theme.mp3")), _ => await controller.AutoDownload(id) }; }
            var conflict = Assert.IsType<ConflictObjectResult>(response); Assert.Contains("already", Prop(conflict.Value!, "detail")?.ToString());
        }
        finally { provider.Release.TrySetResult(); await WaitIdle(download, id, mediaType); }
    }
}
