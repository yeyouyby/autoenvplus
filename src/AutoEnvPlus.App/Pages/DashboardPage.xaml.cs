using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Overview;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace AutoEnvPlus.App.Pages;

public sealed partial class DashboardPage : Page
{
    private static readonly LanguageOverviewDefinition[] OverviewLanguages =
    [
        new("python", "Python", [RuntimeKind.Python]),
        new("javascript", "JavaScript / TypeScript", [RuntimeKind.NodeJs]),
        new("java", "Java", [RuntimeKind.Java]),
        new("cpp", "C / C++", [
            RuntimeKind.Msvc,
            RuntimeKind.Llvm,
            RuntimeKind.Mingw,
            RuntimeKind.CMake,
            RuntimeKind.Ninja,
        ]),
        new("csharp", ".NET", [RuntimeKind.DotNet]),
    ];

    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly AppDownloadManager _downloadManager;
    private CancellationTokenSource? _refreshCancellation;
    private OverviewSnapshot? _snapshot;

    public DashboardPage()
    {
        _downloadManager = ((App)Application.Current).DownloadManager;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _downloadManager.StateChanged += OnDownloadManagerStateChanged;
        await LoadCachedSnapshotAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _refreshCancellation?.Cancel();
        _pageCancellation.Cancel();
        _downloadManager.StateChanged -= OnDownloadManagerStateChanged;
    }

    private async void OnQuickRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync(fullScan: false);
    }

    private async void OnFullScanClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync(fullScan: true);
    }

    private async Task LoadCachedSnapshotAsync()
    {
        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            ManagedRootText.Text = managedRoot;
            OverviewSnapshotStore snapshotStore = new(managedRoot);
            _snapshot = await snapshotStore.LoadAsync(_pageCancellation.Token);
            if (_snapshot is not null)
            {
                RenderSnapshot(_snapshot, cached: true);
            }
            else
            {
                RenderEmptySnapshot(managedRoot);
            }

        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            OverviewInfo.Severity = InfoBarSeverity.Warning;
            OverviewInfo.Title = "无法读取上次概览";
            OverviewInfo.Message = $"没有执行环境扫描。{exception.Message}";
        }
    }

    private async Task RefreshAsync(bool fullScan)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        CancellationTokenSource refresh = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        _refreshCancellation = refresh;
        CancellationToken cancellationToken = refresh.Token;
        SetBusy(true);

        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            ManagedRootText.Text = managedRoot;

            Task<PathInspectionReport>? pathTask = fullScan
                ? Task.Run(
                    () => new PathInspector().InspectCurrent(
                        [
                            "python", "node", "npm", "java", "javac", "dotnet", "cl",
                            "gcc", "g++", "clang", "clang++", "cmake", "ninja",
                        ]),
                    cancellationToken)
                : null;
            Task<IReadOnlyList<DiscoveredRuntime>>? discoveredTask = fullScan
                ? new RuntimeDiscoveryService().DiscoverCurrentAsync(cancellationToken)
                : null;
            Task<RegistryLoadResult> registryTask =
                new ManagedRuntimeRegistry(managedRoot).LoadAsync(cancellationToken);
            Task<RuntimeProfile> globalTask =
                new GlobalRuntimeProfileStore(managedRoot).LoadAsync(cancellationToken);
            Task<IReadOnlyList<KnownProject>> projectsTask =
                new KnownProjectStore(managedRoot).LoadAsync(cancellationToken);
            Task<ActivityLogLoadResult> activityTask =
                new ActivityLogStore(
                    managedRoot,
                    retentionDays: ((App)Application.Current).CurrentSettings.LogRetentionDays)
                    .LoadAsync(cancellationToken);
            Task<OverviewStorageStatus>? storageTask = fullScan
                ? LoadStorageOverviewAsync(managedRoot, cancellationToken)
                : null;
            Task<NetworkSettingsLoadResult> networkTask =
                new NetworkSettingsStore(managedRoot).LoadAsync(cancellationToken);
            Task<DownloadOverview> downloadsTask = Task.Run(
                () => LoadDownloadOverview(_downloadManager),
                cancellationToken);
            Task<RuntimeProviderPluginListResult> pluginsTask =
                new RuntimeProviderPluginStore(managedRoot).ListAsync(cancellationToken);

            List<string> errors = [];
            PathInspectionReport? path = pathTask is null
                ? null
                : await TryLoadAsync(pathTask, "PATH", errors);
            IReadOnlyList<DiscoveredRuntime>? discovered = discoveredTask is null
                ? null
                : await TryLoadAsync(discoveredTask, "语言工具发现", errors);
            RegistryLoadResult? registry = await TryLoadAsync(
                registryTask,
                "托管语言工具",
                errors);
            RuntimeProfile? global = await TryLoadAsync(globalTask, "全局版本选择", errors);
            IReadOnlyList<KnownProject>? projects = await TryLoadAsync(
                projectsTask,
                "最近项目",
                errors);
            ActivityLogLoadResult? activity = await TryLoadAsync(
                activityTask,
                "活动记录",
                errors);
            OverviewStorageStatus? storage = storageTask is null
                ? null
                : await TryLoadAsync(storageTask, "缓存统计", errors);
            NetworkSettingsLoadResult? network = await TryLoadAsync(
                networkTask,
                "网络设置",
                errors);
            DownloadOverview? downloads = await TryLoadAsync(
                downloadsTask,
                "下载库",
                errors);
            RuntimeProviderPluginListResult? plugins = await TryLoadAsync(
                pluginsTask,
                "Provider 插件",
                errors);

            cancellationToken.ThrowIfCancellationRequested();
            if (registry?.Errors.Count > 0)
            {
                errors.AddRange(registry.Errors.Select(error => $"托管语言工具：{error}"));
            }

            if (activity?.Errors.Count > 0)
            {
                errors.AddRange(activity.Errors.Select(error => $"活动记录：{error}"));
            }

            if (network is { Success: false })
            {
                errors.AddRange(network.Errors.Select(error =>
                    $"网络设置：{error.Message}"));
            }

            if (plugins?.Errors.Count > 0)
            {
                errors.Add($"Provider 插件：{plugins.Errors.Count} 个清单或状态错误");
            }

            DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
            OverviewSnapshot snapshot = new(
                capturedAtUtc,
                fullScan ? OverviewSnapshotDepth.Full : OverviewSnapshotDepth.Quick,
                fullScan ? capturedAtUtc : _snapshot?.LastFullScanAtUtc,
                managedRoot,
                CreateLanguageOverview(
                    registry?.Entries ?? [],
                    global ?? RuntimeProfile.Empty,
                    discovered,
                    fullScan),
                fullScan ? CreatePathStatus(path) : _snapshot?.Path,
                fullScan ? storage : _snapshot?.Storage,
                CreateNetworkStatus(network?.Settings),
                new OverviewDownloadStatus(
                    downloads?.FileCount ?? 0,
                    downloads?.TotalBytes ?? 0),
                CreateProviderStatus(plugins),
                (projects ?? [])
                    .OrderByDescending(project => project.LastSeenUtc)
                    .ThenBy(project => project.ProjectRoot, StringComparer.OrdinalIgnoreCase)
                    .Take(OverviewSnapshot.MaximumProjectCount)
                    .Select(project => new OverviewProjectStatus(
                        project.ProjectRoot,
                        project.LastSeenUtc))
                    .ToArray(),
                (activity?.Entries ?? [])
                    .OrderByDescending(entry => entry.TimestampUtc)
                    .Take(OverviewSnapshot.MaximumActivityCount)
                    .Select(entry => new OverviewActivityStatus(
                        OperationText(entry.OperationType),
                        entry.Summary,
                        entry.TimestampUtc))
                    .ToArray(),
                errors.Take(OverviewSnapshot.MaximumErrorCount).ToArray());
            await new OverviewSnapshotStore(managedRoot).SaveAsync(snapshot, cancellationToken);
            _snapshot = snapshot;
            RenderSnapshot(snapshot, cached: false);
            UpdateDownloadTransfer(downloads?.Snapshot);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            OverviewInfo.Severity = InfoBarSeverity.Error;
            OverviewInfo.Title = "无法读取开发环境概览";
            OverviewInfo.Message = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                SetBusy(false);
                _refreshCancellation.Dispose();
                _refreshCancellation = null;
            }
        }
    }

    private static async Task<T?> TryLoadAsync<T>(
        Task<T> task,
        string area,
        ICollection<string> errors)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception)
        {
            errors.Add($"{area}：{exception.Message}");
            return default;
        }
    }

    private static async Task<OverviewStorageStatus> LoadStorageOverviewAsync(
        string managedRoot,
        CancellationToken cancellationToken)
    {
        CacheDirectoryService service = new();
        CacheDirectoryLocation[] locations = service.DiscoverCurrent().ToArray();
        List<CacheDirectoryMeasurement> measurements = [];
        foreach (CacheDirectoryLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            measurements.Add(await service.MeasureBoundedAsync(
                location,
                EnvironmentDiagnosticOptions.DefaultMaximumCacheEntries,
                EnvironmentDiagnosticOptions.DefaultMaximumCacheDepth,
                cancellationToken));
        }

        long totalBytes = measurements.Sum(measurement => measurement.TotalBytes);
        long totalFiles = measurements.Sum(measurement => measurement.FileCount);
        CacheDirectoryMeasurement? largest = measurements
            .Where(measurement => measurement.TotalBytes > 0)
            .OrderByDescending(measurement => measurement.TotalBytes)
            .FirstOrDefault();
        long? freeBytes = TryGetFreeBytes(managedRoot);
        return new OverviewStorageStatus(
            totalBytes,
            totalFiles,
            measurements.Count(measurement => measurement.Location.Exists),
            largest?.Location.Definition.DisplayName,
            largest?.TotalBytes ?? 0,
            freeBytes,
            measurements.SelectMany(measurement => measurement.Errors).ToArray());
    }

    private static DownloadOverview LoadDownloadOverview(AppDownloadManager manager)
    {
        ManagedDownloadLibraryItem[] items = manager.Library.ListFiles().ToArray();
        return new DownloadOverview(
            items.Length,
            items.Sum(item => item.SizeBytes),
            manager.Snapshot);
    }

    private static OverviewNetworkStatus CreateNetworkStatus(NetworkSettings? settings)
    {
        if (settings is null)
        {
            return new OverviewNetworkStatus(
                0,
                0,
                "全局代理设置不可用",
                "Provider 镜像配置仍保留在各语言工具中。");
        }

        GlobalNetworkSettings global = settings.Global ?? new GlobalNetworkSettings();
        int overrideCount = settings.Tools?.Count ?? 0;
        int proxyCount = (global.HttpProxy is null ? 0 : 1)
            + (global.HttpsProxy is null ? 0 : 1);
        string summary = proxyCount == 0
            ? "全局直连"
            : $"已配置 {proxyCount} 个全局代理端点";
        string detail = overrideCount == 0
            ? "Provider 镜像分别继承官方源或语言页设置"
            : $"{overrideCount} 个工具保留兼容覆盖 · 绕过项 {global.NoProxy?.Count ?? 0}";
        return new OverviewNetworkStatus(proxyCount, overrideCount, summary, detail);
    }

    private static OverviewProviderStatus CreateProviderStatus(
        RuntimeProviderPluginListResult? plugins)
    {
        int builtInCount = BuiltInLanguageCatalog.Current.Tools.Sum(tool => tool.Providers.Count);
        if (plugins is null)
        {
            return new OverviewProviderStatus(builtInCount, 0, 0, 1);
        }

        return new OverviewProviderStatus(
            builtInCount,
            plugins.Plugins.Count,
            plugins.EnabledCount,
            plugins.Errors.Count);
    }

    private void OnDownloadManagerStateChanged(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateDownloadTransfer(_downloadManager.Snapshot);
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(() =>
                UpdateDownloadTransfer(_downloadManager.Snapshot));
        }
    }

    private void UpdateDownloadTransfer(AppTransferSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            DownloadTransferText.Text = "当前没有活动传输";
            return;
        }

        if (snapshot.Error is not null)
        {
            DownloadTransferText.Text = $"最近任务失败 · {snapshot.FileName}";
            return;
        }

        if (snapshot.Phase == ManagedTransferPhase.Completed)
        {
            string mode = snapshot.TransferMode == DownloadTransferMode.Segmented
                ? $"{snapshot.TotalSegments} 段完成"
                : "单连接完成";
            DownloadTransferText.Text = $"最近就绪：{snapshot.FileName} · {mode}";
            return;
        }

        if (snapshot.Phase == ManagedTransferPhase.Cancelled)
        {
            DownloadTransferText.Text = $"最近已取消：{snapshot.FileName}";
            return;
        }

        string progress = snapshot.TotalBytes is long total && total > 0
            ? $"{snapshot.CompletedBytes * 100d / total:F0}%"
            : FormatBytes(snapshot.CompletedBytes);
        string segments = snapshot.TotalSegments > 0
            ? $" · 分段 {snapshot.CompletedSegments}/{snapshot.TotalSegments}"
            : string.Empty;
        DownloadTransferText.Text = $"正在传输 {snapshot.FileName} · {progress}{segments}";
    }

    private static IReadOnlyList<OverviewLanguageStatus> CreateLanguageOverview(
        IReadOnlyList<ManagedRuntimeEntry> entries,
        RuntimeProfile global,
        IReadOnlyList<DiscoveredRuntime>? discovered,
        bool fullScan)
    {
        RuntimeInstallation[] installations = entries
            .Select(entry => entry.ToRuntimeInstallation())
            .ToArray();
        List<OverviewLanguageStatus> rows = [];
        foreach (LanguageOverviewDefinition language in OverviewLanguages)
        {
            ManagedRuntimeEntry[] managed = entries
                .Where(entry => language.Kinds.Contains(entry.Kind))
                .ToArray();
            List<string> selectedVersions = [];
            int configuredSelections = 0;
            foreach (RuntimeKind kind in language.Kinds)
            {
                if (global.Selections.ContainsKey(kind))
                {
                    configuredSelections++;
                }

                RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
                    kind,
                    new RuntimeResolutionContext(Global: global),
                    installations,
                    CurrentArchitecture());
                if (resolution.Success)
                {
                    selectedVersions.Add(resolution.Installation!.Version.ToString());
                }
            }

            DiscoveredRuntime[] pathTools = discovered?
                .Where(candidate => language.Kinds.Contains(candidate.Kind) && candidate.IsHealthy)
                .ToArray() ?? [];
            string version = selectedVersions
                .Concat(pathTools.Select(tool => tool.Version?.ToString()))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "未安装";
            string detail = managed.Length > 0
                ? $"托管 {managed.Length} 个版本 · 全局选择 {configuredSelections} 项"
                : fullScan && pathTools.Length > 0
                    ? $"PATH 发现 {pathTools.Length} 个工具入口"
                    : fullScan
                        ? "完整扫描未发现可用工具"
                        : "快速刷新未执行 PATH 探测";
            rows.Add(new OverviewLanguageStatus(
                language.Id,
                language.DisplayName,
                version,
                detail));
        }

        return rows;
    }

    private static OverviewPathStatus? CreatePathStatus(PathInspectionReport? report)
    {
        if (report is null)
        {
            return null;
        }

        return new OverviewPathStatus(
            report.Entries.Count,
            report.MissingCount,
            report.DuplicateCount,
            report.Conflicts.Count,
            report.IsHealthy
            ? "没有发现失效目录、重复项或已知命令冲突。"
            : "只读检查发现需要处理的 PATH 项。打开 PATH 与命令页面查看详情。");
    }

    private void RenderSnapshot(OverviewSnapshot snapshot, bool cached)
    {
        RuntimeOverviewList.ItemsSource = snapshot.Languages
            .Select(language => new RuntimeOverviewRow(
                language.DisplayName,
                language.Version,
                language.Detail))
            .ToArray();

        if (snapshot.Path is OverviewPathStatus path)
        {
            PathDirectoryCountText.Text = path.DirectoryCount.ToString();
            PathMissingCountText.Text = path.MissingDirectoryCount.ToString();
            PathDuplicateCountText.Text = path.DuplicateDirectoryCount.ToString();
            PathConflictCountText.Text = path.CommandConflictCount.ToString();
            PathDetailText.Text = path.Detail;
        }
        else
        {
            PathDirectoryCountText.Text = "—";
            PathMissingCountText.Text = "—";
            PathDuplicateCountText.Text = "—";
            PathConflictCountText.Text = "—";
            PathDetailText.Text = "尚未执行完整 PATH 扫描。";
        }

        if (snapshot.Storage is OverviewStorageStatus storage)
        {
            StorageUsageText.Text = FormatBytes(storage.TotalBytes);
            string largest = storage.LargestCacheName is null
                ? "没有发现非空缓存"
                : $"最大目录 {storage.LargestCacheName}（{FormatBytes(storage.LargestCacheBytes)}）";
            string free = storage.ManagedDriveFreeBytes is long freeBytes
                ? $" · 受管盘可用 {FormatBytes(freeBytes)}"
                : string.Empty;
            StorageDetailText.Text =
                $"{storage.ExistingDirectoryCount} 个现有目录 · {storage.TotalFiles:N0} 个文件 · {largest}{free}";
            if (storage.Errors.Count > 0)
            {
                StorageDetailText.Text += $" · {storage.Errors.Count} 个项目未能完整统计";
            }
        }
        else
        {
            StorageUsageText.Text = "尚无统计";
            StorageDetailText.Text = "缓存大小只在完整扫描时更新。";
        }

        ManagedRootText.Text = snapshot.ManagedRoot;
        NetworkProfileText.Text = snapshot.Network.Summary;
        NetworkDetailText.Text = snapshot.Network.Detail;
        DownloadLibraryText.Text =
            $"{snapshot.Downloads.FileCount:N0} 个文件 · {FormatBytes(snapshot.Downloads.TotalBytes)}";
        UpdateDownloadTransfer(_downloadManager.Snapshot);

        int disabledProviderCount = snapshot.Providers.ImportedProviderCount
            - snapshot.Providers.EnabledImportedProviderCount;
        PluginSummaryText.Text =
            $"目录 {snapshot.Providers.BuiltInProviderCount} · 已启用扩展 {snapshot.Providers.EnabledImportedProviderCount} · 已停用 {disabledProviderCount}";
        PluginDetailText.Text = snapshot.Providers.ErrorCount == 0
            ? "Provider 和镜像槽按语言工具归类；语言包可扩展语言目录。"
            : $"{snapshot.Providers.ErrorCount} 个 Provider 清单或状态需要检查。";

        RecentProjectRow[] projectRows = snapshot.Projects
            .Take(4)
            .Select(project => new RecentProjectRow(project))
            .ToArray();
        RecentProjectsList.ItemsSource = projectRows;
        ProjectsEmptyText.Visibility = projectRows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        RecentActivityRow[] activityRows = snapshot.Activities
            .Take(5)
            .Select(activity => new RecentActivityRow(activity))
            .ToArray();
        RecentActivityList.ItemsSource = activityRows;
        ActivityEmptyText.Visibility = activityRows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (snapshot.Errors.Count > 0)
        {
            OverviewInfo.Severity = InfoBarSeverity.Warning;
            OverviewInfo.Title = "部分环境摘要不可用";
            OverviewInfo.Message = string.Join("；", snapshot.Errors.Take(3))
                + (snapshot.Errors.Count > 3
                    ? $"；另有 {snapshot.Errors.Count - 3} 项"
                    : string.Empty);
            return;
        }

        if (snapshot.Path is { } pathSummary
            && (pathSummary.MissingDirectoryCount > 0
                || pathSummary.DuplicateDirectoryCount > 0
                || pathSummary.CommandConflictCount > 0))
        {
            OverviewInfo.Severity = InfoBarSeverity.Warning;
            OverviewInfo.Title = "环境中有可处理项";
            OverviewInfo.Message = CreateSnapshotTimestamp(snapshot, cached)
                + " · PATH 存在失效、重复或命令冲突。";
            return;
        }

        OverviewInfo.Severity = cached
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Success;
        OverviewInfo.Title = cached
            ? "已载入上次概览"
            : snapshot.Depth == OverviewSnapshotDepth.Full
                ? "完整扫描已完成"
                : "快速刷新已完成";
        OverviewInfo.Message = CreateSnapshotTimestamp(snapshot, cached);
    }

    private void RenderEmptySnapshot(string managedRoot)
    {
        RuntimeOverviewList.ItemsSource = Array.Empty<RuntimeOverviewRow>();
        PathDirectoryCountText.Text = "—";
        PathMissingCountText.Text = "—";
        PathDuplicateCountText.Text = "—";
        PathConflictCountText.Text = "—";
        PathDetailText.Text = "尚无扫描快照。";
        StorageUsageText.Text = "尚无统计";
        StorageDetailText.Text = "缓存大小只在完整扫描时更新。";
        ManagedRootText.Text = managedRoot;
        NetworkProfileText.Text = "尚无快照";
        NetworkDetailText.Text = "全局代理可在设置中配置；镜像位于具体 Provider。";
        DownloadLibraryText.Text = "尚无快照";
        UpdateDownloadTransfer(_downloadManager.Snapshot);
        PluginSummaryText.Text = "尚无快照";
        PluginDetailText.Text = "语言目录将在快速刷新后显示。";
        RecentProjectsList.ItemsSource = Array.Empty<RecentProjectRow>();
        ProjectsEmptyText.Visibility = Visibility.Visible;
        RecentActivityList.ItemsSource = Array.Empty<RecentActivityRow>();
        ActivityEmptyText.Visibility = Visibility.Visible;
        OverviewInfo.Severity = InfoBarSeverity.Informational;
        OverviewInfo.Title = "尚无环境概览";
        OverviewInfo.Message = "选择快速刷新读取受管状态，或选择完整扫描检查 PATH、版本和缓存。";
    }

    private static string CreateSnapshotTimestamp(OverviewSnapshot snapshot, bool cached)
    {
        string source = cached ? "快照" : "更新";
        string fullScan = snapshot.LastFullScanAtUtc is DateTimeOffset full
            ? $" · 上次完整扫描 {full.ToLocalTime():yyyy-MM-dd HH:mm}"
            : " · 尚未完整扫描";
        return $"{source}时间 {snapshot.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}{fullScan}";
    }

    private void SetBusy(bool busy)
    {
        QuickRefreshButton.IsEnabled = !busy;
        FullScanButton.IsEnabled = !busy;
        RefreshProgress.IsActive = busy;
    }

    private void OnNavigateClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void OnRecentProjectClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: RecentProjectRow row } && row.Exists)
        {
            Navigate("projects", row.ProjectRoot);
        }
    }

    private static void Navigate(string tag, string? context = null)
    {
        if (((App)Application.Current).MainWindowInstance is MainWindow window)
        {
            window.NavigateTo(tag, context);
        }
    }

    private static long? TryGetFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    private static RuntimeArchitecture CurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => RuntimeArchitecture.X86,
            Architecture.Arm64 => RuntimeArchitecture.Arm64,
            _ => RuntimeArchitecture.X64,
        };

    private static string OperationText(ActivityOperationType type) => type switch
    {
        ActivityOperationType.RuntimeInstall => "安装",
        ActivityOperationType.RuntimeUninstall => "卸载",
        ActivityOperationType.RuntimeSwitch => "版本切换",
        ActivityOperationType.PathChange => "PATH",
        ActivityOperationType.PathRollback => "PATH 回滚",
        ActivityOperationType.CacheMigration => "缓存迁移",
        ActivityOperationType.CacheRollback => "缓存回滚",
        ActivityOperationType.CacheCleanup => "缓存清理",
        ActivityOperationType.ToolchainInstall => "编译工具",
        ActivityOperationType.ProjectImport => "项目",
        ActivityOperationType.CMakePreset => "CMake",
        ActivityOperationType.DiagnosticExport => "诊断",
        ActivityOperationType.SettingsChange => "设置",
        ActivityOperationType.PackageDownload => "下载",
        ActivityOperationType.PackageImport => "导入",
        ActivityOperationType.PackageInstall => "包安装",
        ActivityOperationType.ProviderPluginImport => "插件导入",
        ActivityOperationType.ProviderPluginStateChange => "插件状态",
        ActivityOperationType.ProviderPluginDelete => "插件删除",
        _ => "其他",
    };

    private sealed record RuntimeOverviewRow(
        string Name,
        string Version,
        string Detail);

    private sealed record LanguageOverviewDefinition(
        string Id,
        string DisplayName,
        IReadOnlyList<RuntimeKind> Kinds);

    private sealed class RecentProjectRow
    {
        public RecentProjectRow(OverviewProjectStatus project)
        {
            ProjectRoot = project.ProjectRoot;
            string trimmed = Path.TrimEndingDirectorySeparator(project.ProjectRoot);
            Name = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = project.ProjectRoot;
            }

            Exists = Directory.Exists(project.ProjectRoot);
            LastSeenText = Exists
                ? project.LastSeenUtc.ToLocalTime().ToString("MM-dd HH:mm")
                : "目录不存在";
            AutomationName = Exists
                ? $"打开最近项目 {Name}"
                : $"最近项目 {Name} 的目录不存在";
        }

        public string Name { get; }

        public string ProjectRoot { get; }

        public bool Exists { get; }

        public string LastSeenText { get; }

        public string AutomationName { get; }
    }

    private sealed class RecentActivityRow
    {
        public RecentActivityRow(OverviewActivityStatus entry)
        {
            Operation = entry.Operation;
            Summary = entry.Summary;
            Timestamp = entry.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm");
        }

        public string Operation { get; }

        public string Summary { get; }

        public string Timestamp { get; }
    }

    private sealed record DownloadOverview(
        int FileCount,
        long TotalBytes,
        AppTransferSnapshot? Snapshot);
}
