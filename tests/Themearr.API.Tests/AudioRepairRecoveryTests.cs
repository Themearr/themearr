using System.Security.Cryptography;
using System.Text.Json;
using Themearr.API.Services;
namespace Themearr.API.Tests;
public class AudioRepairRecoveryTests
{
    [Fact]
    public async Task Historical_report_cannot_hide_startup_recovery_from_polling_clients()
    {
        using var f=new RepairTestFixture();var run=Guid.NewGuid();
        f.Db.CreateAudioRepairRun(new(run,"completed","{}",f.Options.Fingerprint(),f.Db.GetAudioProcessingOptions().Fingerprint(),f.Time.Now),[]);
        Assert.Equal("recovering",f.Repair.GetStatus().State);
        await f.Repair.RecoverAsync();Assert.Equal("completed",f.Repair.GetStatus().State);
    }
    [Theory]
    [InlineData("planned",false,false)][InlineData("backed_up",false,false)]
    [InlineData("prepared",false,false)][InlineData("applying",true,false)]
    [InlineData("applying",true,true)]
    public async Task Recovery_only_finalizes_exact_published_output_and_never_mutates_media(string phase,bool published,bool newer)
    {
        using var f=new RepairTestFixture();var run=Guid.NewGuid();var id=Guid.NewGuid().ToString("N");var suffix=run.ToString("N")+"-"+id;
        var final=Path.Combine(f.Folder,"theme.mp3");var backup=Path.Combine(f.Folder,".themearr-audio-backup-"+suffix);var stage=Path.Combine(f.Folder,".themearr-audio-"+suffix+".mp3");
        var input=Convert.ToHexString(SHA256.HashData(RepairTestFixture.Original));var output=Convert.ToHexString(SHA256.HashData(RepairTestFixture.Processed));
        if(phase!="planned")File.WriteAllBytes(backup,RepairTestFixture.Original);
        if(phase is "prepared" or "applying")File.WriteAllBytes(stage,RepairTestFixture.Processed);
        if(published) {File.Move(stage,final,true);if(newer)File.WriteAllBytes(final,[0x49,0x44,0x33,7,7,7]);}
        var item=new AudioRepairStoredItem {RunId=run,Id=id,Title="Item",Aliases=f.Db.GetAudioRepairCandidates(),Folder=f.Folder,SourcePath=final,FinalPath=final,BackupPath=backup,StagingPath=stage,InputHash=input,BeforeBytes=8,State=phase,BackupBytes=phase=="planned"?0:8,AfterBytes=5,OutputHash=phase is "prepared" or "applying"?output:null};
        f.Db.CreateAudioRepairRun(new(run,"running",JsonSerializer.Serialize(f.Options),f.Options.Fingerprint(),f.Db.GetAudioProcessingOptions().Fingerprint(),f.Time.Now),[item]);f.Db.UpdateAudioRepairItem(item);
        var files=Directory.GetFiles(f.Folder).ToDictionary(x=>x,File.ReadAllBytes);await f.Repair.RecoverAsync();var status=f.Repair.GetStatus(run);
        Assert.Equal(published&&!newer?1:0,status.Succeeded);Assert.Equal(published&&!newer?"completed":"interrupted",status.State);
        Assert.Equal(files.Keys,Directory.GetFiles(f.Folder));foreach(var pair in files)Assert.Equal(pair.Value,File.ReadAllBytes(pair.Key));
        Assert.Equal(0,f.Audio.Calls);Assert.Equal(0,f.Source.Calls);
    }
}
