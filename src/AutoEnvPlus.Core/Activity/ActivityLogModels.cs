namespace AutoEnvPlus.Core.Activity;

public enum ActivityOperationType
{
    RuntimeInstall,
    RuntimeUninstall,
    RuntimeSwitch,
    PathChange,
    PathRollback,
    CacheMigration,
    CacheRollback,
    CacheCleanup,
    ToolchainInstall,
    PowerShellIntegration,
    ProjectImport,
    DiagnosticExport,
    CMakePreset,
    SettingsChange,
    PackageDownload,
    PackageImport,
    PackageInstall,
    ProviderPluginImport,
    ProviderPluginStateChange,
    ProviderPluginDelete,
    Other,
}

public enum ActivityStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record ActivityLogEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
    ActivityOperationType OperationType,
    ActivityStatus Status,
    string Summary,
    IReadOnlyList<string> AffectedPaths,
    string? SnapshotPath,
    string? RollbackPath);

public sealed record ActivityLogLoadResult(
    IReadOnlyList<ActivityLogEntry> Entries,
    IReadOnlyList<string> Errors);
