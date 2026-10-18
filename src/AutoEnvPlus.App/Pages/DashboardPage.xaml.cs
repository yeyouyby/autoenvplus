using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.App.Services;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.App.Updates;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Overview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource _pageCancellation = new();
    private readonly AppDownloadManager _downloadManager;
    private readonly OverviewMonitorService _monitor;
    private OverviewSnapshot? _snapshot;
    private bool _cachedSnapshotLoadStarted;
    private bool _updateCheckStarted;
    private bool _isActive;

    public DashboardPage()
    {
        App app = (App)Application.Current;
        _downloadManager = app.DownloadManager;
        _monitor = app.OverviewMonitor;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        // The shell caches this page across navigations; the previous visit's
        // Unloaded cancelled the token, so re-arm it on every load.
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
        _pageCancellation = new CancellationTokenSource();
        if (_isActive)
        {
            return;
        }

        _isActive = true;
        _downloadManager.StateChanged += OnDownloadManagerStateChanged;
        // Background statistics: the monitor keeps collecting snapshots for
        // the app lifetime; re-render whenever a new one lands while visible.
        _monitor.SnapshotChanged += OnMonitorSnapshotChanged;
        UpdateDownloadTransfer(_downloadManager.Snapshot);
        if (_cachedSnapshotLoadStarted)
        {
            return;
        }

        // Dashboard activation is cache-only; all environment scans stay user-initiated.
        _cachedSnapshotLoadStarted = true;
        RunUpdateCheckOnce();
        await LoadCachedSnapshotAsync();
    }

    private void RunUpdateCheckOnce()
    {
        // One best-effort check per session: query the latest published
        // release and surface a notice when it is newer than this build.
        // Offline or malformed responses stay silent.
        if (_updateCheckStarted)
        {
            return;
        }

        _updateCheckStarted = true;
        _ = RunUpdateCheckAsync();
    }

    private async Task RunUpdateCheckAsync()
    {
        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            ProductIdentityPresentation identity =
                ProductIdentityPresentationPolicy.FromAssembly(typeof(DashboardPage).Assembly);
            AppUpdateInfo? update = await AppUpdateCheckService.CheckForUpdateAsync(
                managedRoot,
                identity.ProductVersion,
                CancellationToken.None);
            if (update is null)
            {
                return;
            }

            UpdateAvailableInfo.Message =
                $"当前 {identity.DisplayVersion}，最新 v{update.LatestVersion}。";
            UpdateReleaseLink.NavigateUri = update.ReleaseUrl;
            UpdateAvailableInfo.IsOpen = true;
        }
        catch (InvalidOperationException)
        {
            // No managed root or no assembly identity: nothing to announce.
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        // Only the page's own awaits stop here; the monitor keeps collecting
        // statistics in the background for the rest of the app lifetime.
        _pageCancellation.Cancel();
        _downloadManager.StateChanged -= OnDownloadManagerStateChanged;
        _monitor.SnapshotChanged -= OnMonitorSnapshotChanged;
    }

    private void OnMonitorSnapshotChanged(object? sender, OverviewSnapshot snapshot)
    {
        void Render()
        {
            if (_isActive)
            {
                _snapshot = snapshot;
                RenderSnapshot(snapshot, cached: false);
            }
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Render();
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
    }

    private async void OnQuickRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync(fullScan: false);
        RestoreActionFocus(sender);
    }

    private async void OnFullScanClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync(fullScan: true);
        RestoreActionFocus(sender);
    }

    private async Task LoadCachedSnapshotAsync()
    {
        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            ManagedRootText.Text = managedRoot;
            OverviewSnapshot? cached = _monitor.CurrentSnapshot
                ?? await _monitor.LoadCachedAsync(managedRoot, _pageCancellation.Token);
            _snapshot = cached;
            if (cached is not null)
            {
                RenderSnapshot(cached, cached: true);
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
        SetBusy(true);
        OverviewInfo.Severity = InfoBarSeverity.Informational;
        OverviewInfo.Title = fullScan ? "正在完整扫描" : "正在快速刷新";
        OverviewInfo.Message = fullScan
            ? "正在读取运行时、PATH、命令和缓存状态。"
            : "正在读取 AutoEnvPlus 受管状态。";

        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            ManagedRootText.Text = managedRoot;
            // Collection is owned by the app-lifetime monitor: navigating away
            // mid-scan no longer aborts it, and the result is shared with any
            // page that subscribes to SnapshotChanged.
            OverviewRefreshResult result = await _monitor.RefreshAsync(managedRoot, fullScan);
            _snapshot = result.Snapshot;
            RenderSnapshot(result.Snapshot, cached: false);
            UpdateDownloadTransfer(_downloadManager.Snapshot);
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
            OverviewInfo.Message = CoreErrorText.Localize(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnDownloadManagerStateChanged(object? sender, EventArgs args)
    {
        if (!_isActive)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateDownloadTransfer(_downloadManager.Snapshot);
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (_isActive)
                {
                    UpdateDownloadTransfer(_downloadManager.Snapshot);
                }
            });
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

    private void RenderSnapshot(OverviewSnapshot snapshot, bool cached)
    {
        // A rendered snapshot replaces the first-run guidance button with the
        // (hidden) cancel affordance so the InfoBar action slot stays coherent.
        OverviewInfo.ActionButton = CancelScanButton;
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
        OverviewInfo.Message = "首次使用建议先快速刷新读取受管状态；完整扫描会额外检查 PATH、命令版本和缓存体积。";
        // First-run guidance must be actionable: the refresh affordance is a
        // small icon button in the header, easy for a new user to miss.
        OverviewInfo.ActionButton = CreateFirstRunRefreshButton();
    }

    private Button CreateFirstRunRefreshButton()
    {
        Button button = new() { Content = "快速刷新" };
        AutomationProperties.SetName(button, "首次快速刷新概览");
        button.Click += async (_, _) =>
        {
            OverviewInfo.ActionButton = CancelScanButton;
            await RefreshAsync(fullScan: false);
        };
        return button;
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
        // A full scan can walk every cache directory and take minutes; the
        // cancel affordance must stay reachable while the scan runs. The
        // first-run guidance button must never stay attached while busy.
        OverviewInfo.ActionButton = CancelScanButton;
        CancelScanButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancelScanClicked(object sender, RoutedEventArgs args)
    {
        // The in-flight collection is owned by the app-lifetime monitor; ask
        // it to stop. Completed reads are discarded with the cancellation.
        _monitor.CancelActiveRefresh();
        OverviewInfo.Severity = InfoBarSeverity.Informational;
        OverviewInfo.Title = "正在取消扫描";
        OverviewInfo.Message = "正在停止剩余检查；已完成的读取会被丢弃。";
    }

    private void RestoreActionFocus(object sender)
    {
        if (_isActive && sender is Button button)
        {
            _ = button.Focus(FocusState.Programmatic);
        }
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

    private sealed record RuntimeOverviewRow(
        string Name,
        string Version,
        string Detail);

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
}
