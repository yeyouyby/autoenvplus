using System.Runtime.InteropServices;
using System.Security;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Storage;

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
    string? Path = null,
    DiagnosticScanScope Scope = DiagnosticScanScope.PathAndCommands);

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
    int ManagedRuntimeCount,
    DiagnosticScanScope CompletedScopes = DiagnosticScanScope.None)
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
        "python3",
        "pip",
        "pip3",
        "node",
        "npm",
        "npx",
        "java",
        "javac",
        "jar",
        "dotnet",
        "cl",
        "clang",
        "clang++",
        "gcc",
        "g++",
        "cmake",
        "ninja",
    ];

    private static readonly string[] ShimCommands =
    [
        "python",
        "python3",
        "pip",
        "pip3",
        "node",
        "npm",
        "npx",
        "java",
        "javac",
        "jar",
        "dotnet",
        "cl",
        "clang",
        "clang++",
        "gcc",
        "g++",
        "cmake",
        "ninja",
    ];

    private readonly string _managedRoot;

    public EnvironmentDiagnosticService(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
    }

    public Task<EnvironmentDiagnosticReport> InspectCurrentAsync(
        CancellationToken cancellationToken = default) =>
        InspectCurrentAsync(new EnvironmentDiagnosticOptions(), cancellationToken);

    public async Task<EnvironmentDiagnosticReport> InspectCurrentAsync(
        EnvironmentDiagnosticOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        bool inspectPath = options.Scopes.HasFlag(DiagnosticScanScope.PathAndCommands);
        bool inspectManaged = options.Scopes.HasFlag(DiagnosticScanScope.ManagedTools);
        bool inspectProject = options.Scopes.HasFlag(DiagnosticScanScope.ProjectEnvironment);
        bool needsRegistry = inspectManaged || inspectProject;

        PathInspectionReport path = inspectPath
            ? new PathInspector().InspectCurrent(Commands)
            : new PathInspectionReport([], []);
        IReadOnlyList<DiscoveredRuntime> runtimes = inspectPath
            ? await new RuntimeDiscoveryService()
                .DiscoverAsync(path, cancellationToken)
                .ConfigureAwait(false)
            : [];

        RegistryLoadResult registry = needsRegistry
            ? await new ManagedRuntimeRegistry(_managedRoot)
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false)
            : new RegistryLoadResult([], []);
        RuntimeProfile global = RuntimeProfile.Empty;
        if (inspectManaged)
        {
            try
            {
                global = await new GlobalRuntimeProfileStore(_managedRoot)
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                registry = new RegistryLoadResult(
                    registry.Entries,
                    registry.Errors.Append(
                        $"Unable to load the global runtime profile: {exception.Message}").ToArray());
            }
        }

        EnvironmentDiagnosticReport report = Analyze(
            path,
            runtimes,
            registry,
            global,
            CurrentArchitecture(),
            DateTimeOffset.UtcNow,
            options.Scopes);
        List<DiagnosticIssue> issues = [.. report.Issues];

        if (inspectManaged)
        {
            await InspectLanguageInventoryAsync(issues, cancellationToken).ConfigureAwait(false);
        }

        ProviderDiagnosticContext? providerContext = null;
        if (options.Scopes.HasFlag(DiagnosticScanScope.ProviderConfiguration)
            || options.Scopes.HasFlag(DiagnosticScanScope.ProviderConnectivity))
        {
            providerContext = await InspectProviderConfigurationAsync(
                issues,
                options.Scopes.HasFlag(DiagnosticScanScope.ProviderConfiguration)
                    ? DiagnosticScanScope.ProviderConfiguration
                    : DiagnosticScanScope.ProviderConnectivity,
                cancellationToken).ConfigureAwait(false);
        }

        if (inspectProject)
        {
            InspectProjectEnvironment(
                options.ProjectRoot!,
                registry,
                issues,
                cancellationToken);
        }

        if (options.Scopes.HasFlag(DiagnosticScanScope.StoragePressure))
        {
            await InspectStoragePressureAsync(options, issues, cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.Scopes.HasFlag(DiagnosticScanScope.ProviderConnectivity)
            && providerContext is not null)
        {
            IReadOnlyList<DiagnosticIssue> connectivityIssues =
                await InspectProviderConnectivityAsync(
                    providerContext,
                    options,
                    cancellationToken).ConfigureAwait(false);
            issues.AddRange(connectivityIssues);
        }
        else if (options.Scopes.HasFlag(DiagnosticScanScope.ProviderConnectivity))
        {
            issues.Add(new DiagnosticIssue(
                "provider-connectivity-skipped",
                DiagnosticSeverity.Warning,
                "实时连接检查未执行",
                "代理或兼容网络设置无法解析，因此没有发起 Provider/镜像请求。",
                Scope: DiagnosticScanScope.ProviderConnectivity));
        }

        return report with
        {
            Issues = issues,
            CompletedScopes = options.Scopes,
        };
    }

    public EnvironmentDiagnosticReport Analyze(
        PathInspectionReport path,
        IReadOnlyList<DiscoveredRuntime> runtimes,
        RegistryLoadResult registry,
        RuntimeProfile globalProfile,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        DateTimeOffset? createdAtUtc = null,
        DiagnosticScanScope scopes = DiagnosticScanScope.PathAndCommands
            | DiagnosticScanScope.ManagedTools)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(globalProfile);
        List<DiagnosticIssue> issues = [];

        if (scopes.HasFlag(DiagnosticScanScope.PathAndCommands))
        {
            AnalyzePath(path, runtimes, issues);
            AnalyzeShim(path, issues);
        }

        List<DiagnosticGlobalSelection> selections = [];
        if (scopes.HasFlag(DiagnosticScanScope.ManagedTools))
        {
            AnalyzeManagedState(
                path,
                runtimes,
                registry,
                globalProfile,
                architecture,
                issues,
                selections,
                scopes.HasFlag(DiagnosticScanScope.PathAndCommands));
        }

        IReadOnlyList<DiagnosticCommandStatus> commands =
            scopes.HasFlag(DiagnosticScanScope.PathAndCommands)
                ? path.CommandResolutions
                    .Select(resolution => new DiagnosticCommandStatus(
                        resolution.Command,
                        resolution.Winner?.ExecutablePath,
                        resolution.Candidates.Count,
                        resolution.Candidates.Count > 1))
                    .ToArray()
                : [];
        IReadOnlyList<DiagnosticRuntimeStatus> runtimeStatuses =
            scopes.HasFlag(DiagnosticScanScope.PathAndCommands)
                ? runtimes
                    .Select(runtime => new DiagnosticRuntimeStatus(
                        runtime.Kind,
                        runtime.Command,
                        runtime.ExecutablePath,
                        runtime.Version,
                        runtime.IsHealthy,
                        runtime.Error))
                    .OrderBy(runtime => runtime.Kind)
                    .ThenBy(runtime => runtime.Command, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

        return new EnvironmentDiagnosticReport(
            createdAtUtc ?? DateTimeOffset.UtcNow,
            path,
            issues,
            commands,
            runtimeStatuses,
            selections,
            registry.Entries.Count,
            scopes);
    }

    private static void AnalyzePath(
        PathInspectionReport path,
        IReadOnlyList<DiscoveredRuntime> runtimes,
        ICollection<DiagnosticIssue> issues)
    {
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
                $"runtime-unhealthy-{runtime.Command}-{StableId(runtime.ExecutablePath)}",
                DiagnosticSeverity.Error,
                $"{runtime.Command} 版本探测失败",
                runtime.Error ?? "语言工具入口未返回可识别的版本。",
                runtime.ExecutablePath));
        }

        foreach (IGrouping<RuntimeKind, DiscoveredRuntime> group in runtimes
            .Where(runtime => runtime.IsHealthy)
            .GroupBy(runtime => runtime.Kind))
        {
            DiscoveredRuntime[] candidates = group
                .DistinctBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length < 2)
            {
                continue;
            }

            string[] versions = candidates
                .Select(runtime => runtime.Version!.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            issues.Add(new DiagnosticIssue(
                versions.Length > 1
                    ? $"runtime-version-drift-{group.Key}"
                    : $"runtime-duplicate-{group.Key}",
                DiagnosticSeverity.Warning,
                versions.Length > 1
                    ? $"{group.Key} 存在多个可执行版本"
                    : $"{group.Key} 存在重复语言工具",
                versions.Length > 1
                    ? $"PATH 中探测到 {candidates.Length} 个入口，版本为 {string.Join("、", versions)}。"
                    : $"PATH 中有 {candidates.Length} 个入口返回相同版本 {versions[0]}。",
                candidates[0].ExecutablePath));
        }
    }

    private void AnalyzeShim(
        PathInspectionReport path,
        ICollection<DiagnosticIssue> issues)
    {
        string shimDirectory = Path.Combine(_managedRoot, "shims");
        bool directoryExists = Directory.Exists(shimDirectory);
        string? userPath = System.Environment.GetEnvironmentVariable(
            "PATH",
            EnvironmentVariableTarget.User);
        string[] userEntries = SplitPath(userPath).ToArray();
        int persistedIndex = Array.FindIndex(
            userEntries,
            entry => PathsEqual(entry, shimDirectory));
        bool persisted = persistedIndex >= 0;
        if (!directoryExists && !persisted)
        {
            return;
        }

        if (!directoryExists)
        {
            issues.Add(new DiagnosticIssue(
                "shim-directory-missing",
                DiagnosticSeverity.Error,
                "AutoEnvPlus Shim 目录失效",
                "用户 PATH 仍引用 AutoEnvPlus Shim，但受管目录不存在。",
                shimDirectory));
            return;
        }

        List<string> missing = [];
        List<string> ambiguous = [];
        List<string> empty = [];
        foreach (string command in ShimCommands)
        {
            string executable = Path.Combine(shimDirectory, command + ".exe");
            string script = Path.Combine(shimDirectory, command + ".cmd");
            bool hasExecutable = File.Exists(executable);
            bool hasScript = File.Exists(script);
            if (!hasExecutable && !hasScript)
            {
                missing.Add(command);
                continue;
            }

            if (hasExecutable && hasScript)
            {
                ambiguous.Add(command);
            }

            foreach (string candidate in new[] { executable, script }.Where(File.Exists))
            {
                try
                {
                    if (new FileInfo(candidate).Length == 0)
                    {
                        empty.Add(command);
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                    empty.Add(command);
                }
            }
        }

        if (missing.Count > 0)
        {
            issues.Add(new DiagnosticIssue(
                "shim-aliases-missing",
                DiagnosticSeverity.Error,
                "AutoEnvPlus Shim 不完整",
                $"缺少命令：{string.Join("、", missing)}。",
                shimDirectory));
        }

        if (ambiguous.Count > 0)
        {
            issues.Add(new DiagnosticIssue(
                "shim-aliases-ambiguous",
                DiagnosticSeverity.Warning,
                "AutoEnvPlus Shim 存在旧实现残留",
                $"以下命令同时存在 .exe 与 .cmd：{string.Join("、", ambiguous)}。",
                shimDirectory));
        }

        if (empty.Count > 0)
        {
            issues.Add(new DiagnosticIssue(
                "shim-aliases-empty",
                DiagnosticSeverity.Error,
                "AutoEnvPlus Shim 文件无效",
                $"以下命令的 Shim 为空或无法读取：{string.Join("、", empty.Distinct(StringComparer.OrdinalIgnoreCase))}。",
                shimDirectory));
        }

        if (!persisted)
        {
            issues.Add(new DiagnosticIssue(
                "shim-not-in-user-path",
                DiagnosticSeverity.Warning,
                "Shim 尚未写入用户 PATH",
                "受管 Shim 已存在，但持久用户 PATH 不包含该目录。",
                shimDirectory));
            return;
        }

        if (persistedIndex > 0)
        {
            issues.Add(new DiagnosticIssue(
                "shim-not-first-in-user-path",
                DiagnosticSeverity.Warning,
                "Shim 不是用户 PATH 第一项",
                $"Shim 位于持久用户 PATH 第 {persistedIndex + 1} 项，前面的同名命令可能绕过版本选择。",
                shimDirectory));
        }

        int currentIndex = path.Entries
            .Select((entry, index) => (entry, index))
            .FirstOrDefault(item => PathsEqual(item.entry.ExpandedValue, shimDirectory))
            .index;
        bool inCurrentProcess = path.Entries.Any(entry =>
            PathsEqual(entry.ExpandedValue, shimDirectory));
        if (!inCurrentProcess)
        {
            issues.Add(new DiagnosticIssue(
                "shim-not-in-current-process",
                DiagnosticSeverity.Information,
                "当前进程尚未加载 Shim PATH",
                "持久用户 PATH 已配置；新终端会读取该设置，当前进程仍保留旧 PATH。",
                shimDirectory));
            return;
        }

        if (currentIndex > 0)
        {
            issues.Add(new DiagnosticIssue(
                "shim-shadowed-in-process-path",
                DiagnosticSeverity.Warning,
                "当前进程中 Shim 排序靠后",
                $"Shim 位于当前进程 PATH 第 {currentIndex + 1} 项。",
                shimDirectory));
        }

        string[] bypassed = path.CommandResolutions
            .Where(resolution => ShimCommands.Contains(
                resolution.Command,
                StringComparer.OrdinalIgnoreCase))
            .Where(resolution => resolution.Winner is not null)
            .Where(resolution => !PathsEqual(
                Path.GetDirectoryName(resolution.Winner!.ExecutablePath),
                shimDirectory))
            .Select(resolution => resolution.Command)
            .ToArray();
        if (bypassed.Length > 0)
        {
            issues.Add(new DiagnosticIssue(
                "shim-commands-bypassed",
                DiagnosticSeverity.Warning,
                "部分命令绕过 AutoEnvPlus Shim",
                $"当前解析未经过 Shim：{string.Join("、", bypassed)}。",
                shimDirectory));
        }
    }

    private void AnalyzeManagedState(
        PathInspectionReport path,
        IReadOnlyList<DiscoveredRuntime> runtimes,
        RegistryLoadResult registry,
        RuntimeProfile globalProfile,
        RuntimeArchitecture architecture,
        ICollection<DiagnosticIssue> issues,
        ICollection<DiagnosticGlobalSelection> selections,
        bool pathWasScanned)
    {
        foreach (string error in registry.Errors)
        {
            issues.Add(new DiagnosticIssue(
                $"state-error-{StableId(error)}",
                DiagnosticSeverity.Error,
                "AutoEnvPlus 状态无法读取",
                error,
                Scope: DiagnosticScanScope.ManagedTools));
        }

        foreach (ManagedRuntimeEntry entry in registry.Entries.Where(entry =>
            !File.Exists(entry.ExecutablePath)))
        {
            issues.Add(new DiagnosticIssue(
                $"managed-missing-{entry.Id}",
                DiagnosticSeverity.Error,
                "托管语言工具入口缺失",
                $"{entry.Id} 仍在注册表中，但入口文件不存在。",
                entry.ExecutablePath,
                DiagnosticScanScope.ManagedTools));
        }

        foreach (IGrouping<string, ManagedRuntimeEntry> duplicates in registry.Entries
            .GroupBy(
                entry => $"{entry.Kind}|{entry.Architecture}|{entry.Version}",
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            ManagedRuntimeEntry first = duplicates.First();
            issues.Add(new DiagnosticIssue(
                $"managed-duplicate-{first.Kind}-{first.Architecture}-{StableId(first.Version.ToString())}",
                DiagnosticSeverity.Warning,
                "托管语言工具记录重复",
                $"{first.Kind} {first.Version} {first.Architecture} 存在 {duplicates.Count()} 条记录。",
                first.InstallRoot,
                DiagnosticScanScope.ManagedTools));
        }

        foreach ((RuntimeKind kind, VersionSelector selector) in globalProfile.Selections)
        {
            ManagedRuntimeResolutionResult managedResolution =
                ManagedRuntimeResolutionService.ResolveRegistered(
                kind,
                new RuntimeResolutionContext(Global: globalProfile),
                registry.Entries,
                architecture);
            RuntimeResolutionResult resolved = managedResolution.Resolution!;
            selections.Add(new DiagnosticGlobalSelection(
                kind,
                selector.ToString(),
                managedResolution.Entry?.Id,
                managedResolution.Entry?.Version,
                managedResolution.Entry?.Architecture,
                managedResolution.Success,
                managedResolution.Errors.FirstOrDefault() ?? resolved.Error));
            if (!managedResolution.Success)
            {
                bool exact = globalProfile.ExactSelections.ContainsKey(kind);
                issues.Add(new DiagnosticIssue(
                    exact
                        ? $"global-exact-unresolved-{kind}"
                        : $"global-unresolved-{kind}",
                    DiagnosticSeverity.Error,
                    exact
                        ? $"{kind} 精确全局身份失效"
                        : $"{kind} 全局选择无法解析",
                    $"选择器 {selector}：{string.Join("；", managedResolution.Errors)}",
                    Scope: DiagnosticScanScope.ManagedTools));
                continue;
            }

            RuntimeInstallation installation = managedResolution.Entry!.ToRuntimeInstallation();

            if (!pathWasScanned
                || !TryGetPrimaryCommand(kind, out string? command)
                || path.CommandResolutions.FirstOrDefault(item =>
                    item.Command.Equals(command, StringComparison.OrdinalIgnoreCase))
                    ?.Winner is not CommandCandidate winner)
            {
                continue;
            }

            ManagedRuntimeEntry? selectedEntry = registry.Entries.FirstOrDefault(entry =>
                entry.Id.Equals(
                    installation.Id,
                    StringComparison.OrdinalIgnoreCase));
            string shimDirectory = Path.Combine(_managedRoot, "shims");
            bool winnerIsShim = PathsEqual(
                Path.GetDirectoryName(winner.ExecutablePath),
                shimDirectory);
            bool winnerIsSelected = selectedEntry is not null
                && PathsEqual(winner.ExecutablePath, selectedEntry.ExecutablePath);
            DiscoveredRuntime? winnerProbe = runtimes.FirstOrDefault(runtime =>
                runtime.Kind == kind
                && PathsEqual(runtime.ExecutablePath, winner.ExecutablePath));
            bool versionMatches = winnerProbe?.Version is null
                || winnerProbe.Version.CompareTo(installation.Version) == 0;
            if (!winnerIsShim && !winnerIsSelected)
            {
                issues.Add(new DiagnosticIssue(
                    $"global-bypassed-{kind}",
                    DiagnosticSeverity.Warning,
                    $"{kind} 全局选择被当前 PATH 绕过",
                    $"全局选择解析为 {installation.Version}，当前命令来自 {winner.ExecutablePath}。",
                    winner.ExecutablePath,
                    DiagnosticScanScope.ManagedTools));
            }
            else if (!versionMatches)
            {
                issues.Add(new DiagnosticIssue(
                    $"global-version-drift-{kind}",
                    DiagnosticSeverity.Warning,
                    $"{kind} 命令版本与全局选择不一致",
                    $"全局选择为 {installation.Version}，当前入口报告 {winnerProbe!.Version}。",
                    winner.ExecutablePath,
                    DiagnosticScanScope.ManagedTools));
            }
        }
    }

    private async Task InspectLanguageInventoryAsync(
        ICollection<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        LanguageCatalog catalog;
        try
        {
            catalog = await new LanguagePackStore(_managedRoot)
                .GetEffectiveCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LanguagePackException exception)
        {
            issues.Add(new DiagnosticIssue(
                $"inventory-language-pack-{exception.Code}",
                DiagnosticSeverity.Error,
                "语言工具库存无法投影到有效目录",
                exception.Message,
                Scope: DiagnosticScanScope.ManagedTools));
            return;
        }

        LanguageToolInventoryStore store = new(_managedRoot);
        LanguageToolInventorySnapshot? snapshot;
        try
        {
            snapshot = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            issues.Add(new DiagnosticIssue(
                "inventory-invalid",
                DiagnosticSeverity.Error,
                "语言工具库存快照无效",
                $"快照无法安全读取（{exception.GetType().Name}）；只有显式重新扫描才会重建它。",
                store.SnapshotPath,
                DiagnosticScanScope.ManagedTools));
            return;
        }

        if (snapshot is null)
        {
            issues.Add(new DiagnosticIssue(
                "inventory-missing",
                DiagnosticSeverity.Information,
                "尚未建立语言工具库存快照",
                "打开首页或语言页不会触发扫描；请在语言页显式扫描需要的语言。",
                store.SnapshotPath,
                DiagnosticScanScope.ManagedTools));
            return;
        }

        if (!snapshot.IsCompatibleWith(catalog))
        {
            issues.Add(new DiagnosticIssue(
                "inventory-catalog-drift",
                DiagnosticSeverity.Warning,
                "语言目录已变化，库存投影已过期",
                "目录指纹与扫描时不同；旧证据仍可只读显示，但应按语言显式刷新。",
                store.SnapshotPath,
                DiagnosticScanScope.ManagedTools));
        }

        DateTimeOffset staleBefore = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(30));
        LanguageToolInventoryEntry[] stale = snapshot.Tools
            .Where(entry => entry.ScannedAtUtc < staleBefore)
            .OrderBy(entry => entry.ScannedAtUtc)
            .ToArray();
        if (stale.Length > 0)
        {
            issues.Add(new DiagnosticIssue(
                "inventory-stale",
                DiagnosticSeverity.Warning,
                "部分语言工具库存长期未刷新",
                $"{stale.Length} 个工具超过 30 天未显式扫描；最早为 {stale[0].ScannedAtUtc.LocalDateTime:g}。",
                store.SnapshotPath,
                DiagnosticScanScope.ManagedTools));
        }

        int unknownToolCount = snapshot.Tools.Count(entry =>
            !catalog.TryGetTool(entry.ToolId, out _));
        if (unknownToolCount > 0)
        {
            issues.Add(new DiagnosticIssue(
                "inventory-unknown-tools",
                DiagnosticSeverity.Warning,
                "库存包含目录中已不存在的工具",
                $"{unknownToolCount} 条旧工具记录不会参与当前投影；重新扫描会清理可淘汰证据。",
                store.SnapshotPath,
                DiagnosticScanScope.ManagedTools));
        }
    }

    private async Task<ProviderDiagnosticContext?> InspectProviderConfigurationAsync(
        ICollection<DiagnosticIssue> issues,
        DiagnosticScanScope issueScope,
        CancellationToken cancellationToken)
    {
        bool inventoryComplete = true;
        LanguageCatalog catalog;
        try
        {
            catalog = await new LanguagePackStore(_managedRoot)
                .GetEffectiveCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LanguagePackException exception)
        {
            inventoryComplete = false;
            issues.Add(new DiagnosticIssue(
                $"language-pack-{exception.Code}",
                DiagnosticSeverity.Error,
                "有效语言目录无法加载",
                exception.Message,
                Scope: issueScope));
            catalog = BuiltInLanguageCatalog.Current;
        }

        foreach (LanguageToolDefinition tool in catalog.Tools)
        {
            foreach (LanguageToolProviderDefinition provider in tool.Providers.Where(provider =>
                provider.ManagedInstallSupported))
            {
                LanguageToolProviderProfile profile = LanguageToolProviderProfile.Create(
                    tool,
                    provider);
                if (!profile.Capabilities.ManagedInstall)
                {
                    inventoryComplete = false;
                    issues.Add(new DiagnosticIssue(
                        $"provider-adapter-missing-{StableId(profile.Identity.ScopedId)}",
                        DiagnosticSeverity.Error,
                        "Provider 声明可安装但没有执行适配器",
                        $"{profile.Identity.ScopedId} 已降级为目录元数据；不会显示虚假安装入口。",
                        Scope: issueScope));
                }
            }
        }

        ProviderSourcePreferences sourcePreferences;
        try
        {
            sourcePreferences = await new ProviderSourcePreferenceStore(_managedRoot)
                .LoadValidatedAsync(catalog, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderSourcePreferenceException exception)
        {
            inventoryComplete = false;
            issues.Add(new DiagnosticIssue(
                $"provider-source-{exception.Error.Code}-{StableId(exception.Error.Path)}",
                DiagnosticSeverity.Error,
                "Provider 来源配置无效",
                $"{exception.Error.Path}：{exception.Error.Message}",
                Scope: issueScope));
            sourcePreferences = ProviderSourcePreferences.Empty;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            inventoryComplete = false;
            issues.Add(new DiagnosticIssue(
                "provider-source-state-io",
                DiagnosticSeverity.Error,
                "Provider 来源配置无法读取",
                $"读取失败：{exception.GetType().Name}。",
                Scope: issueScope));
            sourcePreferences = ProviderSourcePreferences.Empty;
        }

        NetworkSettingsLoadResult network = await new NetworkSettingsStore(_managedRoot)
            .LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!network.Success || network.Settings is null)
        {
            foreach (NetworkSettingsError error in network.Errors)
            {
                issues.Add(new DiagnosticIssue(
                    $"network-settings-{error.Code}-{StableId(error.Path)}",
                    DiagnosticSeverity.Error,
                    "代理与兼容网络设置无效",
                    $"{error.Path}：{error.Message}",
                    Scope: issueScope));
            }
        }

        RuntimeProviderPluginListResult plugins;
        try
        {
            plugins = await new RuntimeProviderPluginStore(_managedRoot)
                .ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeProviderPluginException exception)
        {
            inventoryComplete = false;
            issues.Add(new DiagnosticIssue(
                $"provider-plugin-{exception.Code}",
                DiagnosticSeverity.Error,
                "Provider 插件状态无效",
                exception.Message,
                Scope: issueScope));
            plugins = new RuntimeProviderPluginListResult([], []);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            inventoryComplete = false;
            issues.Add(new DiagnosticIssue(
                "provider-plugin-state-io",
                DiagnosticSeverity.Error,
                "Provider 插件状态无法读取",
                $"读取失败：{exception.GetType().Name}。",
                Scope: issueScope));
            plugins = new RuntimeProviderPluginListResult([], []);
        }

        foreach (RuntimeProviderPluginError error in plugins.Errors)
        {
            inventoryComplete = false;
            issues.Add(new DiagnosticIssue(
                $"provider-plugin-{error.Code}-{StableId(error.PluginId ?? error.FileName ?? "state")}",
                DiagnosticSeverity.Error,
                "Provider 插件状态无效",
                error.Message,
                error.FileName,
                issueScope));
        }

        foreach (RuntimeProviderPluginDescriptor plugin in plugins.Plugins)
        {
            LanguageToolRuntimeBridgeDefinition? bridge = LanguageToolRuntimeBridge.Definitions
                .FirstOrDefault(definition => definition.ToolId.Equals(
                    plugin.LanguageToolId,
                    StringComparison.OrdinalIgnoreCase));
            bool catalogContainsTool = catalog.TryGetTool(plugin.LanguageToolId, out _);
            if (!catalogContainsTool
                || bridge is null
                || bridge.RuntimeKind != plugin.Manifest.Kind)
            {
                inventoryComplete = false;
                issues.Add(new DiagnosticIssue(
                    $"provider-plugin-identity-drift-{StableId(plugin.Id)}",
                    DiagnosticSeverity.Error,
                    "Provider 插件工具身份漂移",
                    $"{plugin.Manifest.DisplayName} 声明 {plugin.LanguageToolId}/{plugin.Manifest.Kind}，但当前语言目录与受控桥接表不再一致。",
                    plugin.ManifestPath,
                    issueScope));
            }
        }

        RuntimeArchitecture architecture = CurrentArchitecture();
        foreach (RuntimeProviderPluginDescriptor plugin in plugins.Plugins.Where(plugin =>
            plugin.IsEnabled))
        {
            bool supportsCurrentArchitecture = plugin.Manifest.Releases
                .SelectMany(release => release.Assets)
                .Any(asset => asset.Architecture is RuntimeArchitecture.Any
                    || asset.Architecture == architecture);
            if (!supportsCurrentArchitecture)
            {
                issues.Add(new DiagnosticIssue(
                    $"provider-architecture-{plugin.Id}",
                    DiagnosticSeverity.Warning,
                    "已启用 Provider 不支持当前架构",
                    $"{plugin.Manifest.DisplayName} 没有 {architecture} 资产。",
                    plugin.ManifestPath,
                    issueScope));
            }
        }

        return network.Settings is null
            ? null
            : new ProviderDiagnosticContext(
                network.Settings,
                plugins,
                catalog,
                sourcePreferences,
                inventoryComplete);
    }

    private void InspectProjectEnvironment(
        string projectRoot,
        RegistryLoadResult registry,
        ICollection<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(projectRoot);
            if (!Directory.Exists(fullRoot))
            {
                issues.Add(new DiagnosticIssue(
                    "project-root-missing",
                    DiagnosticSeverity.Error,
                    "项目目录不存在",
                    "无法执行项目锁与虚拟环境检查。",
                    fullRoot,
                    DiagnosticScanScope.ProjectEnvironment));
                return;
            }

            ProjectManifestLoadResult? manifest =
                new ProjectManifestService().FindAndLoad(fullRoot);
            if (manifest is null)
            {
                issues.Add(new DiagnosticIssue(
                    "project-manifest-not-found",
                    DiagnosticSeverity.Information,
                    "未找到 AutoEnvPlus 项目锁",
                    "所选目录及其父目录中没有 autoenvplus.toml。",
                    fullRoot,
                    DiagnosticScanScope.ProjectEnvironment));
            }
            else
            {
                foreach (ProjectManifestError error in manifest.Errors)
                {
                    issues.Add(new DiagnosticIssue(
                        $"project-manifest-{error.LineNumber}",
                        DiagnosticSeverity.Error,
                        "项目锁无法解析",
                        $"第 {error.LineNumber} 行：{error.Message}",
                        manifest.Manifest.ManifestPath,
                        DiagnosticScanScope.ProjectEnvironment));
                }

                if (manifest.Success)
                {
                    AnalyzeProjectSelections(manifest.Manifest, registry, issues);
                }
            }

            ProjectVirtualEnvironmentDiscoveryResult environments =
                new ProjectVirtualEnvironmentDiscoveryService().Discover(
                    fullRoot,
                    cancellationToken: cancellationToken);
            foreach (string warning in environments.Warnings)
            {
                issues.Add(new DiagnosticIssue(
                    $"project-scan-{StableId(warning)}",
                    DiagnosticSeverity.Warning,
                    "项目环境扫描未完全覆盖",
                    warning,
                    fullRoot,
                    DiagnosticScanScope.ProjectEnvironment));
            }

            foreach (ProjectVirtualEnvironment environment in environments.Environments
                .Where(environment => environment.Health != ProjectVirtualEnvironmentHealth.Healthy))
            {
                bool isDefinition = environment.Kind.Equals(
                    "environment-definition",
                    StringComparison.OrdinalIgnoreCase);
                bool isOrphanedPythonEnvironment = environment.LanguageId.Equals(
                        "python",
                        StringComparison.OrdinalIgnoreCase)
                    && environment.Executable is null
                    && environment.Kind is "virtual-environment" or "conda-environment";
                issues.Add(new DiagnosticIssue(
                    $"project-environment-{StableId(environment.LanguageId + environment.Kind + environment.Root)}",
                    environment.Health == ProjectVirtualEnvironmentHealth.Invalid
                        ? DiagnosticSeverity.Error
                        : DiagnosticSeverity.Warning,
                    isDefinition
                        ? "虚拟环境已声明但尚未创建"
                        : isOrphanedPythonEnvironment
                            ? "可能孤立或损坏的 Python 虚拟环境"
                            : $"{environment.LanguageId} 项目环境需要检查",
                    environment.Warnings.Count > 0
                        ? string.Join("；", environment.Warnings)
                        : $"{environment.Manager} 报告 {environment.Health}。",
                    environment.Root,
                    DiagnosticScanScope.ProjectEnvironment));
            }

            if (manifest is { Success: true })
            {
                AnalyzeProjectEnvironmentVersions(
                    manifest.Manifest,
                    environments.Environments,
                    issues);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            issues.Add(new DiagnosticIssue(
                "project-inspection-failed",
                DiagnosticSeverity.Error,
                "无法检查项目环境",
                exception.Message,
                projectRoot,
                DiagnosticScanScope.ProjectEnvironment));
        }
    }

    private static void AnalyzeProjectSelections(
        ProjectEnvironmentManifest manifest,
        RegistryLoadResult registry,
        ICollection<DiagnosticIssue> issues)
    {
        foreach ((RuntimeKind kind, VersionSelector selector) in manifest.Tools)
        {
            ManagedRuntimeResolutionResult resolved =
                ManagedRuntimeResolutionService.ResolveRegistered(
                kind,
                new RuntimeResolutionContext(Project: manifest.ToRuntimeProfile()),
                registry.Entries,
                CurrentArchitecture());
            if (!resolved.Success)
            {
                bool exact = manifest.ExactSelections.TryGetValue(
                    kind,
                    out RuntimeSelectionIdentity? identity);
                issues.Add(new DiagnosticIssue(
                    exact
                        ? $"project-exact-unresolved-{kind}"
                        : $"project-lock-unresolved-{kind}",
                    DiagnosticSeverity.Error,
                    exact
                        ? $"{kind} 项目精确身份失效"
                        : $"{kind} 项目锁无法解析",
                    exact
                        ? $"选择器 {selector} 固定到 {identity!.RuntimeId} / {identity.ProviderId}：{string.Join("；", resolved.Errors)}"
                        : $"选择器 {selector} 没有匹配的托管语言工具：{string.Join("；", resolved.Errors)}",
                    manifest.ManifestPath,
                    DiagnosticScanScope.ProjectEnvironment));
            }
        }
    }

    private static void AnalyzeProjectEnvironmentVersions(
        ProjectEnvironmentManifest manifest,
        IEnumerable<ProjectVirtualEnvironment> environments,
        ICollection<DiagnosticIssue> issues)
    {
        if (!manifest.Tools.TryGetValue(RuntimeKind.Python, out VersionSelector? selector)
            || selector.Kind == VersionSelectorKind.Channel)
        {
            return;
        }

        foreach (ProjectVirtualEnvironment environment in environments.Where(environment =>
            environment.LanguageId.Equals("python", StringComparison.OrdinalIgnoreCase)
            && environment.Version is not null
            && environment.Health != ProjectVirtualEnvironmentHealth.Invalid
            && environment.Kind is "virtual-environment" or "conda-environment"))
        {
            if (!RuntimeVersion.TryParse(environment.Version, out RuntimeVersion? version))
            {
                continue;
            }

            RuntimeInstallation candidate = new(
                "project-environment",
                RuntimeKind.Python,
                version!,
                RuntimeArchitecture.Any,
                environment.Root,
                RuntimeOwnership.External,
                []);
            if (!selector.Matches(candidate))
            {
                issues.Add(new DiagnosticIssue(
                    $"project-python-version-drift-{StableId(environment.Root)}",
                    DiagnosticSeverity.Warning,
                    "Python 虚拟环境与项目锁不一致",
                    $"项目锁要求 {selector}，虚拟环境报告 {version}。",
                    environment.Root,
                    DiagnosticScanScope.ProjectEnvironment));
            }
        }
    }

    private async Task InspectStoragePressureAsync(
        EnvironmentDiagnosticOptions options,
        ICollection<DiagnosticIssue> issues,
        CancellationToken cancellationToken)
    {
        if (InspectManagedStoragePlacement(issues))
        {
            InspectPendingManagedOperations(issues);
        }

        IReadOnlyList<CacheDirectoryLocation> locations;
        try
        {
            locations = new CacheDirectoryService().DiscoverCurrent();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            issues.Add(new DiagnosticIssue(
                "cache-discovery-failed",
                DiagnosticSeverity.Error,
                "无法读取缓存位置",
                exception.Message,
                Scope: DiagnosticScanScope.StoragePressure));
            locations = [];
        }

        foreach (CacheDirectoryLocation location in locations.Where(location =>
             !string.IsNullOrWhiteSpace(location.Warning)))
        {
            issues.Add(new DiagnosticIssue(
                $"cache-config-{location.Definition.Id}",
                DiagnosticSeverity.Warning,
                $"{location.Definition.DisplayName} 缓存配置需要检查",
                location.Warning!,
                location.ConfigurationFilePath ?? location.DirectoryPath,
                DiagnosticScanScope.StoragePressure));
        }

        List<CacheDirectoryLocation> safeLocations = [];
        foreach (CacheDirectoryLocation location in locations.Where(location => location.Exists))
        {
            try
            {
                if (EnsureOrdinaryDirectoryForInspection(
                    location.DirectoryPath,
                    $"{location.Definition.DisplayName} cache"))
                {
                    safeLocations.Add(location);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                issues.Add(new DiagnosticIssue(
                    $"cache-path-unsafe-{location.Definition.Id}",
                    DiagnosticSeverity.Error,
                    $"{location.Definition.DisplayName} 缓存路径不安全",
                    exception.Message,
                    location.DirectoryPath,
                    DiagnosticScanScope.StoragePressure));
            }
        }

        string? systemDrive = GetSystemDriveRoot();
        if (systemDrive is not null)
        {
            foreach (CacheDirectoryLocation location in safeLocations.Where(location =>
                IsOnDrive(location.DirectoryPath, systemDrive)
                && !IsInsideOrEqual(_managedRoot, location.DirectoryPath)))
            {
                issues.Add(new DiagnosticIssue(
                    $"cache-system-drive-{location.Definition.Id}",
                    DiagnosticSeverity.Warning,
                    $"{location.Definition.DisplayName} 缓存仍位于系统盘",
                    "可在存储页面预览并迁移；诊断不会自动移动或删除文件。",
                    location.DirectoryPath,
                    DiagnosticScanScope.StoragePressure));
            }
        }

        CacheDirectoryService cacheDirectoryService = new();
        foreach (CacheDirectoryLocation location in safeLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CacheDirectoryMeasurement measurement = await cacheDirectoryService
                .MeasureBoundedAsync(
                    location,
                    options.MaximumCacheEntries,
                    options.MaximumCacheDepth,
                    cancellationToken)
                .ConfigureAwait(false);
            if (measurement.TotalBytes >= options.LargeCacheBytes)
            {
                issues.Add(new DiagnosticIssue(
                    $"cache-large-{measurement.Location.Definition.Id}",
                    DiagnosticSeverity.Warning,
                    $"{measurement.Location.Definition.DisplayName} 缓存占用较大",
                    $"{FormatBytes(measurement.TotalBytes)}，共 {measurement.FileCount} 个文件；阈值为 {FormatBytes(options.LargeCacheBytes)}。",
                    measurement.Location.DirectoryPath,
                    DiagnosticScanScope.StoragePressure));
            }

            if (measurement.Errors.Count > 0)
            {
                issues.Add(new DiagnosticIssue(
                    $"cache-measurement-{measurement.Location.Definition.Id}",
                    DiagnosticSeverity.Warning,
                    $"{measurement.Location.Definition.DisplayName} 缓存未完整统计",
                    $"{measurement.Errors.Count} 个路径无法读取；报告的大小可能偏小。",
                    measurement.Location.DirectoryPath,
                    DiagnosticScanScope.StoragePressure));
            }
        }

        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        AddDriveRoot(_managedRoot, roots);
        foreach (CacheDirectoryLocation location in safeLocations)
        {
            AddDriveRoot(location.DirectoryPath, roots);
        }

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DriveInfo drive = new(root);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                double freeRatio = (double)drive.AvailableFreeSpace / drive.TotalSize;
                if (drive.AvailableFreeSpace < options.LowDiskFreeBytes
                    || freeRatio < options.LowDiskFreeRatio)
                {
                    issues.Add(new DiagnosticIssue(
                        $"disk-pressure-{StableId(root)}",
                        DiagnosticSeverity.Warning,
                        $"{root} 磁盘空间偏低",
                        $"可用 {FormatBytes(drive.AvailableFreeSpace)}（{freeRatio:P0}）；检查阈值为 {FormatBytes(options.LowDiskFreeBytes)} 或 {options.LowDiskFreeRatio:P0}。",
                        root,
                        DiagnosticScanScope.StoragePressure));
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                issues.Add(new DiagnosticIssue(
                    $"disk-inspection-{StableId(root)}",
                    DiagnosticSeverity.Warning,
                    "无法读取磁盘容量",
                    exception.Message,
                    root,
                    DiagnosticScanScope.StoragePressure));
            }
        }
    }

    internal bool InspectManagedStoragePlacement(ICollection<DiagnosticIssue> issues)
    {
        try
        {
            _ = EnsureOrdinaryDirectoryForInspection(_managedRoot, "AutoEnvPlus managed root");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            issues.Add(new DiagnosticIssue(
                "managed-root-unsafe",
                DiagnosticSeverity.Error,
                "AutoEnvPlus 受管根无法安全检查",
                exception.Message,
                _managedRoot,
                DiagnosticScanScope.StoragePressure));
            return false;
        }

        string? systemDrive = GetSystemDriveRoot();
        if (systemDrive is not null && IsOnDrive(_managedRoot, systemDrive))
        {
            issues.Add(new DiagnosticIssue(
                "managed-root-system-drive",
                DiagnosticSeverity.Warning,
                "AutoEnvPlus 受管根位于系统盘",
                "语言工具、下载、状态与 Shim 将占用系统盘；可迁移到非系统盘后再更新 AUTOENVPLUS_HOME。",
                _managedRoot,
                DiagnosticScanScope.StoragePressure));
        }

        string localApplicationData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);
        if (localApplicationData.Length > 0)
        {
            string legacyRoot = Path.Combine(localApplicationData, "AutoEnvPlus");
            bool legacyExists = false;
            try
            {
                legacyExists = EnsureOrdinaryDirectoryForInspection(
                    legacyRoot,
                    "legacy AutoEnvPlus data root");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                issues.Add(new DiagnosticIssue(
                    "legacy-localappdata-state-unsafe",
                    DiagnosticSeverity.Warning,
                    "旧 AutoEnvPlus 数据路径无法安全检查",
                    exception.Message,
                    legacyRoot,
                    DiagnosticScanScope.StoragePressure));
            }

            if (legacyExists
                && !IsInsideOrEqual(_managedRoot, legacyRoot)
                && !IsInsideOrEqual(legacyRoot, _managedRoot))
            {
                bool legacyOnSystemDrive = systemDrive is not null
                    && IsOnDrive(legacyRoot, systemDrive);
                issues.Add(new DiagnosticIssue(
                    "legacy-localappdata-state",
                    DiagnosticSeverity.Warning,
                    legacyOnSystemDrive
                        ? "系统盘仍有旧 AutoEnvPlus 数据"
                        : "仍有旧 AutoEnvPlus 数据",
                    legacyOnSystemDrive
                        ? "当前受管根不会自动合并或删除这份系统盘旧数据；确认无引用后再从存储页面处理。"
                        : "当前受管根不会自动合并或删除这份旧数据；确认无引用后再从存储页面处理。",
                    legacyRoot,
                    DiagnosticScanScope.StoragePressure));
            }
        }

        return true;
    }

    private void InspectPendingManagedOperations(ICollection<DiagnosticIssue> issues)
    {
        InspectPendingDirectory(
            Path.Combine(_managedRoot, ".staging"),
            "runtime-staging-pending",
            "存在未完成的语言工具安装暂存",
            issues);
        InspectPendingDirectory(
            Path.Combine(_managedRoot, "downloads", "library", ".autoenvplus-staging"),
            "download-staging-pending",
            "存在未完成的分段下载或导入暂存",
            issues);

        string downloadsRoot = Path.Combine(_managedRoot, "downloads");
        int partialCount;
        try
        {
            partialCount = CountPartialFilesBounded(downloadsRoot, maximumDepth: 3, maximumEntries: 4096);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            issues.Add(new DiagnosticIssue(
                "download-partial-inspection-failed",
                DiagnosticSeverity.Warning,
                "无法完整检查下载残留",
                exception.Message,
                downloadsRoot,
                DiagnosticScanScope.StoragePressure));
            return;
        }

        if (partialCount > 0)
        {
            issues.Add(new DiagnosticIssue(
                "download-partials-pending",
                DiagnosticSeverity.Warning,
                "存在未完成的语言工具下载",
                $"发现 {partialCount} 个 .partial 或恢复元数据文件；可能来自中断下载，也可能正被活动任务使用。",
                downloadsRoot,
                DiagnosticScanScope.StoragePressure));
        }
    }

    internal static void InspectPendingDirectory(
        string path,
        string issueId,
        string title,
        ICollection<DiagnosticIssue> issues)
    {
        try
        {
            if (!EnsureOrdinaryDirectoryForInspection(path, "managed staging root"))
            {
                return;
            }

            ManagedPathSafety.EnsureNoReparsePointInPath(path);
            int count = Directory.EnumerateFileSystemEntries(
                    path,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Take(129)
                .Count();
            if (count > 0)
            {
                issues.Add(new DiagnosticIssue(
                    issueId,
                    DiagnosticSeverity.Warning,
                    title,
                    count > 128
                        ? "至少有 129 个暂存项；诊断只读取，不会清理活动或恢复证据。"
                        : $"发现 {count} 个暂存项；诊断只读取，不会清理活动或恢复证据。",
                    path,
                    DiagnosticScanScope.StoragePressure));
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            issues.Add(new DiagnosticIssue(
                issueId + "-unsafe",
                DiagnosticSeverity.Error,
                "暂存目录无法安全检查",
                exception.Message,
                path,
                DiagnosticScanScope.StoragePressure));
        }
    }

    internal static int CountPartialFilesBounded(
        string root,
        int maximumDepth,
        int maximumEntries)
    {
        if (!EnsureOrdinaryDirectoryForInspection(root, "managed downloads root"))
        {
            return 0;
        }

        Stack<(string Path, int Depth)> pending = [];
        pending.Push((Path.GetFullPath(root), 0));
        int inspected = 0;
        int partials = 0;
        while (pending.TryPop(out (string Path, int Depth) current))
        {
            if (!EnsureOrdinaryDirectoryForInspection(
                current.Path,
                "managed downloads directory"))
            {
                throw new InvalidDataException("下载目录在检查期间消失。");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(
                current.Path,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                if (++inspected > maximumEntries)
                {
                    throw new InvalidDataException("下载残留检查达到安全条目上限。");
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidDataException("下载目录包含重解析点或设备路径。");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth < maximumDepth)
                    {
                        pending.Push((entry, current.Depth + 1));
                    }

                    continue;
                }

                string fileName = Path.GetFileName(entry);
                if (fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains(".partial.", StringComparison.OrdinalIgnoreCase))
                {
                    partials++;
                }
            }
        }

        return partials;
    }

    private static string? GetSystemDriveRoot()
    {
        string windows = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.Windows);
        return windows.Length == 0 ? null : Path.GetPathRoot(Path.GetFullPath(windows));
    }

    internal static bool IsOnDrive(string path, string driveRoot) => string.Equals(
        Path.GetPathRoot(NormalizePathForComparison(path)),
        Path.GetPathRoot(NormalizePathForComparison(driveRoot)),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideOrEqual(string root, string candidate)
    {
        string fullRoot = NormalizePathForComparison(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string fullCandidate = NormalizePathForComparison(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool EnsureOrdinaryDirectoryForInspection(
        string path,
        string description)
    {
        EnsureSupportedPathNamespace(path);
        ManagedPathSafety.EnsureNoReparsePointInPath(path);
        FileAttributes? attributes = TryGetExistingAttributes(path);
        if (attributes is null)
        {
            return false;
        }

        if ((attributes.Value & FileAttributes.Directory) == 0
            || (attributes.Value & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be an ordinary directory without reparse points or device entries.");
        }

        ManagedPathSafety.EnsureNoReparsePointInPath(path);
        return true;
    }

    private static FileAttributes? TryGetExistingAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new IOException("The storage path could not be inspected safely.", exception);
        }
    }

    private static void EnsureSupportedPathNamespace(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool extendedDrive = fullPath.Length >= 7
            && fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            && char.IsAsciiLetter(fullPath[4])
            && fullPath[5] == ':'
            && fullPath[6] is '\\' or '/';
        bool extendedUnc = fullPath.StartsWith(
            @"\\?\UNC\",
            StringComparison.OrdinalIgnoreCase);
        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\??\", StringComparison.Ordinal)
            || (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !extendedDrive
                && !extendedUnc))
        {
            throw new InvalidDataException(
                "Device namespaces and volume aliases cannot be inspected safely.");
        }
    }

    private static string NormalizePathForComparison(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + fullPath[8..];
        }

        if (fullPath.Length >= 7
            && fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            && char.IsAsciiLetter(fullPath[4])
            && fullPath[5] == ':'
            && fullPath[6] is '\\' or '/')
        {
            return fullPath[4..];
        }

        return fullPath;
    }

    private async Task<IReadOnlyList<DiagnosticIssue>> InspectProviderConnectivityAsync(
        ProviderDiagnosticContext context,
        EnvironmentDiagnosticOptions options,
        CancellationToken cancellationToken)
    {
        List<EndpointProbe> candidates = [];
        List<DiagnosticIssue> results = [];
        if (!context.InventoryComplete)
        {
            results.Add(new DiagnosticIssue(
                "provider-connectivity-partial",
                DiagnosticSeverity.Warning,
                "实时连接检查仅覆盖可解析来源",
                "语言包、Provider 插件或用户来源状态存在错误；未对无法加载的来源发起请求。",
                Scope: DiagnosticScanScope.ProviderConnectivity));
        }

        IEnumerable<ProviderSourceOwner> preferredOwners =
            context.SourcePreferences.Overrides.Select(item => item.Owner)
                .Concat(context.SourcePreferences.CustomSources
                    .Where(item => item.IsEnabled)
                    .Select(item => item.Owner))
                .DistinctBy(
                    owner => $"{owner.LanguageToolId}|{owner.ProviderId}|{owner.SlotId}",
                    StringComparer.OrdinalIgnoreCase);
        foreach (ProviderSourceOwner owner in preferredOwners)
        {
            if (!context.Catalog.TryGetTool(
                    owner.LanguageToolId,
                    out LanguageToolDefinition? preferredTool))
            {
                continue;
            }

            ProviderSourceResolutionResult source = ProviderSourceResolver.Resolve(
                context.Catalog,
                context.SourcePreferences,
                owner);
            EffectiveNetworkSettings? network = ResolveNetworkForLanguageTool(
                context.Settings,
                preferredTool!);
            if (source.Source?.EffectiveEndpoint is null || network is null)
            {
                continue;
            }

            candidates.Add(new EndpointProbe(
                $"{preferredTool!.DisplayName} / {source.Source.DisplayName}",
                source.Source.EffectiveEndpoint,
                network,
                0));
        }

        HashSet<string> defaultLanguages = context.Catalog.DefaultLanguages
            .Select(language => language.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageToolDefinition tool in context.Catalog.Tools.Where(tool =>
            tool.LanguageIds.Any(defaultLanguages.Contains)))
        {
            EffectiveNetworkSettings? network = ResolveNetworkForLanguageTool(
                context.Settings,
                tool);
            if (network is null)
            {
                continue;
            }

            foreach (LanguageToolProviderDefinition provider in tool.Providers)
            {
                ProviderSourceListResolutionResult resolved =
                    ProviderSourceResolver.ResolveProvider(
                        context.Catalog,
                        context.SourcePreferences,
                        tool.Id,
                        provider.Id);
                foreach (ResolvedProviderSource source in resolved.Sources.Where(source =>
                    source.EffectiveEndpoint is not null))
                {
                    candidates.Add(new EndpointProbe(
                        $"{tool.DisplayName} / {source.DisplayName}",
                        source.EffectiveEndpoint!,
                        network,
                        source.Origin is ProviderSourceOrigin.UserOverride
                            or ProviderSourceOrigin.Custom
                                ? 0
                                : 2));
                }
            }
        }

        foreach (RuntimeProviderPluginDescriptor plugin in context.Plugins.Plugins.Where(plugin =>
            plugin.IsEnabled))
        {
            string toolId = NetworkToolIds.TryGetRuntimeScope(
                plugin.Manifest.Kind,
                out string runtimeToolId)
                    ? runtimeToolId
                    : NetworkToolIds.Downloads;
            NetworkSettingsResolutionResult resolution = NetworkSettingsResolver.Resolve(
                context.Settings,
                toolId);
            if (resolution.EffectiveSettings is null)
            {
                continue;
            }

            foreach (RuntimeProviderPluginAsset asset in plugin.Manifest.Releases
                .SelectMany(release => release.Assets))
            {
                candidates.Add(new EndpointProbe(
                    plugin.Manifest.DisplayName,
                    asset.DownloadUri,
                    resolution.EffectiveSettings,
                    1));
            }
        }

        EndpointProbe[] distinct = candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Endpoint.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(
                candidate => candidate.Endpoint.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length > options.MaximumConnectivityEndpoints)
        {
            results.Add(new DiagnosticIssue(
                "provider-connectivity-limit",
                DiagnosticSeverity.Information,
                "实时连接检查已按上限取样",
                $"共有 {distinct.Length} 个唯一 Provider/镜像端点，本次检查前 {options.MaximumConnectivityEndpoints} 个。",
                Scope: DiagnosticScanScope.ProviderConnectivity));
            distinct = distinct[..options.MaximumConnectivityEndpoints];
        }

        using SemaphoreSlim concurrency = new(6, 6);
        DiagnosticIssue?[] probeResults = await Task.WhenAll(distinct.Select(async candidate =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProbeEndpointAsync(candidate, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);
        results.AddRange(probeResults
            .Where(issue => issue is not null)
            .Select(issue => issue!));
        return results;
    }

    private static EffectiveNetworkSettings? ResolveNetworkForLanguageTool(
        NetworkSettings settings,
        LanguageToolDefinition tool)
    {
        string toolId = tool.Id switch
        {
            "pip" => NetworkToolIds.Pip,
            "npm" => NetworkToolIds.Npm,
            "pnpm" => NetworkToolIds.Pnpm,
            "yarn" => NetworkToolIds.Yarn,
            "nuget-cli" => NetworkToolIds.NuGet,
            "apache-maven" => NetworkToolIds.Maven,
            "gradle" => NetworkToolIds.Gradle,
            "vcpkg" => NetworkToolIds.Vcpkg,
            "conan" => NetworkToolIds.Conan,
            _ when tool.LanguageIds.Contains("python", StringComparer.OrdinalIgnoreCase) =>
                NetworkToolIds.RuntimePython,
            _ when tool.LanguageIds.Any(languageId => languageId.Equals(
                "javascript",
                StringComparison.OrdinalIgnoreCase)
                || languageId.Equals("typescript", StringComparison.OrdinalIgnoreCase)) =>
                NetworkToolIds.RuntimeNode,
            _ when tool.LanguageIds.Contains("java", StringComparer.OrdinalIgnoreCase) =>
                NetworkToolIds.RuntimeJava,
            _ when tool.LanguageIds.Any(languageId => languageId.Equals(
                "csharp",
                StringComparison.OrdinalIgnoreCase)
                || languageId.Equals("fsharp", StringComparison.OrdinalIgnoreCase)
                || languageId.Equals("visual-basic", StringComparison.OrdinalIgnoreCase)) =>
                NetworkToolIds.RuntimeDotNet,
            _ when tool.LanguageIds.Any(languageId => languageId.Equals(
                "c",
                StringComparison.OrdinalIgnoreCase)
                || languageId.Equals("cpp", StringComparison.OrdinalIgnoreCase)) =>
                NetworkToolIds.RuntimeCpp,
            _ => NetworkToolIds.Downloads,
        };

        return NetworkSettingsResolver.Resolve(settings, toolId).EffectiveSettings;
    }

    private static async Task<DiagnosticIssue?> ProbeEndpointAsync(
        EndpointProbe candidate,
        EnvironmentDiagnosticOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = NetworkHttpClientFactory.Create(
                candidate.Network,
                options.ConnectivityTimeout);
            using HttpRequestMessage request = new(HttpMethod.Head, candidate.Endpoint);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status >= 500 || status is 408 or 429)
            {
                return new DiagnosticIssue(
                    $"provider-http-{StableId(candidate.Name + candidate.Endpoint.IdnHost)}",
                    DiagnosticSeverity.Warning,
                    "Provider 或镜像响应异常",
                    $"{candidate.Name}：{candidate.Endpoint.IdnHost} 返回 HTTP {status}。",
                    Scope: DiagnosticScanScope.ProviderConnectivity);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticIssue(
                $"provider-timeout-{StableId(candidate.Name + candidate.Endpoint.IdnHost)}",
                DiagnosticSeverity.Warning,
                "Provider 或镜像连接超时",
                $"{candidate.Name}：{candidate.Endpoint.IdnHost}。",
                Scope: DiagnosticScanScope.ProviderConnectivity);
        }
        catch (HttpRequestException exception)
        {
            string detail = exception.StatusCode is { } statusCode
                ? $"{candidate.Endpoint.IdnHost} 返回 HTTP {(int)statusCode}。"
                : $"{candidate.Endpoint.IdnHost} 传输失败。";
            return new DiagnosticIssue(
                $"provider-transport-{StableId(candidate.Name + candidate.Endpoint.IdnHost)}",
                DiagnosticSeverity.Warning,
                "Provider 或镜像无法连接",
                $"{candidate.Name}：{detail}",
                Scope: DiagnosticScanScope.ProviderConnectivity);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException)
        {
            return new DiagnosticIssue(
                $"provider-io-{StableId(candidate.Name + candidate.Endpoint.IdnHost)}",
                DiagnosticSeverity.Warning,
                "Provider 或镜像无法连接",
                $"{candidate.Name}：{candidate.Endpoint.IdnHost} 传输失败。",
                Scope: DiagnosticScanScope.ProviderConnectivity);
        }
    }

    private static void AddDriveRoot(string path, ISet<string> roots)
    {
        try
        {
            string? root = Path.GetPathRoot(NormalizePathForComparison(path));
            if (!string.IsNullOrWhiteSpace(root))
            {
                roots.Add(root);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
        }
    }

    private static IEnumerable<string> SplitPath(string? value) =>
        (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => System.Environment.ExpandEnvironmentVariables(
                entry.Trim().Trim('"')))
            .Where(entry => entry.Length > 0);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(left).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryGetPrimaryCommand(RuntimeKind kind, out string command)
    {
        command = kind switch
        {
            RuntimeKind.Python => "python",
            RuntimeKind.NodeJs => "node",
            RuntimeKind.Java => "java",
            RuntimeKind.DotNet => "dotnet",
            RuntimeKind.Msvc => "cl",
            RuntimeKind.CMake => "cmake",
            RuntimeKind.Ninja => "ninja",
            RuntimeKind.Llvm => "clang",
            RuntimeKind.Mingw => "gcc",
            _ => string.Empty,
        };
        return command.Length > 0;
    }

    private static string StableId(string value)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        const double gibibyte = 1024d * 1024d * 1024d;
        const double mebibyte = 1024d * 1024d;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:F1} GiB"
            : $"{bytes / mebibyte:F1} MiB";
    }

    private static RuntimeArchitecture CurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => RuntimeArchitecture.X86,
            Architecture.Arm64 => RuntimeArchitecture.Arm64,
            _ => RuntimeArchitecture.X64,
        };

    private sealed record ProviderDiagnosticContext(
        NetworkSettings Settings,
        RuntimeProviderPluginListResult Plugins,
        LanguageCatalog Catalog,
        ProviderSourcePreferences SourcePreferences,
        bool InventoryComplete);

    private sealed record EndpointProbe(
        string Name,
        Uri Endpoint,
        EffectiveNetworkSettings Network,
        int Priority);
}
