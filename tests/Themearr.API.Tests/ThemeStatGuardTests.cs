using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins <see cref="ThemeFiles.ThemeStat"/>, the shared stat behind the status derivation
/// and both theme-audio endpoints (#48 concurrency review, F4). A theme can vanish
/// between enumeration and stat — container-correct naming replaces across theme names
/// on every re-download whose container changed, so the gap is hit by ordinary polling —
/// and before this primitive existed the stat's FileNotFoundException 500'd a whole
/// library listing over one mid-replacement folder.
///
/// The vanish is pinned at this seam, not through the readers: the enumerate-then-stat
/// gap cannot be interleaved deterministically from outside (a dangling symlink does not
/// model it — .NET on Unix falls back to lstat and reports the link's own length), and a
/// timing-based test would be exactly the flake this suite bans. The readers consume the
/// primitive in a line apiece, which review covers.
/// </summary>
public class ThemeStatGuardTests
{
    [Fact]
    public void ThemeStat_vanishedFile_isNull_notAnException()
    {
        using var dir = new TempDir();

        // Never created: from the reader's point of view this IS the vanished file —
        // enumeration promised a path and the stat finds nothing there.
        Assert.Null(ThemeFiles.ThemeStat(dir.File("theme.mp3")));
    }

    [Fact]
    public void ThemeStat_presentFile_reportsLengthAndLastWrite()
    {
        using var dir = new TempDir();
        dir.Write("theme.m4a", new byte[] { 1, 2, 3, 4, 5 });

        var stat = ThemeFiles.ThemeStat(dir.File("theme.m4a"));

        Assert.NotNull(stat);
        Assert.Equal(5, stat.Value.Length);
        // Sanity, not precision: a real mtime, not the sentinel a failed stat reports.
        Assert.True(stat.Value.LastWriteTimeUtc > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void HasUsableTheme_consumesTheGuardedStat_zeroByteThemeStillUnusable()
    {
        // The pre-existing 0-byte rule must survive the ThemeStat refactor: a truncated
        // theme is not usable, and a vanished one now reports the same way (null → 0).
        using var dir = new TempDir();
        dir.Write("theme.mp3", Array.Empty<byte>());

        Assert.False(ThemeFiles.HasUsableTheme(dir.Path));
    }
}
