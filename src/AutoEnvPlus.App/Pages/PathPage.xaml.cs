using System.Globalization;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class PathPage : Page
{
    private static readonly string[] KnownCommands =
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
    private readonly string _cliPath;
    private readonly string _nativeShimPath;
    private readonly UserPathManager _pathManager;
    private bool _isBusy;

    public PathPage()
    {
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _cliPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus.exe");
        _nativeShimPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus-shim.exe");
        _pathManager = new UserPathManager(
            _managedRoot,
            new WindowsUserEnvironmentVariableStore());
        InitializeComponent();
        ApplyLocalizedStrings();
        Loaded += OnPageLoaded;
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
        PageTitleText.Text = Loc.Get("Path.Header.Title");
        PageSubtitleText.Text = Loc.Get("Path.Header.Subtitle");
        ShimStateText.Text = Loc.Get("Path.Shim.StateInitial");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshPathButton,
            Loc.Get("Path.Refresh.AccessibleName"));
        ToolTipService.SetToolTip(
            RefreshPathButton,
            Loc.Get("Path.Refresh.AccessibleName"));
        ActivateShimButtonText.Text = Loc.Get("Path.Shim.ActivateLabel");
        CommandRoutePivotItem.Header = Loc.Get("Path.CommandRoute.Header");
        CommandRouteCommandHeaderText.Text = Loc.Get("Path.CommandRoute.CommandColumn");
        CommandRouteEntryHeaderText.Text = Loc.Get("Path.CommandRoute.EntryColumn");
        CommandRouteStateHeaderText.Text = Loc.Get("Path.CommandRoute.StateColumn");
        PathDirectoryPivotItem.Header = Loc.Get("Path.Directory.Header");
        PathDirectoryIndexHeaderText.Text = Loc.Get("Path.Directory.IndexColumn");
        PathDirectoryScopeHeaderText.Text = Loc.Get("Path.Directory.ScopeColumn");
        PathDirectoryValueHeaderText.Text = Loc.Get("Path.Directory.DirectoryColumn");
        PathDirectoryStatusHeaderText.Text = Loc.Get("Path.Directory.StatusColumn");
        SnapshotPivotItem.Header = Loc.Get("Path.Snapshots.Header");
        UserPathEditorPivotItem.Header = Loc.Get("Path.UserPathEditor.Header");
        UserPathEditorTitleText.Text = Loc.Get("Path.UserPathEditor.Header");
        UserPathEditorHintText.Text = Loc.Get("Path.UserPathEditor.Hint");
        SmartRepairButtonText.Text = Loc.Get("Path.UserPathEditor.SmartRepair.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SmartRepairButton,
            Loc.Get("Path.UserPathEditor.SmartRepair.Label"));
        ToolTipService.SetToolTip(
            SmartRepairButton,
            Loc.Get("Path.UserPathEditor.SmartRepair.ToolTip"));
        RollbackSectionTitleText.Text = Loc.Get("Path.Snapshots.SectionTitle");
        RollbackSectionHintText.Text = Loc.Get("Path.Snapshots.SectionHint");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshSnapshotsButton,
            Loc.Get("Path.Snapshots.RefreshAccessibleName"));
        ToolTipService.SetToolTip(
            RefreshSnapshotsButton,
            Loc.Get("Path.Snapshots.RefreshAccessibleName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RollbackPathButton,
            Loc.Get("Path.Snapshots.RollbackLabel"));
        RollbackPathButtonText.Text = Loc.Get("Path.Snapshots.RollbackLabel");
        SnapshotSummaryText.Text = Loc.Get("Path.Snapshots.SummaryInitial");
        SnapshotEmptyText.Text = Loc.Get("Path.Snapshots.Empty");
        SwitchScopeRepeater.ItemsSource = new[]
        {
            new SwitchScopeRow(
                Loc.Get("Path.Scopes.Session"),
                Loc.Get("Path.Scopes.SessionPriority"),
                Loc.Get("Path.Scopes.SessionDetail")),
            new SwitchScopeRow(
                Loc.Get("Path.Scopes.Project"),
                Loc.Get("Path.Scopes.ProjectPriority"),
                Loc.Get("Path.Scopes.ProjectDetail")),
            new SwitchScopeRow(
                Loc.Get("Path.Scopes.Global"),
                Loc.Get("Path.Scopes.GlobalPriority"),
                Loc.Get("Path.Scopes.GlobalDetail")),
            new SwitchScopeRow(
                Loc.Get("Path.Scopes.Auto"),
                Loc.Get("Path.Scopes.AutoPriority"),
                Loc.Get("Path.Scopes.AutoDetail")),
        };
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        SetBusy(true);
        try
        {
            await LoadReportAsync();
            await LoadUserPathEntriesAsync();
            await LoadSnapshotsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadReportAsync()
    {
        // The inspection walks every PATH entry and probes 18 commands across
        // PATHEXT combinations; keep that filesystem work off the UI thread.
        PathInspectionReport report = await Task.Run(
            () => new PathInspector().InspectCurrent(KnownCommands));

        PathList.ItemsSource = report.Entries.Select(entry => new PathEntryRow(
            (entry.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ScopeLabel(entry.Scope),
            entry.ExpandedValue,
            StatusLabel(entry))).ToArray();

        string shimDirectory = Path.Combine(_managedRoot, "shims");
        CommandRouteList.ItemsSource = report.CommandResolutions
            .Select(resolution => new CommandRouteRow(
                resolution.Command,
                resolution.Winner?.ExecutablePath ?? Loc.Get("Path.CommandRoute.NotFound"),
                RouteState(resolution, shimDirectory)))
            .ToArray();
        ShimRouteState shim = InspectShimState(report, shimDirectory);
        ShimStateText.Text = shim.Message;
        bool isHealthy = report.IsHealthy && shim.IsHealthy;
        SummaryInfo.Severity = isHealthy
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        SummaryInfo.Title = isHealthy
            ? Loc.Get("Path.Summary.HealthyTitle")
            : Loc.Get("Path.Summary.UnhealthyTitle");
        SummaryInfo.Message = string.Join(
            Loc.Get("Path.Text.Separator"),
            Loc.Format("Path.Summary.Missing", report.MissingCount),
            Loc.Format("Path.Summary.Duplicates", report.DuplicateCount),
            Loc.Format("Path.Summary.Conflicts", report.Conflicts.Count),
            Loc.Format(
                "Path.Summary.ShimManaged",
                shim.ManagedCommandCount,
                KnownCommands.Length));
    }

    private async void OnRefreshPathClicked(object sender, RoutedEventArgs args)
    {
        SetBusy(true);
        try
        {
            await LoadReportAsync();
            await LoadUserPathEntriesAsync();
            await LoadSnapshotsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadUserPathEntriesAsync()
    {
        try
        {
            // Inspect the persisted user PATH in isolation so every row is a
            // user-owned entry with its own existence/duplicate verdict.
            string? userPath = System.Environment.GetEnvironmentVariable(
                "PATH",
                EnvironmentVariableTarget.User);
            PathInspectionReport userReport = await Task.Run(
                () => new PathInspector().Inspect(userPath, userPath, null));
            UserPathEntryRow[] rows = userReport.Entries
                .Select(entry => new UserPathEntryRow(
                    entry.RawValue,
                    entry.ExpandedValue,
                    StatusLabel(entry)))
                .ToArray();
            UserPathList.ItemsSource = rows;
            UserPathList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            UserPathEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UserPathEmptyText.Text = Loc.Get("Path.UserPathEditor.Empty");
            UserPathSummaryText.Text = rows.Length == 0
                ? string.Empty
                : Loc.Format(
                    "Path.UserPathEditor.Summary",
                    rows.Length,
                    userReport.MissingCount,
                    userReport.DuplicateCount);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            UserPathList.ItemsSource = null;
            UserPathList.Visibility = Visibility.Collapsed;
            UserPathEmptyText.Visibility = Visibility.Visible;
            UserPathEmptyText.Text = Loc.Get("Path.UserPathEditor.LoadFailedTitle");
            UserPathSummaryText.Text = CoreErrorText.Localize(exception.Message);
        }
    }

    private void OnRemoveEntryButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button
            && button.DataContext is UserPathEntryRow row
            && button.Content is StackPanel panel
            && panel.Children.Count > 1
            && panel.Children[1] is TextBlock text)
        {
            text.Text = Loc.Get("Path.UserPathEditor.RemoveEntry.Label");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                Loc.Format("Path.UserPathEditor.RemoveEntry.AccessibleName", row.ExpandedValue));
        }
    }

    private async void OnRemoveUserPathEntryClicked(object sender, RoutedEventArgs args)
    {
        if (_isBusy || sender is not Button { Tag: UserPathEntryRow row })
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Path.UserPathEditor.RemoveConfirmTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Path.UserPathEditor.RemoveConfirmContent",
                    row.ExpandedValue),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Path.UserPathEditor.RemoveConfirmButton"),
            CloseButtonText = Loc.Get("Path.Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            UserPathMutationPlan plan = _pathManager.PlanRemove(row.ExpandedValue);
            UserPathMutationResult result = await _pathManager.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Path.Messages.PathApplyFailed"));
            }

            await LoadReportAsync();
            await LoadUserPathEntriesAsync();
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = Loc.Get("Path.UserPathEditor.RemovedTitle");
            SummaryInfo.Message = Loc.Format(
                "Path.UserPathEditor.RemovedMessage",
                row.ExpandedValue);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                Loc.Format("Path.Activity.EntryRemoved", row.ExpandedValue),
                [row.ExpandedValue],
                result.SnapshotPath,
                result.SnapshotPath);
            await LoadSnapshotsAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Path.UserPathEditor.RemoveFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Failed,
                Loc.Format(
                    "Path.Activity.EntryRemoveFailed",
                    exception.GetType().Name),
                [row.ExpandedValue]);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSmartRepairClicked(object sender, RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        UserPathRepairPlan repair = _pathManager.PlanRepair();
        if (!repair.Changed)
        {
            SummaryInfo.Severity = InfoBarSeverity.Informational;
            SummaryInfo.Title = Loc.Get("Path.UserPathEditor.RepairNoChangeTitle");
            SummaryInfo.Message = Loc.Get("Path.UserPathEditor.RepairNoChangeMessage");
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Path.UserPathEditor.RepairConfirmTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Path.UserPathEditor.RepairConfirmContent",
                    repair.RemovedMissing.Count,
                    repair.RemovedDuplicates.Count),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Path.UserPathEditor.RepairConfirmButton"),
            CloseButtonText = Loc.Get("Path.Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            UserPathMutationResult result = await _pathManager.ApplyAsync(repair.Mutation);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Path.Messages.PathApplyFailed"));
            }

            await LoadReportAsync();
            await LoadUserPathEntriesAsync();
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = Loc.Get("Path.UserPathEditor.RepairedTitle");
            SummaryInfo.Message = Loc.Format(
                "Path.UserPathEditor.RepairedMessage",
                repair.RemovedCount);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                Loc.Format("Path.Activity.Repaired", repair.RemovedCount),
                repair.Mutation.Before.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(entry => entry.Trim())
                    .ToArray(),
                result.SnapshotPath,
                result.SnapshotPath);
            await LoadSnapshotsAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Path.UserPathEditor.RepairFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Failed,
                Loc.Format(
                    "Path.Activity.RepairFailed",
                    exception.GetType().Name));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnActivateShimClicked(object sender, RoutedEventArgs args)
    {
        if (!File.Exists(_cliPath))
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Path.Messages.CliMissingTitle");
            SummaryInfo.Message = Loc.Format("Path.Messages.CliMissing", _cliPath);
            return;
        }

        string shimDirectory = Path.Combine(_managedRoot, "shims");
        UserPathMutationPlan plan = _pathManager.PlanEnsureFirst(shimDirectory);
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Path.Shim.ConfirmTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Path.Shim.ConfirmContent",
                    shimDirectory,
                    plan.Changed
                        ? Loc.Get("Path.Shim.PathChangeAdd")
                        : Loc.Get("Path.Shim.PathChangeNone")),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Path.Shim.ConfirmButton"),
            CloseButtonText = Loc.Get("Path.Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            CommandShimInstallResult shims = await new CommandShimManager().InstallAsync(
                _managedRoot,
                _cliPath,
                [],
                File.Exists(_nativeShimPath) ? _nativeShimPath : null);
            plan = _pathManager.PlanEnsureFirst(shims.ShimDirectory);
            UserPathMutationResult result = await _pathManager.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Path.Messages.PathApplyFailed"));
            }

            await LoadReportAsync();
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = Loc.Get("Path.Messages.ShimEnabledTitle");
            string implementation = shims.Implementation == CommandShimImplementation.NativeWin32
                ? Loc.Get("Path.Shim.NativeImplementation")
                : Loc.Get("Path.Shim.CmdImplementation");
            SummaryInfo.Message = result.Changed
                ? Loc.Format(
                    "Path.Messages.ShimInstalledChanged",
                    implementation,
                    result.SnapshotPath)
                : Loc.Format("Path.Messages.ShimInstalledUnchanged", implementation);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                result.Changed
                    ? Loc.Format("Path.Activity.ShimInstalledChanged", implementation)
                    : Loc.Format("Path.Activity.ShimInstalledUnchanged", implementation),
                [shims.ShimDirectory],
                result.SnapshotPath,
                result.SnapshotPath);
            await LoadSnapshotsAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Path.Messages.ShimEnableFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Failed,
                Loc.Format(
                    "Path.Activity.ShimEnableFailed",
                    exception.GetType().Name),
                [shimDirectory]);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRefreshSnapshotsClicked(object sender, RoutedEventArgs args)
    {
        SetBusy(true);
        try
        {
            await LoadSnapshotsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSnapshotSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateRollbackButton();

    private async void OnRollbackPathClicked(object sender, RoutedEventArgs args)
    {
        if (SnapshotList.SelectedItem is not PathSnapshotRow { CanRollback: true } selected)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Path.Snapshots.RollbackLabel"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Path.Snapshots.ConfirmContent",
                    selected.CreatedAt,
                    selected.AddedDirectory),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Path.Snapshots.RollbackLabel"),
            CloseButtonText = Loc.Get("Path.Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            UserPathMutationResult result = await _pathManager.RollbackAsync(
                selected.Snapshot.SnapshotPath);
            if (!result.Success)
            {
                SummaryInfo.Severity = InfoBarSeverity.Error;
                SummaryInfo.Title = Loc.Get("Path.Messages.RollbackFailedTitle");
                SummaryInfo.Message = result.Error ?? Loc.Get("Path.Messages.RollbackFailed");
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PathRollback,
                    ActivityStatus.Failed,
                    Loc.Get("Path.Activity.RollbackRejected"),
                    [selected.AddedDirectory],
                    selected.Snapshot.SnapshotPath,
                    selected.Snapshot.SnapshotPath);
                await LoadSnapshotsAsync();
                return;
            }

            await LoadReportAsync();
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = Loc.Get("Path.Messages.RollbackSuccessTitle");
            SummaryInfo.Message = Loc.Get("Path.Messages.RollbackSuccess");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathRollback,
                ActivityStatus.Succeeded,
                Loc.Get("Path.Activity.RollbackSucceeded"),
                [selected.AddedDirectory],
                selected.Snapshot.SnapshotPath,
                selected.Snapshot.SnapshotPath);
            await LoadSnapshotsAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Path.Messages.RollbackFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathRollback,
                ActivityStatus.Failed,
                Loc.Format("Path.Activity.RollbackFailed", exception.GetType().Name),
                [selected.AddedDirectory],
                selected.Snapshot.SnapshotPath,
                selected.Snapshot.SnapshotPath);
            await LoadSnapshotsAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadSnapshotsAsync()
    {
        try
        {
            IReadOnlyList<UserPathSnapshotInfo> snapshots = await _pathManager.GetSnapshotsAsync();
            PathSnapshotRow[] rows = snapshots
                .Select(snapshot => new PathSnapshotRow(
                    snapshot,
                    snapshot.CreatedAtUtc.ToLocalTime().ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.CurrentCulture),
                    SnapshotSubjectLabel(snapshot),
                    SnapshotStateLabel(snapshot.State)))
                .ToArray();
            SnapshotList.ItemsSource = rows;
            SnapshotList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            SnapshotEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SnapshotEmptyText.Text = Loc.Get("Path.Snapshots.Empty");

            int availableCount = rows.Count(row => row.CanRollback);
            SnapshotSummaryText.Text = rows.Length == 0
                ? Loc.Get("Path.Snapshots.SummaryEmpty")
                : Loc.Format("Path.Snapshots.SummaryCount", rows.Length, availableCount);
            SnapshotList.SelectedItem = rows.FirstOrDefault(row => row.CanRollback)
                ?? rows.FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            SnapshotList.ItemsSource = null;
            SnapshotList.Visibility = Visibility.Collapsed;
            SnapshotEmptyText.Visibility = Visibility.Visible;
            SnapshotEmptyText.Text = Loc.Get("Path.Snapshots.LoadFailed");
            SnapshotSummaryText.Text = exception.Message;
        }

        UpdateRollbackButton();
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ActivateShimButton.IsEnabled = !isBusy;
        RefreshPathButton.IsEnabled = !isBusy;
        RefreshSnapshotsButton.IsEnabled = !isBusy;
        SmartRepairButton.IsEnabled = !isBusy;
        CommandRouteList.IsEnabled = !isBusy;
        PathList.IsEnabled = !isBusy;
        UserPathList.IsEnabled = !isBusy;
        SnapshotList.IsEnabled = !isBusy;
        UpdateRollbackButton();
    }

    private void UpdateRollbackButton()
    {
        RollbackPathButton.IsEnabled = !_isBusy
            && SnapshotList.SelectedItem is PathSnapshotRow { CanRollback: true };
    }

    private static string ScopeLabel(PathEntryScope scope) => scope switch
    {
        PathEntryScope.User => Loc.Get("Path.Scope.User"),
        PathEntryScope.Machine => Loc.Get("Path.Scope.Machine"),
        PathEntryScope.UserAndMachine => Loc.Get("Path.Scope.UserAndMachine"),
        _ => Loc.Get("Path.Scope.Process"),
    };

    private static string StatusLabel(PathInspectionEntry entry)
    {
        if (!entry.Exists)
        {
            return Loc.Get("Path.Status.Missing");
        }

        return entry.IsDuplicate
            ? Loc.Get("Path.Status.Duplicate")
            : Loc.Get("Path.Status.Ok");
    }

    private static string SnapshotStateLabel(UserPathSnapshotState state) => state switch
    {
        UserPathSnapshotState.RollbackAvailable => Loc.Get("Path.SnapshotState.Available"),
        UserPathSnapshotState.AlreadyRolledBack => Loc.Get("Path.SnapshotState.RolledBack"),
        _ => Loc.Get("Path.SnapshotState.Changed"),
    };

    private static string SnapshotSubjectLabel(UserPathSnapshotInfo snapshot) => snapshot.Kind switch
    {
        // Repair snapshots have no single subject directory; the kind label
        // itself is what the rollback list should show.
        UserPathSnapshotKind.Repair => Loc.Get("Path.Snapshots.SmartRepair"),
        UserPathSnapshotKind.RemoveDirectory => Loc.Format(
            "Path.Snapshots.RemovedDirectory",
            snapshot.AddedDirectory),
        _ => snapshot.AddedDirectory,
    };

    private static string RouteState(
        CommandResolution resolution,
        string shimDirectory)
    {
        if (resolution.Winner is null)
        {
            return Loc.Get("Path.CommandRoute.NotFound");
        }

        if (PathsEqual(
            Path.GetDirectoryName(resolution.Winner.ExecutablePath),
            shimDirectory))
        {
            return resolution.Candidates.Count > 1
                ? Loc.Format("Path.RouteState.ShimCandidates", resolution.Candidates.Count)
                : Loc.Get("Path.RouteState.Shim");
        }

        return resolution.Candidates.Count > 1
            ? Loc.Format("Path.RouteState.DirectCandidates", resolution.Candidates.Count)
            : Loc.Get("Path.RouteState.Direct");
    }

    private ShimRouteState InspectShimState(
        PathInspectionReport report,
        string shimDirectory)
    {
        bool persisted = (System.Environment.GetEnvironmentVariable(
                "PATH",
                EnvironmentVariableTarget.User) ?? string.Empty)
            .Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => PathsEqual(
                System.Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')),
                shimDirectory));
        if (!Directory.Exists(shimDirectory))
        {
            return new ShimRouteState(
                !persisted,
                0,
                persisted
                    ? Loc.Get("Path.Shim.StateMissingDirectory")
                    : Loc.Get("Path.Shim.StateNotConfigured"));
        }

        string[] missing = KnownCommands.Where(command =>
                !File.Exists(Path.Combine(shimDirectory, command + ".exe"))
                && !File.Exists(Path.Combine(shimDirectory, command + ".cmd")))
            .ToArray();
        int managedCount = report.CommandResolutions.Count(resolution =>
            resolution.Winner is not null
            && PathsEqual(
                Path.GetDirectoryName(resolution.Winner.ExecutablePath),
                shimDirectory));
        if (missing.Length > 0)
        {
            return new ShimRouteState(
                false,
                managedCount,
                Loc.Format(
                    "Path.Shim.StateIncomplete",
                    string.Join(Loc.Get("Path.Text.ListSeparator"), missing)));
        }

        if (!persisted)
        {
            return new ShimRouteState(
                false,
                managedCount,
                Loc.Get("Path.Shim.StateNotPersisted"));
        }

        if (managedCount == KnownCommands.Length)
        {
            return new ShimRouteState(
                true,
                managedCount,
                Loc.Get("Path.Shim.StateActive"));
        }

        return new ShimRouteState(
            true,
            managedCount,
            Loc.Get("Path.Shim.StatePersisted"));
    }

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

    private sealed record PathEntryRow(string Index, string Scope, string Path, string Status);

    private sealed record UserPathEntryRow(
        string RawValue,
        string ExpandedValue,
        string Status);

    private sealed record CommandRouteRow(string Command, string Winner, string State);

    private sealed record SwitchScopeRow(string Scope, string Priority, string Detail);

    private sealed record ShimRouteState(
        bool IsHealthy,
        int ManagedCommandCount,
        string Message);

    private sealed record PathSnapshotRow(
        UserPathSnapshotInfo Snapshot,
        string CreatedAt,
        string AddedDirectory,
        string State)
    {
        public bool CanRollback => Snapshot.CanRollback;

        public string SnapshotPath => Snapshot.SnapshotPath;
    }
}
