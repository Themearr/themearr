using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins container sniffing and the part-promotion that lands a theme under its sniffed
/// name (issue #48). The extension is decided by the file's own leading bytes, never by
/// a CDN Content-Type header — the confirmed production failure is precisely a promise
/// ("mp3" converter API) contradicted by the delivered bytes
/// (<c>format_name=mov,mp4,m4a</c> probed inside a theme.mp3), so the header is the one
/// witness known to lie. The result is a closed two-value set {.mp3, .m4a}: a filename
/// must never be derived from remote data. Promotion is ONE atomic move from the
/// in-flight part to the final name — deciding the name and landing the file together is
/// what keeps a reader from ever observing a wrongly-named or half-renamed theme (#48
/// concurrency review).
/// </summary>
public class ThemeFilesContainerSniffTests
{
    // An MP4/M4A file starts with a box: 4-byte size, then "ftyp", then a brand.
    private static byte[] Ftyp(string brand)
    {
        var bytes = new List<byte> { 0x00, 0x00, 0x00, 0x18 };
        bytes.AddRange("ftyp"u8.ToArray());
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(brand));
        return bytes.ToArray();
    }

    private static readonly byte[] Id3 = [0x49, 0x44, 0x33, 9, 9, 9, 9, 9];

    [Fact]
    public void Sniff_ftypBox_isM4a_regardlessOfBrand()
    {
        // The brand varies in the wild (M4A , isom, mp42, dash) and none of it changes
        // what the container is — only the "ftyp" marker at offset 4 decides.
        Assert.Equal(".m4a", ThemeFiles.SniffedThemeExtension(Ftyp("M4A ")));
        Assert.Equal(".m4a", ThemeFiles.SniffedThemeExtension(Ftyp("isom")));
        Assert.Equal(".m4a", ThemeFiles.SniffedThemeExtension(Ftyp("dash")));
    }

    [Fact]
    public void Sniff_id3Tag_isMp3()
    {
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension(Id3));
    }

    [Fact]
    public void Sniff_mpegFrameSync_isMp3()
    {
        // Tagless MP3s open straight on a frame: 11 set bits (0xFF, then top 3 of the
        // next byte). Both common layer/version bytes are covered.
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension(new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0 }));
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension(new byte[] { 0xFF, 0xF3, 0x18, 0xC4, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Sniff_unknownContent_keepsTheHistoricalMp3Name()
    {
        // Unidentifiable bytes keep the pre-#48 name: possibly wrong, but exactly as
        // wrong as every download was before sniffing existed — no regression, and the
        // extension still comes from the fixed set.
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension("just some text"u8));
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension(Array.Empty<byte>()));
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension(new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void Sniff_ftypAtTheWrongOffset_isNotM4a()
    {
        // "ftyp" belongs at offset 4, after the box size. At offset 0 this is not an MP4
        // box — a text file that happens to start with the letters must not become .m4a.
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension("ftyp is a word here"u8));
    }

    [Fact]
    public void Promote_mp4Part_landsAsThemeM4a()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(ThemeFiles.ThemePartPath(dir.Path), Ftyp("M4A "));

        var final = ThemeFiles.PromoteThemePart(dir.Path);

        Assert.Equal(dir.File("theme.m4a"), final);
        Assert.Equal(Ftyp("M4A "), File.ReadAllBytes(final));
        Assert.False(File.Exists(ThemeFiles.ThemePartPath(dir.Path)));
        Assert.False(File.Exists(dir.File("theme.mp3")));
    }

    [Fact]
    public void Promote_mp3Part_landsAsThemeMp3()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(ThemeFiles.ThemePartPath(dir.Path), Id3);

        var final = ThemeFiles.PromoteThemePart(dir.Path);

        Assert.Equal(dir.File("theme.mp3"), final);
        Assert.Equal(Id3, File.ReadAllBytes(final));
        Assert.False(File.Exists(dir.File("theme.m4a")));
    }

    [Fact]
    public void Promote_unknownBytes_landAsThemeMp3()
    {
        using var dir = new TempDir();
        dir.Write("theme.mp3.part", "not any known audio container");

        Assert.Equal(dir.File("theme.mp3"), ThemeFiles.PromoteThemePart(dir.Path));
    }

    [Fact]
    public void Promote_staleSameNameTheme_isOverwrittenAtomically()
    {
        // A re-download that keeps its container must replace, not collide: the stale
        // theme.m4a from a previous run is overwritten by the single move.
        using var dir = new TempDir();
        dir.Write("theme.m4a", new byte[] { 1, 2, 3 });
        File.WriteAllBytes(ThemeFiles.ThemePartPath(dir.Path), Ftyp("isom"));

        var final = ThemeFiles.PromoteThemePart(dir.Path);

        Assert.Equal(dir.File("theme.m4a"), final);
        Assert.Equal(Ftyp("isom"), File.ReadAllBytes(final));
    }

    [Fact]
    public void Promote_otherNameSibling_isLeftForTheGatedCleanup()
    {
        // Division of labor, on purpose: promotion lands the new file; removing the
        // now-stale other-extension sibling is DownloadService's cleanup, which runs
        // under the same per-folder gate so the two can never interleave with another
        // landing. Promotion deleting siblings itself would duplicate that logic
        // outside the gate's owner.
        using var dir = new TempDir();
        dir.Write("theme.mp3", new byte[] { 0x49, 0x44, 0x33, 9 });
        File.WriteAllBytes(ThemeFiles.ThemePartPath(dir.Path), Ftyp("M4A "));

        var final = ThemeFiles.PromoteThemePart(dir.Path);

        Assert.Equal(dir.File("theme.m4a"), final);
        Assert.True(File.Exists(dir.File("theme.mp3")));   // still there — cleanup's job
    }

    [Fact]
    public void Promote_missingPart_throwsClearly_insteadOfSilentNoFileSuccess()
    {
        // A provider that reports success without delivering a file used to land as a
        // silent "downloaded" with no theme on disk. The promote step is where that
        // finally becomes a loud, actionable failure.
        using var dir = new TempDir();

        var ex = Assert.Throws<InvalidOperationException>(() => ThemeFiles.PromoteThemePart(dir.Path));
        Assert.Contains("no file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Promote_emptyPart_refusesAndRemovesThePart()
    {
        // Mirrors WriteAtomicAsync's 0-byte rule: an empty delivery must neither land
        // nor leave a partial behind for the next attempt to trip over.
        using var dir = new TempDir();
        dir.Write("theme.mp3.part", Array.Empty<byte>());

        Assert.Throws<InvalidOperationException>(() => ThemeFiles.PromoteThemePart(dir.Path));
        Assert.False(File.Exists(ThemeFiles.ThemePartPath(dir.Path)));
        Assert.False(File.Exists(dir.File("theme.mp3")));
    }
}
