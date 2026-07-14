using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Toolchains;

public sealed record VisualCppInstallation(
    string InstanceId,
    string DisplayName,
    string InstallationPath,
    string VisualStudioVersion,
    string? MsvcToolsVersion,
    string? ActivationScript,
    bool IsComplete,
    bool IsLaunchable,
    IReadOnlyList<CppArchitecturePair>? AvailableArchitecturePairs = null);

public sealed record CppArchitecturePair(
    RuntimeArchitecture HostArchitecture,
    RuntimeArchitecture TargetArchitecture,
    string VcVarsArgument);

public sealed record WindowsSdkInstallation(
    RuntimeVersion Version,
    string RootPath,
    IReadOnlyList<RuntimeArchitecture> Architectures);

public sealed record CppToolchainDiscoveryResult(
    IReadOnlyList<VisualCppInstallation> VisualStudioInstallations,
    IReadOnlyList<WindowsSdkInstallation> WindowsSdks,
    IReadOnlyList<DiscoveredRuntime> BuildTools,
    IReadOnlyList<string> Errors);

public sealed record CppActivationPlan(
    string DisplayName,
    RuntimeArchitecture HostArchitecture,
    RuntimeArchitecture TargetArchitecture,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
