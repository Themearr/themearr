using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexPathTests
{
    [Theory]
    [InlineData(@"M:\Movies\Red One (2024)\Red One (2024).mkv", "M:/Movies/Red One (2024)")] // Windows Plex path on Linux
    [InlineData("/data/movies/Red One (2024)/file.mkv", "/data/movies/Red One (2024)")]        // Linux path unchanged
    [InlineData(@"\\NAS\Movies\Heat (1995)\heat.mkv", "//NAS/Movies/Heat (1995)")]              // UNC share
    [InlineData("file-with-no-dir.mkv", "")]
    public void ParentDir_handlesWindowsAndLinuxSeparators(string filePath, string expected)
    {
        Assert.Equal(expected, PlexPath.ParentDir(filePath));
    }

    [Fact]
    public void ApplyMapping_translatesWindowsSourceToLinuxTarget()
    {
        // The core bug: a Windows Plex parent path must map onto the container mount.
        var result = PlexPath.ApplyMapping("M:/Movies/Red One (2024)", @"M:\Movies", "/movies");
        Assert.Equal("/movies/Red One (2024)", result);
    }

    [Fact]
    public void ApplyMapping_exactMatch_returnsTarget()
    {
        Assert.Equal("/movies", PlexPath.ApplyMapping("M:/Movies", @"M:\Movies", "/movies"));
    }

    [Fact]
    public void ApplyMapping_windowsCaseInsensitive()
    {
        // Windows paths are case-insensitive; a drive-letter/case mismatch shouldn't break mapping.
        Assert.Equal("/movies/Heat (1995)", PlexPath.ApplyMapping(@"m:\movies\Heat (1995)", @"M:\Movies", "/movies"));
    }

    [Fact]
    public void ApplyMapping_linuxPassthrough()
    {
        Assert.Equal("/movies/X", PlexPath.ApplyMapping("/data/movies/X", "/data/movies", "/movies"));
    }

    [Fact]
    public void ApplyMapping_noMatch_returnsEmpty()
    {
        Assert.Equal("", PlexPath.ApplyMapping("D:/Other/X", @"M:\Movies", "/movies"));
        // must not partial-match a sibling prefix ("M:\MoviesHD" is not under "M:\Movies")
        Assert.Equal("", PlexPath.ApplyMapping("M:/MoviesHD/X", @"M:\Movies", "/movies"));
    }

    [Fact]
    public void Segments_splitsOnBothSeparators()
    {
        Assert.Equal(new[] { "M:", "Movies", "Red One (2024)" }, PlexPath.Segments(@"M:\Movies\Red One (2024)"));
        Assert.Equal(new[] { "data", "movies", "X" }, PlexPath.Segments("/data/movies/X/"));
    }
}
