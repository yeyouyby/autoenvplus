using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
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
    private static readonly RuntimeKind[] OverviewKinds =
    [
        RuntimeKind.Python,
        RuntimeKind.NodeJs,
        RuntimeKind.Java,
        RuntimeKind.DotNet,
    ];

    private readonly CancellationTokenSource _pageCancellation = new();
    private CancellationTokenSource? _refreshCancellation;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _refreshCancellation?.Cancel();
        _pageCancellation.Cancel();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
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

            Task<PathInspectionReport> pathTask = Task.Run(
                () => new PathInspector().InspectCurrent(
                    ["python", "node", "npm", "java", "javac", "dotnet", "cl", "clang", "cmake"]),
                cancellationToken);
            Task<IReadOnlyList<DiscoveredRuntime>> discoveredTask =
                new RuntimeDiscoveryService().DiscoverCurrentAsync(cancellationToken);
            Task<RegistryLoadResult> registryTask =
                new ManagedRuntimeRegistry(managedRoot).LoadAsync(cancellationToken);
            Task<RuntimeProfile> globalTask =
                new GlobalRuntimeProfileStore(managedRoot).LoadAsync(cancellationToken);
            Task<IReadOnlyList<KnownProject>> projectsTask =
                new KnownProjectStore(managedRoot).LoadAsync(cancellationToken);
            Task<ActivityLogLoadResult> activityTask =
                new ActivityLogStore(managedRoot).LoadAsync(cancellationToken);
            Task<StorageOverview> storageTask = LoadStorageOverviewAsync(
                managedRoot,
                cancellationToken);

            List<string> errors = [];
            PathInspectionReport? path = await TryLoadAsync(pathTask, "PATH", errors);
            IReadOnlyList<DiscoveredRuntime>? discovered = await TryLoadAsync(
                discoveredTask,
                "运行时发现",
                errors);
            RegistryLoadResult? registry = await TryLoadAsync(
                registryTask,
                "托管运行时",
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
            StorageOverview? storage = await TryLoadAsync(storageTask, "缓存统计", errors);

            cancellationToken.ThrowIfCancellationRequested();
            if (registry?.Errors.Count > 0)
            {
                errors.AddRange(registry.Errors.Select(error => $"托管运行时：{error}"));
            }

            if (activity?.Errors.Count > 0)
            {
                errors.AddRange(activity.Errors.Select(error => $"活动记录：{error}"));
            }

            UpdateRuntimeOverview(
                registry?.Entries ?? [],
                global ?? RuntimeProfile.Empty,
                discovered ?? []);
            UpdatePathOverview(path);
            UpdateProjects(projects ?? []);
            UpdateActivity(activity?.Entries ?? []);
            UpdateStorage(managedRoot, storage);
            UpdateOverallStatus(path, errors);
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

    private static async Task<StorageOverview> LoadStorageOverviewAsync(
        string managedRoot,
        CancellationToken cancellationToken)
    {
        CacheDirectoryService service = new();
        CacheDirectoryLocation[] locations = service.DiscoverCurrent().ToArray();
        CacheDirectoryMeasurement[] measurements = await Task.WhenAll(
            locations.Select(location => service.MeasureAsync(location, cancellationToken)));
        long totalBytes = measurements.Sum(measurement => measurement.TotalBytes);
        long totalFiles = measurements.Sum(measurement => measurement.FileCount);
        CacheDirectoryMeasurement? largest = measurements
            .Where(measurement => measurement.TotalBytes > 0)
            .OrderByDescending(measurement => measurement.TotalBytes)
            .FirstOrDefault();
        long? freeBytes = TryGetFreeBytes(managedRoot);
        return new StorageOverview(
            totalBytes,
            totalFiles,
            measurements.Count(measurement => measurement.Location.Exists),
            largest?.Location.Definition.DisplayName,
            largest?.TotalBytes ?? 0,
            freeBytes,
            measurements.SelectMany(measurement => measurement.Errors).ToArray());
    }

    private void UpdateRuntimeOverview(
        IReadOnlyList<ManagedRuntimeEntry> entries,
        RuntimeProfile global,
        IReadOnlyList<DiscoveredRuntime> discovered)
    {
        RuntimeInstallation[] installations = entries
            .Select(entry => entry.ToRuntimeInstallation())
            .ToArray();
        List<RuntimeOverviewRow> rows = [];
        foreach (RuntimeKind kind in OverviewKinds)
        {
            VersionSelector? selector = global.Selections.TryGetValue(kind, out VersionSelector? configured)
                ? configured
                : null;
            RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
                kind,
                new RuntimeResolutionContext(Global: global),
                installations,
                CurrentArchitecture());
            DiscoveredRuntime? pathWinner = discovered.FirstOrDefault(candidate =>
                candidate.Kind == kind && candidate.IsHealthy);
            int managedCount = entries.Count(entry => entry.Kind == kind);
            string version = resolution.Success
                ? resolution.Installation!.Version.ToString()
                : pathWinner?.Version?.ToString() ?? "未安装";
            string selection = selector is null ? "自动选择" : $"全局 {selector}";
            string detail = managedCount > 0
                ? $"{selection} · 托管 {managedCount} 个版本"
                : pathWinner is not null
                    ? $"PATH · {pathWinner.ExecutablePath}"
                    : "没有发现可用版本";
            rows.Add(new RuntimeOverviewRow(DisplayName(kind), version, detail));
        }

        RuntimeOverviewList.ItemsSource = rows;
    }

    private void UpdatePathOverview(PathInspectionReport? report)
    {
        if (report is null)
        {
            PathDirectoryCountText.Text = "—";
            PathMissingCountText.Text = "—";
            PathDuplicateCountText.Text = "—";
            PathConflictCountText.Text = "—";
            PathDetailText.Text = "PATH 摘要不可用；可打开环境诊断查看详细错误。";
            return;
        }

        PathDirectoryCountText.Text = report.Entries.Count.ToString();
        PathMissingCountText.Text = report.MissingCount.ToString();
        PathDuplicateCountText.Text = report.DuplicateCount.ToString();
        PathConflictCountText.Text = report.Conflicts.Count.ToString();
        PathDetailText.Text = report.IsHealthy
            ? "没有发现失效目录、重复项或已知命令冲突。"
            : "只读检查发现需要处理的 PATH 项；打开 PATH 页面预览修复和回滚。";
    }

    private void UpdateProjects(IReadOnlyList<KnownProject> projects)
    {
        RecentProjectRow[] rows = projects
            .OrderByDescending(project => project.LastSeenUtc)
            .ThenBy(project => project.ProjectRoot, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(project => new RecentProjectRow(project))
            .ToArray();
        RecentProjectsList.ItemsSource = rows;
        ProjectsEmptyText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateActivity(IReadOnlyList<ActivityLogEntry> entries)
    {
        RecentActivityRow[] rows = entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(5)
            .Select(entry => new RecentActivityRow(entry))
            .ToArray();
        RecentActivityList.ItemsSource = rows;
        ActivityEmptyText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateStorage(string managedRoot, StorageOverview? storage)
    {
        if (storage is null)
        {
            StorageUsageText.Text = "统计不可用";
            StorageDetailText.Text = "打开缓存与存储页面可重新扫描各工具目录。";
            return;
        }

        StorageUsageText.Text = FormatBytes(storage.TotalBytes);
        string largest = storage.LargestName is null
            ? "没有发现非空缓存"
            : $"最大目录 {storage.LargestName}（{FormatBytes(storage.LargestBytes)}）";
        string free = storage.FreeBytes is long freeBytes
            ? $" · 受管盘可用 {FormatBytes(freeBytes)}"
            : string.Empty;
        StorageDetailText.Text =
            $"{storage.ExistingDirectoryCount} 个现有目录 · {storage.TotalFiles:N0} 个文件 · {largest}{free}";
        if (storage.Errors.Count > 0)
        {
            StorageDetailText.Text += $" · {storage.Errors.Count} 个项目未能完整统计";
        }

        ManagedRootText.Text = managedRoot;
    }

    private void UpdateOverallStatus(
        PathInspectionReport? path,
        IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
        {
            OverviewInfo.Severity = InfoBarSeverity.Warning;
            OverviewInfo.Title = "部分环境摘要不可用";
            OverviewInfo.Message = string.Join("；", errors.Take(3))
                + (errors.Count > 3 ? $"；另有 {errors.Count - 3} 项" : string.Empty);
            return;
        }

        if (path is { IsHealthy: false })
        {
            OverviewInfo.Severity = InfoBarSeverity.Warning;
            OverviewInfo.Title = "环境中有可处理项";
            OverviewInfo.Message = "运行时和项目状态已读取；PATH 存在失效、重复或命令冲突。";
            return;
        }

        OverviewInfo.Severity = InfoBarSeverity.Success;
        OverviewInfo.Title = "开发环境摘要已更新";
        OverviewInfo.Message = "当前检查为只读操作；安装、切换、迁移和清理仍会单独预览确认。";
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
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

    private static string DisplayName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.NodeJs => "Node.js",
        RuntimeKind.DotNet => ".NET SDK",
        _ => kind.ToString(),
    };

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
        ActivityOperationType.ToolchainInstall => "工具链",
        ActivityOperationType.ProjectImport => "项目",
        ActivityOperationType.CMakePreset => "CMake",
        ActivityOperationType.DiagnosticExport => "诊断",
        ActivityOperationType.SettingsChange => "设置",
        _ => "其他",
    };

    private sealed record RuntimeOverviewRow(
        string Name,
        string Version,
        string Detail);

    private sealed class RecentProjectRow
    {
        public RecentProjectRow(KnownProject project)
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
        public RecentActivityRow(ActivityLogEntry entry)
        {
            Operation = OperationText(entry.OperationType);
            Summary = entry.Summary;
            Timestamp = entry.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm");
        }

        public string Operation { get; }

        public string Summary { get; }

        public string Timestamp { get; }
    }

    private sealed record StorageOverview(
        long TotalBytes,
        long TotalFiles,
        int ExistingDirectoryCount,
        string? LargestName,
        long LargestBytes,
        long? FreeBytes,
        IReadOnlyList<string> Errors);
}
