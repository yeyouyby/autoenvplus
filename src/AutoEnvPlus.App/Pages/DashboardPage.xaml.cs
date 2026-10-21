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
        ApplyLocalizedStrings();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Applies every localized string for this page from the resource table.
    /// Unpackaged WinUI 3 apps cannot use x:Uid: the XAML loader resolves
    /// ms-resource property bags against the OS system language only, so it
    /// neither matches the standard dotted-key PRI layout nor honors the
    /// in-app language preference. Code-behind assignment through
    /// <see cref="Loc"/> is the only path that follows
    /// <c>Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c>.
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        AutomationProperties.SetName(this, Loc.Get("Dashboard.Page.AccessibleName"));
        OverviewTitleText.Text = Loc.Get("Dashboard.Header.Title");
        OverviewSubtitleText.Text = Loc.Get("Dashboard.Header.Subtitle");
        AutomationProperties.SetName(
            RefreshProgress,
            Loc.Get("Dashboard.Refresh.ProgressAccessibleName"));
        AutomationProperties.SetHelpText(
            QuickRefreshButton,
            Loc.Get("Dashboard.Refresh.Quick.HelpText"));
        AutomationProperties.SetName(
            QuickRefreshButton,
            Loc.Get("Dashboard.Refresh.Quick.AccessibleName"));
        ToolTipService.SetToolTip(
            QuickRefreshButton,
            Loc.Get("Dashboard.Refresh.Quick.Label"));
        AutomationProperties.SetHelpText(
            FullScanButton,
            Loc.Get("Dashboard.Refresh.FullScan.HelpText"));
        AutomationProperties.SetName(
            FullScanButton,
            Loc.Get("Dashboard.Refresh.FullScan.AccessibleName"));
        ToolTipService.SetToolTip(
            FullScanButton,
            Loc.Get("Dashboard.Refresh.FullScan.ToolTip"));
        OverviewInfo.Title = Loc.Get("Dashboard.Messages.LoadingTitle");
        AutomationProperties.SetName(
            CancelScanButton,
            Loc.Get("Dashboard.CancelScan.AccessibleName"));
        CancelScanButton.Content = Loc.Get("Dashboard.CancelScan.Label");
        AutomationProperties.SetName(
            UpdateAvailableInfo,
            Loc.Get("Dashboard.Update.AvailableAccessibleName"));
        UpdateAvailableInfo.Title = Loc.Get("Dashboard.Update.AvailableTitle");
        AutomationProperties.SetName(
            UpdateReleaseLink,
            Loc.Get("Dashboard.Update.ReleaseLinkAccessibleName"));
        UpdateReleaseLink.Content = Loc.Get("Dashboard.Update.ReleaseLinkLabel");
        QuickActionsTitleText.Text = Loc.Get("Dashboard.QuickActions.Title");
        AutomationProperties.SetName(
            RuntimeQuickAction,
            Loc.Get("Dashboard.QuickAction.Runtime.AccessibleName"));
        RuntimeQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Runtime.Label");
        AutomationProperties.SetName(
            ProjectsQuickAction,
            Loc.Get("Dashboard.QuickAction.Projects.AccessibleName"));
        ProjectsQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Projects.Label");
        AutomationProperties.SetName(
            NetworkQuickAction,
            Loc.Get("Dashboard.QuickAction.Network.AccessibleName"));
        NetworkQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Network.Label");
        AutomationProperties.SetName(
            DownloadsQuickAction,
            Loc.Get("Dashboard.QuickAction.Downloads.AccessibleName"));
        DownloadsQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Downloads.Label");
        AutomationProperties.SetName(
            PathQuickAction,
            Loc.Get("Dashboard.QuickAction.Path.Label"));
        PathQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Path.Label");
        AutomationProperties.SetName(
            StorageQuickAction,
            Loc.Get("Dashboard.QuickAction.Storage.AccessibleName"));
        StorageQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Storage.Label");
        AutomationProperties.SetName(
            DoctorQuickAction,
            Loc.Get("Dashboard.QuickAction.Doctor.AccessibleName"));
        DoctorQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Doctor.Label");
        AutomationProperties.SetName(
            ActivityQuickAction,
            Loc.Get("Dashboard.QuickAction.Activity.AccessibleName"));
        ActivityQuickActionText.Text = Loc.Get("Dashboard.QuickAction.Activity.Label");
        AutomationProperties.SetName(
            PluginsQuickAction,
            Loc.Get("Dashboard.QuickAction.Plugins.AccessibleName"));
        PluginsQuickActionText.Text = Loc.Get("Dashboard.Plugins.Title");
        RuntimeOverviewTitleText.Text = Loc.Get("Dashboard.Runtime.Title");
        RuntimeOverviewSubtitleText.Text = Loc.Get("Dashboard.Runtime.Subtitle");
        AutomationProperties.SetName(
            RuntimeOverviewLink,
            Loc.Get("Dashboard.Runtime.OpenLink"));
        ToolTipService.SetToolTip(
            RuntimeOverviewLink,
            Loc.Get("Dashboard.Runtime.OpenLink"));
        PathHealthTitleText.Text = Loc.Get("Dashboard.PathHealth.Title");
        PathDirectoryCountLabel.Text = Loc.Get("Dashboard.PathHealth.DirectoryCountLabel");
        PathMissingCountLabel.Text = Loc.Get("Dashboard.PathHealth.MissingCountLabel");
        PathDuplicateCountLabel.Text = Loc.Get("Dashboard.PathHealth.DuplicateCountLabel");
        PathConflictCountLabel.Text = Loc.Get("Dashboard.PathHealth.ConflictCountLabel");
        PathDetailText.Text = Loc.Get("Dashboard.PathHealth.Checking");
        StorageTitleText.Text = Loc.Get("Dashboard.Storage.Title");
        StorageUsageText.Text = Loc.Get("Dashboard.Common.Loading");
        AutomationProperties.SetName(
            StorageLink,
            Loc.Get("Dashboard.Storage.OpenLink"));
        ToolTipService.SetToolTip(
            StorageLink,
            Loc.Get("Dashboard.Storage.OpenLink"));
        NetworkTitleText.Text = Loc.Get("Dashboard.Network.Title");
        NetworkProfileText.Text = Loc.Get("Dashboard.Common.Loading");
        TransferTitleText.Text = Loc.Get("Dashboard.Transfer.Title");
        DownloadLibraryText.Text = Loc.Get("Dashboard.Common.Loading");
        AutomationProperties.SetName(
            NetworkSettingsLink,
            Loc.Get("Dashboard.Network.OpenSettings"));
        ToolTipService.SetToolTip(
            NetworkSettingsLink,
            Loc.Get("Dashboard.Network.OpenSettings"));
        AutomationProperties.SetName(
            DownloadsLink,
            Loc.Get("Dashboard.Network.OpenDownloads"));
        ToolTipService.SetToolTip(
            DownloadsLink,
            Loc.Get("Dashboard.Network.OpenDownloads"));
        PluginsTitleText.Text = Loc.Get("Dashboard.Plugins.Title");
        PluginSummaryText.Text = Loc.Get("Dashboard.Common.Loading");
        PluginDetailText.Text = Loc.Get("Dashboard.Plugins.Detail");
        AutomationProperties.SetName(
            PluginsLink,
            Loc.Get("Dashboard.Plugins.OpenLink"));
        ToolTipService.SetToolTip(
            PluginsLink,
            Loc.Get("Dashboard.Plugins.OpenLink"));
        RecentProjectsTitleText.Text = Loc.Get("Dashboard.RecentProjects.Title");
        AutomationProperties.SetName(
            RecentProjectsLink,
            Loc.Get("Dashboard.RecentProjects.OpenLink"));
        ToolTipService.SetToolTip(
            RecentProjectsLink,
            Loc.Get("Dashboard.RecentProjects.OpenLink"));
        ProjectsEmptyText.Text = Loc.Get("Dashboard.RecentProjects.Empty");
        RecentActivityTitleText.Text = Loc.Get("Dashboard.RecentActivity.Title");
        AutomationProperties.SetName(
            RecentActivityLink,
            Loc.Get("Dashboard.RecentActivity.OpenLink"));
        ToolTipService.SetToolTip(
            RecentActivityLink,
            Loc.Get("Dashboard.RecentActivity.OpenLink"));
        ActivityEmptyText.Text = Loc.Get("Dashboard.RecentActivity.Empty");
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

            UpdateAvailableInfo.Message = Loc.Format(
                "Dashboard.Update.Message",
                ProductIdentityText.DisplayVersion(identity),
                update.LatestVersion);
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
            OverviewInfo.Title = Loc.Get("Dashboard.Messages.ReadFailedTitle");
            OverviewInfo.Message = Loc.Format(
                "Dashboard.Messages.ReadFailedMessage",
                exception.Message);
        }
    }

    private async Task RefreshAsync(bool fullScan)
    {
        SetBusy(true);
        OverviewInfo.Severity = InfoBarSeverity.Informational;
        OverviewInfo.Title = fullScan
            ? Loc.Get("Dashboard.Messages.FullScanTitle")
            : Loc.Get("Dashboard.Messages.QuickRefreshTitle");
        OverviewInfo.Message = fullScan
            ? Loc.Get("Dashboard.Messages.FullScanMessage")
            : Loc.Get("Dashboard.Messages.QuickRefreshMessage");

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
            OverviewInfo.Title = Loc.Get("Dashboard.Messages.RefreshFailedTitle");
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
            DownloadTransferText.Text = Loc.Get("Dashboard.Transfer.None");
            return;
        }

        if (snapshot.Error is not null)
        {
            DownloadTransferText.Text = Loc.Format(
                "Dashboard.Transfer.Failed",
                snapshot.FileName);
            return;
        }

        if (snapshot.Phase == ManagedTransferPhase.Completed)
        {
            string mode = snapshot.TransferMode == DownloadTransferMode.Segmented
                ? Loc.Format("Dashboard.Transfer.SegmentsDone", snapshot.TotalSegments)
                : Loc.Get("Dashboard.Transfer.SingleConnectionDone");
            DownloadTransferText.Text = Loc.Format(
                "Dashboard.Transfer.Ready",
                snapshot.FileName,
                mode);
            return;
        }

        if (snapshot.Phase == ManagedTransferPhase.Cancelled)
        {
            DownloadTransferText.Text = Loc.Format(
                "Dashboard.Transfer.Cancelled",
                snapshot.FileName);
            return;
        }

        string progress = snapshot.TotalBytes is long total && total > 0
            ? $"{snapshot.CompletedBytes * 100d / total:F0}%"
            : FormatBytes(snapshot.CompletedBytes);
        string segments = snapshot.TotalSegments > 0
            ? Loc.Format(
                "Dashboard.Transfer.Segments",
                snapshot.CompletedSegments,
                snapshot.TotalSegments)
            : string.Empty;
        DownloadTransferText.Text = Loc.Format(
            "Dashboard.Transfer.InProgress",
            snapshot.FileName,
            progress,
            segments);
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
            PathDetailText.Text = Loc.Get("Dashboard.PathHealth.NotScanned");
        }

        if (snapshot.Storage is OverviewStorageStatus storage)
        {
            StorageUsageText.Text = FormatBytes(storage.TotalBytes);
            string largest = storage.LargestCacheName is null
                ? Loc.Get("Dashboard.Storage.NoCacheFound")
                : Loc.Format(
                    "Dashboard.Storage.LargestCache",
                    storage.LargestCacheName,
                    FormatBytes(storage.LargestCacheBytes));
            string free = storage.ManagedDriveFreeBytes is long freeBytes
                ? Loc.Format("Dashboard.Storage.DriveFree", FormatBytes(freeBytes))
                : string.Empty;
            StorageDetailText.Text = Loc.Format(
                "Dashboard.Storage.Summary",
                storage.ExistingDirectoryCount,
                storage.TotalFiles.ToString("N0"),
                largest,
                free);
            if (storage.Errors.Count > 0)
            {
                StorageDetailText.Text += Loc.Format(
                    "Dashboard.Storage.Errors",
                    storage.Errors.Count);
            }
        }
        else
        {
            StorageUsageText.Text = Loc.Get("Dashboard.Storage.NoStats");
            StorageDetailText.Text = Loc.Get("Dashboard.Storage.FullScanOnly");
        }

        ManagedRootText.Text = snapshot.ManagedRoot;
        NetworkProfileText.Text = snapshot.Network.Summary;
        NetworkDetailText.Text = snapshot.Network.Detail;
        DownloadLibraryText.Text = Loc.Format(
            "Dashboard.Transfer.LibrarySummary",
            snapshot.Downloads.FileCount.ToString("N0"),
            FormatBytes(snapshot.Downloads.TotalBytes));
        UpdateDownloadTransfer(_downloadManager.Snapshot);

        int disabledProviderCount = snapshot.Providers.ImportedProviderCount
            - snapshot.Providers.EnabledImportedProviderCount;
        PluginSummaryText.Text = Loc.Format(
            "Dashboard.Plugins.Summary",
            snapshot.Providers.BuiltInProviderCount,
            snapshot.Providers.EnabledImportedProviderCount,
            disabledProviderCount);
        PluginDetailText.Text = snapshot.Providers.ErrorCount == 0
            ? Loc.Get("Dashboard.Plugins.Classification")
            : Loc.Format("Dashboard.Plugins.Errors", snapshot.Providers.ErrorCount);

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
            OverviewInfo.Title = Loc.Get("Dashboard.Messages.PartialUnavailableTitle");
            OverviewInfo.Message = string.Join(
                    Loc.Get("Dashboard.Messages.ErrorSeparator"),
                    snapshot.Errors.Take(3))
                + (snapshot.Errors.Count > 3
                    ? Loc.Format("Dashboard.Messages.MoreErrors", snapshot.Errors.Count - 3)
                    : string.Empty);
            return;
        }

        if (snapshot.Path is { } pathSummary
            && (pathSummary.MissingDirectoryCount > 0
                || pathSummary.DuplicateDirectoryCount > 0
                || pathSummary.CommandConflictCount > 0))
        {
            OverviewInfo.Severity = InfoBarSeverity.Warning;
            OverviewInfo.Title = Loc.Get("Dashboard.Messages.ActionableTitle");
            OverviewInfo.Message = CreateSnapshotTimestamp(snapshot, cached)
                + Loc.Get("Dashboard.Messages.ActionableSuffix");
            return;
        }

        OverviewInfo.Severity = cached
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Success;
        OverviewInfo.Title = cached
            ? Loc.Get("Dashboard.Messages.LoadedTitle")
            : snapshot.Depth == OverviewSnapshotDepth.Full
                ? Loc.Get("Dashboard.Messages.FullScanDoneTitle")
                : Loc.Get("Dashboard.Messages.QuickDoneTitle");
        OverviewInfo.Message = CreateSnapshotTimestamp(snapshot, cached);
    }

    private void RenderEmptySnapshot(string managedRoot)
    {
        RuntimeOverviewList.ItemsSource = Array.Empty<RuntimeOverviewRow>();
        PathDirectoryCountText.Text = "—";
        PathMissingCountText.Text = "—";
        PathDuplicateCountText.Text = "—";
        PathConflictCountText.Text = "—";
        PathDetailText.Text = Loc.Get("Dashboard.PathHealth.NoSnapshot");
        StorageUsageText.Text = Loc.Get("Dashboard.Storage.NoStats");
        StorageDetailText.Text = Loc.Get("Dashboard.Storage.FullScanOnly");
        ManagedRootText.Text = managedRoot;
        NetworkProfileText.Text = Loc.Get("Dashboard.Common.NoSnapshot");
        NetworkDetailText.Text = Loc.Get("Dashboard.Network.EmptyDetail");
        DownloadLibraryText.Text = Loc.Get("Dashboard.Common.NoSnapshot");
        UpdateDownloadTransfer(_downloadManager.Snapshot);
        PluginSummaryText.Text = Loc.Get("Dashboard.Common.NoSnapshot");
        PluginDetailText.Text = Loc.Get("Dashboard.Plugins.EmptyDetail");
        RecentProjectsList.ItemsSource = Array.Empty<RecentProjectRow>();
        ProjectsEmptyText.Visibility = Visibility.Visible;
        RecentActivityList.ItemsSource = Array.Empty<RecentActivityRow>();
        ActivityEmptyText.Visibility = Visibility.Visible;
        OverviewInfo.Severity = InfoBarSeverity.Informational;
        OverviewInfo.Title = Loc.Get("Dashboard.Messages.EmptyTitle");
        OverviewInfo.Message = Loc.Get("Dashboard.Messages.EmptyMessage");
        // First-run guidance must be actionable: the refresh affordance is a
        // small icon button in the header, easy for a new user to miss.
        OverviewInfo.ActionButton = CreateFirstRunRefreshButton();
    }

    private Button CreateFirstRunRefreshButton()
    {
        Button button = new() { Content = Loc.Get("Dashboard.Refresh.Quick.Label") };
        AutomationProperties.SetName(button, Loc.Get("Dashboard.Refresh.FirstRunAccessibleName"));
        button.Click += async (_, _) =>
        {
            OverviewInfo.ActionButton = CancelScanButton;
            await RefreshAsync(fullScan: false);
        };
        return button;
    }

    private static string CreateSnapshotTimestamp(OverviewSnapshot snapshot, bool cached)
    {
        string source = cached
            ? Loc.Get("Dashboard.Timestamp.SnapshotSource")
            : Loc.Get("Dashboard.Timestamp.UpdateSource");
        string fullScan = snapshot.LastFullScanAtUtc is DateTimeOffset full
            ? Loc.Format(
                "Dashboard.Timestamp.LastFullScan",
                full.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
            : Loc.Get("Dashboard.Timestamp.NeverFullScanned");
        return Loc.Format(
            "Dashboard.Timestamp.Caption",
            source,
            snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            fullScan);
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
        OverviewInfo.Title = Loc.Get("Dashboard.Messages.CancellingTitle");
        OverviewInfo.Message = Loc.Get("Dashboard.Messages.CancellingMessage");
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
                : Loc.Get("Dashboard.RecentProjects.MissingDirectory");
            AutomationName = Exists
                ? Loc.Format("Dashboard.RecentProjects.OpenAutomationName", Name)
                : Loc.Format("Dashboard.RecentProjects.MissingAutomationName", Name);
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
