using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// /setup is reachable at any time by an already-configured user, so saving the Plex
/// wizard must not destroy configuration the wizard never collected. Path mappings are
/// the case that bites: they are configured in Settings, the wizard has no editor for
/// them, and losing them breaks LocalFolderResolver's mapping strategy — after which a
/// Docker install whose Plex paths differ from Themearr's silently stops resolving movie
/// folders, and so stops being able to write theme.mp3.
///
/// The Radarr branch of the same wizard already round-trips the settings it does not own;
/// these tests hold the Plex branch to the same rule.
/// </summary>
public class SetupPreservesPathMappingsTests
{
    private static (SetupController Controller, Database Db) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var plex = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        return (new SetupController(db, plex), db);
    }

    /// <summary>A configured install: one server, one library, and a path mapping.</summary>
    private static void SeedConfiguredInstall(Database db) =>
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = "/media/movies",
            ["target"] = "/mnt/movies",
        }]);

    private static PlexSelectionRequest ValidRequest() => new()
    {
        Servers           = [new Dictionary<string, object?> { ["id"] = "s1", ["url"] = "http://plex:32400" }],
        SelectedLibraries = new Dictionary<string, List<string>> { ["s1"] = ["1"] },
        LibraryPaths      = ["/mnt/movies"],
        // PathMappings deliberately left unset — the wizard has no editor for them,
        // so an omitted field means "not mine to change", not "delete them".
    };

    [Fact]
    public void Re_running_the_wizard_keeps_path_mappings_it_never_asked_about()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);
        SeedConfiguredInstall(db);

        Assert.IsType<OkObjectResult>(controller.SaveSelection(ValidRequest()));

        var mappings = db.GetPathMappings();
        var mapping  = Assert.Single(mappings);
        Assert.Equal("/media/movies", mapping["source"]);
        Assert.Equal("/mnt/movies",   mapping["target"]);
    }

    /// <summary>
    /// The wizard still owns what it does collect, so those writes must land — otherwise
    /// the fix for the wipe would quietly break setup itself.
    /// </summary>
    [Fact]
    public void The_wizard_still_writes_the_settings_it_does_collect()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);
        SeedConfiguredInstall(db);

        Assert.IsType<OkObjectResult>(controller.SaveSelection(ValidRequest()));

        Assert.Equal(["/mnt/movies"], db.GetLibraryPaths());
        Assert.Equal(["1"], db.GetSelectedLibraries()["s1"]);
        Assert.True(db.IsSetupComplete());
    }

    /// <summary>
    /// A caller that genuinely sends mappings still sets them. Omission and an explicit
    /// value have to mean different things, or the endpoint could never clear them.
    /// </summary>
    [Fact]
    public void Explicitly_supplied_mappings_are_still_written()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);
        SeedConfiguredInstall(db);

        var req = ValidRequest();
        req.PathMappings = [new Dictionary<string, string>
        {
            ["source"] = "/data/films",
            ["target"] = "/mnt/films",
        }];

        Assert.IsType<OkObjectResult>(controller.SaveSelection(req));

        var mapping = Assert.Single(db.GetPathMappings());
        Assert.Equal("/data/films", mapping["source"]);
        Assert.Equal("/mnt/films",  mapping["target"]);
    }

    /// <summary>
    /// An explicit empty list is still a clear — Settings needs to be able to remove the
    /// last mapping. Only an absent field is treated as "leave alone".
    /// </summary>
    [Fact]
    public void An_explicit_empty_list_still_clears_them()
    {
        using var dir = new TempDir();
        var (controller, db) = New(dir);
        SeedConfiguredInstall(db);

        var req = ValidRequest();
        req.PathMappings = [];

        Assert.IsType<OkObjectResult>(controller.SaveSelection(req));

        Assert.Empty(db.GetPathMappings());
    }
}
