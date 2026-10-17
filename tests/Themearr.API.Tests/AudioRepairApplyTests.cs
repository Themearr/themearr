using Themearr.API.Services;
namespace Themearr.API.Tests;

public class AudioRepairApplyTests
{
    [Fact]
    public async Task Apply_repairs_shared_file_once_retains_backup_and_preserves_history_and_verdict()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();f.Db.AddThemeHistory(f.Id,"Item",2020,"Chosen","url","movie","manual",100);
        f.Scalar("UPDATE movies SET recheck_state='kept'");var p=await f.Preview();var item=Assert.Single(p.Items);
        var start=f.Repair.StartApply(p.PreviewId,[item.Id]);Assert.True(start.Started);Assert.False(f.Repair.StartApply(p.PreviewId,[item.Id]).Started);
        var status=await f.Finish(start.RunId);Assert.Equal(1,status.Succeeded);Assert.Equal(3,status.SourceBytesSaved);Assert.Equal(8,status.BackupBytes);
        Assert.Equal(RepairTestFixture.Processed,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));
        Assert.Equal(RepairTestFixture.Original,File.ReadAllBytes(Assert.Single(Directory.GetFiles(f.Folder,".themearr-audio-backup-*"))));
        Assert.Equal(1,f.Audio.Calls);Assert.Equal(0,f.Source.Calls);Assert.Single(f.Db.GetThemeHistory());Assert.Equal("kept",f.Scalar("SELECT recheck_state FROM movies LIMIT 1"));
        Assert.Empty((await f.Preview()).Items);
    }
    [Theory]
    [InlineData("external")][InlineData("delete")][InlineData("download")][InlineData("options")][InlineData("roots")]
    public async Task Changes_during_encoding_skip_publication_and_preserve_the_newer_state(string change)
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();f.Audio.Hold=true;var p=await f.Preview();
        var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);Assert.True(start.Started);await f.Audio.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        switch(change) {
            case "external": File.WriteAllBytes(Path.Combine(f.Folder,"theme.mp3"),[0x49,0x44,0x33,7]);break;
            case "delete": f.Downloads.DeleteTheme(f.Id,"movie");break;
            case "download": Assert.True(f.Downloads.Start(f.Id,"https://www.youtube.com/watch?v=abcdefghijk","movie","manual"));while(f.Downloads.GetAttemptStatus(f.Id,"movie",null).InProgress)await Task.Delay(10);break;
            case "options": f.Db.SetAudioProcessingOptions(new(false,60));break;
            case "roots": f.Db.SetLibraryPaths([f.Dir.File("Other")]);break;
        }
        var path=Path.Combine(f.Folder,"theme.mp3");var expected=File.Exists(path)?File.ReadAllBytes(path):null;
        f.Audio.Release.TrySetResult();var status=await f.Finish(start.RunId);Assert.Equal(1,status.Stale);Assert.Equal(0,status.Succeeded);
        if(expected==null)Assert.False(File.Exists(path));else Assert.Equal(expected,File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(f.Folder,".themearr-audio-backup-*"));Assert.Empty(Directory.GetFiles(f.Folder,".themearr-audio-*.mp3"));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Encoder_failure_or_invalid_container_preserves_original(bool invalid)
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();f.Audio.Fail=!invalid;f.Audio.Invalid=invalid;var p=await f.Preview();
        var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);Assert.True(start.Started);var status=await f.Finish(start.RunId);
        Assert.Equal(1,status.Failed);Assert.Equal(0,status.Succeeded);Assert.Equal(RepairTestFixture.Original,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));
    }
    [Fact]
    public async Task Cancellation_stops_encoding_and_keeps_original_and_backup()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();f.Audio.Hold=true;var p=await f.Preview();var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);
        Assert.True(start.Started);await f.Audio.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.True(f.Repair.Cancel(start.RunId!.Value));
        var status=await f.Finish(start.RunId);Assert.Equal("cancelled",status.State);Assert.Equal(1,status.Cancelled);
        Assert.Equal(RepairTestFixture.Original,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));Assert.Equal(8,status.BackupBytes);
    }
    [Fact]
    public async Task Ledger_commit_failure_rolls_back_the_published_file_inside_the_boundary()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();
        f.Scalar("CREATE TRIGGER reject_success BEFORE UPDATE ON audio_repair_items WHEN NEW.state='succeeded' BEGIN SELECT RAISE(ABORT,'controlled'); END");
        var p=await f.Preview();var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);Assert.True(start.Started);var status=await f.Finish(start.RunId);
        Assert.Equal(1,status.Failed);Assert.Equal(RepairTestFixture.Original,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));
        Assert.Equal("failed",f.Scalar("SELECT state FROM audio_repair_items LIMIT 1"));
    }
}
