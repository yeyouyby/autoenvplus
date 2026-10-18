using System.Text;
using System.Text.Json;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Toolchains;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class ProjectsPage : Page
{
    private readonly string? _initialProjectRoot;
    private string? _projectRoot;
    private string? _manifestPath;
    private ProjectEnvironmentImportResult? _import;
    private string? _lastCMakePresetSnapshot;
    private CancellationTokenSource? _virtualEnvironmentScanCancellation;

    public ProjectsPage()
        : this(null)
    {
    }

    internal ProjectsPage(string? initialProjectRoot)
    {
        _initialProjectRoot = string.IsNullOrWhiteSpace(initialProjectRoot)
            ? null
            : Path.GetFullPath(initialProjectRoot);
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
        PageTitleText.Text = Loc.Get("Projects.Header.Title");
        PageSubtitleText.Text = Loc.Get("Projects.Header.Subtitle");
        ChooseProjectButton.Content = Loc.Get("Projects.Actions.ChooseProjectFolder");
        ProjectInfo.Title = Loc.Get("Projects.Info.InitialTitle");
        ProjectInfo.Message = Loc.Get("Projects.Info.InitialMessage");
        ProjectDetailsText.Text = Loc.Get("Projects.Details.Placeholder");
        OpenProjectTerminalButton.Content = Loc.Get("Projects.Actions.OpenTerminal");
        CreateManifestButton.Content = Loc.Get("Projects.Actions.CreateManifest");
        CreateLockButton.Content = Loc.Get("Projects.Actions.CreateLock");
        CreateCMakePresetButton.Content = Loc.Get("Projects.Actions.CreateCMakePreset");
        RollbackCMakePresetButton.Content = Loc.Get("Projects.Actions.RollbackCMakePreset");
        VirtualEnvironmentSectionTitleText.Text = Loc.Get("Projects.VirtualEnvironments.SectionTitle");
        VirtualEnvironmentSectionSubtitleText.Text = Loc.Get("Projects.VirtualEnvironments.SectionSubtitle");
        ToolTipService.SetToolTip(
            DiscoverVirtualEnvironmentsButton,
            Loc.Get("Projects.VirtualEnvironments.Discover.ToolTip"));
        DiscoverVirtualEnvironmentsButtonText.Text = Loc.Get("Projects.VirtualEnvironments.Discover.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            VirtualEnvironmentProgressRing,
            Loc.Get("Projects.VirtualEnvironments.Progress.AccessibleName"));
        VirtualEnvironmentProgressText.Text = Loc.Get("Projects.VirtualEnvironments.Progress.Label");
        VirtualEnvironmentEmptyText.Text = Loc.Get("Projects.VirtualEnvironments.Placeholder");
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        if (_initialProjectRoot is not null)
        {
            await TryLoadProjectAsync(_initialProjectRoot);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _virtualEnvironmentScanCancellation?.Cancel();
        _virtualEnvironmentScanCancellation?.Dispose();
        _virtualEnvironmentScanCancellation = null;
    }

    private async void OnChooseProjectClicked(object sender, RoutedEventArgs args)
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            CommitButtonText = Loc.Get("Projects.Picker.CommitButton"),
        };
        picker.FileTypeFilter.Add("*");
        if (((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await TryLoadProjectAsync(folder.Path);
    }

    private async Task TryLoadProjectAsync(string startPath)
    {
        try
        {
            if (!Directory.Exists(startPath))
            {
                throw new DirectoryNotFoundException(
                    Loc.Format("Projects.Messages.ProjectRootMissing", startPath));
            }

            await LoadProjectAsync(startPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("Projects.Messages.LoadFailedTitle");
            ProjectInfo.Message = CoreErrorText.Localize(exception.Message);
        }
    }

    private async Task LoadProjectAsync(string startPath)
    {
        _virtualEnvironmentScanCancellation?.Cancel();
        _virtualEnvironmentScanCancellation?.Dispose();
        _virtualEnvironmentScanCancellation = null;
        _manifestPath = new ProjectManifestService().FindManifest(startPath);
        _import = new ProjectEnvironmentImportService().Discover(startPath);
        _projectRoot = _manifestPath is not null
            ? Path.GetDirectoryName(_manifestPath)
            : _import.ProjectRoot ?? Path.GetFullPath(startPath);
        ProjectPathText.Text = _projectRoot;
        StringBuilder details = new();

        if (_manifestPath is not null)
        {
            ProjectManifestLoadResult manifest = new ProjectManifestService().Load(_manifestPath);
            details.AppendLine($"Manifest: {_manifestPath}");
            foreach ((AutoEnvPlus.Core.Runtimes.RuntimeKind kind, AutoEnvPlus.Core.Runtimes.VersionSelector selector) in manifest.Manifest.Tools)
            {
                details.AppendLine($"{kind}: {selector}");
            }

            foreach (ProjectManifestError error in manifest.Errors)
            {
                details.AppendLine($"line {error.LineNumber}: {error.Message}");
            }
        }
        else if (_import.Found)
        {
            details.AppendLine(Loc.Get("Projects.Details.ImportedDeclarations"));
            foreach (ImportedRuntimeSelection source in _import.Sources)
            {
                details.AppendLine($"{source.Kind}: {source.Selector} ← {source.SourcePath} ({source.RawValue})");
            }

            foreach (string warning in _import.Warnings)
            {
                details.AppendLine(Loc.Format("Projects.Details.WarningLine", warning));
            }
        }
        else
        {
            details.AppendLine(Loc.Get("Projects.Details.NoDeclarations"));
        }

        ProjectDetailsText.Text = details.ToString().TrimEnd();
        OpenProjectTerminalButton.IsEnabled = _manifestPath is not null;
        CreateManifestButton.IsEnabled = _manifestPath is null && _import.Found;
        CreateLockButton.IsEnabled = _manifestPath is not null;
        CreateCMakePresetButton.IsEnabled = File.Exists(
            Path.Combine(_projectRoot!, "CMakeLists.txt"));
        DiscoverCMakePresetSnapshot();
        ResetVirtualEnvironmentDiscovery();
        await new KnownProjectStore(GetManagedRoot()).AddAsync(_projectRoot!);
        ProjectInfo.Severity = InfoBarSeverity.Success;
        ProjectInfo.Title = Loc.Get("Projects.Messages.LoadedTitle");
        ProjectInfo.Message = _manifestPath is not null
            ? Loc.Get("Projects.Messages.LoadedWithManifest")
            : Loc.Get("Projects.Messages.LoadedWithoutManifest");
    }

    private async void OnDiscoverVirtualEnvironmentsClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null)
        {
            return;
        }

        string scannedRoot = _projectRoot;
        _virtualEnvironmentScanCancellation?.Cancel();
        _virtualEnvironmentScanCancellation?.Dispose();
        CancellationTokenSource scanCancellation = new();
        _virtualEnvironmentScanCancellation = scanCancellation;
        CancellationToken cancellationToken = scanCancellation.Token;
        SetVirtualEnvironmentBusy(true);
        VirtualEnvironmentList.ItemsSource = null;
        VirtualEnvironmentList.Visibility = Visibility.Collapsed;
        VirtualEnvironmentEmptyText.Visibility = Visibility.Collapsed;
        VirtualEnvironmentInfo.IsOpen = true;
        VirtualEnvironmentInfo.Severity = InfoBarSeverity.Informational;
        VirtualEnvironmentInfo.Title = Loc.Get("Projects.VirtualEnvironments.ScanningTitle");
        VirtualEnvironmentInfo.Message = Loc.Get("Projects.VirtualEnvironments.ScanningMessage");
        try
        {
            ProjectVirtualEnvironmentDiscoveryResult result = await Task.Run(
                () => new ProjectVirtualEnvironmentDiscoveryService().Discover(
                    scannedRoot,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (!IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                return;
            }

            ShowVirtualEnvironmentDiscovery(result);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                VirtualEnvironmentInfo.IsOpen = true;
                VirtualEnvironmentInfo.Severity = InfoBarSeverity.Informational;
                VirtualEnvironmentInfo.Title = Loc.Get("Projects.VirtualEnvironments.CancelledTitle");
                VirtualEnvironmentInfo.Message = Loc.Get("Projects.VirtualEnvironments.CancelledMessage");
                VirtualEnvironmentEmptyText.Text = Loc.Get("Projects.VirtualEnvironments.CancelledHint");
                VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            if (IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                VirtualEnvironmentInfo.IsOpen = true;
                VirtualEnvironmentInfo.Severity = InfoBarSeverity.Error;
                VirtualEnvironmentInfo.Title = Loc.Get("Projects.VirtualEnvironments.FailedTitle");
                VirtualEnvironmentInfo.Message = CoreErrorText.Localize(exception.Message);
                VirtualEnvironmentEmptyText.Text = Loc.Get("Projects.VirtualEnvironments.FailedHint");
                VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                SetVirtualEnvironmentBusy(false);
                scanCancellation.Dispose();
                _virtualEnvironmentScanCancellation = null;
            }
        }
    }

    private void ResetVirtualEnvironmentDiscovery()
    {
        VirtualEnvironmentList.ItemsSource = null;
        VirtualEnvironmentList.Visibility = Visibility.Collapsed;
        VirtualEnvironmentInfo.IsOpen = false;
        VirtualEnvironmentEmptyText.Text = Loc.Get("Projects.VirtualEnvironments.ResetHint");
        VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
        VirtualEnvironmentProgress.Visibility = Visibility.Collapsed;
        DiscoverVirtualEnvironmentsButton.IsEnabled = _projectRoot is not null;
    }

    private void ShowVirtualEnvironmentDiscovery(ProjectVirtualEnvironmentDiscoveryResult result)
    {
        VirtualEnvironmentRow[] rows = result.Environments
            .Select(environment => new VirtualEnvironmentRow(environment))
            .ToArray();
        int invalidCount = result.Environments.Count(environment =>
            environment.Health == ProjectVirtualEnvironmentHealth.Invalid);
        int attentionCount = result.Environments.Count(environment =>
            environment.Health == ProjectVirtualEnvironmentHealth.NeedsAttention);

        VirtualEnvironmentInfo.IsOpen = true;
        VirtualEnvironmentInfo.Severity = invalidCount > 0
            || attentionCount > 0
            || result.Warnings.Count > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        VirtualEnvironmentInfo.Title = rows.Length == 0
            ? Loc.Get("Projects.VirtualEnvironments.NoneFoundTitle")
            : Loc.Format("Projects.VirtualEnvironments.FoundTitle", rows.Length);
        VirtualEnvironmentInfo.Message = rows.Length == 0
            ? result.Warnings.Count == 0
                ? Loc.Format(
                    "Projects.VirtualEnvironments.NoneFoundMessage",
                    result.InspectedPathCount)
                : string.Join(Loc.Get("Projects.Separator"), result.Warnings.Take(3))
            : Loc.Format(
                    "Projects.VirtualEnvironments.FoundMessage",
                    attentionCount,
                    invalidCount,
                    result.InspectedPathCount)
                + (result.ScanLimitReached
                    ? Loc.Get("Projects.VirtualEnvironments.ScanLimitSuffix")
                    : string.Empty);

        if (rows.Length == 0)
        {
            VirtualEnvironmentList.ItemsSource = null;
            VirtualEnvironmentList.Visibility = Visibility.Collapsed;
            VirtualEnvironmentEmptyText.Text = Loc.Get("Projects.VirtualEnvironments.EmptyResult");
            VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
            return;
        }

        VirtualEnvironmentList.ItemsSource = rows;
        VirtualEnvironmentList.Visibility = Visibility.Visible;
        VirtualEnvironmentEmptyText.Visibility = Visibility.Collapsed;
    }

    private void SetVirtualEnvironmentBusy(bool busy)
    {
        DiscoverVirtualEnvironmentsButton.IsEnabled = !busy && _projectRoot is not null;
        VirtualEnvironmentProgress.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool PathsEqual(string left, string? right) => right is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentVirtualEnvironmentScan(
        CancellationTokenSource scanCancellation,
        string scannedRoot) => ReferenceEquals(_virtualEnvironmentScanCancellation, scanCancellation)
        && PathsEqual(scannedRoot, _projectRoot);

    private async void OnCreateManifestClicked(object sender, RoutedEventArgs args)
    {
        if (_import is null || !_import.Found)
        {
            return;
        }

        try
        {
            _manifestPath = await new ProjectEnvironmentImportService().WriteManifestAsync(_import);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = Loc.Get("Projects.Messages.ManifestCreatedTitle");
            ProjectInfo.Message = _manifestPath;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Succeeded,
                Loc.Get("Projects.Messages.ManifestCreatedActivity"),
                [_projectRoot!, _manifestPath]);
            await LoadProjectAsync(_projectRoot!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("Projects.Messages.ManifestFailedTitle");
            ProjectInfo.Message = CoreErrorText.Localize(exception.Message);
            if (_projectRoot is not null)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.ProjectImport,
                    ActivityStatus.Failed,
                    Loc.Format("Projects.Messages.ManifestFailedActivity", exception.GetType().Name),
                    [_projectRoot]);
            }
        }
    }

    private async void OnOpenProjectTerminalClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null || _manifestPath is null)
        {
            return;
        }

        OpenProjectTerminalButton.IsEnabled = false;
        try
        {
            ProjectTerminalService service = new(GetManagedRoot());
            ProjectTerminalHost[] availableHosts = service.IsHostAvailable(
                ProjectTerminalHost.WindowsTerminal)
                    ? [ProjectTerminalHost.WindowsTerminal, ProjectTerminalHost.WindowsPowerShell]
                    : [ProjectTerminalHost.WindowsPowerShell];
            TerminalHostChoice[] hostChoices = availableHosts
                .Select(host => new TerminalHostChoice(host, TerminalHostDisplayName(host)))
                .ToArray();
            Dictionary<ProjectTerminalHost, ProjectTerminalPlan> plans = [];
            foreach (ProjectTerminalHost host in availableHosts)
            {
                plans[host] = await service.CreatePlanAsync(_projectRoot, host);
            }

            ComboBox hostSelector = new()
            {
                ItemsSource = hostChoices,
                DisplayMemberPath = nameof(TerminalHostChoice.Label),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(hostSelector, Loc.Get("Projects.Terminal.HostLabel"));
            TextBlock previewText = new()
            {
                IsTextSelectionEnabled = true,
                MinWidth = 500,
                TextWrapping = TextWrapping.Wrap,
            };
            StackPanel dialogContent = new() { Spacing = 10 };
            dialogContent.Children.Add(new TextBlock
            {
                Text = Loc.Get("Projects.Terminal.HostLabel"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            dialogContent.Children.Add(hostSelector);
            if (availableHosts.Length == 1)
            {
                dialogContent.Children.Add(new InfoBar
                {
                    IsClosable = false,
                    IsOpen = true,
                    Severity = InfoBarSeverity.Informational,
                    Title = Loc.Get("Projects.Terminal.WindowsTerminalUnavailableTitle"),
                    Message = Loc.Get("Projects.Terminal.WindowsTerminalUnavailableMessage"),
                });
            }

            dialogContent.Children.Add(previewText);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Projects.Terminal.DialogTitle"),
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = dialogContent,
                },
                CloseButtonText = Loc.Get("Projects.Dialogs.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            ProjectTerminalPlan selectedPlan = plans[hostChoices[0].Host];
            void UpdateTerminalPreview()
            {
                if (hostSelector.SelectedItem is not TerminalHostChoice choice)
                {
                    return;
                }

                selectedPlan = plans[choice.Host];
                previewText.Text = CreateTerminalPreview(selectedPlan);
                confirmation.PrimaryButtonText = choice.Host == ProjectTerminalHost.WindowsTerminal
                    ? Loc.Get("Projects.Terminal.OpenInWindowsTerminal")
                    : Loc.Get("Projects.Terminal.OpenNewPowerShell");
                confirmation.IsPrimaryButtonEnabled = selectedPlan.CanLaunch;
            }

            hostSelector.SelectionChanged += (_, _) => UpdateTerminalPreview();
            UpdateTerminalPreview();
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            int processId = await service.LaunchAsync(selectedPlan);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = Loc.Get("Projects.Terminal.LaunchedTitle");
            ProjectInfo.Message = Loc.Format(
                "Projects.Terminal.LaunchedMessage",
                processId,
                TerminalHostDisplayName(selectedPlan.EffectiveHost));
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("Projects.Terminal.LaunchFailedTitle");
            ProjectInfo.Message = CoreErrorText.Localize(exception.Message);
        }
        finally
        {
            OpenProjectTerminalButton.IsEnabled = _manifestPath is not null;
        }
    }

    private static string CreateTerminalPreview(ProjectTerminalPlan plan)
    {
        StringBuilder preview = new();
        preview.AppendLine(Loc.Format("Projects.Terminal.Preview.Project", plan.ProjectRoot));
        preview.AppendLine(Loc.Format("Projects.Terminal.Preview.Manifest", plan.ManifestPath));
        preview.AppendLine(Loc.Format(
            "Projects.Terminal.Preview.Host",
            TerminalHostDisplayName(plan.EffectiveHost)));
        preview.AppendLine($"\nShell\n{plan.ShellExecutable} {string.Join(' ', plan.ShellArguments)}");
        preview.AppendLine($"\nShim\n{plan.ShimDirectory}");
        preview.AppendLine(Loc.Get("Projects.Terminal.Preview.SelectionsHeader"));
        if (plan.Selections.Count == 0)
        {
            preview.AppendLine(Loc.Get("Projects.Terminal.Preview.SelectionsEmpty"));
        }
        else
        {
            foreach (ProjectTerminalSelection selection in plan.Selections)
            {
                preview.AppendLine(
                    $"{selection.Kind}: {selection.RequestedSelector} → {selection.ResolvedVersion} "
                    + $"({selection.RuntimeId} @ {selection.ProviderId})");
                preview.AppendLine($"  {selection.EnvironmentVariable}={selection.ResolvedVersion}");
            }
        }

        preview.AppendLine(Loc.Get("Projects.Terminal.Preview.SessionVariablesHeader"));
        if (plan.EnvironmentOverrides.Count == 0)
        {
            preview.AppendLine(Loc.Get("Projects.Terminal.Preview.SessionVariablesEmpty"));
        }
        else
        {
            foreach (KeyValuePair<string, string> pair in plan.EnvironmentOverrides
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                {
                    preview.AppendLine(Loc.Format("Projects.Terminal.Preview.PathOverride", pair.Key));
                }
                else if (pair.Key.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("PIP_INDEX_URL", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("NPM_CONFIG_REGISTRY", StringComparison.OrdinalIgnoreCase))
                {
                    // PRODUCT.md: the terminal preview must not echo endpoints.
                    preview.AppendLine(Loc.Format("Projects.Terminal.Preview.RedactedValue", pair.Key));
                }
                else
                {
                    preview.AppendLine($"  {pair.Key}={pair.Value}");
                }
            }
        }

        ProjectTerminalNetworkSummary network = plan.NetworkSummary;
        preview.AppendLine(Loc.Get("Projects.Terminal.Preview.NetworkHeader"));
        if (!network.Applied)
        {
            preview.AppendLine(Loc.Get("Projects.Terminal.Preview.NetworkNotApplied"));
        }
        else
        {
            preview.AppendLine(Loc.Format(
                "Projects.Terminal.Preview.ProxySource",
                ProxySourceText(network.ProxySource)));
            preview.AppendLine(Loc.Format(
                "Projects.Terminal.Preview.ProxyLine",
                ConfiguredText(network.HttpProxyConfigured),
                ConfiguredText(network.HttpsProxyConfigured),
                network.NoProxyEntryCount));
            if (network.PipEnvironmentApplied)
            {
                preview.AppendLine(Loc.Format(
                    "Projects.Terminal.Preview.PipMirror",
                    MirrorText(network.PipMirrorConfigured)));
            }

            if (network.NpmEnvironmentApplied)
            {
                preview.AppendLine(Loc.Format(
                    "Projects.Terminal.Preview.NpmRegistry",
                    MirrorText(network.NpmMirrorConfigured)));
            }

            if (plan.EnvironmentRemovals.Count > 0)
            {
                preview.AppendLine(Loc.Format(
                    "Projects.Terminal.Preview.EnvironmentRemovals",
                    plan.EnvironmentRemovals.Count));
            }
        }

        foreach (string warning in plan.Warnings)
        {
            preview.AppendLine(Loc.Format("Projects.Terminal.Preview.WarningLine", warning));
        }

        foreach (string error in plan.Errors)
        {
            preview.AppendLine(Loc.Format("Projects.Terminal.Preview.ErrorLine", error));
        }

        return preview.ToString().TrimEnd();
    }

    private static string TerminalHostDisplayName(ProjectTerminalHost host) => host switch
    {
        ProjectTerminalHost.WindowsTerminal => "Windows Terminal",
        ProjectTerminalHost.WindowsPowerShell => "Windows PowerShell",
        _ => host.ToString(),
    };

    private static string ProxySourceText(ProjectTerminalProxySource source) => source switch
    {
        ProjectTerminalProxySource.Pip => Loc.Get("Projects.Terminal.ProxySource.Pip"),
        ProjectTerminalProxySource.Npm => Loc.Get("Projects.Terminal.ProxySource.Npm"),
        ProjectTerminalProxySource.MatchingPackageTools => Loc.Get("Projects.Terminal.ProxySource.Matching"),
        ProjectTerminalProxySource.Downloads => Loc.Get("Projects.Terminal.ProxySource.Downloads"),
        _ => Loc.Get("Projects.Terminal.DirectConnection"),
    };

    private static string ConfiguredText(bool configured) => configured
        ? Loc.Get("Projects.Terminal.ProxyConfigured")
        : Loc.Get("Projects.Terminal.DirectConnection");

    private static string MirrorText(bool configured) => configured
        ? Loc.Get("Projects.Terminal.MirrorConfigured")
        : Loc.Get("Projects.Terminal.MirrorOfficial");

    private async void OnCreateLockClicked(object sender, RoutedEventArgs args)
    {
        if (_manifestPath is null)
        {
            return;
        }

        try
        {
            RegistryLoadResult registry = await new ManagedRuntimeRegistry(GetManagedRoot()).LoadAsync();
            ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
                _manifestPath,
                registry.Entries);
            if (!result.Success)
            {
                ProjectInfo.Severity = InfoBarSeverity.Warning;
                ProjectInfo.Title = Loc.Get("Projects.Messages.LockNotGeneratedTitle");
                ProjectInfo.Message = string.Join(Loc.Get("Projects.Separator"), result.Errors);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.ProjectImport,
                    ActivityStatus.Failed,
                    Loc.Get("Projects.Messages.LockNotGeneratedActivity"),
                    [_manifestPath]);
                return;
            }

            string lockPath = result.LockPath ?? Path.Combine(
                Path.GetDirectoryName(_manifestPath)!,
                ProjectLockFileService.LockFileName);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = Loc.Get("Projects.Messages.LockCreatedTitle");
            ProjectInfo.Message = Loc.Format(
                "Projects.Messages.LockCreatedMessage",
                lockPath,
                result.Document!.Runtimes.Count);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Succeeded,
                Loc.Format("Projects.Messages.LockCreatedActivity", result.Document.Runtimes.Count),
                [_manifestPath, lockPath]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("Projects.Messages.LockFailedTitle");
            ProjectInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Failed,
                Loc.Format("Projects.Messages.LockFailedActivity", exception.GetType().Name),
                [_manifestPath]);
        }
    }

    private async void OnCreateCMakePresetClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null
            || !File.Exists(Path.Combine(_projectRoot, "CMakeLists.txt")))
        {
            return;
        }

        CreateCMakePresetButton.IsEnabled = false;
        try
        {
            CppToolchainDiscoveryResult discovered = await new CppToolchainDiscoveryService()
                .DiscoverAsync();
            CMakePresetChoice[] choices = discovered.VisualStudioInstallations
                .SelectMany(installation => (installation.AvailableArchitecturePairs ?? [])
                    .Select(pair => new CMakePresetChoice(installation, pair)))
                .OrderBy(choice => choice.Installation.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.Pair.HostArchitecture == AutoEnvPlus.Core.Runtimes.RuntimeArchitecture.X64 ? 0 : 1)
                .ThenBy(choice => choice.Pair.TargetArchitecture)
                .ToArray();
            if (choices.Length == 0)
            {
                throw new InvalidOperationException(
                    Loc.Get("Projects.CMake.NoMsvcPairs"));
            }

            ComboBox selector = new()
            {
                ItemsSource = choices,
                DisplayMemberPath = nameof(CMakePresetChoice.Label),
                SelectedIndex = 0,
                MinWidth = 500,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(selector, Loc.Get("Projects.CMake.SelectorAccessibleName"));
            ContentDialog selection = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Projects.CMake.SelectDialogTitle"),
                Content = selector,
                PrimaryButtonText = Loc.Get("Projects.CMake.PreviewJson"),
                CloseButtonText = Loc.Get("Projects.Dialogs.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await selection.ShowAsync() != ContentDialogResult.Primary
                || selector.SelectedItem is not CMakePresetChoice choice)
            {
                return;
            }

            CMakeUserPresetsService service = new(GetManagedRoot(), _projectRoot);
            CMakeUserPresetsPlan plan = service.CreatePlan(
                choice.Installation,
                choice.Pair);
            TextBox preview = new()
            {
                AcceptsReturn = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                IsReadOnly = true,
                MinWidth = 620,
                MinHeight = 360,
                MaxHeight = 520,
                Text = plan.After,
                TextWrapping = TextWrapping.NoWrap,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format(
                    "Projects.CMake.WriteDialogTitle",
                    CMakeUserPresetsService.PresetsFileName),
                Content = preview,
                PrimaryButtonText = Loc.Get("Projects.CMake.WriteAndSnapshot"),
                CloseButtonText = Loc.Get("Projects.Dialogs.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            CMakeUserPresetsResult result = await service.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Projects.CMake.WriteFailed"));
            }

            _lastCMakePresetSnapshot = result.SnapshotPath;
            RollbackCMakePresetButton.IsEnabled = _lastCMakePresetSnapshot is not null;
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = Loc.Get("Projects.CMake.WrittenTitle");
            ProjectInfo.Message = Loc.Format(
                "Projects.CMake.WrittenMessage",
                result.PresetsPath,
                plan.ConfigurePresetName,
                result.SnapshotPath);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CMakePreset,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "Projects.CMake.WrittenActivity",
                    CMakeUserPresetsService.PresetsFileName,
                    plan.ConfigurePresetName),
                result.SnapshotPath is null
                    ? [result.PresetsPath]
                    : [result.PresetsPath, result.SnapshotPath],
                result.SnapshotPath,
                result.SnapshotPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("Projects.CMake.FailedTitle");
            ProjectInfo.Message = CoreErrorText.Localize(exception.Message);
            if (_projectRoot is not null)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CMakePreset,
                    ActivityStatus.Failed,
                    Loc.Format("Projects.CMake.FailedActivity", exception.GetType().Name),
                    [_projectRoot]);
            }
        }
        finally
        {
            CreateCMakePresetButton.IsEnabled = _projectRoot is not null
                && File.Exists(Path.Combine(_projectRoot, "CMakeLists.txt"));
        }
    }

    private void DiscoverCMakePresetSnapshot()
    {
        // CMake preset snapshots persist under the managed root; without this
        // lookup the rollback affordance stays dead after an app restart even
        // though a valid snapshot for this project exists on disk.
        if (_lastCMakePresetSnapshot is not null || _projectRoot is null)
        {
            RollbackCMakePresetButton.IsEnabled = _lastCMakePresetSnapshot is not null;
            return;
        }

        try
        {
            string snapshotDirectory = Path.Combine(
                GetManagedRoot(),
                "state",
                "cmake-preset-snapshots");
            if (!Directory.Exists(snapshotDirectory))
            {
                RollbackCMakePresetButton.IsEnabled = false;
                return;
            }

            string projectRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_projectRoot));
            foreach (FileInfo file in new DirectoryInfo(snapshotDirectory)
                .EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                try
                {
                    CMakeUserPresetsSnapshot? snapshot = JsonSerializer.Deserialize<
                        CMakeUserPresetsSnapshot>(
                        File.ReadAllText(file.FullName),
                        SnapshotJsonOptions);
                    if (snapshot is not null
                        && Path.TrimEndingDirectorySeparator(
                                Path.GetFullPath(snapshot.ProjectRoot))
                            .Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastCMakePresetSnapshot = file.FullName;
                        break;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or JsonException)
                {
                    // Skip unreadable snapshot files; the newest valid one wins.
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // A failed snapshot lookup must not block loading the project.
        }

        RollbackCMakePresetButton.IsEnabled = _lastCMakePresetSnapshot is not null;
    }

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private async void OnRollbackCMakePresetClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null || _lastCMakePresetSnapshot is null)
        {
            return;
        }

        string snapshot = _lastCMakePresetSnapshot;
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Projects.CMake.RollbackDialogTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format("Projects.CMake.RollbackDialogText", _projectRoot, snapshot),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Projects.CMake.RollbackButton"),
            CloseButtonText = Loc.Get("Projects.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            CMakeUserPresetsResult result = await new CMakeUserPresetsService(
                GetManagedRoot(),
                _projectRoot).RollbackAsync(snapshot);
            if (!result.Success)
            {
                ProjectInfo.Severity = InfoBarSeverity.Error;
                ProjectInfo.Title = Loc.Get("Projects.CMake.RollbackFailedTitle");
                ProjectInfo.Message = result.Error;
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CMakePreset,
                    ActivityStatus.Failed,
                    Loc.Get("Projects.CMake.RollbackRejectedActivity"),
                    [_projectRoot, snapshot],
                    snapshot,
                    snapshot);
                return;
            }

            _lastCMakePresetSnapshot = null;
            RollbackCMakePresetButton.IsEnabled = false;
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = Loc.Get("Projects.CMake.RollbackSucceededTitle");
            ProjectInfo.Message = result.PresetsPath;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CMakePreset,
                ActivityStatus.Succeeded,
                Loc.Get("Projects.CMake.RollbackSucceededActivity"),
                [_projectRoot, result.PresetsPath, snapshot],
                snapshot,
                snapshot);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("Projects.CMake.RollbackFailedTitle");
            ProjectInfo.Message = CoreErrorText.Localize(exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CMakePreset,
                ActivityStatus.Failed,
                Loc.Format("Projects.CMake.RollbackFailedActivity", exception.GetType().Name),
                [_projectRoot, snapshot],
                snapshot,
                snapshot);
        }
    }

    private static string GetManagedRoot() => ManagedRootResolver.ResolveOrThrow();

    private sealed record VirtualEnvironmentRow(ProjectVirtualEnvironment Environment)
    {
        public string Title => $"{LanguageName(Environment.LanguageId)} · "
            + $"{Environment.Manager} · {KindName(Environment.Kind)}";

        public string HealthText => Environment.Health switch
        {
            ProjectVirtualEnvironmentHealth.Healthy => Loc.Get("Projects.Environment.Healthy"),
            ProjectVirtualEnvironmentHealth.NeedsAttention => Loc.Get("Projects.Environment.NeedsAttention"),
            _ => Loc.Get("Projects.Environment.Invalid"),
        };

        public string VersionText => Environment.Version is null
            ? string.Empty
            : Loc.Format("Projects.Environment.VersionText", Environment.Version);

        public Visibility VersionVisibility => Environment.Version is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string Root => Environment.Root;

        public string ExecutableText => Environment.Executable is null
            ? string.Empty
            : Loc.Format("Projects.Environment.ExecutableText", Environment.Executable);

        public Visibility ExecutableVisibility => Environment.Executable is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string EvidenceText => Loc.Format(
            "Projects.Environment.EvidenceText",
            string.Join(" · ", Environment.Evidence));

        public string WarningText => Environment.Warnings.Count == 0
            ? string.Empty
            : Loc.Format(
                "Projects.Environment.WarningText",
                string.Join(Loc.Get("Projects.Separator"), Environment.Warnings));

        public Visibility WarningVisibility => Environment.Warnings.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        private static string LanguageName(string languageId) => languageId switch
        {
            "python" => "Python",
            "nodejs" => "Node.js",
            "dotnet" => ".NET",
            "java" => "Java",
            "rust" => "Rust",
            "go" => "Go",
            _ => languageId,
        };

        private static string KindName(string kind) => kind switch
        {
            "virtual-environment" => Loc.Get("Projects.Environment.Kind.VirtualEnvironment"),
            "conda-environment" => Loc.Get("Projects.Environment.Kind.CondaEnvironment"),
            "environment-definition" => Loc.Get("Projects.Environment.Kind.EnvironmentDefinition"),
            "dependency-environment" => Loc.Get("Projects.Environment.Kind.DependencyEnvironment"),
            "local-tool-manifest" => Loc.Get("Projects.Environment.Kind.LocalToolManifest"),
            "build-wrapper" => Loc.Get("Projects.Environment.Kind.BuildWrapper"),
            "toolchain-selection" => Loc.Get("Projects.Environment.Kind.ToolchainSelection"),
            "build-output" => Loc.Get("Projects.Environment.Kind.BuildOutput"),
            "workspace" => Loc.Get("Projects.Environment.Kind.Workspace"),
            _ => kind,
        };
    }

    private sealed record TerminalHostChoice(ProjectTerminalHost Host, string Label);

    private sealed record CMakePresetChoice(
        VisualCppInstallation Installation,
        CppArchitecturePair Pair)
    {
        public string Label => Loc.Format(
            "Projects.CMake.ChoiceLabel",
            Installation.DisplayName,
            Pair.TargetArchitecture,
            Pair.HostArchitecture);
    }
}
