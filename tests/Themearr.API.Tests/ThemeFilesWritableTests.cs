using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeFilesWritableTests
{
    [Fact]
    public void IsDirectoryWritable_writableFolder_true()
    {
        using var dir = new TempDir();
        Assert.True(ThemeFiles.IsDirectoryWritable(dir.Path));
    }

    [Fact]
    public void IsDirectoryWritable_missingFolder_false()
    {
        Assert.False(ThemeFiles.IsDirectoryWritable("/no/such/themearr/folder/here"));
    }

    [Fact]
    public void IsDirectoryWritable_leavesNoProbeFileBehind()
    {
        using var dir = new TempDir();
        Assert.True(ThemeFiles.IsDirectoryWritable(dir.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void IsDirectoryWritable_readOnlyFolder_false()
    {
        if (OperatingSystem.IsWindows()) return; // chmod-based perms are POSIX-only

        using var dir = new TempDir();
        // Strip all write bits (r-xr-xr-x) to simulate a media bind-mount the
        // service user can't write to — the #1 Proxmox download failure.
        System.Diagnostics.Process.Start("chmod", $"555 {dir.Path}")!.WaitForExit();
        try
        {
            Assert.False(ThemeFiles.IsDirectoryWritable(dir.Path));
        }
        finally
        {
            System.Diagnostics.Process.Start("chmod", $"755 {dir.Path}")!.WaitForExit();
        }
    }
}
