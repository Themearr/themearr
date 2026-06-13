using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeFilesContainmentTests
{
    [Fact]
    public void IsWithinRoots_folderUnderRoot_true()
    {
        Assert.True(ThemeFiles.IsWithinRoots("/media/movies/The Matrix (1999)", ["/media/movies"]));
    }

    [Fact]
    public void IsWithinRoots_folderEqualsRoot_true()
    {
        Assert.True(ThemeFiles.IsWithinRoots("/media/movies", ["/media/movies"]));
    }

    [Fact]
    public void IsWithinRoots_folderOutsideRoot_false()
    {
        Assert.False(ThemeFiles.IsWithinRoots("/opt/themearr", ["/media/movies"]));
    }

    [Fact]
    public void IsWithinRoots_siblingPrefixTrap_false()
    {
        // "/media/movies-secret" must NOT be considered inside "/media/movies".
        Assert.False(ThemeFiles.IsWithinRoots("/media/movies-secret/x", ["/media/movies"]));
    }

    [Fact]
    public void IsWithinRoots_dotDotEscape_false()
    {
        Assert.False(ThemeFiles.IsWithinRoots("/media/movies/../../opt/themearr", ["/media/movies"]));
    }

    [Fact]
    public void IsWithinRoots_multipleRoots_matchesSecond_true()
    {
        Assert.True(ThemeFiles.IsWithinRoots("/tank/films/Heat (1995)", ["/media/movies", "/tank/films"]));
    }

    [Fact]
    public void IsWithinRoots_noRoots_false()
    {
        Assert.False(ThemeFiles.IsWithinRoots("/media/movies/x", Array.Empty<string>()));
    }
}
