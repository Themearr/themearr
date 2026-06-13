using System.Text;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeFilesAtomicWriteTests
{
    private const long Max = 100L * 1024 * 1024;

    /// <summary>A stream that yields some bytes then throws — models a CDN drop mid-download.</summary>
    private sealed class ThrowingStream(byte[] head) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < head.Length)
            {
                var n = Math.Min(count, head.Length - _pos);
                Array.Copy(head, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            throw new IOException("connection reset");
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task WriteAtomicAsync_happyPath_writesContent_andLeavesNoPartFile()
    {
        using var dir = new TempDir();
        var final = dir.File("theme.mp3");
        var payload = Encoding.UTF8.GetBytes("ID3 fake mp3 bytes");

        var written = await ThemeFiles.WriteAtomicAsync(new MemoryStream(payload), final, Max, default);

        Assert.Equal(payload.Length, written);
        Assert.Equal(payload, await File.ReadAllBytesAsync(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task WriteAtomicAsync_overwritesExistingThemeOnSuccess()
    {
        using var dir = new TempDir();
        var final = dir.File("theme.mp3");
        await File.WriteAllTextAsync(final, "OLD THEME");

        await ThemeFiles.WriteAtomicAsync(new MemoryStream(Encoding.UTF8.GetBytes("NEW THEME")), final, Max, default);

        Assert.Equal("NEW THEME", await File.ReadAllTextAsync(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task WriteAtomicAsync_sourceThrowsMidStream_preservesExistingTheme_andCleansUpPart()
    {
        using var dir = new TempDir();
        var final = dir.File("theme.mp3");
        await File.WriteAllTextAsync(final, "OLD THEME");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            ThemeFiles.WriteAtomicAsync(new ThrowingStream(Encoding.UTF8.GetBytes("partial")), final, Max, default));

        // The previous good theme must survive a failed re-download.
        Assert.Equal("OLD THEME", await File.ReadAllTextAsync(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task WriteAtomicAsync_emptyBody_throws_andDoesNotCreateTheme()
    {
        using var dir = new TempDir();
        var final = dir.File("theme.mp3");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ThemeFiles.WriteAtomicAsync(new MemoryStream(Array.Empty<byte>()), final, Max, default));

        // A zero-byte download must never land as theme.mp3.
        Assert.False(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task WriteAtomicAsync_emptyBody_preservesExistingTheme()
    {
        using var dir = new TempDir();
        var final = dir.File("theme.mp3");
        await File.WriteAllTextAsync(final, "OLD THEME");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ThemeFiles.WriteAtomicAsync(new MemoryStream(Array.Empty<byte>()), final, Max, default));

        Assert.Equal("OLD THEME", await File.ReadAllTextAsync(final));
        Assert.False(File.Exists(final + ".part"));
    }

    [Fact]
    public async Task WriteAtomicAsync_exceedsSizeLimit_throws_andCleansUp()
    {
        using var dir = new TempDir();
        var final = dir.File("theme.mp3");
        var tooBig = new byte[1024];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThemeFiles.WriteAtomicAsync(new MemoryStream(tooBig), final, maxBytes: 100, default));

        Assert.False(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));
    }
}
