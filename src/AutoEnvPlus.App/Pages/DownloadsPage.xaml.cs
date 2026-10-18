using System.Collections.ObjectModel;
using System.Diagnostics;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Packages;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Settings;
using AutoEnvPlus.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly AppDownloadManager _manager;
    private readonly ObservableCollection<LibraryRow> _libraryRows = [];
    private readonly List<(DateTimeOffset AtUtc, long Bytes)> _speedSamples = [];
    private bool _suppressFileNameTracking;
    private bool _fileNameEdited;
    private Guid? _lastCompletedTransfer;
    private Guid _speedSamplesTransferId;
    private bool _pipInstallRunning;
    private bool _defaultsLoaded;

    public DownloadsPage()
    {
        InitializeComponent();
        ApplyLocalizedStrings();
        _manager = ((App)Application.Current).DownloadManager;
        ConnectionCountPicker.ItemsSource = ManagedSegmentedDownloader.SupportedConnectionCounts;
        ConnectionCountPicker.SelectedItem = 8;
        HashAlgorithmPicker.SelectedIndex = 0;
        IntegrityFields.Visibility = Visibility.Collapsed;
        LibraryList.ItemsSource = _libraryRows;
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
        PageTitleText.Text = Loc.Get("Downloads.Header.Title");
        PageSubtitleText.Text = Loc.Get("Downloads.Header.Subtitle");
        ToolTipService.SetToolTip(
            ImportButton,
            Loc.Get("Downloads.Actions.Import.ToolTip"));
        ImportButtonText.Text = Loc.Get("Downloads.Actions.Import.Label");
        ToolTipService.SetToolTip(
            OpenLibraryButton,
            Loc.Get("Downloads.Actions.OpenLibrary.ToolTip"));
        OpenLibraryButtonText.Text = Loc.Get("Downloads.Actions.OpenLibrary.Label");
        DownloadInfo.Title = Loc.Get("Downloads.Info.InitialTitle");
        DownloadInfo.Message = Loc.Get("Downloads.Info.InitialMessage");
        AddFromUrlSectionTitleText.Text = Loc.Get("Downloads.AddFromUrl.SectionTitle");
        SourceUrlTextBox.Header = Loc.Get("Downloads.AddFromUrl.SourceUrl.Header");
        FileNameTextBox.Header = Loc.Get("Downloads.AddFromUrl.FileName.Header");
        ConnectionCountPicker.Header = Loc.Get("Downloads.AddFromUrl.ConnectionCount.Header");
        MaximumSizeBox.Header = Loc.Get("Downloads.AddFromUrl.MaximumSize.Header");
        IntegrityToggle.Header = Loc.Get("Downloads.AddFromUrl.Integrity.Header");
        IntegrityToggle.OffContent = Loc.Get("Downloads.AddFromUrl.Integrity.Off");
        IntegrityToggle.OnContent = Loc.Get("Downloads.AddFromUrl.Integrity.On");
        HashAlgorithmPicker.Header = Loc.Get("Downloads.AddFromUrl.HashAlgorithm.Header");
        ExpectedHashTextBox.Header = Loc.Get("Downloads.AddFromUrl.ExpectedHash.Header");
        ExpectedHashTextBox.PlaceholderText = Loc.Get("Downloads.AddFromUrl.ExpectedHash.Placeholder");
        StartDownloadButtonText.Text = Loc.Get("Downloads.Actions.StartDownload");
        ToolTipService.SetToolTip(
            CancelTransferButton,
            Loc.Get("Downloads.Actions.CancelTransfer.ToolTip"));
        CancelTransferButtonText.Text = Loc.Get("Downloads.Actions.CancelTransfer.Label");
        TransferTitleText.Text = Loc.Get("Downloads.Transfer.IdleTitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            TransferProgressBar,
            Loc.Get("Downloads.Transfer.Progress.AccessibleName"));
        LibrarySectionTitleText.Text = Loc.Get("Downloads.Library.SectionTitle");
        LibrarySummaryText.Text = Loc.Get("Downloads.Library.SummaryLoading");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshLibraryButton,
            Loc.Get("Downloads.Library.Refresh.AccessibleName"));
        ToolTipService.SetToolTip(
            RefreshLibraryButton,
            Loc.Get("Downloads.Library.Refresh.ToolTip"));
    }

    private void OnInstallWheelButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(
                button,
                Loc.Get("Downloads.Library.Install.ToolTip"));
        }
    }

    private void OnInstallWheelButtonTextLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock textBlock)
        {
            textBlock.Text = Loc.Get("Downloads.Library.Install.Label");
        }
    }

    private void OnOpenItemFolderButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(
                button,
                Loc.Get("Downloads.Library.OpenLocation.ToolTip"));
        }
    }

    private void OnDeleteLibraryItemButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(
                button,
                Loc.Get("Downloads.Library.Delete.ToolTip"));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _manager.StateChanged += OnManagerStateChanged;
        await LoadDownloadDefaultsAsync();
        RefreshLibrary();
        UpdateTransferState();
    }

    private async Task LoadDownloadDefaultsAsync()
    {
        if (_defaultsLoaded)
        {
            return;
        }

        _defaultsLoaded = true;
        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            AutoEnvPlusApplicationSettings settings =
                await new AutoEnvPlusApplicationSettingsStore(managedRoot).LoadAsync();
            if (ManagedSegmentedDownloader.SupportedConnectionCounts.Contains(
                    settings.DefaultDownloadConnections))
            {
                ConnectionCountPicker.SelectedItem = settings.DefaultDownloadConnections;
            }

            MaximumSizeBox.Value = settings.DefaultDownloadMaximumBytes / (1024d * 1024d);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            DownloadInfo.Severity = InfoBarSeverity.Warning;
            DownloadInfo.Title = Loc.Get("Downloads.Messages.DefaultsUnavailableTitle");
            DownloadInfo.Message = Loc.Format(
                "Downloads.Messages.DefaultsUnavailableMessage",
                exception.Message);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        // The pip install cancellation source lives in the app-scoped
        // AppDownloadManager, so leaving this page no longer aborts a
        // running install; only the UI subscription is detached here.
        _manager.StateChanged -= OnManagerStateChanged;
    }

    private async void OnStartDownloadClicked(object sender, RoutedEventArgs args)
    {
        if (_manager.Snapshot?.IsBusy == true || _pipInstallRunning)
        {
            return;
        }

        if (!Uri.TryCreate(SourceUrlTextBox.Text.Trim(), UriKind.Absolute, out Uri? sourceUri)
            || sourceUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(sourceUri.UserInfo))
        {
            ShowError(
                Loc.Get("Downloads.Messages.DownloadFailedToStartTitle"),
                Loc.Get("Downloads.Messages.DownloadInvalidUrl"));
            return;
        }

        string fileName = FileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            ShowError(
                Loc.Get("Downloads.Messages.DownloadFailedToStartTitle"),
                Loc.Get("Downloads.Messages.DownloadMissingFileName"));
            return;
        }

        if (!await ConfirmReplaceExistingFileAsync(fileName))
        {
            return;
        }

        if (!TryGetMaximumBytes(out long maximumBytes, out string? sizeError))
        {
            ShowError(
                Loc.Get("Downloads.Messages.DownloadFailedToStartTitle"),
                sizeError ?? Loc.Get("Downloads.Messages.InvalidInput"));
            return;
        }

        if (!TryCreateIntegrity(out PackageHashExpectation? integrity, out string? hashError))
        {
            ShowError(
                Loc.Get("Downloads.Messages.DownloadFailedToStartTitle"),
                hashError ?? Loc.Get("Downloads.Messages.InvalidHash"));
            return;
        }

        EffectiveNetworkSettings? network = await LoadDownloadNetworkSettingsAsync();
        if (network is null)
        {
            return;
        }

        int connectionCount = ConnectionCountPicker.SelectedItem is int selected ? selected : 8;
        try
        {
            SegmentedDownloadResult result = await _manager.DownloadAsync(
                new SegmentedDownloadRequest(
                    sourceUri,
                    fileName,
                    connectionCount,
                    maximumBytes,
                    integrity),
                network);
            string mode = result.TransferMode == DownloadTransferMode.Segmented
                ? Loc.Format("Downloads.Transfer.ModeSegmented", result.SegmentCount)
                : result.FallbackReason is null
                    ? Loc.Get("Downloads.Transfer.ModeSingle")
                    : Loc.Format(
                        "Downloads.Transfer.ModeSingleFallback",
                        FallbackText(result.FallbackReason.Value));
            DownloadInfo.Severity = InfoBarSeverity.Success;
            DownloadInfo.Title = Loc.Get("Downloads.Messages.DownloadCompletedTitle");
            DownloadInfo.Message = $"{result.FilePath} · {mode}";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "Downloads.Messages.DownloadCompletedActivity",
                    Path.GetFileName(result.FilePath),
                    mode),
                [result.FilePath]);
            RefreshLibrary();
        }
        catch (OperationCanceledException)
        {
            DownloadInfo.Severity = InfoBarSeverity.Informational;
            DownloadInfo.Title = Loc.Get("Downloads.Messages.DownloadCancelledTitle");
            DownloadInfo.Message = Loc.Get("Downloads.Messages.DownloadCancelledMessage");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Cancelled,
                Loc.Format("Downloads.Messages.DownloadCancelledActivity", fileName),
                [_manager.LibraryRoot]);
        }
        catch (HttpRequestException exception)
        {
            ShowError(
                Loc.Get("Downloads.Messages.DownloadIncompleteTitle"),
                AppDownloadManager.DescribeNetworkFailure(exception));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Failed,
                Loc.Format(
                    "Downloads.Messages.DownloadFailedActivity",
                    fileName,
                    exception.GetType().Name),
                [_manager.LibraryRoot]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError(Loc.Get("Downloads.Messages.DownloadIncompleteTitle"), exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Failed,
                Loc.Format(
                    "Downloads.Messages.DownloadFailedActivity",
                    fileName,
                    exception.GetType().Name),
                [_manager.LibraryRoot]);
        }
    }

    private async void OnImportClicked(object sender, RoutedEventArgs args)
    {
        if ((_manager.Snapshot?.IsBusy == true || _pipInstallRunning)
            || ((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = Loc.Get("Downloads.Picker.ImportCommitButton"),
        };
        foreach (string extension in ManagedDownloadLibrary.AllowedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (!TryGetMaximumBytes(out long maximumBytes, out string? sizeError))
        {
            ShowError(
                Loc.Get("Downloads.Messages.ImportFailedTitle"),
                sizeError ?? Loc.Get("Downloads.Messages.InvalidInput"));
            return;
        }

        if (!TryCreateIntegrity(out PackageHashExpectation? integrity, out string? hashError))
        {
            ShowError(
                Loc.Get("Downloads.Messages.ImportFailedTitle"),
                hashError ?? Loc.Get("Downloads.Messages.InvalidHash"));
            return;
        }

        string importTargetPath = Path.Combine(_manager.LibraryRoot, file.Name);
        bool importConflict = File.Exists(importTargetPath);
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Downloads.Import.DialogTitle", file.Name),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                        "Downloads.Import.DialogText",
                        file.Path,
                        _manager.LibraryRoot)
                    + (importConflict
                        ? Loc.Get("Downloads.Import.ConflictNote")
                        : string.Empty),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = importConflict
                ? Loc.Get("Downloads.Import.OverwriteButton")
                : Loc.Get("Downloads.Import.CopyButton"),
            CloseButtonText = Loc.Get("Downloads.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (importConflict)
        {
            try
            {
                File.Delete(importTargetPath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                ShowError(Loc.Get("Downloads.Messages.OverwriteFailedTitle"), exception.Message);
                return;
            }
        }

        try
        {
            LocalPackageImportResult result = await _manager.ImportAsync(
                new LocalPackageImportRequest(file.Path, null, maximumBytes, integrity));
            DownloadInfo.Severity = InfoBarSeverity.Success;
            DownloadInfo.Title = Loc.Get("Downloads.Messages.ImportCompletedTitle");
            DownloadInfo.Message = result.HasVerifiedExpectedHash
                ? Loc.Format(
                    "Downloads.Messages.ImportVerifiedMessage",
                    result.FilePath,
                    result.ExpectedHashAlgorithm?.DisplayName())
                : Loc.Format("Downloads.Messages.ImportUnverifiedMessage", result.FilePath);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageImport,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "Downloads.Messages.ImportCompletedActivity",
                    Path.GetFileName(result.FilePath)),
                [result.FilePath]);
            RefreshLibrary();
        }
        catch (OperationCanceledException)
        {
            DownloadInfo.Severity = InfoBarSeverity.Informational;
            DownloadInfo.Title = Loc.Get("Downloads.Messages.ImportCancelledTitle");
            DownloadInfo.Message = Loc.Get("Downloads.Messages.ImportCancelledMessage");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageImport,
                ActivityStatus.Cancelled,
                Loc.Format("Downloads.Messages.ImportCancelledActivity", file.Name),
                [_manager.LibraryRoot]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError(Loc.Get("Downloads.Messages.ImportIncompleteTitle"), exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageImport,
                ActivityStatus.Failed,
                Loc.Format(
                    "Downloads.Messages.ImportFailedActivity",
                    file.Name,
                    exception.GetType().Name),
                [_manager.LibraryRoot]);
        }
    }

    private async Task<bool> ConfirmReplaceExistingFileAsync(string fileName)
    {
        // The core library refuses to clobber an existing destination (a
        // safety guarantee); without this pre-check the user only learns
        // about the conflict after the whole transfer finishes.
        string targetPath = Path.Combine(_manager.LibraryRoot, fileName);
        if (!File.Exists(targetPath))
        {
            return true;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Downloads.Replace.DialogTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format("Downloads.Replace.DialogText", targetPath),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Downloads.Replace.ConfirmButton"),
            CloseButtonText = Loc.Get("Downloads.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        try
        {
            File.Delete(targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            ShowError(Loc.Get("Downloads.Messages.OverwriteFailedTitle"), exception.Message);
            return false;
        }
    }

    private void OnCancelTransferClicked(object sender, RoutedEventArgs args)
    {
        _manager.Cancel();
        _manager.CancelPipInstall();
    }

    private void OnRefreshLibraryClicked(object sender, RoutedEventArgs args) =>
        RefreshLibrary();

    private void OnOpenLibraryClicked(object sender, RoutedEventArgs args) =>
        OpenFolder(_manager.LibraryRoot);

    private void OnOpenItemFolderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: LibraryRow row })
        {
            OpenFolder(Path.GetDirectoryName(row.Item.FilePath)!);
        }
    }

    private async void OnDeleteLibraryItemClicked(object sender, RoutedEventArgs args)
    {
        if ((_manager.Snapshot?.IsBusy == true || _pipInstallRunning)
            || sender is not Button { Tag: LibraryRow row })
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Downloads.Delete.DialogTitle", row.FileName),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format("Downloads.Delete.DialogText", row.Item.FilePath),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Downloads.Delete.ConfirmButton"),
            CloseButtonText = Loc.Get("Downloads.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ManagedDownloadDeleteResult result = await _manager.Library.DeleteAsync(row.FileName);
            DownloadInfo.Severity = InfoBarSeverity.Success;
            DownloadInfo.Title = result.Deleted
                ? Loc.Get("Downloads.Messages.DeleteCompletedTitle")
                : Loc.Get("Downloads.Messages.DeleteMissingTitle");
            DownloadInfo.Message = row.Item.FilePath;
            RefreshLibrary();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError(Loc.Get("Downloads.Messages.DeleteFailedTitle"), exception.Message);
        }
    }

    private async void OnInstallWheelClicked(object sender, RoutedEventArgs args)
    {
        if (_manager.Snapshot?.IsBusy == true
            || _pipInstallRunning
            || sender is not Button { Tag: LibraryRow { Item.Extension: ".whl" } row })
        {
            return;
        }

        string managedRoot = ManagedRootResolver.ResolveOrThrow();
        RegistryLoadResult registry;
        try
        {
            registry = await new ManagedRuntimeRegistry(managedRoot).LoadAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            ShowError(Loc.Get("Downloads.Messages.PythonRegistryReadFailedTitle"), exception.Message);
            return;
        }

        if (registry.Errors.Count > 0)
        {
            ShowError(
                Loc.Get("Downloads.Messages.PythonRegistryInvalidTitle"),
                string.Join(Loc.Get("Downloads.Separator"), registry.Errors.Take(3)));
            return;
        }

        RuntimeChoice[] runtimes = registry.Entries
            .Where(entry => entry.Kind == RuntimeKind.Python && File.Exists(entry.ExecutablePath))
            .OrderByDescending(entry => entry.Version)
            .Select(entry => new RuntimeChoice(entry))
            .ToArray();
        if (runtimes.Length == 0)
        {
            ShowError(
                Loc.Get("Downloads.Messages.NoManagedPythonTitle"),
                Loc.Get("Downloads.Messages.NoManagedPythonMessage"));
            return;
        }

        ComboBox runtimePicker = new()
        {
            Header = Loc.Get("Downloads.Pip.BasePythonHeader"),
            ItemsSource = runtimes,
            DisplayMemberPath = nameof(RuntimeChoice.DisplayName),
            SelectedIndex = 0,
        };
        AutomationProperties.SetName(
            runtimePicker,
            Loc.Get("Downloads.Pip.BasePythonHeader"));
        TextBox environmentName = new()
        {
            Header = Loc.Get("Downloads.Pip.EnvironmentNameHeader"),
            Text = CreateEnvironmentName(row.FileName),
        };
        ComboBox sourceModePicker = new()
        {
            Header = Loc.Get("Downloads.Pip.SourceModeHeader"),
            ItemsSource = new[]
            {
                new SourceModeChoice(
                    PipPackageSourceMode.OfflineManagedLibrary,
                    Loc.Get("Downloads.Pip.SourceMode.Offline")),
                new SourceModeChoice(
                    PipPackageSourceMode.ConfiguredNetwork,
                    Loc.Get("Downloads.Pip.SourceMode.Network")),
            },
            DisplayMemberPath = nameof(SourceModeChoice.DisplayName),
            SelectedIndex = 0,
        };
        AutomationProperties.SetName(
            sourceModePicker,
            Loc.Get("Downloads.Pip.SourceModeHeader"));
        StackPanel choices = new() { Spacing = 12 };
        choices.Children.Add(runtimePicker);
        choices.Children.Add(environmentName);
        choices.Children.Add(sourceModePicker);
        choices.Children.Add(new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Severity = InfoBarSeverity.Warning,
            Title = Loc.Get("Downloads.Pip.NonTransactionalTitle"),
            Message = Loc.Get("Downloads.Pip.NonTransactionalMessage"),
        });
        ContentDialog optionsDialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Downloads.Pip.DialogTitle", row.FileName),
            Content = choices,
            PrimaryButtonText = Loc.Get("Downloads.Pip.PlanButton"),
            CloseButtonText = Loc.Get("Downloads.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await optionsDialog.ShowAsync() != ContentDialogResult.Primary
            || runtimePicker.SelectedItem is not RuntimeChoice runtime
            || sourceModePicker.SelectedItem is not SourceModeChoice sourceMode)
        {
            return;
        }

        EffectiveNetworkSettings? pipNetwork = null;
        if (sourceMode.Mode == PipPackageSourceMode.ConfiguredNetwork)
        {
            pipNetwork = await LoadToolNetworkSettingsAsync(NetworkToolIds.Pip, "pip");
            if (pipNetwork is null)
            {
                return;
            }
        }

        PipLocalPackageInstallService service = new(managedRoot);
        PipLocalPackageInstallPlan plan;
        try
        {
            plan = await service.CreatePlanAsync(new PipLocalPackageInstallRequest(
                runtime.Entry,
                row.Item.FilePath,
                environmentName.Text.Trim(),
                sourceMode.Mode,
                pipNetwork));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError(Loc.Get("Downloads.Messages.PipPlanFailedTitle"), exception.Message);
            return;
        }

        string createStep = plan.RequiresEnvironmentCreation
            ? FormatCommand(plan.CreateEnvironmentCommand)
            : Loc.Get("Downloads.Pip.ReuseEnvironmentStep");
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Downloads.Pip.ConfirmDialogTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = Loc.Format(
                        "Downloads.Pip.ConfirmDialogText",
                        plan.WheelPath,
                        plan.EnvironmentRoot,
                        createStep,
                        FormatCommand(plan.InstallPackageCommand),
                        service.TemporaryRoot,
                        service.PipCacheRoot),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = Loc.Get("Downloads.Pip.ExecuteButton"),
            CloseButtonText = Loc.Get("Downloads.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _pipInstallRunning = true;
        CancellationToken pipToken = _manager.BeginPipInstall();
        UpdateTransferState();
        try
        {
            Progress<PipLocalPackageInstallProgress> progress = new(value =>
            {
                TransferTitleText.Text = $"{PipStageText(value.Stage)} · {row.FileName}";
                TransferDetailText.Text = value.Message;
                TransferBytesText.Text = string.Empty;
                TransferProgressBar.IsIndeterminate =
                    value.Stage is not PipLocalPackageInstallStage.Completed
                    and not PipLocalPackageInstallStage.Cancelled;
            });
            PipLocalPackageInstallResult result = await service.ExecuteAsync(
                plan,
                progress,
                pipToken);
            if (result.Success)
            {
                DownloadInfo.Severity = InfoBarSeverity.Success;
                DownloadInfo.Title = Loc.Get("Downloads.Messages.PipCompletedTitle");
                DownloadInfo.Message = AppendPipOutputCaptureNotice(
                    plan.EnvironmentRoot,
                    result);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PackageInstall,
                    ActivityStatus.Succeeded,
                    Loc.Format(
                        "Downloads.Messages.PipCompletedActivity",
                        row.FileName,
                        plan.EnvironmentName),
                    [row.Item.FilePath, plan.EnvironmentRoot]);
            }
            else if (result.WasCancelled)
            {
                DownloadInfo.Severity = InfoBarSeverity.Warning;
                DownloadInfo.Title = Loc.Get("Downloads.Messages.PipCancelledTitle");
                DownloadInfo.Message = AppendPipOutputCaptureNotice(
                    Loc.Get("Downloads.Messages.PipCancelledMessage"),
                    result);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PackageInstall,
                    ActivityStatus.Cancelled,
                    Loc.Format("Downloads.Messages.PipCancelledActivity", row.FileName),
                    [plan.EnvironmentRoot]);
            }
            else
            {
                ShowError(
                    Loc.Get("Downloads.Messages.PipIncompleteTitle"),
                    AppendPipOutputCaptureNotice(
                        result.Error ?? Loc.Get("Downloads.Messages.PipFailedFallback"),
                        result));
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PackageInstall,
                    ActivityStatus.Failed,
                    Loc.Format("Downloads.Messages.PipFailedActivity", row.FileName),
                    [plan.EnvironmentRoot]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError(Loc.Get("Downloads.Messages.PipPlanInvalidTitle"), exception.Message);
        }
        finally
        {
            _pipInstallRunning = false;
            _manager.EndPipInstall();
            UpdateTransferState();
        }
    }

    private void OnIntegrityToggled(object sender, RoutedEventArgs args) =>
        IntegrityFields.Visibility = IntegrityToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnSourceUrlChanged(object sender, TextChangedEventArgs args)
    {
        if (_fileNameEdited
            || !Uri.TryCreate(SourceUrlTextBox.Text.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        string inferred = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(inferred))
        {
            return;
        }

        _suppressFileNameTracking = true;
        FileNameTextBox.Text = inferred;
        _suppressFileNameTracking = false;
    }

    private void OnFileNameChanged(object sender, TextChangedEventArgs args)
    {
        if (!_suppressFileNameTracking)
        {
            _fileNameEdited = !string.IsNullOrWhiteSpace(FileNameTextBox.Text);
        }
    }

    private async Task<EffectiveNetworkSettings?> LoadDownloadNetworkSettingsAsync()
    {
        NetworkSettingsLoadResult loaded = await new NetworkSettingsStore(
            ManagedRootResolver.ResolveOrThrow()).LoadAsync();
        if (!loaded.Success || loaded.Settings is null)
        {
            ShowError(
                Loc.Get("Downloads.Messages.NetworkSettingsReadFailedTitle"),
                string.Join(
                    Loc.Get("Downloads.Separator"),
                    loaded.Errors.Take(3).Select(error => error.Message)));
            return null;
        }

        NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(
            loaded.Settings,
            NetworkToolIds.Downloads);
        if (!resolved.Success || resolved.EffectiveSettings is null)
        {
            ShowError(
                Loc.Get("Downloads.Messages.DownloadNetworkSettingsInvalidTitle"),
                string.Join(
                    Loc.Get("Downloads.Separator"),
                    resolved.Errors.Take(3).Select(error => error.Message)));
            return null;
        }

        return resolved.EffectiveSettings;
    }

    private async Task<EffectiveNetworkSettings?> LoadToolNetworkSettingsAsync(
        string toolId,
        string displayName)
    {
        ProviderSourceNetworkSettingsLoadResult loaded =
            await new ProviderSourceNetworkSettingsLoader(
                ManagedRootResolver.ResolveOrThrow()).LoadForToolsAsync([toolId]);
        if (!loaded.Success || loaded.Settings is null)
        {
            ShowError(
                Loc.Get("Downloads.Messages.NetworkSettingsReadFailedTitle"),
                string.Join(Loc.Get("Downloads.Separator"), loaded.Errors.Take(3)));
            return null;
        }

        NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(
            loaded.Settings,
            toolId);
        if (!resolved.Success || resolved.EffectiveSettings is null)
        {
            ShowError(
                Loc.Format("Downloads.Messages.ToolNetworkSettingsInvalidTitle", displayName),
                string.Join(
                    Loc.Get("Downloads.Separator"),
                    resolved.Errors.Take(3).Select(error => error.Message)));
            return null;
        }

        return resolved.EffectiveSettings;
    }

    private static string CreateEnvironmentName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        char[] characters = stem
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-'
                    ? character
                    : '-')
            .ToArray();
        string normalized = new string(characters)
            .Trim('.', '_', '-')
            .Replace("..", ".", StringComparison.Ordinal);
        if (normalized.Length > 48)
        {
            normalized = normalized[..48].TrimEnd('.', '_', '-');
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? "wheel-env"
            : $"{normalized}-env";
    }

    private static string FormatCommand(PipLocalPackageInstallCommand command) =>
        command.ExecutablePath + " " + string.Join(' ', command.ArgumentList.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument) =>
        argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : argument;

    private static string PipStageText(PipLocalPackageInstallStage stage) => stage switch
    {
        PipLocalPackageInstallStage.Validating => Loc.Get("Downloads.Pip.Stage.Validating"),
        PipLocalPackageInstallStage.CreatingEnvironment => Loc.Get("Downloads.Pip.Stage.CreatingEnvironment"),
        PipLocalPackageInstallStage.InstallingPackage => Loc.Get("Downloads.Pip.Stage.InstallingPackage"),
        PipLocalPackageInstallStage.Completed => Loc.Get("Downloads.Pip.Stage.Completed"),
        PipLocalPackageInstallStage.Cancelled => Loc.Get("Downloads.Pip.Stage.Cancelled"),
        _ => stage.ToString(),
    };

    private static string AppendPipOutputCaptureNotice(
        string message,
        PipLocalPackageInstallResult result)
    {
        List<string> truncatedStreams = [];
        if (result.StandardOutputTruncated)
        {
            truncatedStreams.Add("stdout");
        }

        if (result.StandardErrorTruncated)
        {
            truncatedStreams.Add("stderr");
        }

        string notice = truncatedStreams.Count == 0
            ? message
            : Loc.Format(
                "Downloads.Pip.OutputTruncatedNotice",
                message,
                string.Join('/', truncatedStreams),
                PipLocalPackageProcessRunner.MaximumCapturedOutputCharacters);
        return notice + DescribePipOutput(result);
    }

    private static string DescribePipOutput(PipLocalPackageInstallResult result)
    {
        // Surface the pip subprocess output tail so the user can see what pip
        // actually did (resolved dependencies, installed packages) or the real
        // error text instead of a generic failure string.
        const int MaxTail = 400;
        PipLocalPackageInstallStageResult? installStage = result.Stages.FirstOrDefault(
            stage => stage.Stage == PipLocalPackageInstallStage.InstallingPackage);
        string? output = result.Success
            ? installStage?.StandardOutput
            : installStage?.StandardError ?? installStage?.StandardOutput;
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        string tail = output.Length > MaxTail
            ? "…" + output[^MaxTail..]
            : output;
        return Loc.Format("Downloads.Pip.OutputTail", tail.TrimEnd());
    }

    private bool TryCreateIntegrity(
        out PackageHashExpectation? integrity,
        out string? error)
    {
        integrity = null;
        error = null;
        if (!IntegrityToggle.IsOn)
        {
            return true;
        }

        PackageHashAlgorithm algorithm =
            (HashAlgorithmPicker.SelectedItem as ComboBoxItem)?.Tag as string == "sha512"
                ? PackageHashAlgorithm.Sha512
                : PackageHashAlgorithm.Sha256;
        string expected = ExpectedHashTextBox.Text.Trim();
        if (!algorithm.IsValidHash(expected))
        {
            error = Loc.Format(
                "Downloads.Messages.InvalidHashFormat",
                algorithm.DisplayName());
            return false;
        }

        integrity = new PackageHashExpectation(algorithm, expected);
        return true;
    }

    private bool TryGetMaximumBytes(out long maximumBytes, out string? error)
    {
        maximumBytes = 0;
        error = null;
        double megabytes = MaximumSizeBox.Value;
        double maximumMegabytes = AutoEnvPlusApplicationSettings.MaximumDownloadMaximumBytes
            / (1024d * 1024d);
        if (double.IsNaN(megabytes) || megabytes < 1 || megabytes > maximumMegabytes)
        {
            error = Loc.Format("Downloads.Messages.InvalidMaximumSize", maximumMegabytes);
            return false;
        }

        maximumBytes = checked((long)(megabytes * 1024d * 1024d));
        return true;
    }

    private void OnManagerStateChanged(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateTransferState();
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(UpdateTransferState);
        }
    }

    private void UpdateTransferState()
    {
        AppTransferSnapshot? snapshot = _manager.Snapshot;
        bool busy = snapshot?.IsBusy == true || _pipInstallRunning;
        StartDownloadButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        CancelTransferButton.IsEnabled = busy;
        if (snapshot is null)
        {
            TransferTitleText.Text = Loc.Get("Downloads.Transfer.IdleTitle");
            TransferBytesText.Text = string.Empty;
            TransferDetailText.Text = _manager.LibraryRoot;
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Value = 0;
            return;
        }

        TransferTitleText.Text = $"{PhaseText(snapshot.Phase, snapshot.Error)} · {snapshot.FileName}";
        string progressText = snapshot.TotalBytes is long total
            ? Loc.Format(
                "Downloads.Transfer.ProgressFormat",
                FormatBytes(snapshot.CompletedBytes),
                FormatBytes(total),
                total > 0 ? snapshot.CompletedBytes * 100d / total : 0)
            : FormatBytes(snapshot.CompletedBytes);
        TransferBytesText.Text = busy
            ? progressText + ComputeSpeedAndEta(snapshot)
            : progressText;
        TransferProgressBar.IsIndeterminate = busy && snapshot.TotalBytes is null;
        TransferProgressBar.Maximum = Math.Max(1, snapshot.TotalBytes ?? 1);
        TransferProgressBar.Value = Math.Min(
            TransferProgressBar.Maximum,
            snapshot.CompletedBytes);
        string segments = snapshot.TotalSegments > 0
            ? Loc.Format(
                "Downloads.Transfer.SegmentsSuffix",
                snapshot.CompletedSegments,
                snapshot.TotalSegments)
            : string.Empty;
        TransferDetailText.Text = $"{snapshot.Error ?? snapshot.OutputPath ?? snapshot.SourceDisplay}{segments}";

        if (!busy
            && snapshot.Phase == ManagedTransferPhase.Completed
            && _lastCompletedTransfer != snapshot.Id)
        {
            _lastCompletedTransfer = snapshot.Id;
            RefreshLibrary();
        }
    }

    private string ComputeSpeedAndEta(AppTransferSnapshot snapshot)
    {
        // Speed is measured in the page over a sliding window of progress
        // samples; the core transfer service reports progress but not rates.
        if (snapshot.Id != _speedSamplesTransferId)
        {
            _speedSamples.Clear();
            _speedSamplesTransferId = snapshot.Id;
        }

        if (snapshot.Phase is not (ManagedTransferPhase.Downloading
            or ManagedTransferPhase.Copying))
        {
            _speedSamples.Clear();
            return string.Empty;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _speedSamples.Add((now, snapshot.CompletedBytes));
        _speedSamples.RemoveAll(sample => now - sample.AtUtc > SpeedSampleWindow);

        if (_speedSamples.Count < 2)
        {
            return string.Empty;
        }

        (DateTimeOffset firstAt, long firstBytes) = _speedSamples[0];
        (DateTimeOffset lastAt, long lastBytes) = _speedSamples[^1];
        double elapsedSeconds = (lastAt - firstAt).TotalSeconds;
        if (elapsedSeconds < 1 || lastBytes < firstBytes)
        {
            return string.Empty;
        }

        double bytesPerSecond = (lastBytes - firstBytes) / elapsedSeconds;
        if (bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        string speed = $"{FormatBytes((long)bytesPerSecond)}/s";
        if (snapshot.TotalBytes is not long total || total <= snapshot.CompletedBytes)
        {
            return $" · {speed}";
        }

        TimeSpan eta = TimeSpan.FromSeconds(
            (total - snapshot.CompletedBytes) / bytesPerSecond);
        return Loc.Format("Downloads.Transfer.SpeedWithEta", speed, FormatEta(eta));
    }

    private static string FormatEta(TimeSpan eta) => eta.TotalHours >= 1
        ? Loc.Format("Downloads.Transfer.EtaHours", eta.TotalHours)
        : eta.TotalMinutes >= 1
            ? Loc.Format("Downloads.Transfer.EtaMinutesSeconds", (int)eta.TotalMinutes, eta.Seconds)
            : Loc.Format("Downloads.Transfer.EtaSeconds", eta.Seconds);

    private static readonly TimeSpan SpeedSampleWindow = TimeSpan.FromSeconds(6);

    private async void RefreshLibrary()
    {
        try
        {
            ManagedDownloadLibraryItem[] items = (await _manager.Library.ListFilesAsync())
                .ToArray();
            _libraryRows.Clear();
            foreach (ManagedDownloadLibraryItem item in items)
            {
                _libraryRows.Add(new LibraryRow(item));
            }

            LibrarySummaryText.Text = Loc.Format(
                "Downloads.Library.SummaryFormat",
                items.Length,
                FormatBytes(items.Sum(item => item.SizeBytes)),
                _manager.LibraryRoot);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            ShowError(Loc.Get("Downloads.Messages.LibraryReadFailedTitle"), exception.Message);
            LibrarySummaryText.Text = Loc.Get("Downloads.Library.SummaryFailed");
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            ProcessStartInfo startInfo = new()
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            ShowError(Loc.Get("Downloads.Messages.OpenLibraryFailedTitle"), exception.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        DownloadInfo.Severity = InfoBarSeverity.Error;
        DownloadInfo.Title = title;
        DownloadInfo.Message = CoreErrorText.Localize(message);
    }

    private static string PhaseText(ManagedTransferPhase phase, string? error) => error is not null
        ? Loc.Get("Downloads.Transfer.Phase.Failed")
        : phase switch
        {
            ManagedTransferPhase.Probing => Loc.Get("Downloads.Transfer.Phase.Probing"),
            ManagedTransferPhase.Downloading => Loc.Get("Downloads.Transfer.Phase.Downloading"),
            ManagedTransferPhase.Copying => Loc.Get("Downloads.Transfer.Phase.Copying"),
            ManagedTransferPhase.Verifying => Loc.Get("Downloads.Transfer.Phase.Verifying"),
            ManagedTransferPhase.Committing => Loc.Get("Downloads.Transfer.Phase.Committing"),
            ManagedTransferPhase.Completed => Loc.Get("Downloads.Transfer.Phase.Completed"),
            ManagedTransferPhase.Cancelled => Loc.Get("Downloads.Transfer.Phase.Cancelled"),
            _ => phase.ToString(),
        };

    private static string FallbackText(DownloadFallbackReason reason) => reason switch
    {
        DownloadFallbackReason.RangeNotSupported => Loc.Get("Downloads.Transfer.Fallback.RangeNotSupported"),
        DownloadFallbackReason.StableEntityUnavailable => Loc.Get("Downloads.Transfer.Fallback.StableEntityUnavailable"),
        DownloadFallbackReason.EntityChanged => Loc.Get("Downloads.Transfer.Fallback.EntityChanged"),
        _ => reason.ToString(),
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} B" : $"{size:F1} {units[unit]}";
    }

    private sealed record LibraryRow(ManagedDownloadLibraryItem Item)
    {
        public string FileName => Item.FileName;

        public string InstallActionAutomationName =>
            Loc.Format("Downloads.Library.InstallActionAccessibleName", FileName);

        public string OpenLocationActionAutomationName =>
            Loc.Format("Downloads.Library.OpenLocationActionAccessibleName", FileName);

        public string DeleteActionAutomationName =>
            Loc.Format("Downloads.Library.DeleteActionAccessibleName", FileName);

        public Visibility PipInstallVisibility => Item.Extension.Equals(
            ".whl",
            StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string Summary
        {
            get
            {
                string origin = Item.Origin switch
                {
                    ManagedDownloadOrigin.Network => Loc.Get("Downloads.Library.Origin.Network"),
                    ManagedDownloadOrigin.LocalImport => Loc.Get("Downloads.Library.Origin.LocalImport"),
                    _ => Loc.Get("Downloads.Library.Origin.Existing"),
                };
                string mode = Item.TransferMode switch
                {
                    DownloadTransferMode.Segmented => Loc.Get("Downloads.Library.TransferMode.Segmented"),
                    DownloadTransferMode.SingleStream => Loc.Get("Downloads.Transfer.ModeSingle"),
                    _ => Loc.Get("Downloads.Library.TransferMode.LocalCopy"),
                };
                return $"{FormatBytes(Item.SizeBytes)} · {origin} · {mode} · "
                    + (Item.Source ?? Loc.Get("Downloads.Library.SourceUnknown"));
            }
        }

        public string IntegritySummary => Item.HasVerifiedExpectedHash
            ? Loc.Format(
                "Downloads.Library.IntegrityVerified",
                Item.ExpectedHashAlgorithm?.DisplayName(),
                Item.VerifiedHash)
            : Item.HasRecordedExpectedHashEvidence && Item.ContentIdentityChanged
                ? Loc.Get("Downloads.Library.IntegrityChanged")
                : Item.HasRecordedExpectedHashEvidence
                    ? Loc.Get("Downloads.Library.IntegrityRecordedOnly")
                    : Item.ContentSha256 is string sha256
                        ? Loc.Format("Downloads.Library.IntegritySha256", sha256)
                        : Loc.Get("Downloads.Library.IntegrityNone");
    }

    private sealed record RuntimeChoice(ManagedRuntimeEntry Entry)
    {
        public string DisplayName =>
            $"Python {Entry.Version} · {Entry.Architecture} · {Entry.InstallRoot}";
    }

    private sealed record SourceModeChoice(
        PipPackageSourceMode Mode,
        string DisplayName);
}
