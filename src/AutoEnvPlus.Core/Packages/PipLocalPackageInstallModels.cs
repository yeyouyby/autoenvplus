using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Packages;

public enum PipPackageSourceMode
{
    OfflineManagedLibrary,
    ConfiguredNetwork,
}

public enum PipLocalPackageInstallStage
{
    Validating,
    CreatingEnvironment,
    InstallingPackage,
    Completed,
    Cancelled,
}

public enum PipLocalPackageInstallStageStatus
{
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public enum PipLocalPackageRollbackBehavior
{
    NotTransactional,
}

public sealed record PipLocalPackageInstallRequest(
    ManagedRuntimeEntry Runtime,
    string WheelPath,
    string EnvironmentName,
    PipPackageSourceMode SourceMode = PipPackageSourceMode.OfflineManagedLibrary,
    EffectiveNetworkSettings? NetworkSettings = null);

public sealed record PipLocalPackageFileSnapshot(
    string Path,
    long Length,
    long LastWriteTimeUtcTicks,
    string Sha256);

public sealed record PipLocalPackageInstallCommand(
    string ExecutablePath,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

public sealed record PipLocalPackageInstallPlan(
    string ManagedRoot,
    string LibraryRoot,
    string EnvironmentName,
    string EnvironmentRoot,
    string WheelPath,
    PipPackageSourceMode SourceMode,
    ManagedRuntimeEntry Runtime,
    EffectiveNetworkSettings? NetworkSettings,
    bool RequiresEnvironmentCreation,
    PipLocalPackageFileSnapshot RuntimeExecutableSnapshot,
    PipLocalPackageFileSnapshot WheelSnapshot,
    PipLocalPackageFileSnapshot? EnvironmentExecutableSnapshot,
    PipLocalPackageInstallCommand CreateEnvironmentCommand,
    PipLocalPackageInstallCommand InstallPackageCommand,
    string IntegritySha256);

public sealed record PipLocalPackageInstallProgress(
    PipLocalPackageInstallStage Stage,
    string Message);

public sealed record PipLocalPackageProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? StartError = null,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false)
{
    public bool Success => ExitCode == 0 && StartError is null;
}

public sealed record PipLocalPackageInstallStageResult(
    PipLocalPackageInstallStage Stage,
    PipLocalPackageInstallStageStatus Status,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null,
    string? Error = null,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false);

public sealed record PipLocalPackageInstallResult(
    bool Success,
    bool EnvironmentCreated,
    bool PackageInstalled,
    PipLocalPackageInstallStage FinalStage,
    IReadOnlyList<PipLocalPackageInstallStageResult> Stages,
    PipLocalPackageRollbackBehavior RollbackBehavior,
    string? Error)
{
    public bool WasCancelled => FinalStage == PipLocalPackageInstallStage.Cancelled;

    public bool StandardOutputTruncated => Stages.Any(stage =>
        stage.StandardOutputTruncated);

    public bool StandardErrorTruncated => Stages.Any(stage =>
        stage.StandardErrorTruncated);
}

public interface IPipLocalPackageProcessRunner
{
    Task<PipLocalPackageProcessResult> RunAsync(
        PipLocalPackageInstallCommand command,
        CancellationToken cancellationToken = default);
}
