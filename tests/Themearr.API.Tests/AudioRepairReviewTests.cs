using Themearr.API.Services;
namespace Themearr.API.Tests;
public class AudioRepairReviewTests
{
    [Fact]
    public async Task Planted_staging_symlink_never_overwrites_an_external_file_or_gets_cleaned_up()
    {
        using var f=new RepairTestFixture(false);await f.Repair.RecoverAsync();f.Audio.Hold=true;var p=await f.Preview();var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);
        Assert.True(start.Started);await f.Audio.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var backup=Assert.Single(Directory.GetFiles(f.Folder,".themearr-audio-backup-*"));var stage=backup.Replace(".themearr-audio-backup-",".themearr-audio-")+".mp3";
        var victim=f.Dir.File("unrelated.txt");File.WriteAllText(victim,"external sentinel");File.CreateSymbolicLink(stage,victim);
        f.Audio.Release.TrySetResult();var status=await f.Finish(start.RunId);
        Assert.Equal("external sentinel",File.ReadAllText(victim));Assert.Equal(1,status.Failed);Assert.NotNull(new FileInfo(stage).LinkTarget);
        Assert.Equal(RepairTestFixture.Original,File.ReadAllBytes(Path.Combine(f.Folder,"theme.mp3")));
    }
    [Fact]
    public async Task Cancellation_racing_completion_always_leaves_a_terminal_job()
    {
        for(var n=0;n<80;n++) {
            using var f=new RepairTestFixture(false);await f.Repair.RecoverAsync();var p=await f.Preview();var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);Assert.True(start.Started);
            var cancelling=Task.Run(async()=>{for(var i=0;i<30;i++){f.Repair.Cancel(start.RunId!.Value);await Task.Yield();}});
            await cancelling;var report=await f.Finish(start.RunId);Assert.Contains(report.State,new[]{"completed","cancelled"});
            Assert.False(f.Repair.Cancel(start.RunId!.Value));
        }
    }
    [Fact]
    public async Task Shutdown_racing_completion_does_not_use_a_disposed_cancellation_source()
    {
        for(var n=0;n<80;n++) {
            using var f=new RepairTestFixture(false);await f.Repair.RecoverAsync();var p=await f.Preview();var start=f.Repair.StartApply(p.PreviewId,[p.Items[0].Id]);Assert.True(start.Started);
            await Task.WhenAll(Task.Run(()=>f.Repair.StopAsync(default)),Task.Run(()=>f.Repair.Cancel(start.RunId!.Value)));
            Assert.Contains(f.Repair.GetStatus(start.RunId).State,new[]{"completed","cancelled"});
        }
    }
}
