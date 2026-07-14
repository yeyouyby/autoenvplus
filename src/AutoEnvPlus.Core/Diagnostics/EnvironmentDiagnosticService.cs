using System.Runtime.InteropServices;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record DiagnosticIssue(
    string Id,
    DiagnosticSeverity Severity,
    string Title,
    string Detail,
    string? Path = null);

public sealed record DiagnosticCommandStatus(
    string Command,
    string? WinnerPath,
    int CandidateCount,
    bool HasConflict);

public sealed record DiagnosticRuntimeStatus(
    RuntimeKind Kind,
    string Command,
    string ExecutablePath,
    RuntimeVersion? Version,
    bool IsHealthy,
    string? Error);

public sealed record DiagnosticGlobalSelection(
    RuntimeKind Kind,
    string Selector,
    string? RuntimeId,
    RuntimeVersion? Version,
    RuntimeArchitecture? Architecture,
    bool Success,
    string? Error);

public sealed record EnvironmentDiagnosticReport(
    DateTimeOffset CreatedAtUtc,
    PathInspectionReport Path,
    IReadOnlyList<DiagnosticIssue> Issues,
    IReadOnlyList<DiagnosticCommandStatus> Commands,
    IReadOnlyList<DiagnosticRuntimeStatus> Runtimes,
    IReadOnlyList<DiagnosticGlobalSelection> GlobalSelections,
    int ManagedRuntimeCount)
{
    public int ErrorCount => Issues.Count(issue => issue.Severity == DiagnosticSeverity.Error);

    public int WarningCount => Issues.Count(issue => issue.Severity == DiagnosticSeverity.Warning);

    public bool IsHealthy => ErrorCount == 0 && WarningCount == 0;
}

public sealed class EnvironmentDiagnosticService
{
    private static readonly string[] Commands =
    [
        "python",
        "node",
        "npm",
        "java",
        "javac",
        "dotnet",
        "cl",
        "clang",
        "cmake",
        "ninja",
    ];

    private readonly string _managedRoot;

    public EnvironmentDiagnosticService(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
    }

    public async Task<EnvironmentDiagnosticReport> InspectCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        PathInspectionReport path = new PathInspector().InspectCurrent(Commands);
        IReadOnlyList<DiscoveredRuntime> runtimes = await new RuntimeDiscoveryService()
            .DiscoverAsync(path, cancellationToken).ConfigureAwait(false);
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(_managedRoot)
            .LoadAsync(cancellationToken).ConfigureAwait(false);
        RuntimeProfile global;
        try
        {
            global = await new GlobalRuntimeProfileStore(_managedRoot)
                .LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            global = RuntimeProfile.Empty;
            registry = new RegistryLoadResult(
                registry.Entries,
                registry.Errors.Append(
                    $"Unable to load the global runtime profile: {exception.Message}").ToArray());
        }

        return Analyze(
            path,
            runtimes,
            registry,
            global,
            CurrentArchitecture(),
            DateTimeOffset.UtcNow);
    }

    public EnvironmentDiagnosticReport Analyze(
        PathInspectionReport path,
        IReadOnlyList<DiscoveredRuntime> runtimes,
        RegistryLoadResult registry,
        RuntimeProfile globalProfile,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(globalProfile);
        List<DiagnosticIssue> issues = [];

        foreach (PathInspectionEntry entry in path.Entries.Where(entry => !entry.Exists))
        {
            issues.Add(new DiagnosticIssue(
                $"path-missing-{entry.Index}",
                DiagnosticSeverity.Warning,
                "PATH 目录不存在",
                $"进程 PATH 第 {entry.Index + 1} 项无法访问。",
                entry.ExpandedValue));
        }

        foreach (PathInspectionEntry entry in path.Entries.Where(entry => entry.IsDuplicate))
        {
            issues.Add(new DiagnosticIssue(
                $"path-duplicate-{entry.Index}",
                DiagnosticSeverity.Warning,
                "PATH 包含重复目录",
                $"第 {entry.Index + 1} 项与第 {(entry.FirstOccurrenceIndex ?? 0) + 1} 项重复。",
                entry.ExpandedValue));
        }

        foreach (CommandConflict conflict in path.Conflicts)
        {
            issues.Add(new DiagnosticIssue(
                $"command-conflict-{conflict.Command.ToLowerInvariant()}",
                DiagnosticSeverity.Warning,
                $"{conflict.Command} 存在命令遮蔽",
                $"将使用 {conflict.Candidates[0].ExecutablePath}；PATH 中还有 {conflict.Candidates.Count - 1} 个同名候选。",
                conflict.Candidates[0].ExecutablePath));
        }

        foreach (DiscoveredRuntime runtime in runtimes.Where(runtime => !runtime.IsHealthy))
        {
            issues.Add(new DiagnosticIssue(
                $"runtime-unhealthy-{runtime.Command}-{runtime.ExecutablePath}",
                DiagnosticSeverity.Error,
                $"{runtime.Command} 版本探测失败",
                runtime.Error ?? "运行时入口未返回可识别的版本。",
                runtime.ExecutablePath));
        }

        foreach (string error in registry.Errors)
        {
            issues.Add(new DiagnosticIssue(
                $"state-error-{issues.Count}",
                DiagnosticSeverity.Error,
                "AutoEnvPlus 状态无法读取",
                error));
        }

        foreach (ManagedRuntimeEntry entry in registry.Entries.Where(entry => !File.Exists(entry.ExecutablePath)))
        {
            issues.Add(new DiagnosticIssue(
                $"managed-missing-{entry.Id}",
                DiagnosticSeverity.Error,
                "托管运行时文件缺失",
                $"{entry.Id} 仍在注册表中，但入口文件不存在。",
                entry.ExecutablePath));
        }

        List<DiagnosticGlobalSelection> selections = [];
        foreach ((RuntimeKind kind, VersionSelector selector) in globalProfile.Selections)
        {
            RuntimeResolutionResult resolved = new RuntimeResolver().Resolve(
                kind,
                new RuntimeResolutionContext(Global: globalProfile),
                registry.Entries.Select(entry => entry.ToRuntimeInstallation()),
                architecture);
            selections.Add(new DiagnosticGlobalSelection(
                kind,
                selector.ToString(),
                resolved.Installation?.Id,
                resolved.Installation?.Version,
                resolved.Installation?.Architecture,
                resolved.Success,
                resolved.Error));
            if (!resolved.Success)
            {
                issues.Add(new DiagnosticIssue(
                    $"global-unresolved-{kind}",
                    DiagnosticSeverity.Error,
                    $"{kind} 全局选择无法解析",
                    $"选择器 {selector}：{resolved.Error}"));
            }
        }

        IReadOnlyList<DiagnosticCommandStatus> commands = path.CommandResolutions
            .Select(resolution => new DiagnosticCommandStatus(
                resolution.Command,
                resolution.Winner?.ExecutablePath,
                resolution.Candidates.Count,
                resolution.Candidates.Count > 1))
            .ToArray();
        IReadOnlyList<DiagnosticRuntimeStatus> runtimeStatuses = runtimes
            .Select(runtime => new DiagnosticRuntimeStatus(
                runtime.Kind,
                runtime.Command,
                runtime.ExecutablePath,
                runtime.Version,
                runtime.IsHealthy,
                runtime.Error))
            .OrderBy(runtime => runtime.Kind)
            .ThenBy(runtime => runtime.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new EnvironmentDiagnosticReport(
            createdAtUtc ?? DateTimeOffset.UtcNow,
            path,
            issues,
            commands,
            runtimeStatuses,
            selections,
            registry.Entries.Count);
    }

    private static RuntimeArchitecture CurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => RuntimeArchitecture.X86,
            Architecture.Arm64 => RuntimeArchitecture.Arm64,
            _ => RuntimeArchitecture.X64,
        };
}
