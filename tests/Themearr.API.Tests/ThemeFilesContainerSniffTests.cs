using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins container sniffing for theme naming (issue #48). The extension is decided by the
/// file's own leading bytes, never by a CDN Content-Type header — the confirmed
/// production failure is precisely a promise ("mp3" converter API) contradicted by the
/// delivered bytes (<c>format_name=mov,mp4,m4a</c> probed inside a theme.mp3), so the
/// header is the one witness known to lie. The result is a closed two-value set
/// {.mp3, .m4a}: a filename must never be derived from remote data.
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
        Assert.Equal(".mp3", ThemeFiles.SniffedThemeExtension(new byte[] { 0x49, 0x44, 0x33, 9, 9, 9, 9, 9 }));
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
    public void Normalize_mp3NamedFileWithMp4Bytes_isRenamedToM4a()
    {
        using var dir = new TempDir();
        var mp3Path = dir.File("theme.mp3");
        File.WriteAllBytes(mp3Path, Ftyp("M4A "));

        var final = ThemeFiles.NormalizeThemeExtension(mp3Path);

        Assert.Equal(dir.File("theme.m4a"), final);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(mp3Path));
        Assert.Equal(Ftyp("M4A "), File.ReadAllBytes(final));
    }

    [Fact]
    public void Normalize_mp3NamedFileWithMp3Bytes_isLeftAlone()
    {
        using var dir = new TempDir();
        var mp3Path = dir.File("theme.mp3");
        File.WriteAllBytes(mp3Path, new byte[] { 0x49, 0x44, 0x33, 9, 9, 9 });

        Assert.Equal(mp3Path, ThemeFiles.NormalizeThemeExtension(mp3Path));
        Assert.True(File.Exists(mp3Path));
    }

    [Fact]
    public void Normalize_staleM4aSibling_isOverwrittenByTheRename()
    {
        // A re-download that changes container must replace, not collide: the stale
        // theme.m4a from a previous run is overwritten by the corrected new file.
        using var dir = new TempDir();
        dir.Write("theme.m4a", new byte[] { 1, 2, 3 });
        var mp3Path = dir.File("theme.mp3");
        File.WriteAllBytes(mp3Path, Ftyp("isom"));

        var final = ThemeFiles.NormalizeThemeExtension(mp3Path);

        Assert.Equal(dir.File("theme.m4a"), final);
        Assert.Equal(Ftyp("isom"), File.ReadAllBytes(final));
        Assert.False(File.Exists(mp3Path));
    }

    [Fact]
    public void Normalize_m4aNamedFileWithMp3Bytes_isRenamedToMp3()
    {
        // Symmetric on purpose: the function corrects toward whatever the bytes say,
        // so a future caller holding a differently-named theme gets the same behavior.
        using var dir = new TempDir();
        var m4aPath = dir.File("theme.m4a");
        File.WriteAllBytes(m4aPath, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });

        var final = ThemeFiles.NormalizeThemeExtension(m4aPath);

        Assert.Equal(dir.File("theme.mp3"), final);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(m4aPath));
    }
}
