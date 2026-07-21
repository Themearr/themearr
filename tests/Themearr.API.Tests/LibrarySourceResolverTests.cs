using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class LibrarySourceResolverTests
{
    private sealed class FakeSource(string name) : ILibrarySource
    {
        public string   Name         => name;
        public TimeSpan SyncInterval => TimeSpan.FromHours(24);

        public Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MovieRecord>>([]);

        public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
            Task.FromResult<Stream?>(null);

        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public string? SyncBlockedReason => null;
    }

    private static LibrarySourceResolver New(TempDir dir, string? configured, out Database db)
    {
        db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        if (configured is not null) db.SetSetting("library_source", configured);
        return new LibrarySourceResolver(db, [new FakeSource("plex"), new FakeSource("radarr")]);
    }

    [Fact]
    public void Defaults_to_plex_when_nothing_is_configured()
    {
        using var dir = new TempDir();
        var resolver = New(dir, null, out _);

        Assert.Equal("plex", resolver.Active.Name);
    }

    [Fact]
    public void Uses_the_configured_source()
    {
        using var dir = new TempDir();
        var resolver = New(dir, "radarr", out _);

        Assert.Equal("radarr", resolver.Active.Name);
    }

    [Fact]
    public void An_unknown_configured_source_falls_back_to_plex_rather_than_throwing()
    {
        using var dir = new TempDir();
        var resolver = New(dir, "jellyfin", out _);

        Assert.Equal("plex", resolver.Active.Name);
    }

    [Fact]
    public void The_setting_is_read_each_time_so_a_change_takes_effect_without_a_restart()
    {
        using var dir = new TempDir();
        var resolver = New(dir, "plex", out var db);

        db.SetSetting("library_source", "radarr");

        Assert.Equal("radarr", resolver.Active.Name);
    }
}
