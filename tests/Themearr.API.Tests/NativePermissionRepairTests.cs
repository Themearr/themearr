using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class NativePermissionRepairTests
{
    [Fact]
    public async Task Legacy_writable_install_requests_marker_only_repair_and_preserves_data()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "data"));
        var database = Path.Combine(dir.Path, "data", "themearr.db");
        await File.WriteAllTextAsync(database, "live database");
        var helper = Path.Combine(dir.Path, "old-helper");
        // Historical helpers ignore arguments; the marker is the compatible signal.
        await File.WriteAllTextAsync(helper, $"#!/bin/sh\ntest -f '{dir.Path}/data/.repair-permissions' || exit 21\nrm '{dir.Path}/data/.repair-permissions'\necho repaired > '{dir.Path}/repaired'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var service = new NativePermissionRepairService(NullLogger<NativePermissionRepairService>.Instance);

        Assert.True(await service.RepairIfNeededAsync(dir.Path, helper, useSudo: false, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(dir.Path, "repaired")));
        Assert.Equal("live database", await File.ReadAllTextAsync(database));
        Assert.False(File.Exists(Path.Combine(dir.Path, "data", ".repair-permissions")));
    }

    [Fact]
    public async Task Existing_marker_symlink_cannot_truncate_live_database()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "data"));
        var database = Path.Combine(dir.Path, "data", "themearr.db");
        await File.WriteAllTextAsync(database, "live database");
        File.CreateSymbolicLink(Path.Combine(dir.Path, "data", ".repair-permissions"), database);
        var helper = Path.Combine(dir.Path, "old-helper");
        await File.WriteAllTextAsync(helper, $"#!/bin/sh\nrm '{dir.Path}/data/.repair-permissions'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var service = new NativePermissionRepairService(NullLogger<NativePermissionRepairService>.Instance);

        Assert.True(await service.RepairIfNeededAsync(dir.Path, helper, useSudo: false, CancellationToken.None));

        Assert.Equal("live database", await File.ReadAllTextAsync(database));
    }

    [Fact]
    public async Task Install_without_known_helper_is_not_modified()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "data"));
        var service = new NativePermissionRepairService(NullLogger<NativePermissionRepairService>.Instance);

        Assert.False(await service.RepairIfNeededAsync(dir.Path, Path.Combine(dir.Path, "absent"), useSudo: false, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(dir.Path, "data")));
    }
}
