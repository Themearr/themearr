using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeFilesStatusTests
{
    [Fact]
    public void HasUsableTheme_emptyFolder_false()
    {
        using var dir = new TempDir();
        Assert.False(ThemeFiles.HasUsableTheme(dir.Path));
    }

    [Fact]
    public void HasUsableTheme_nonEmptyThemeMp3_true()
    {
        using var dir = new TempDir();
        dir.Write("theme.mp3", new byte[] { 0x49, 0x44, 0x33, 1, 2, 3 }); // a few bytes
        Assert.True(ThemeFiles.HasUsableTheme(dir.Path));
    }

    [Fact]
    public void HasUsableTheme_zeroByteThemeMp3_false()
    {
        // The core bug: a truncated/zero-byte theme.mp3 must NOT count as downloaded,
        // otherwise it's marked done forever and never retried.
        using var dir = new TempDir();
        dir.Write("theme.mp3", Array.Empty<byte>());
        Assert.False(ThemeFiles.HasUsableTheme(dir.Path));
    }

    [Fact]
    public void HasUsableTheme_onlyPartFile_false()
    {
        using var dir = new TempDir();
        dir.Write("theme.mp3.part", new byte[] { 1, 2, 3 });
        Assert.False(ThemeFiles.HasUsableTheme(dir.Path));
    }

    [Fact]
    public void HasUsableTheme_onlyYtdlFile_false()
    {
        using var dir = new TempDir();
        dir.Write("theme.ytdl", new byte[] { 1, 2, 3 });
        Assert.False(ThemeFiles.HasUsableTheme(dir.Path));
    }

    [Fact]
    public void HasUsableTheme_nonEmptyAlternateExtension_true()
    {
        using var dir = new TempDir();
        dir.Write("theme.m4a", new byte[] { 1, 2, 3 });
        Assert.True(ThemeFiles.HasUsableTheme(dir.Path));
    }

    [Fact]
    public void HasUsableTheme_missingFolder_false()
    {
        Assert.False(ThemeFiles.HasUsableTheme("/no/such/themearr/folder/here"));
    }
}
