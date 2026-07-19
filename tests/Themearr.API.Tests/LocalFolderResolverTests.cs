using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class LocalFolderResolverTests
{
    private static (LocalFolderResolver Resolver, Database Db) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return (new LocalFolderResolver(db), db);
    }

    [Fact]
    public void A_path_that_exists_resolves_directly()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);

        var (folder, mode) = resolver.Resolve(Path.Combine(movieDir, "heat.mkv"));

        Assert.Equal(movieDir, folder);
        Assert.Equal("direct", mode);
    }

    [Fact]
    public void A_configured_mapping_is_applied_when_the_reported_path_does_not_exist()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = "/mnt/plex/Movies",
            ["target"] = dir.Path,
        }]);

        var (folder, mode) = resolver.Resolve("/mnt/plex/Movies/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void A_windows_style_path_is_mapped_despite_backslashes()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = @"P:\Movies",
            ["target"] = dir.Path,
        }]);

        var (folder, mode) = resolver.Resolve(@"P:\Movies\Heat (1995)\heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void With_no_mapping_the_folder_is_found_by_suffix_under_a_library_path()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/else/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("suffix", mode);
    }

    [Fact]
    public void An_unknown_path_with_nothing_configured_is_unresolved()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);

        var (folder, mode) = resolver.Resolve("/mnt/nowhere/Heat (1995)/heat.mkv");

        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }

    [Fact]
    public void An_empty_path_is_unresolved_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);

        var (folder, mode) = resolver.Resolve("");

        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }
}
