using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowLibrarySettingsTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    // There is no shared IApiKeyStore double in the test project (ApiAuthMiddlewareTests
    // and ApiKeyEndpointTests each define their own), so this file gets a local one.
    private sealed class StubKeyStore : IApiKeyStore
    {
        public string Current => "test-key";
        public string Regenerate() => "test-key";
    }

    private static SettingsPayload Payload() => new()
    {
        SelectedServers   = [],
        SelectedLibraries = [],
        PathMappings      = [],
        LibraryPaths      = [],
        Advanced          = new Dictionary<string, int>(),
    };

    [Fact]
    public void Get_returns_the_stored_show_libraries()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var result = Assert.IsType<OkObjectResult>(
            TestControllers.NewSettingsController(db, new StubKeyStore()).Get());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal("3", body.GetProperty("selectedShowLibraries")
                              .GetProperty("srv1")[0].GetString());
    }

    [Fact]
    public void Save_writes_show_libraries_when_supplied()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var req = Payload();
        req.SelectedShowLibraries = new() { ["srv1"] = ["7"] };

        TestControllers.NewSettingsController(db, new StubKeyStore()).Save(req);

        Assert.Equal(["7"], db.GetSelectedShowLibraries()["srv1"]);
    }

    /// <summary>
    /// Save() writes the movie library selection unconditionally, so a payload that omits
    /// the show field must NOT be treated as "select nothing" — an older cached frontend
    /// bundle after an upgrade would otherwise silently wipe the operator's show libraries,
    /// looking like Themearr forgetting its own settings.
    /// </summary>
    [Fact]
    public void Save_that_omits_show_libraries_leaves_the_stored_selection_intact()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var req = Payload();               // SelectedShowLibraries deliberately left null
        TestControllers.NewSettingsController(db, new StubKeyStore()).Save(req);

        Assert.Equal(["3"], db.GetSelectedShowLibraries()["srv1"]);
    }

    /// <summary>An explicit empty dictionary IS a real "deselect everything" instruction.</summary>
    [Fact]
    public void Save_with_an_explicit_empty_map_clears_the_selection()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var req = Payload();
        req.SelectedShowLibraries = [];
        TestControllers.NewSettingsController(db, new StubKeyStore()).Save(req);

        Assert.Empty(db.GetSelectedShowLibraries());
    }
}
