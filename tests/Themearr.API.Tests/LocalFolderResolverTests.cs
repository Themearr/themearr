using System.Globalization;
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
    public void The_longest_matching_suffix_wins_over_a_shorter_one()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var topLevelDir = Path.Combine(dir.Path, "Heat (1995)");
        var nestedDir = Path.Combine(dir.Path, "Movies", "Heat (1995)");
        Directory.CreateDirectory(topLevelDir);
        Directory.CreateDirectory(nestedDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/Movies/Heat (1995)/heat.mkv");

        Assert.Equal(nestedDir, folder);
        Assert.Equal("suffix", mode);
    }

    [Fact]
    public void A_deeply_nested_folder_is_found_by_the_directory_scan()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "a", "b", "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/Heat (1995)/heat.mkv");

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

    [Fact]
    public void The_depth_limit_is_measured_the_same_whether_the_library_path_has_a_trailing_slash()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        // A movie two directory levels below the library root.
        var movieDir = Path.Combine(dir.Path, "sub", "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        // Depth budget of 1: a folder two levels down must NOT be reachable by the scan.
        db.SetSetting("search_depth", "1");
        // Stored WITH a trailing slash, exactly as a user might type the path.
        db.SetLibraryPaths([dir.Path + Path.DirectorySeparatorChar]);

        // A source path whose suffix can't locate the folder (it's under 'sub'),
        // forcing the depth-limited directory scan.
        var (folder, mode) = resolver.Resolve("/plex/Heat (1995)/heat.mkv");

        // The folder is genuinely 2 levels deep, past the depth-1 budget, so it
        // must be unresolved regardless of the trailing slash. The bug lets the
        // slash swallow a separator so it counts as depth 1 and wrongly matches.
        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }

    [Fact]
    public void Folder_name_matching_is_case_insensitive_even_under_a_non_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        // Turkish lowercases ASCII 'I' to dotless 'ı', so a culture-sensitive
        // ToLower() would fail to match a lowercase source segment.
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            using var dir = new TempDir();
            var (resolver, db) = New(dir);
            var movieDir = Path.Combine(dir.Path, "sub", "TITANIC (1997)");
            Directory.CreateDirectory(movieDir);
            db.SetLibraryPaths([dir.Path]);

            // Under 'sub' so suffix can't find it -> the name-matching scan runs.
            var (folder, mode) = resolver.Resolve("/plex/titanic (1997)/movie.mkv");

            Assert.Equal(movieDir, folder);
            Assert.Equal("suffix", mode);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
