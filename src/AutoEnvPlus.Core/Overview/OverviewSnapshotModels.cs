namespace AutoEnvPlus.Core.Overview;

public enum OverviewSnapshotDepth
{
    Quick,
    Full,
}

public sealed record OverviewLanguageStatus(
    string LanguageId,
    string DisplayName,
    string Version,
    string Detail);

public sealed record OverviewPathStatus(
    int DirectoryCount,
    int MissingDirectoryCount,
    int DuplicateDirectoryCount,
    int CommandConflictCount,
    string Detail);

public sealed record OverviewStorageStatus(
    long TotalBytes,
    long TotalFiles,
    int ExistingDirectoryCount,
    string? LargestCacheName,
    long LargestCacheBytes,
    long? ManagedDriveFreeBytes,
    IReadOnlyList<string> Errors);

public sealed record OverviewNetworkStatus(
    int ProxyEndpointCount,
    int ToolOverrideCount,
    string Summary,
    string Detail);

public sealed record OverviewDownloadStatus(
    int FileCount,
    long TotalBytes);

public sealed record OverviewProviderStatus(
    int BuiltInProviderCount,
    int ImportedProviderCount,
    int EnabledImportedProviderCount,
    int ErrorCount);

public sealed record OverviewProjectStatus(
    string ProjectRoot,
    DateTimeOffset LastSeenUtc);

public sealed record OverviewActivityStatus(
    string Operation,
    string Summary,
    DateTimeOffset TimestampUtc);

public sealed record OverviewSnapshot(
    DateTimeOffset CapturedAtUtc,
    OverviewSnapshotDepth Depth,
    DateTimeOffset? LastFullScanAtUtc,
    string ManagedRoot,
    IReadOnlyList<OverviewLanguageStatus> Languages,
    OverviewPathStatus? Path,
    OverviewStorageStatus? Storage,
    OverviewNetworkStatus Network,
    OverviewDownloadStatus Downloads,
    OverviewProviderStatus Providers,
    IReadOnlyList<OverviewProjectStatus> Projects,
    IReadOnlyList<OverviewActivityStatus> Activities,
    IReadOnlyList<string> Errors)
{
    public const int MaximumLanguageCount = 64;
    public const int MaximumProjectCount = 12;
    public const int MaximumActivityCount = 20;
    public const int MaximumErrorCount = 64;

    public void Validate()
    {
        if (!Enum.IsDefined(Depth))
        {
            throw new InvalidDataException("The overview snapshot depth is unsupported.");
        }

        if (CapturedAtUtc == default || string.IsNullOrWhiteSpace(ManagedRoot))
        {
            throw new InvalidDataException("The overview snapshot identity is incomplete.");
        }

        if (Network is null || Downloads is null || Providers is null)
        {
            throw new InvalidDataException("The overview snapshot summaries are incomplete.");
        }

        if (LastFullScanAtUtc > CapturedAtUtc.AddMinutes(5))
        {
            throw new InvalidDataException("The full-scan timestamp is later than the snapshot.");
        }

        ValidateCount(Languages, MaximumLanguageCount, "languages");
        ValidateCount(Projects, MaximumProjectCount, "projects");
        ValidateCount(Activities, MaximumActivityCount, "activities");
        ValidateCount(Errors, MaximumErrorCount, "errors");
        ValidateCount(Storage?.Errors ?? [], MaximumErrorCount, "storage errors");

        EnsureNonNegative(
            Path?.DirectoryCount,
            Path?.MissingDirectoryCount,
            Path?.DuplicateDirectoryCount,
            Path?.CommandConflictCount,
            Storage?.TotalBytes,
            Storage?.TotalFiles,
            Storage?.ExistingDirectoryCount,
            Storage?.LargestCacheBytes,
            Storage?.ManagedDriveFreeBytes,
            Network.ProxyEndpointCount,
            Network.ToolOverrideCount,
            Downloads.FileCount,
            Downloads.TotalBytes,
            Providers.BuiltInProviderCount,
            Providers.ImportedProviderCount,
            Providers.EnabledImportedProviderCount,
            Providers.ErrorCount);

        if (Providers.EnabledImportedProviderCount > Providers.ImportedProviderCount)
        {
            throw new InvalidDataException(
                "The enabled Provider count exceeds the imported Provider count.");
        }

        foreach (OverviewLanguageStatus language in Languages)
        {
            EnsureText(language.LanguageId, "language ID", 80);
            EnsureText(language.DisplayName, "language display name", 120);
            EnsureText(language.Version, "language version", 160);
            EnsureText(language.Detail, "language detail", 512);
        }

        if (Path is not null)
        {
            EnsureText(Path.Detail, "PATH detail", 1_024);
        }

        EnsureText(Network.Summary, "network summary", 256);
        EnsureText(Network.Detail, "network detail", 1_024);

        foreach (OverviewProjectStatus project in Projects)
        {
            EnsureText(project.ProjectRoot, "project root", 1_024);
        }

        foreach (OverviewActivityStatus activity in Activities)
        {
            EnsureText(activity.Operation, "activity operation", 120);
            EnsureText(activity.Summary, "activity summary", 1_024);
        }

        foreach (string error in Errors.Concat(Storage?.Errors ?? []))
        {
            EnsureText(error, "overview error", 2_048);
        }
    }

    private static void ValidateCount<T>(
        IReadOnlyCollection<T>? items,
        int maximum,
        string description)
    {
        if (items is null || items.Count > maximum)
        {
            throw new InvalidDataException(
                $"The overview snapshot {description} collection is invalid.");
        }
    }

    private static void EnsureNonNegative(params long?[] values)
    {
        if (values.Any(value => value < 0))
        {
            throw new InvalidDataException("The overview snapshot contains a negative count.");
        }
    }

    private static void EnsureText(string? value, string description, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"The overview snapshot {description} is invalid.");
        }
    }
}
