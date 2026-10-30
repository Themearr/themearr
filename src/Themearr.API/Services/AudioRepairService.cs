using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services;

public sealed class AudioRepairService(Database db, DownloadService downloads, IThemeAudioProcessor processor,
    ILogger<AudioRepairService> log, PlexService? plex = null, TimeProvider? clock = null) : BackgroundService
{
    private sealed record PreviewEntry(AudioRepairFileSnapshot Input, IReadOnlyList<AudioRepairCandidate> Aliases);
    private sealed record CachedPreview(AudioRepairPreview Public, string SettingsHash, Dictionary<string,PreviewEntry> Entries);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _recovery = new(1,1);
    private bool _ready, _available, _scanning;
    private CachedPreview? _preview;
    private Guid? _active;
    private CancellationTokenSource? _cancel;
    private Task? _worker;
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private static bool Terminal(string state) => state is "succeeded" or "failed" or "stale" or "cancelled" or "interrupted";

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await _recovery.WaitAsync(ct);
        try {
            lock(_sync) if(_ready) return;
            var available=await processor.IsAvailableAsync(ct);
            foreach(var run in db.GetAudioRepairRuns().Where(r=>r.State is "running" or "cancelling")) {
                var items=db.GetAudioRepairItems(run.Id);
                foreach(var item in items.Where(i=>!Terminal(i.State))) {
                    var matched=false;
                    if(item.State is "prepared" or "applying") {
                        try { matched=downloads.RepairOutputMatches(item,ct); }
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
                    }
                    db.UpdateAudioRepairItem(item with {State=matched?"succeeded":"interrupted",Error=matched?null:"Interrupted repair requires a fresh preview."});
                }
                db.FinishAudioRepairRun(run.Id,db.GetAudioRepairItems(run.Id).Any(i=>i.State=="interrupted")?"interrupted":"completed");
            }
            lock(_sync) {_available=available;_ready=true;}
        } finally {_recovery.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try {await RecoverAsync(stoppingToken);await Task.Delay(Timeout.Infinite,stoppingToken);}
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? worker;CancellationTokenSource? cancel;
        lock(_sync) {worker=_worker;cancel=_cancel;}
        try {cancel?.Cancel();}catch(ObjectDisposedException) { }
        if(worker!=null)await worker.WaitAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
    public async Task<AudioRepairPreview> PreviewAsync(AudioRepairPreviewRequest request, CancellationToken ct = default)
    {
        if(request.MediaType is not ("movie" or "show" or "all") || request.Options is null || request.Options.ValidationError()!=null || !request.Options.Enabled)
            throw new ArgumentException("Choose movie, show or all and valid enabled processing options.");
        lock(_sync) {
            if(!_ready || _active!=null || _scanning)throw new InvalidOperationException("Repair is recovering or busy.");
            _scanning=true;_preview=null;
        }
        try {
            var available=await processor.IsAvailableAsync(ct);lock(_sync)_available=available;
            if(!available)throw new InvalidOperationException("Install ffmpeg and ffprobe before repairing audio.");
            var all=db.GetAudioRepairCandidates();
            if(request.Items is { } selections && (selections.Count>1000 || selections.Any(s=>s.MediaType is not ("movie" or "show") || !all.Any(a=>a.MediaType==s.MediaType && a.Id==s.Id)
                || request.MediaType!="all" && s.MediaType!=request.MediaType)))throw new ArgumentException("Unknown or invalid library item selection.");
            var selected=all.Where(a=>(request.MediaType=="all" || a.MediaType==request.MediaType)
                && (request.Items==null || request.Items.Any(s=>s.MediaType==a.MediaType && s.Id==a.Id))).ToList();
            var entries=new Dictionary<string,PreviewEntry>();var items=new List<AudioRepairPreviewItem>();var excluded=new List<AudioRepairExclusion>();
            var settingsHash=db.GetAudioProcessingOptions().Fingerprint();var optionsHash=request.Options.Fingerprint();
            var folders=new HashSet<string>(StringComparer.Ordinal);
            foreach(var owner in selected) {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<AudioRepairCandidate> aliases=[owner];
                try {
                    var folder=ThemeFiles.CanonicalFolder(owner.Folder);if(!folders.Add(folder))continue;
                    aliases=all.Where(a=>SameFolder(a.Folder,folder)).ToList();
                    if(aliases.Any(a=>a.Ignored) && !request.IncludeIgnored) {excluded.Add(new(aliases.Select(a=>a.Alias).ToList(),"Ignored item shares this file."));continue;}
                    var input=downloads.CaptureRepairInput(owner,ct);
                    if(db.HasSuccessfulAudioRepair(folder,input.InputHash,optionsHash))continue;
                    if(items.Count>=1000) {excluded.Add(new(aliases.Select(a=>a.Alias).ToList(),"Preview limit reached; use another batch."));continue;}
                    var id=Guid.NewGuid().ToString("N");entries.Add(id,new(input,aliases));
                    items.Add(new(id,owner.Title,aliases.Select(a=>a.Alias).ToList(),input.BeforeBytes,Estimate(request.Options),input.InputHash,aliases.Any(a=>a.RequiresExplicitSelection)));
                } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
                    excluded.Add(new(aliases.Select(a=>a.Alias).ToList(),SafeError(ex)));
                }
            }
            var preview=new AudioRepairPreview(Guid.NewGuid(),Now.AddMinutes(15),request.Options,optionsHash,items,excluded,items.Sum(i=>i.BeforeBytes),items.All(i=>i.EstimatedAfterBytes!=null)?items.Sum(i=>i.EstimatedAfterBytes!.Value):null);
            lock(_sync) _preview=new(preview,settingsHash,entries);
            return preview;
        } finally {lock(_sync)_scanning=false;}
    }
    private static bool SameFolder(string candidate,string folder) {try{return ThemeFiles.CanonicalFolder(candidate)==folder;}catch(ArgumentException){return false;}}
    private static long? Estimate(AudioProcessingOptions options) => options.TrimSeconds is { } seconds?Math.Min(StreamLimits.MaxThemeBytes,(long)seconds*options.BitrateKbps*125+65536):null;
    private static string SafeError(Exception ex) => ex is UnauthorizedAccessException?"Folder is outside configured roots or contains a symbolic link.":ex is IOException?"Unable to read or write repair files; check access and free space.":ex.Message;

    public AudioRepairStartResult StartApply(Guid previewId, IReadOnlyList<string> itemIds)
    {
        lock(_sync) {
            var cached=_preview;
            if(!_ready || _active!=null || _scanning || cached==null || cached.Public.PreviewId!=previewId || cached.Public.ExpiresAt<=Now)
                return new(false,null,"Preview expired, was replaced, or repair is busy. Create a fresh preview.");
            if(itemIds==null || itemIds.Count==0 || itemIds.Count>1000 || itemIds.Any(id=>!cached.Entries.ContainsKey(id)))return new(false,null,"Select valid preview items.");
            if(db.GetAudioProcessingOptions().Fingerprint()!=cached.SettingsHash)return new(false,null,"Processing settings changed. Create a fresh preview.");
            var runId=Guid.NewGuid();
            var work=itemIds.Distinct().Select(id=> {
                var entry=cached.Entries[id];var input=entry.Input;var suffix=runId.ToString("N")+"-"+id;
                return (Entry:entry,Item:new AudioRepairStoredItem {RunId=runId,Id=id,Title=cached.Public.Items.First(i=>i.Id==id).Title,Aliases=entry.Aliases,
                    Folder=input.Folder,SourcePath=input.SourcePath,FinalPath=input.FinalPath,BackupPath=Path.Combine(input.Folder,".themearr-audio-backup-"+suffix),
                    StagingPath=Path.Combine(input.Folder,".themearr-audio-"+suffix+".mp3"),InputHash=input.InputHash,Revision=input.Revision,BeforeBytes=input.BeforeBytes});
            }).ToList();
            db.CreateAudioRepairRun(new(runId,"running",JsonSerializer.Serialize(cached.Public.Options,AudioProcessingOptions.Json),cached.Public.OptionsFingerprint,cached.SettingsHash,Now),work.Select(w=>w.Item).ToList());
            _active=runId;_preview=null;_cancel=new();
            var token=_cancel.Token;
            _worker=Task.Run(()=>RunAsync(runId,work,cached.Public.Options,cached.SettingsHash,token));
            return new(true,runId);
        }
    }
    private async Task RunAsync(Guid runId,List<(PreviewEntry Entry,AudioRepairStoredItem Item)> work,AudioProcessingOptions options,string settingsHash,CancellationToken ct)
    {
        string? failure=null;
        try {
            foreach(var (entry,original) in work) {
                var item=original;
                DirectoryInfo? workspace=null;var staged=false;
                try {
                    ct.ThrowIfCancellationRequested();
                    if(!downloads.BackupRepairInput(entry.Input,item.BackupPath,settingsHash,Estimate(options) ?? StreamLimits.MaxThemeBytes,ct)) {
                        item=item with {State="stale",Error="Theme or processing settings changed since preview."};continue;
                    }
                    item=item with {State="backed_up",BackupBytes=item.BeforeBytes};db.UpdateAudioRepairItem(item);
                    // Media folders may be writable by other processes. Never let an
                    // encoder follow a predictable output symlink in one of them.
                    workspace=Directory.CreateTempSubdirectory("themearr-repair-");
                    var privateInput=Path.Combine(workspace.FullName,Path.GetFileName(item.BackupPath));
                    var privateOutput=Path.Combine(workspace.FullName,"output.mp3");
                    using(var source=File.OpenRead(item.BackupPath))
                    using(var copy=new FileStream(privateInput,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        await StreamLimits.CopyWithLimitAsync(source,copy,StreamLimits.MaxThemeBytes,ct);
                    if(ThemeFiles.Fingerprint(privateInput,ct)!=item.InputHash)throw new IOException("Backup changed before encoding.");
                    var result=await processor.ProcessAsync(privateInput,privateOutput,options,_=>{},ct);
                    var stat=ThemeFiles.ThemeStat(privateOutput);
                    if(new FileInfo(privateOutput).LinkTarget!=null || stat==null || stat.Value.Length<=0 || stat.Value.Length>StreamLimits.MaxThemeBytes || stat.Value.Length!=result.Bytes
                        || ThemeFiles.Fingerprint(privateOutput,ct)!=result.Fingerprint)throw new InvalidOperationException("Encoder output failed verification.");
                    using(var file=File.OpenRead(privateOutput)) {
                        var header=new byte[8];var read=file.Read(header);
                        if(read<3 || !(header.AsSpan(0,3).SequenceEqual("ID3"u8) || header[0]==0xff && (header[1]&0xe0)==0xe0))throw new InvalidOperationException("Encoder did not produce MP3 audio.");
                    }
                    if(!downloads.StageRepairOutput(entry.Input,item,privateOutput,settingsHash,ct)) {item=item with {State="stale",Error="Theme or processing settings changed during repair."};continue;}
                    staged=true;
                    item=item with {State="prepared",OutputHash=result.Fingerprint,AfterBytes=result.Bytes};db.UpdateAudioRepairItem(item);
                    item=item with {State="applying"};db.UpdateAudioRepairItem(item);
                    if(!downloads.CommitAudioRepair(entry.Input,item,privateInput,settingsHash,ct)) {item=item with {State="stale",Error="Theme or processing settings changed during repair."};continue;}
                    item=item with {State="succeeded"};
                    var warnings=new List<string>();
                    foreach(var alias in item.Aliases.Where(a=>a.Source=="plex").DistinctBy(a=>a.SourceRef)) {
                        try {if(plex==null || !await plex.RefreshItemMetadataAsync(alias.Source,alias.SourceRef))warnings.Add("Audio repaired; Plex metadata refresh was unavailable or failed.");}
                        catch(Exception ex) {log.LogWarning("Repair Plex refresh failed: {Error}",LogSanitizer.Clean(ex.Message));warnings.Add("Audio repaired; Plex metadata refresh failed.");}
                    }
                    item=item with {Warnings=warnings};
                } catch(OperationCanceledException) when(ct.IsCancellationRequested) {item=item with {State="cancelled",Error="Repair cancelled."};}
                catch(Exception ex) {log.LogWarning("Audio repair failed: {Error}",LogSanitizer.Clean(ex.Message));item=item with {State="failed",Error=SafeError(ex)};}
                finally {
                    // Backups remain for the operator, including failed and stale items.
                    if(File.Exists(item.BackupPath))item=item with {BackupBytes=new FileInfo(item.BackupPath).Length};
                    if(staged)try {File.Delete(item.StagingPath);}catch(IOException) { }
                    if(workspace!=null)try {workspace.Delete(recursive:true);}catch(IOException) { }
                    db.UpdateAudioRepairItem(item);
                }
            }
        } catch(Exception ex) {failure="Repair stopped unexpectedly; create a fresh preview.";log.LogError("Audio repair job failed: {Error}",LogSanitizer.Clean(ex.Message));}
        finally {
            lock(_sync) {
                db.FinishAudioRepairRun(runId,failure!=null?"failed":ct.IsCancellationRequested?"cancelled":"completed",failure);
                _active=null;_cancel?.Dispose();_cancel=null;
            }
        }
    }
    public AudioRepairStatus GetStatus(Guid? runId = null)
    {
        lock(_sync) {
            var run=db.GetAudioRepairRuns().FirstOrDefault(r=>runId==null || r.Id==runId);
            if(run==null)return new(null,_ready?(runId==null?"idle":"unknown"):"recovering",_available,[],0,0,0,0,0,0,0,0,0,0);
            var items=db.GetAudioRepairItems(run.Id);var successes=items.Where(i=>i.State=="succeeded").ToList();
            return new(run.Id,_ready?run.State:"recovering",_available,items.Select(i=>new AudioRepairItemStatus(i.Id,i.Title,i.Aliases.Select(a=>a.Alias).ToList(),i.State,i.BeforeBytes,i.AfterBytes,i.BackupBytes,i.Error,i.Warnings)).ToList(),
                items.Count,items.Count(i=>Terminal(i.State)),successes.Count,items.Count(i=>i.State=="failed"),items.Count(i=>i.State=="stale"),items.Count(i=>i.State=="cancelled"),
                successes.Sum(i=>i.BeforeBytes),successes.Sum(i=>i.AfterBytes ?? 0),items.Sum(i=>i.BackupBytes),successes.Sum(i=>i.BeforeBytes-(i.AfterBytes ?? 0)),run.Error);
        }
    }
    public bool Cancel(Guid runId)
    {
        CancellationTokenSource? cancel;
        lock(_sync) {if(_active!=runId)return false;cancel=_cancel;db.FinishAudioRepairRun(runId,"cancelling");}
        try {cancel?.Cancel();}catch(ObjectDisposedException) { }
        return true;
    }
}
