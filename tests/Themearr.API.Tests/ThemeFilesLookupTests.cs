using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeFilesLookupTests
{
    [Fact]
    public void FindThemeFile_skips_partial_downloads()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "theme.mp3.part"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "theme.ytdl"), "x");
        Assert.Null(ThemeFiles.FindThemeFile(dir.Path));

        File.WriteAllBytes(Path.Combine(dir.Path, "theme.mp3"), [0x49, 0x44, 0x33]);
        Assert.Equal("theme.mp3", Path.GetFileName(ThemeFiles.FindThemeFile(dir.Path)));
    }

    [Theory]
    [InlineData("theme.mp3",  "audio/mpeg")]
    [InlineData("theme.m4a",  "audio/mp4")]
    [InlineData("theme.ogg",  "audio/ogg")]
    [InlineData("theme.opus", "audio/opus")]
    [InlineData("theme.webm", "audio/webm")]
    [InlineData("theme.flac", "audio/flac")]
    [InlineData("theme.wav",  "audio/mpeg")]   // unknown → safe default
    public void ContentTypeFor_maps_known_extensions(string name, string expected) =>
        Assert.Equal(expected, ThemeFiles.ContentTypeFor(name));

    [Fact]
    public void DeleteThemes_removes_themes_but_leaves_partials_and_other_files()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "theme.mp3"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "theme.m4a"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "theme.mp3.part"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "movie.mkv"), "x");

        Assert.True(ThemeFiles.DeleteThemes(dir.Path));

        Assert.False(File.Exists(Path.Combine(dir.Path, "theme.mp3")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "theme.m4a")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "theme.mp3.part")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "movie.mkv")));

        Assert.False(ThemeFiles.DeleteThemes(dir.Path));   // nothing left to delete
    }
}
