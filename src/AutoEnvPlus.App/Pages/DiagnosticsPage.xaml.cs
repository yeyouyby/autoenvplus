using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Diagnostics;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private CancellationTokenSource _pageCancellation = new();
    private CancellationTokenSource? _scanCancellation;
    private EnvironmentDiagnosticReport? _lastReport;
    private bool _scanning;
    private readonly string _managedRoot;
    private readonly string _cliPath;
    private readonly string _nativeShimPath;
    private readonly UserPathManager _pathManager;

    public DiagnosticsPage()
    {
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _cliPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus.exe");
        _nativeShimPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus-shim.exe");
        _pathManager = new UserPathManager(
            _managedRoot,
            new WindowsUserEnvironmentVariableStore());
        InitializeComponent();
        ApplyLocalizedStrings();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateScopeControls();
        ShowPendingScopes();
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
        PageTitleText.Text = Loc.Get("Diagnostics.Header.Title");
        PageSubtitleText.Text = Loc.Get("Diagnostics.Header.Subtitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DiagnosticProgress,
            Loc.Get("Diagnostics.Progress.AccessibleName"));
        ToolTipService.SetToolTip(
            ExportButton,
            Loc.Get("Diagnostics.Export.ToolTip"));
        ExportButtonText.Text = Loc.Get("Diagnostics.Export.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            CancelButton,
            Loc.Get("Diagnostics.Cancel.AccessibleName"));
        ToolTipService.SetToolTip(
            CancelButton,
            Loc.Get("Diagnostics.Cancel.ToolTip"));
        ToolTipService.SetToolTip(
            RefreshButton,
            Loc.Get("Diagnostics.Scan.ToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshButton,
            Loc.Get("Diagnostics.Scan.AccessibleName"));
        ScanButtonText.Text = Loc.Get("Diagnostics.Scan.Label");
        ScopeExpander.Header = Loc.Get("Diagnostics.Scope.Header");
        PathScopeCheckBox.Content = Loc.Get("Diagnostics.Scope.Path");
        ManagedScopeCheckBox.Content = Loc.Get("Diagnostics.Scope.Managed");
        ProviderScopeCheckBox.Content = Loc.Get("Diagnostics.Scope.Provider");
        ProjectScopeCheckBox.Content = Loc.Get("Diagnostics.Scope.Project");
        StorageScopeCheckBox.Content = Loc.Get("Diagnostics.Scope.Storage");
        ConnectivityScopeCheckBox.Content = Loc.Get("Diagnostics.Scope.Connectivity");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ProjectPathTextBox,
            Loc.Get("Diagnostics.ProjectPath.AccessibleName"));
        ProjectPathTextBox.PlaceholderText = Loc.Get("Diagnostics.ProjectPath.PlaceholderText");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BrowseProjectButton,
            Loc.Get("Diagnostics.BrowseProject.AccessibleName"));
        ToolTipService.SetToolTip(
            BrowseProjectButton,
            Loc.Get("Diagnostics.ProjectPath.PlaceholderText"));
        SummaryInfo.Title = Loc.Get("Diagnostics.Summary.WaitingTitle");
        SummaryInfo.Message = Loc.Get("Diagnostics.Summary.WaitingMessage");
        SummaryPivotItem.Header = Loc.Get("Diagnostics.Summary.Header");
        IssuePivotItem.Header = Loc.Get("Diagnostics.Issues.Header");
        CommandPivotItem.Header = Loc.Get("Diagnostics.Commands.Header");
        CommandHeaderText.Text = Loc.Get("Diagnostics.Commands.CommandColumn");
        CommandEntryHeaderText.Text = Loc.Get("Diagnostics.Commands.EntryColumn");
        CommandCandidateHeaderText.Text = Loc.Get("Diagnostics.Commands.CandidateColumn");
        RuntimePivotItem.Header = Loc.Get("Diagnostics.Runtime.Header");
        GlobalSelectionTitleText.Text = Loc.Get("Diagnostics.Runtime.GlobalSelectionTitle");
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        // The shell caches this page across navigations; the previous visit's
        // Unloaded cancelled the token, so re-arm it on every load.
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
        _pageCancellation = new CancellationTokenSource();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _scanCancellation?.Cancel();
        _pageCancellation.Cancel();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) =>
        await RefreshAsync();

    private void OnCancelClicked(object sender, RoutedEventArgs args) =>
        _scanCancellation?.Cancel();

    private void OnIssueRepairButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button
            && button.DataContext is IssueRow { Repair: { } repair, Title: { } title }
            && button.Content is StackPanel panel
            && panel.Children.Count > 1
            && panel.Children[1] is TextBlock text)
        {
            text.Text = Loc.Get("Diagnostics.Repair.Button.Label");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button,
                Loc.Format("Diagnostics.Repair.Button.AccessibleName", title));
            ToolTipService.SetToolTip(button, RepairToolTip(repair));
        }
    }

    private static string RepairToolTip(DiagnosticRepairKind repair) => repair switch
    {
        DiagnosticRepairKind.UserPathSmartRepair =>
            Loc.Get("Diagnostics.Repair.Path.ToolTip"),
        DiagnosticRepairKind.ShimReinstall =>
            Loc.Get("Diagnostics.Repair.ShimReinstall.ToolTip"),
        DiagnosticRepairKind.ShimEnsureFirst =>
            Loc.Get("Diagnostics.Repair.ShimFirst.ToolTip"),
        _ => string.Empty,
    };

    private async void OnIssueRepairClicked(object sender, RoutedEventArgs args)
    {
        if (_scanning || sender is not Button { Tag: IssueRow { Repair: { } repair } row })
        {
            return;
        }

        switch (repair)
        {
            case DiagnosticRepairKind.UserPathSmartRepair:
                await RepairUserPathAsync();
                break;
            case DiagnosticRepairKind.ShimReinstall:
                await RepairShimReinstallAsync();
                break;
            case DiagnosticRepairKind.ShimEnsureFirst:
                await RepairShimEnsureFirstAsync();
                break;
        }
    }

    private async Task RepairUserPathAsync()
    {
        UserPathRepairPlan repair = _pathManager.PlanRepair();
        if (!repair.Changed)
        {
            SummaryInfo.Severity = InfoBarSeverity.Informational;
            SummaryInfo.Title = Loc.Get("Diagnostics.Repair.PathNoChangeTitle");
            SummaryInfo.Message = Loc.Get("Diagnostics.Repair.PathNoChangeMessage");
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Diagnostics.Repair.PathConfirmTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Diagnostics.Repair.PathConfirmContent",
                    repair.RemovedMissing.Count,
                    repair.RemovedDuplicates.Count),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Diagnostics.Repair.PathConfirmButton"),
            CloseButtonText = Loc.Get("Diagnostics.Repair.Cancel"),
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
                    result.Error ?? Loc.Get("Diagnostics.Repair.ApplyFailed"));
            }

            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                Loc.Format("Diagnostics.Repair.Activity.Path", repair.RemovedCount),
                [result.SnapshotPath ?? string.Empty],
                result.SnapshotPath,
                result.SnapshotPath);
            await FinishRepairAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            await LogRepairFailureAsync(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RepairShimReinstallAsync()
    {
        if (!File.Exists(_cliPath))
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Diagnostics.Repair.FailedTitle");
            SummaryInfo.Message = Loc.Format("Diagnostics.Repair.CliMissing", _cliPath);
            return;
        }

        string shimDirectory = Path.Combine(_managedRoot, "shims");
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Diagnostics.Repair.ShimReinstallConfirmTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Diagnostics.Repair.ShimReinstallConfirmContent",
                    shimDirectory),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Diagnostics.Repair.ShimReinstallConfirmButton"),
            CloseButtonText = Loc.Get("Diagnostics.Repair.Cancel"),
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
            UserPathMutationPlan plan = _pathManager.PlanEnsureFirst(shims.ShimDirectory);
            UserPathMutationResult result = await _pathManager.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Diagnostics.Repair.ApplyFailed"));
            }

            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                Loc.Format("Diagnostics.Repair.Activity.ShimReinstalled", shims.ShimDirectory),
                [shims.ShimDirectory],
                result.SnapshotPath,
                result.SnapshotPath);
            await FinishRepairAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            await LogRepairFailureAsync(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RepairShimEnsureFirstAsync()
    {
        string shimDirectory = Path.Combine(_managedRoot, "shims");
        UserPathMutationPlan plan = _pathManager.PlanEnsureFirst(shimDirectory);
        if (!plan.Changed)
        {
            SummaryInfo.Severity = InfoBarSeverity.Informational;
            SummaryInfo.Title = Loc.Get("Diagnostics.Repair.ShimFirstNoChangeTitle");
            SummaryInfo.Message = Loc.Get("Diagnostics.Repair.ShimFirstNoChangeMessage");
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Diagnostics.Repair.ShimFirstConfirmTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Diagnostics.Repair.ShimFirstConfirmContent",
                    shimDirectory),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Diagnostics.Repair.ShimFirstConfirmButton"),
            CloseButtonText = Loc.Get("Diagnostics.Repair.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            UserPathMutationResult result = await _pathManager.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Diagnostics.Repair.ApplyFailed"));
            }

            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                Loc.Format("Diagnostics.Repair.Activity.ShimFirst", shimDirectory),
                [shimDirectory],
                result.SnapshotPath,
                result.SnapshotPath);
            await FinishRepairAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            await LogRepairFailureAsync(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// A repair changes the environment the report was computed from, so
    /// announce the success and immediately rescan with the same scopes to
    /// show the post-repair state.
    /// </summary>
    private async Task FinishRepairAsync()
    {
        SummaryInfo.Severity = InfoBarSeverity.Success;
        SummaryInfo.Title = Loc.Get("Diagnostics.Repair.SucceededTitle");
        SummaryInfo.Message = Loc.Get("Diagnostics.Repair.SucceededMessage");
        await RefreshAsync();
    }

    private async Task LogRepairFailureAsync(Exception exception)
    {
        SummaryInfo.Severity = InfoBarSeverity.Error;
        SummaryInfo.Title = Loc.Get("Diagnostics.Repair.FailedTitle");
        SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.PathChange,
            ActivityStatus.Failed,
            Loc.Format("Diagnostics.Repair.Activity.Failed", exception.GetType().Name));
    }

    private void OnScopeSelectionChanged(object sender, RoutedEventArgs args)
    {
        UpdateScopeControls();
        if (!_scanning)
        {
            ShowPendingScopes();
        }
    }

    private void OnProjectScopeChanged(object sender, RoutedEventArgs args)
    {
        UpdateScopeControls();
        if (!_scanning)
        {
            ShowPendingScopes();
        }
    }

    private async void OnBrowseProjectClicked(object sender, RoutedEventArgs args)
    {
        if (((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ProjectPathTextBox.Text = folder.Path;
                ShowPendingScopes();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.ProjectPickFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs args)
    {
        if (_lastReport is null
            || ((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        try
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"autoenvplus-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add(Loc.Get("Diagnostics.Export.FileTypeLabel"), [".json"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            string json = DiagnosticReportExportSerializer.Serialize(_lastReport);
            await FileIO.WriteTextAsync(file, json, UnicodeEncoding.Utf8);
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.ExportedTitle");
            SummaryInfo.Message = file.Path;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.DiagnosticExport,
                ActivityStatus.Succeeded,
                Loc.Get("Diagnostics.Activity.Exported"),
                [file.Path]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.ExportFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.DiagnosticExport,
                ActivityStatus.Failed,
                Loc.Format("Diagnostics.Activity.ExportFailed", exception.GetType().Name));
        }
    }

    private async Task RefreshAsync()
    {
        if (_scanning || BuildOptions() is not EnvironmentDiagnosticOptions options)
        {
            return;
        }

        _scanning = true;
        _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        SetBusy(true);
        SummaryInfo.Severity = InfoBarSeverity.Informational;
        SummaryInfo.Title = Loc.Get("Diagnostics.Messages.ScanningTitle");
        SummaryInfo.Message = Loc.Format(
            "Diagnostics.Messages.ScanningMessage",
            string.Join(
                Loc.Get("Diagnostics.Text.ListSeparator"),
                EnumerateScopes(options.Scopes).Select(ScopeName)));
        try
        {
            EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
                GetManagedRoot()).InspectCurrentAsync(options, _scanCancellation.Token);
            ShowReport(report);
        }
        catch (OperationCanceledException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Informational;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.CancelledTitle");
            SummaryInfo.Message = Loc.Get("Diagnostics.Messages.CancelledMessage");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.ScanFailedTitle");
            SummaryInfo.Message = CoreErrorText.Localize(exception.Message);
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            _scanning = false;
            SetBusy(false);
        }
    }

    private EnvironmentDiagnosticOptions? BuildOptions()
    {
        DiagnosticScanScope scopes = SelectedScopes();
        if (scopes == DiagnosticScanScope.None)
        {
            SummaryInfo.Severity = InfoBarSeverity.Warning;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.NoScopeTitle");
            SummaryInfo.Message = Loc.Get("Diagnostics.Messages.NoScopeMessage");
            return null;
        }

        string? projectRoot = ProjectScopeCheckBox.IsChecked == true
            ? ProjectPathTextBox.Text.Trim()
            : null;
        if (ProjectScopeCheckBox.IsChecked == true
            && string.IsNullOrWhiteSpace(projectRoot))
        {
            SummaryInfo.Severity = InfoBarSeverity.Warning;
            SummaryInfo.Title = Loc.Get("Diagnostics.Messages.NoProjectTitle");
            SummaryInfo.Message = Loc.Get("Diagnostics.Messages.NoProjectMessage");
            return null;
        }

        return new EnvironmentDiagnosticOptions
        {
            Scopes = scopes,
            ProjectRoot = projectRoot,
        };
    }

    private void ShowPendingScopes()
    {
        DiagnosticScanScope scopes = SelectedScopes();
        DiagnosticScanScope[] selected = EnumerateScopes(scopes).ToArray();
        int expensive = selected.Count(scope => scope is
            DiagnosticScanScope.ProjectEnvironment
            or DiagnosticScanScope.StoragePressure
            or DiagnosticScanScope.ProviderConnectivity);
        ScopeHintText.Text = selected.Length == 0
            ? Loc.Get("Diagnostics.Scope.HintNone")
            : Loc.Format("Diagnostics.Scope.HintCount", selected.Length)
                + (expensive == 0
                    ? Loc.Get("Diagnostics.Scope.HintCheap")
                    : Loc.Format("Diagnostics.Scope.HintExpensive", expensive));
        ScopeList.ItemsSource = selected
            .Select(scope => new ScopeRow(
                ScopeName(scope),
                Loc.Get("Diagnostics.Scope.PendingResult"),
                ScopeDescription(scope)))
            .ToArray();
    }

    private void ShowReport(EnvironmentDiagnosticReport report)
    {
        _lastReport = report;
        SummaryInfo.Severity = report.ErrorCount > 0
            ? InfoBarSeverity.Error
            : report.WarningCount > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        SummaryInfo.Title = report.IsHealthy
            ? Loc.Get("Diagnostics.Messages.HealthyTitle")
            : Loc.Get("Diagnostics.Messages.UnhealthyTitle");
        SummaryInfo.Message = string.Join(
            Loc.Get("Diagnostics.Text.Separator"),
            Loc.Format("Diagnostics.Summary.Errors", report.ErrorCount),
            Loc.Format("Diagnostics.Summary.Warnings", report.WarningCount),
            Loc.Format(
                "Diagnostics.Summary.Scopes",
                EnumerateScopes(report.CompletedScopes).Count()),
            Loc.Format(
                "Diagnostics.Summary.Time",
                report.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));

        ScopeList.ItemsSource = EnumerateScopes(report.CompletedScopes)
            .Select(scope =>
            {
                DiagnosticIssue[] scoped = report.Issues.Where(issue =>
                    issue.Scope == scope).ToArray();
                int errors = scoped.Count(issue =>
                    issue.Severity == DiagnosticSeverity.Error);
                int warnings = scoped.Count(issue =>
                    issue.Severity == DiagnosticSeverity.Warning);
                string result = errors > 0
                    ? Loc.Format("Diagnostics.Scope.ResultErrors", errors)
                    : warnings > 0
                        ? Loc.Format("Diagnostics.Scope.ResultWarnings", warnings)
                        : Loc.Get("Diagnostics.Scope.ResultPassed");
                string detail = scoped.Length == 0
                    ? ScopeDescription(scope)
                    : Loc.Format(
                        "Diagnostics.Scope.ResultDetail",
                        ScopeDescription(scope),
                        scoped.Length);
                return new ScopeRow(ScopeName(scope), result, detail);
            })
            .ToArray();

        IssueList.ItemsSource = report.Issues.Count == 0
            ? [new IssueRow(
                Loc.Get("Diagnostics.Issues.AllScopesLabel"),
                Loc.Get("Diagnostics.Severity.Info"),
                Loc.Get("Diagnostics.Issues.NoneFoundTitle"),
                Loc.Get("Diagnostics.Issues.NoneFoundDetail"),
                string.Empty,
                new SolidColorBrush(Colors.ForestGreen))]
            : report.Issues
                .OrderBy(issue => issue.Scope)
                .ThenByDescending(issue => issue.Severity)
                .ThenBy(issue => issue.Title, StringComparer.CurrentCulture)
                .Select(IssueRow.FromIssue)
                .ToArray();
        CommandList.ItemsSource = report.Commands
            .Select(command => new CommandRow(
                command.Command,
                command.WinnerPath ?? Loc.Get("Diagnostics.Commands.NotFound"),
                command.HasConflict
                    ? Loc.Format("Diagnostics.Commands.CandidatesShadowed", command.CandidateCount)
                    : Loc.Format("Diagnostics.Commands.Candidates", command.CandidateCount)))
            .ToArray();
        RuntimeList.ItemsSource = report.Runtimes
            .Select(runtime => new RuntimeRow(
                runtime.Kind + Loc.Get("Diagnostics.Text.Separator") + runtime.Command,
                runtime.Version?.ToString() ?? Loc.Get("Diagnostics.Runtime.ProbeFailed"),
                runtime.ExecutablePath))
            .ToArray();
        GlobalSelectionList.ItemsSource = report.GlobalSelections
            .Select(selection => new GlobalSelectionRow(
                selection.Kind.ToString(),
                selection.Selector,
                selection.Success
                    ? string.Join(
                        Loc.Get("Diagnostics.Text.Separator"),
                        selection.RuntimeId,
                        selection.Version,
                        selection.Architecture)
                    : selection.Error ?? Loc.Get("Diagnostics.Runtime.ResolveFailed")))
            .ToArray();
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ExportButton.IsEnabled = !busy && _lastReport is not null;
        DiagnosticProgress.IsActive = busy;
        ScopeExpander.IsEnabled = !busy;
        if (!busy)
        {
            UpdateScopeControls();
        }
    }

    private void UpdateScopeControls()
    {
        bool projectSelected = ProjectScopeCheckBox.IsChecked == true;
        ProjectPathTextBox.IsEnabled = !_scanning && projectSelected;
        BrowseProjectButton.IsEnabled = !_scanning && projectSelected;
    }

    private DiagnosticScanScope SelectedScopes()
    {
        DiagnosticScanScope scopes = DiagnosticScanScope.None;
        if (PathScopeCheckBox.IsChecked == true)
        {
            scopes |= DiagnosticScanScope.PathAndCommands;
        }

        if (ManagedScopeCheckBox.IsChecked == true)
        {
            scopes |= DiagnosticScanScope.ManagedTools;
        }

        if (ProjectScopeCheckBox.IsChecked == true)
        {
            scopes |= DiagnosticScanScope.ProjectEnvironment;
        }

        if (ProviderScopeCheckBox.IsChecked == true)
        {
            scopes |= DiagnosticScanScope.ProviderConfiguration;
        }

        if (StorageScopeCheckBox.IsChecked == true)
        {
            scopes |= DiagnosticScanScope.StoragePressure;
        }

        if (ConnectivityScopeCheckBox.IsChecked == true)
        {
            scopes |= DiagnosticScanScope.ProviderConnectivity;
        }

        return scopes;
    }

    private static IEnumerable<DiagnosticScanScope> EnumerateScopes(
        DiagnosticScanScope scopes)
    {
        foreach (DiagnosticScanScope scope in new[]
        {
            DiagnosticScanScope.PathAndCommands,
            DiagnosticScanScope.ManagedTools,
            DiagnosticScanScope.ProjectEnvironment,
            DiagnosticScanScope.ProviderConfiguration,
            DiagnosticScanScope.StoragePressure,
            DiagnosticScanScope.ProviderConnectivity,
        })
        {
            if (scopes.HasFlag(scope))
            {
                yield return scope;
            }
        }
    }

    private static string ScopeName(DiagnosticScanScope scope) => scope switch
    {
        DiagnosticScanScope.PathAndCommands => Loc.Get("Diagnostics.Scope.Path"),
        DiagnosticScanScope.ManagedTools => Loc.Get("Diagnostics.Scope.ManagedName"),
        DiagnosticScanScope.ProjectEnvironment => Loc.Get("Diagnostics.Scope.ProjectName"),
        DiagnosticScanScope.ProviderConfiguration => Loc.Get("Diagnostics.Scope.ProviderName"),
        DiagnosticScanScope.StoragePressure => Loc.Get("Diagnostics.Scope.StorageName"),
        DiagnosticScanScope.ProviderConnectivity => Loc.Get("Diagnostics.Scope.ConnectivityName"),
        _ => Loc.Get("Diagnostics.Scope.Other"),
    };

    private static string ScopeDescription(DiagnosticScanScope scope) => scope switch
    {
        DiagnosticScanScope.PathAndCommands => Loc.Get("Diagnostics.Scope.PathDescription"),
        DiagnosticScanScope.ManagedTools => Loc.Get("Diagnostics.Scope.ManagedDescription"),
        DiagnosticScanScope.ProjectEnvironment => Loc.Get("Diagnostics.Scope.ProjectDescription"),
        DiagnosticScanScope.ProviderConfiguration => Loc.Get("Diagnostics.Scope.ProviderDescription"),
        DiagnosticScanScope.StoragePressure => Loc.Get("Diagnostics.Scope.StorageDescription"),
        DiagnosticScanScope.ProviderConnectivity => Loc.Get("Diagnostics.Scope.ConnectivityDescription"),
        _ => string.Empty,
    };

    private static string GetManagedRoot() => ManagedRootResolver.ResolveOrThrow();

    private sealed record ScopeRow(string Scope, string Result, string Detail);

    private sealed record IssueRow(
        string Scope,
        string Severity,
        string Title,
        string Detail,
        string Path,
        Brush SeverityBrush,
        DiagnosticRepairKind? Repair = null,
        DiagnosticIssue? Issue = null)
    {
        public Visibility PathVisibility => string.IsNullOrWhiteSpace(Path)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility RepairVisibility => Repair is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        public static IssueRow FromIssue(DiagnosticIssue issue) => new(
            ScopeName(issue.Scope),
            issue.Severity switch
            {
                DiagnosticSeverity.Error => Loc.Get("Diagnostics.Severity.Error"),
                DiagnosticSeverity.Warning => Loc.Get("Diagnostics.Severity.Warning"),
                _ => Loc.Get("Diagnostics.Severity.Info"),
            },
            LocalizeIssueText(issue, isTitle: true) ?? issue.Title,
            LocalizeIssueText(issue, isTitle: false) ?? issue.Detail,
            issue.Path ?? string.Empty,
            new SolidColorBrush(issue.Severity switch
            {
                DiagnosticSeverity.Error => Colors.Firebrick,
                DiagnosticSeverity.Warning => Colors.DarkGoldenrod,
                _ => Colors.DodgerBlue,
            }),
            issue.Repair,
            issue);
    }

    /// <summary>
    /// Resolves the issue text from the <c>Diagnostics.Issue.<Kind>.Title/.Detail</c>
    /// resource keys, formatted with the issue's ordered <c>Args</c>. Issues
    /// without a kind (or without matching keys) keep the Core-produced text,
    /// which the CLI and exports also use.
    /// </summary>
    private static string? LocalizeIssueText(DiagnosticIssue issue, bool isTitle)
    {
        if (string.IsNullOrEmpty(issue.Kind))
        {
            return null;
        }

        return Loc.TryFormat(
            "Diagnostics.Issue." + issue.Kind + (isTitle ? ".Title" : ".Detail"),
            issue.Args ?? []);
    }

    private sealed record CommandRow(string Command, string Winner, string CandidateText);

    private sealed record RuntimeRow(string Runtime, string Version, string Path);

    private sealed record GlobalSelectionRow(string Runtime, string Selector, string Resolution);
}
