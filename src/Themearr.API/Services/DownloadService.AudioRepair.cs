namespace Themearr.API.Services;

public partial class DownloadService
{
    // Publication revision is separate from the delete epoch: ordinary sequential
    // landings must not cancel the movie/show job waiting for the same folder gate.
    private readonly Dictionary<string,long> _contentRevisions = new(StringComparer.Ordinal);

    private void ValidateRepairFolder(string folder)
    {
        var roots = ThemeDatabase.GetLibraryPaths().Select(ThemeFiles.CanonicalFolder).ToArray();
        var root = roots.FirstOrDefault(r => ThemeFiles.IsWithinRoots(folder,[r]));
        if (root == null) throw new UnauthorizedAccessException("Repair requires a folder inside configured library roots.");
        for (var current = new DirectoryInfo(folder); current.FullName != root; current = current.Parent
            ?? throw new UnauthorizedAccessException("Repair folder is outside its library root."))
            if (current.LinkTarget != null) throw new UnauthorizedAccessException("Repair does not follow symbolic links inside library roots.");
    }

    internal AudioRepairFileSnapshot CaptureRepairInput(AudioRepairCandidate owner, CancellationToken ct = default)
    {
        lock (_admissionLock)
        {
            var raw = ThemeDatabase.GetThemeFolder(owner.Id,owner.MediaType) ?? throw new InvalidOperationException("Library item no longer exists.");
            var folder = ThemeFiles.CanonicalFolder(raw); ValidateRepairFolder(folder);
            var files = ThemeFiles.FinalThemeFiles(folder);
            if (files.Count != 1) throw new InvalidOperationException(files.Count == 0 ? "No local theme file." : "Multiple theme files require an explicit cleanup before repair.");
            var source = files[0]; if (new FileInfo(source).LinkTarget != null) throw new UnauthorizedAccessException("Repair does not follow theme-file symbolic links.");
            var before = ThemeFiles.ThemeStat(source) ?? throw new InvalidOperationException("Theme file disappeared.");
            if (before.Length > StreamLimits.MaxThemeBytes) throw new InvalidOperationException("Theme exceeds the repair size limit.");
            var hash = ThemeFiles.Fingerprint(source,ct);
            if (ThemeFiles.ThemeStat(source) != before) throw new InvalidOperationException("Theme changed while previewing.");
            return new(owner.MediaType,owner.Id,folder,source,Path.Combine(folder,"theme.mp3"),hash,before.Length,before.LastWriteTimeUtc.Ticks,_contentRevisions.GetValueOrDefault(folder));
        }
    }

    private bool RepairInputCurrent(AudioRepairFileSnapshot input, CancellationToken ct)
    {
        try {
        ValidateRepairFolder(input.Folder);
        var raw = ThemeDatabase.GetThemeFolder(input.Id,input.MediaType);
        if (raw == null || ThemeFiles.CanonicalFolder(raw) != input.Folder || _contentRevisions.GetValueOrDefault(input.Folder) != input.Revision) return false;
        var files = ThemeFiles.FinalThemeFiles(input.Folder);
        if (files.Count != 1 || files[0] != input.SourcePath || new FileInfo(input.SourcePath).LinkTarget != null) return false;
        var stat = ThemeFiles.ThemeStat(input.SourcePath);
        return stat?.Length == input.BeforeBytes && stat?.LastWriteTimeUtc.Ticks == input.LastWriteTicks && ThemeFiles.Fingerprint(input.SourcePath,ct) == input.InputHash;
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) {return false;}
    }

    internal bool BackupRepairInput(AudioRepairFileSnapshot input, string backupPath, string settingsHash, long outputSpaceBound, CancellationToken ct)
    {
        lock (_admissionLock)
        {
            ct.ThrowIfCancellationRequested();
            if (ThemeDatabase.GetAudioProcessingOptions().Fingerprint() != settingsHash || !RepairInputCurrent(input,ct)) return false;
            if (Path.GetDirectoryName(backupPath) != input.Folder || !Path.GetFileName(backupPath).StartsWith(".themearr-audio-backup-",StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Invalid repair backup location.");
            if (new DriveInfo(input.Folder).AvailableFreeSpace < checked(input.BeforeBytes * 2 + outputSpaceBound))
                throw new IOException("Not enough free space for backup, output and rollback.");
            File.Copy(input.SourcePath,backupPath,overwrite:false);
            if (ThemeFiles.Fingerprint(backupPath,ct) != input.InputHash) throw new IOException("Backup did not match the previewed file.");
            return true;
        }
    }

    internal bool StageRepairOutput(AudioRepairFileSnapshot input, AudioRepairStoredItem item, string privateOutput, string settingsHash, CancellationToken ct)
    {
        lock (_admissionLock) {
            ct.ThrowIfCancellationRequested();ValidateStoredRepairPaths(item);
            if(ThemeDatabase.GetAudioProcessingOptions().Fingerprint()!=settingsHash || !RepairInputCurrent(input,ct))return false;
            using var source=File.OpenRead(privateOutput);
            using var destination=new FileStream(item.StagingPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            try {source.CopyTo(destination);return true;}
            catch {destination.Dispose();try {File.Delete(item.StagingPath);}catch(IOException) { }throw;}
        }
    }

    internal bool CommitAudioRepair(AudioRepairFileSnapshot input, AudioRepairStoredItem prepared, string rollbackPath, string settingsHash, CancellationToken ct)
    {
        lock (_admissionLock)
        {
            ct.ThrowIfCancellationRequested();
            ValidateStoredRepairPaths(prepared);
            if (ThemeDatabase.GetAudioProcessingOptions().Fingerprint() != settingsHash || !RepairInputCurrent(input,ct)) return false;
            if (new FileInfo(prepared.BackupPath).LinkTarget != null || ThemeFiles.Fingerprint(prepared.BackupPath,ct) != input.InputHash)
                throw new IOException("Repair backup changed before publication.");
            if (new FileInfo(prepared.StagingPath).LinkTarget != null || ThemeFiles.Fingerprint(prepared.StagingPath,ct) != prepared.OutputHash)
                throw new IOException("Prepared audio changed before publication.");
            var published = false;
            ThemeDatabase.CommitAudioRepairItem(prepared, () => {
                // These reads occur after SQLite's writer reservation, so a root/options
                // save cannot slip between the current configuration and the file move.
                ValidateRepairFolder(input.Folder);
                if (ThemeDatabase.GetAudioProcessingOptions().Fingerprint() != settingsHash) throw new InvalidOperationException("Processing options changed.");
                ct.ThrowIfCancellationRequested();
                _folderRevisions[input.Folder] = _folderRevisions.GetValueOrDefault(input.Folder) + 1;
                _contentRevisions[input.Folder] = _contentRevisions.GetValueOrDefault(input.Folder) + 1;
                File.Move(prepared.StagingPath,input.FinalPath,overwrite:true); published = true;
                if (input.SourcePath != input.FinalPath) File.Delete(input.SourcePath);
            }, () => {
                if (!published) return;
                var restore = Path.Combine(input.Folder,".themearr-audio-restore-"+Guid.NewGuid().ToString("N"));
                try {
                    File.Copy(rollbackPath,restore,overwrite:false); File.Move(restore,input.SourcePath,overwrite:true);
                    if (input.FinalPath != input.SourcePath) File.Delete(input.FinalPath);
                } finally { try { File.Delete(restore); } catch (IOException) { } }
            });
            return true;
        }
    }

    private static void ValidateStoredRepairPaths(AudioRepairStoredItem item)
    {
        if (!Guid.TryParseExact(item.Id,"N",out _) || item.RunId == Guid.Empty) throw new UnauthorizedAccessException("Invalid repair item identity.");
        var suffix = item.RunId.ToString("N")+"-"+item.Id;
        if (item.FinalPath != Path.Combine(item.Folder,"theme.mp3")
            || item.BackupPath != Path.Combine(item.Folder,".themearr-audio-backup-"+suffix)
            || item.StagingPath != Path.Combine(item.Folder,".themearr-audio-"+suffix+".mp3")
            || Path.GetDirectoryName(item.SourcePath) != item.Folder || !Path.GetFileName(item.SourcePath).StartsWith("theme.",StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid stored repair paths.");
    }

    internal bool RepairOutputMatches(AudioRepairStoredItem item, CancellationToken ct)
    {
        lock (_admissionLock)
        {
            ValidateStoredRepairPaths(item); ValidateRepairFolder(item.Folder);
            if (!item.Aliases.Any(a => ThemeDatabase.GetThemeFolder(a.Id,a.MediaType) is { } raw && ThemeFiles.CanonicalFolder(raw) == item.Folder)) return false;
            var files = ThemeFiles.FinalThemeFiles(item.Folder);
            return files.Count == 1 && files[0] == item.FinalPath && new FileInfo(item.FinalPath).LinkTarget == null
                && item.OutputHash != null && ThemeFiles.Fingerprint(item.FinalPath,ct) == item.OutputHash;
        }
    }
}
