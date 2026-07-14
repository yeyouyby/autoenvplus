using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Installation;

public sealed record ArchiveInstallPlan(
    RuntimePackageAsset Asset,
    string ManagedRoot,
    string DestinationRoot,
    string ExpectedExecutableRelativePath,
    long MaximumDownloadBytes = 1_073_741_824,
    int MaximumArchiveEntries = 100_000,
    long MaximumUncompressedBytes = 4_294_967_296);

public enum InstallOutcome
{
    Installed,
    AlreadyInstalled,
    Failed,
}

public sealed record InstallResult(
    InstallOutcome Outcome,
    string? InstallRoot,
    string? Error)
{
    public bool Success => Outcome is InstallOutcome.Installed or InstallOutcome.AlreadyInstalled;
}

public sealed record InstallProgress(
    string Stage,
    long? CompletedBytes = null,
    long? TotalBytes = null);
