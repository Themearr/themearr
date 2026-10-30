namespace Themearr.API.Services;

public sealed record AudioRepairSelection(string MediaType, string Id);
public sealed record AudioRepairPreviewRequest(string MediaType, AudioProcessingOptions Options,
    IReadOnlyList<AudioRepairSelection>? Items = null, bool IncludeIgnored = false);
public sealed record AudioRepairAlias(string MediaType, string Id, string Title);
public sealed record AudioRepairPreviewItem(string Id, string Title, IReadOnlyList<AudioRepairAlias> Aliases,
    long BeforeBytes, long? EstimatedAfterBytes, string InputFingerprint, bool RequiresExplicitSelection);
public sealed record AudioRepairExclusion(IReadOnlyList<AudioRepairAlias> Aliases, string Reason);
public sealed record AudioRepairPreview(Guid PreviewId, DateTimeOffset ExpiresAt, AudioProcessingOptions Options,
    string OptionsFingerprint, IReadOnlyList<AudioRepairPreviewItem> Items, IReadOnlyList<AudioRepairExclusion> Exclusions,
    long BeforeBytes, long? EstimatedAfterBytes);
public sealed record AudioRepairStartResult(bool Started, Guid? RunId, string? Error = null);
public sealed record AudioRepairApplyRequest(Guid PreviewId, IReadOnlyList<string> ItemIds);
public sealed record AudioRepairCancelRequest(Guid RunId);
public sealed record AudioRepairItemStatus(string Id, string Title, IReadOnlyList<AudioRepairAlias> Aliases,
    string State, long BeforeBytes, long? AfterBytes, long BackupBytes, string? Error, IReadOnlyList<string> Warnings);
public sealed record AudioRepairStatus(Guid? RunId, string State, bool EncoderAvailable,
    IReadOnlyList<AudioRepairItemStatus> Items, int Total, int Processed, int Succeeded, int Failed,
    int Stale, int Cancelled, long BeforeBytes, long AfterBytes, long BackupBytes, long SourceBytesSaved,
    string? Error = null);

internal sealed record AudioRepairCandidate(string MediaType, string Id, string Title, string Folder,
    bool Ignored, string? Source, string? SourceRef, string? PickedBy, string? RecheckState)
{
    internal AudioRepairAlias Alias => new(MediaType, Id, Title);
    internal bool RequiresExplicitSelection => Ignored || PickedBy == "manual" || RecheckState == "kept";
}
internal sealed record AudioRepairFileSnapshot(string MediaType, string Id, string Folder, string SourcePath,
    string FinalPath, string InputHash, long BeforeBytes, long LastWriteTicks, long Revision);
internal sealed record AudioRepairStoredRun(Guid Id, string State, string OptionsJson, string OptionsHash,
    string SettingsHash, DateTimeOffset StartedAt, string? Error = null);
internal sealed record AudioRepairStoredItem
{
    public Guid RunId { get; init; }
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<AudioRepairCandidate> Aliases { get; init; } = [];
    public string State { get; init; } = "planned";
    public string Folder { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string FinalPath { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public string StagingPath { get; init; } = "";
    public string InputHash { get; init; } = "";
    public string? OutputHash { get; init; }
    public long Revision { get; init; }
    public long BeforeBytes { get; init; }
    public long? AfterBytes { get; init; }
    public long BackupBytes { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
