namespace AutoEnvPlus.Core.Storage;

public sealed class CacheCleanupPlan
{
    internal CacheCleanupPlan(
        object ownerToken,
        string id,
        DateTimeOffset createdAtUtc,
        CacheDirectoryLocation source,
        string trashPath,
        IReadOnlyList<CacheCleanupEntryIdentity> entries,
        long fileCount,
        long totalBytes)
    {
        OwnerToken = ownerToken;
        Id = id;
        CreatedAtUtc = createdAtUtc;
        Source = source;
        TrashPath = trashPath;
        Entries = entries;
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }

    public string Id { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public CacheDirectoryLocation Source { get; }

    public string TrashPath { get; }

    public int TopLevelEntryCount => Entries.Count;

    public long FileCount { get; }

    public long TotalBytes { get; }

    internal object OwnerToken { get; }

    internal IReadOnlyList<CacheCleanupEntryIdentity> Entries { get; }
}

public sealed record CacheCleanupProgress(
    string Stage,
    string? RelativePath = null,
    int CompletedEntries = 0,
    int TotalEntries = 0,
    long CompletedBytes = 0,
    long TotalBytes = 0);

public enum CacheCleanupItemState
{
    Recoverable,
    RestorePending,
    PurgePending,
}

public sealed class CacheCleanupItem
{
    internal CacheCleanupItem(
        object ownerToken,
        CacheDirectoryLocation location,
        string id,
        DateTimeOffset createdAtUtc,
        string manifestPath,
        string trashPath,
        CacheCleanupItemState state,
        string? restoreBlockedReason,
        long fileCount,
        long totalBytes)
    {
        OwnerToken = ownerToken;
        Location = location;
        Id = id;
        CreatedAtUtc = createdAtUtc;
        ManifestPath = manifestPath;
        TrashPath = trashPath;
        State = state;
        RestoreBlockedReason = restoreBlockedReason;
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }

    public string Id { get; }

    public string CacheId => Location.Definition.Id;

    public string DisplayName => Location.Definition.DisplayName;

    public DateTimeOffset CreatedAtUtc { get; }

    public string SourcePath => Location.DirectoryPath;

    public string ManifestPath { get; }

    public string TrashPath { get; }

    public CacheCleanupItemState State { get; }

    public bool CanRestore => State != CacheCleanupItemState.PurgePending
        && RestoreBlockedReason is null;

    public string? RestoreBlockedReason { get; }

    public bool CanPurge => true;

    public long FileCount { get; }

    public long TotalBytes { get; }

    internal object OwnerToken { get; }

    internal CacheDirectoryLocation Location { get; }
}

public sealed record CacheCleanupCatalog(
    IReadOnlyList<CacheCleanupItem> Items,
    IReadOnlyList<string> Errors);

public sealed record CacheCleanupOperationResult(
    bool Success,
    bool Cancelled,
    string SourcePath,
    string? ItemId,
    string? ManifestPath,
    bool RecoveryAvailable,
    bool PurgePending,
    long FileCount,
    long TotalBytes,
    string? Error);

internal sealed record CacheCleanupEntryIdentity(
    string Name,
    bool IsDirectory,
    long FileCount,
    long TotalBytes,
    string Fingerprint);
