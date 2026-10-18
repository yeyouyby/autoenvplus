using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class StoragePage : Page
{
    private readonly CacheDirectoryService _service = new();
    private readonly ObservableCollection<CacheRow> _rows = [];
    private readonly ObservableCollection<CleanupItemRow> _cleanupItems = [];
    private CancellationTokenSource _pageCancellation = new();
    private readonly string _managedRoot;
    private readonly CacheCleanupService _cleanupService;
    private CancellationTokenSource? _operationCancellation;
    private string? _lastMigrationSnapshot;
    private bool _isBusy;

    public StoragePage()
    {
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _cleanupService = new CacheCleanupService(_managedRoot);
        InitializeComponent();
        ApplyLocalizedStrings();
        CacheList.ItemsSource = _rows;
        CleanupItemPicker.ItemsSource = _cleanupItems;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _operationCancellation?.Cancel();
            _pageCancellation.Cancel();
        };
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
        PageTitleText.Text = Loc.Get("Storage.Header.Title");
        PageSubtitleText.Text = Loc.Get("Storage.Header.Subtitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StorageOperationProgress,
            Loc.Get("Storage.Progress.AccessibleName"));
        ToolTipService.SetToolTip(
            CancelStorageOperationButton,
            Loc.Get("Storage.Actions.CancelOperation.ToolTip"));
        CancelStorageOperationButtonText.Text = Loc.Get("Storage.Actions.CancelOperation.Label");
        ToolTipService.SetToolTip(
            RollbackButton,
            Loc.Get("Storage.Actions.Rollback.ToolTip"));
        RollbackButtonText.Text = Loc.Get("Storage.Actions.Rollback.Label");
        MeasureButton.Content = Loc.Get("Storage.Actions.MeasureAll");
        StorageInfo.Title = Loc.Get("Storage.Info.InitialTitle");
        StorageInfo.Message = Loc.Get("Storage.Info.InitialMessage");
        CleanupSectionTitleText.Text = Loc.Get("Storage.Cleanup.SectionTitle");
        CleanupItemCountText.Text = Loc.Get("Storage.Cleanup.EmptyCount");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            CleanupItemPicker,
            Loc.Get("Storage.Cleanup.Picker.AccessibleName"));
        CleanupItemPicker.PlaceholderText = Loc.Get("Storage.Cleanup.Picker.Placeholder");
        ToolTipService.SetToolTip(
            RestoreCleanupButton,
            Loc.Get("Storage.Cleanup.Restore.ToolTip"));
        RestoreCleanupButtonText.Text = Loc.Get("Storage.Cleanup.Restore.Label");
        ToolTipService.SetToolTip(
            PurgeCleanupButton,
            Loc.Get("Storage.Cleanup.Purge.ToolTip"));
        PurgeCleanupButtonText.Text = Loc.Get("Storage.Cleanup.Purge.Label");
        CacheToolColumnHeaderText.Text = Loc.Get("Storage.Cache.Columns.Tool");
        CacheDirectoryColumnHeaderText.Text = Loc.Get("Storage.Cache.Columns.Directory");
        CacheSourceColumnHeaderText.Text = Loc.Get("Storage.Cache.Columns.Source");
        CacheSizeColumnHeaderText.Text = Loc.Get("Storage.Cache.Columns.Size");
        CacheActionsColumnHeaderText.Text = Loc.Get("Storage.Cache.Columns.Actions");
    }

    private void OnOpenFolderButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            button.Content = Loc.Get("Storage.Cache.Actions.Open.Label");
            ToolTipService.SetToolTip(
                button,
                Loc.Get("Storage.Cache.Actions.Open.ToolTip"));
        }
    }

    private void OnMigrateButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            button.Content = Loc.Get("Storage.Cache.Actions.Migrate");
        }
    }

    private void OnCleanupButtonTextLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock textBlock)
        {
            textBlock.Text = Loc.Get("Storage.Cache.Actions.Cleanup");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        // The shell caches this page across navigations; the previous visit's
        // Unloaded cancelled the token, so re-arm it on every load.
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
        _pageCancellation = new CancellationTokenSource();
        try
        {
            await ReloadStorageAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.RefreshFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
        }
    }

    private void LoadLocations()
    {
        _rows.Clear();
        foreach (CacheDirectoryLocation location in _service.DiscoverCurrent())
        {
            _rows.Add(new CacheRow(location));
        }
    }

    private async Task ReloadStorageAsync()
    {
        LoadLocations();
        DiscoverLatestMigrationSnapshot();
        await RefreshCleanupItemsAsync();
    }

    private void DiscoverLatestMigrationSnapshot()
    {
        // Migration snapshots persist under the managed root; without this
        // lookup the rollback affordance would stay dead after an app
        // restart even though valid snapshots exist on disk.
        if (_lastMigrationSnapshot is not null)
        {
            return;
        }

        try
        {
            string snapshotDirectory = Path.Combine(
                _managedRoot,
                "state",
                "cache-migration-snapshots");
            if (!Directory.Exists(snapshotDirectory))
            {
                return;
            }

            FileInfo? latest = new DirectoryInfo(snapshotDirectory)
                .EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is not null)
            {
                _lastMigrationSnapshot = latest.FullName;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // A failed snapshot lookup must not block the rest of the page.
        }

        RollbackButton.IsEnabled = _lastMigrationSnapshot is not null;
    }

    private async Task RefreshAfterMutationAsync()
    {
        try
        {
            await ReloadStorageAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            string completedTitle = StorageInfo.Title;
            string completedMessage = StorageInfo.Message;
            StorageInfo.Severity = InfoBarSeverity.Warning;
            StorageInfo.Title = Loc.Format("Storage.Messages.RefreshPartialTitle", completedTitle);
            StorageInfo.Message = Loc.Format(
                "Storage.Messages.RefreshPartialMessage",
                completedMessage,
                exception.Message);
        }
    }

    private async Task RefreshCleanupItemsAsync()
    {
        CacheCleanupCatalog catalog = await _cleanupService.DiscoverItemsAsync(
            _rows.Select(row => row.Location),
            _pageCancellation.Token);
        _cleanupItems.Clear();
        foreach (CacheCleanupItem item in catalog.Items)
        {
            _cleanupItems.Add(new CleanupItemRow(item));
        }

        CleanupItemCountText.Text = _cleanupItems.Count == 0
            ? Loc.Get("Storage.Cleanup.EmptyCount")
            : Loc.Format("Storage.Cleanup.CountFormat", _cleanupItems.Count);
        CleanupItemPicker.SelectedIndex = _cleanupItems.Count > 0 ? 0 : -1;
        UpdateCleanupButtons();
        if (catalog.Errors.Count > 0)
        {
            StorageInfo.Severity = InfoBarSeverity.Warning;
            StorageInfo.Title = Loc.Get("Storage.Messages.CleanupRecheckFailedTitle");
            StorageInfo.Message = string.Join(
                Loc.Get("Storage.Separator"),
                catalog.Errors.Take(3));
        }
    }

    private async void OnMeasureClicked(object sender, RoutedEventArgs args)
    {
        CancellationToken operationToken = BeginStorageOperation();
        StorageInfo.Severity = InfoBarSeverity.Informational;
        StorageInfo.Title = Loc.Get("Storage.Messages.MeasuringTitle");
        try
        {
            List<string> measurementErrors = [];
            foreach (CacheRow row in _rows)
            {
                row.SizeText = row.Location.Exists
                    ? Loc.Get("Storage.Cache.Size.Measuring")
                    : Loc.Get("Storage.Cache.Size.Missing");
                CacheDirectoryMeasurement measurement = await _service.MeasureAsync(
                    row.Location,
                    operationToken);
                row.SizeText = measurement.Location.Exists
                    ? Loc.Format(
                        "Storage.Cache.Size.Summary",
                        FormatBytes(measurement.TotalBytes),
                        measurement.FileCount)
                    : Loc.Get("Storage.Cache.Size.Missing");
                measurementErrors.AddRange(measurement.Errors);
            }

            if (measurementErrors.Count > 0)
            {
                StorageInfo.Severity = InfoBarSeverity.Warning;
                StorageInfo.Title = Loc.Get("Storage.Messages.MeasurePartialTitle");
                StorageInfo.Message = string.Join(
                    Loc.Get("Storage.Separator"),
                    measurementErrors.Take(3));
            }
            else
            {
                StorageInfo.Severity = InfoBarSeverity.Success;
                StorageInfo.Title = Loc.Get("Storage.Messages.MeasureCompletedTitle");
                StorageInfo.Message = Loc.Get("Storage.Messages.MeasureCompletedMessage");
            }
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Title = Loc.Get("Storage.Messages.MeasureCancelledTitle");
            StorageInfo.Message = string.Empty;
        }
        finally
        {
            EndStorageOperation();
        }
    }

    private async void OnMigrateClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CacheRow row } || !row.CanMigrate)
        {
            return;
        }

        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            CommitButtonText = Loc.Get("Storage.Picker.MigrateCommitButton"),
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

        string destination = Path.Combine(folder.Path, "AutoEnvPlusCaches", row.Location.Definition.Id);
        CacheMigrationService migrationService = new(_managedRoot);
        CacheMigrationPlan plan;
        try
        {
            plan = migrationService.CreatePlan(row.Location, destination);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or DirectoryNotFoundException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.MigrationPlanFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Storage.Migration.DialogTitle", row.Name),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Storage.Migration.DialogText",
                    plan.Source.DirectoryPath,
                    plan.DestinationPath,
                    plan.ConfigurationDescription),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Storage.Migration.ConfirmButton"),
            CloseButtonText = Loc.Get("Storage.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CancellationToken operationToken = BeginStorageOperation();
        bool terminalActivityWritten = false;
        try
        {
            Progress<CacheMigrationProgress> progress = new(value =>
            {
                StorageInfo.Severity = InfoBarSeverity.Informational;
                StorageInfo.Title = value.Stage switch
                {
                    "measure" => Loc.Get("Storage.Migration.Stage.Measure"),
                    "copy" => Loc.Get("Storage.Migration.Stage.Copy"),
                    "commit" => Loc.Get("Storage.Migration.Stage.Commit"),
                    "configure" => Loc.Get("Storage.Migration.Stage.Configure"),
                    "complete" => Loc.Get("Storage.Migration.Stage.Complete"),
                    _ => value.Stage,
                };
                StorageInfo.Message = value.TotalBytes > 0
                    ? $"{value.CompletedBytes / 1_048_576d:F1} / {value.TotalBytes / 1_048_576d:F1} MB · {value.RelativePath}"
                    : value.RelativePath ?? string.Empty;
            });
            CacheMigrationResult result = await migrationService.MigrateAsync(
                plan,
                new WindowsUserEnvironmentVariableStore(),
                progress,
                operationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Storage.Migration.Failed"));
            }

            _lastMigrationSnapshot = result.SnapshotPath;
            RollbackButton.IsEnabled = _lastMigrationSnapshot is not null;
            StorageInfo.Severity = InfoBarSeverity.Success;
            StorageInfo.Title = Loc.Get("Storage.Messages.MigrationCompletedTitle");
            StorageInfo.Message = result.SnapshotPath is null
                ? Loc.Format(
                    "Storage.Messages.MigrationCompletedNoSnapshot",
                    result.DestinationPath)
                : Loc.Format(
                    "Storage.Messages.MigrationCompletedWithSnapshot",
                    result.DestinationPath,
                    result.SnapshotPath);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheMigration,
                ActivityStatus.Succeeded,
                Loc.Format("Storage.Messages.MigrationCompletedActivity", row.Name),
                [plan.Source.DirectoryPath, result.DestinationPath ?? plan.DestinationPath],
                result.SnapshotPath,
                result.SnapshotPath);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = Loc.Get("Storage.Messages.MigrationCancelledTitle");
            StorageInfo.Message = Loc.Get("Storage.Messages.MigrationCancelledMessage");
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheMigration,
                    ActivityStatus.Cancelled,
                    Loc.Format("Storage.Messages.MigrationCancelledActivity", row.Name),
                    [plan.Source.DirectoryPath, plan.DestinationPath]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.MigrationFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheMigration,
                    ActivityStatus.Failed,
                    Loc.Format(
                        "Storage.Messages.MigrationFailedActivity",
                        row.Name,
                        exception.GetType().Name),
                    [plan.Source.DirectoryPath, plan.DestinationPath]);
            }
        }
        finally
        {
            EndStorageOperation();
        }
    }

    private async void OnCleanupClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CacheRow row } || !row.CanClean)
        {
            return;
        }

        CacheCleanupPlan plan;
        CancellationToken planCancellation = BeginStorageOperation();
        StorageInfo.Severity = InfoBarSeverity.Informational;
        StorageInfo.Title = Loc.Format("Storage.Messages.CleanupCheckingTitle", row.Name);
        StorageInfo.Message = Loc.Get("Storage.Messages.CleanupCheckingMessage");
        try
        {
            plan = await _cleanupService.CreatePlanAsync(
                row.Location,
                planCancellation);
            row.SizeText = Loc.Format(
                "Storage.Cache.Size.Summary",
                FormatBytes(plan.TotalBytes),
                plan.FileCount);
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Title = Loc.Get("Storage.Messages.CleanupPlanCancelledTitle");
            StorageInfo.Message = string.Empty;
            return;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.CleanupPlanFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            return;
        }
        finally
        {
            EndStorageOperation();
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Storage.Cleanup.DialogTitle", row.Name),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Storage.Cleanup.DialogText",
                    plan.Source.DirectoryPath,
                    plan.TrashPath,
                    plan.FileCount,
                    FormatBytes(plan.TotalBytes),
                    plan.TopLevelEntryCount),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Storage.Cleanup.ConfirmButton"),
            CloseButtonText = Loc.Get("Storage.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CancellationToken cleanupCancellation = BeginStorageOperation();
        bool terminalActivityWritten = false;
        try
        {
            Progress<CacheCleanupProgress> progress = CreateCleanupProgress(
                Loc.Get("Storage.Cleanup.ProgressIsolating"));
            CacheCleanupOperationResult result = await _cleanupService.CleanupAsync(
                plan,
                progress,
                cleanupCancellation);
            if (result.Success)
            {
                StorageInfo.Severity = InfoBarSeverity.Success;
                StorageInfo.Title = Loc.Get("Storage.Messages.CleanupIsolatedTitle");
                StorageInfo.Message = Loc.Format(
                    "Storage.Messages.CleanupIsolatedMessage",
                    result.FileCount,
                    FormatBytes(result.TotalBytes));
            }
            else
            {
                StorageInfo.Severity = result.Cancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
                StorageInfo.Title = result.Cancelled
                    ? Loc.Get("Storage.Messages.CleanupCancelledTitle")
                    : Loc.Get("Storage.Messages.CleanupIncompleteTitle");
                StorageInfo.Message = result.Error ?? Loc.Get("Storage.Messages.CleanupKeptMessage");
            }

            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheCleanup,
                result.Success
                    ? ActivityStatus.Succeeded
                    : result.Cancelled
                        ? ActivityStatus.Cancelled
                        : ActivityStatus.Failed,
                result.Success
                    ? Loc.Format("Storage.Messages.CleanupIsolatedActivity", row.Name)
                    : result.Cancelled
                        ? Loc.Format("Storage.Messages.CleanupCancelledConsistentActivity", row.Name)
                        : Loc.Format("Storage.Messages.CleanupIncompleteActivity", row.Name),
                [plan.Source.DirectoryPath, plan.TrashPath],
                result.ManifestPath,
                result.RecoveryAvailable ? result.ManifestPath : null);
            terminalActivityWritten = true;

            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = Loc.Get("Storage.Messages.CleanupCancelledTitle");
            StorageInfo.Message = Loc.Get("Storage.Messages.CleanupKeptMessage");
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Cancelled,
                    Loc.Format("Storage.Messages.CleanupCancelledActivity", row.Name),
                    [plan.Source.DirectoryPath, plan.TrashPath]);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.CleanupFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Failed,
                    Loc.Format(
                        "Storage.Messages.CleanupFailedActivity",
                        row.Name,
                        exception.GetType().Name),
                    [plan.Source.DirectoryPath, plan.TrashPath]);
            }
        }
        finally
        {
            EndStorageOperation();
        }
    }

    private void OnCleanupItemSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateCleanupButtons();

    private async void OnRestoreCleanupClicked(object sender, RoutedEventArgs args)
    {
        if (CleanupItemPicker.SelectedItem is not CleanupItemRow row || !row.CanRestore)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Storage.Restore.DialogTitle", row.Item.DisplayName),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Storage.Restore.DialogText",
                    row.Item.SourcePath,
                    row.Item.TrashPath,
                    row.Item.FileCount,
                    FormatBytes(row.Item.TotalBytes)),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Storage.Restore.ConfirmButton"),
            CloseButtonText = Loc.Get("Storage.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CancellationToken restoreCancellation = BeginStorageOperation();
        bool terminalActivityWritten = false;
        try
        {
            CacheCleanupOperationResult result = await _cleanupService.RestoreAsync(
                row.Item,
                CreateCleanupProgress(Loc.Get("Storage.Restore.ProgressRestoring")),
                restoreCancellation);
            StorageInfo.Severity = result.Success
                ? InfoBarSeverity.Success
                : result.Cancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
            StorageInfo.Title = result.Success
                ? Loc.Get("Storage.Messages.RestoreCompletedTitle")
                : result.Cancelled
                    ? Loc.Get("Storage.Messages.RestoreCancelledTitle")
                    : Loc.Get("Storage.Messages.RestoreRejectedTitle");
            StorageInfo.Message = result.Success
                ? Loc.Format(
                    "Storage.Messages.RestoreCompletedMessage",
                    result.FileCount,
                    FormatBytes(result.TotalBytes))
                : result.Error ?? Loc.Get("Storage.Messages.RestoreKeptMessage");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheCleanup,
                result.Success
                    ? ActivityStatus.Succeeded
                    : result.Cancelled
                        ? ActivityStatus.Cancelled
                        : ActivityStatus.Failed,
                result.Success
                    ? Loc.Format("Storage.Messages.RestoreCompletedActivity", row.Item.DisplayName)
                    : result.Cancelled
                        ? Loc.Format("Storage.Messages.RestoreCancelledActivity", row.Item.DisplayName)
                        : Loc.Format("Storage.Messages.RestoreRejectedActivity", row.Item.DisplayName),
                [row.Item.SourcePath, row.Item.TrashPath],
                row.Item.ManifestPath,
                row.Item.ManifestPath);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = Loc.Get("Storage.Messages.RestoreCancelledTitle");
            StorageInfo.Message = Loc.Get("Storage.Messages.RestoreCancelledMessage");
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Cancelled,
                    Loc.Format("Storage.Messages.RestoreCancelledActivity", row.Item.DisplayName),
                    [row.Item.SourcePath, row.Item.TrashPath],
                    row.Item.ManifestPath,
                    row.Item.ManifestPath);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.RestoreFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Failed,
                    Loc.Format(
                        "Storage.Messages.RestoreFailedActivity",
                        row.Item.DisplayName,
                        exception.GetType().Name),
                    [row.Item.SourcePath, row.Item.TrashPath],
                    row.Item.ManifestPath,
                    row.Item.ManifestPath);
            }
        }
        finally
        {
            EndStorageOperation();
        }
    }

    private async void OnPurgeCleanupClicked(object sender, RoutedEventArgs args)
    {
        if (CleanupItemPicker.SelectedItem is not CleanupItemRow row || !row.Item.CanPurge)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("Storage.Purge.DialogTitle", row.Item.DisplayName),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "Storage.Purge.DialogText",
                    row.Item.TrashPath,
                    row.Item.FileCount,
                    FormatBytes(row.Item.TotalBytes)),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Storage.Purge.ConfirmButton"),
            CloseButtonText = Loc.Get("Storage.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CancellationToken purgeCancellation = BeginStorageOperation();
        bool terminalActivityWritten = false;
        try
        {
            CacheCleanupOperationResult result = await _cleanupService.PurgeAsync(
                row.Item,
                CreateCleanupProgress(Loc.Get("Storage.Purge.ProgressPurging")),
                purgeCancellation);
            StorageInfo.Severity = result.Success
                ? InfoBarSeverity.Success
                : result.Cancelled
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error;
            StorageInfo.Title = result.Success
                ? Loc.Get("Storage.Messages.PurgeCompletedTitle")
                : result.Cancelled
                    ? Loc.Get("Storage.Messages.PurgeInterruptedTitle")
                    : Loc.Get("Storage.Messages.PurgeIncompleteTitle");
            StorageInfo.Message = result.Success
                ? Loc.Format(
                    "Storage.Messages.PurgeCompletedMessage",
                    FormatBytes(result.TotalBytes))
                : $"{result.Error ?? Loc.Get("Storage.Messages.PurgePendingFallback")} "
                    + Loc.Get("Storage.Messages.PurgeResumeHint");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheCleanup,
                result.Success
                    ? ActivityStatus.Succeeded
                    : result.Cancelled
                        ? ActivityStatus.Cancelled
                        : ActivityStatus.Failed,
                result.Success
                    ? Loc.Format("Storage.Messages.PurgeCompletedActivity", row.Item.DisplayName)
                    : result.Cancelled
                        ? Loc.Format("Storage.Messages.PurgeInterruptedActivity", row.Item.DisplayName)
                        : Loc.Format("Storage.Messages.PurgeIncompleteActivity", row.Item.DisplayName),
                [row.Item.SourcePath, row.Item.TrashPath],
                row.Item.ManifestPath);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Warning;
            StorageInfo.Title = Loc.Get("Storage.Messages.PurgeInterruptedTitle");
            StorageInfo.Message = Loc.Get("Storage.Messages.PurgeResumeHint");
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Cancelled,
                    Loc.Format("Storage.Messages.PurgeInterruptedActivity", row.Item.DisplayName),
                    [row.Item.SourcePath, row.Item.TrashPath],
                    row.Item.ManifestPath);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.PurgeFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Failed,
                    Loc.Format(
                        "Storage.Messages.PurgeFailedActivity",
                        row.Item.DisplayName,
                        exception.GetType().Name),
                    [row.Item.SourcePath, row.Item.TrashPath],
                    row.Item.ManifestPath);
            }
        }
        finally
        {
            EndStorageOperation();
        }
    }

    private Progress<CacheCleanupProgress> CreateCleanupProgress(string title) => new(value =>
    {
        StorageInfo.Severity = InfoBarSeverity.Informational;
        StorageInfo.Title = title;
        StorageInfo.Message = value.TotalBytes > 0
            ? $"{FormatBytes(value.CompletedBytes)} / {FormatBytes(value.TotalBytes)} · {value.RelativePath}"
            : value.RelativePath ?? string.Empty;
    });

    private void OnOpenFolderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CacheRow row } || !row.CanOpen)
        {
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(row.DirectoryPath);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.OpenFolderFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
        }
    }

    private async void OnRollbackClicked(object sender, RoutedEventArgs args)
    {
        if (_lastMigrationSnapshot is null)
        {
            return;
        }

        string snapshot = _lastMigrationSnapshot;
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Storage.Rollback.DialogTitle"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format("Storage.Rollback.DialogText", snapshot),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Storage.Rollback.ConfirmButton"),
            CloseButtonText = Loc.Get("Storage.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CancellationToken rollbackToken = BeginStorageOperation();
        bool terminalActivityWritten = false;
        try
        {
            CacheMigrationResult result = await new CacheMigrationService(_managedRoot).RollbackAsync(
                snapshot,
                new WindowsUserEnvironmentVariableStore(),
                rollbackToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Storage.Rollback.Failed"));
            }

            _lastMigrationSnapshot = null;
            StorageInfo.Severity = InfoBarSeverity.Success;
            StorageInfo.Title = Loc.Get("Storage.Messages.RollbackCompletedTitle");
            StorageInfo.Message = Loc.Format(
                "Storage.Messages.RollbackCompletedMessage",
                result.DestinationPath);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheRollback,
                ActivityStatus.Succeeded,
                Loc.Get("Storage.Messages.RollbackCompletedActivity"),
                result.DestinationPath is null ? [snapshot] : [snapshot, result.DestinationPath],
                snapshot,
                snapshot);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = Loc.Get("Storage.Messages.RollbackCancelledTitle");
            StorageInfo.Message = string.Empty;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheRollback,
                    ActivityStatus.Cancelled,
                    Loc.Get("Storage.Messages.RollbackCancelledActivity"),
                    [snapshot],
                    snapshot,
                    snapshot);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = Loc.Get("Storage.Messages.RollbackFailedTitle");
            StorageInfo.Message = CoreErrorText.Localize(exception.Message);
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheRollback,
                    ActivityStatus.Failed,
                    Loc.Format(
                        "Storage.Messages.RollbackFailedActivity",
                        exception.GetType().Name),
                    [snapshot],
                    snapshot,
                    snapshot);
            }
        }
        finally
        {
            EndStorageOperation();
        }
    }

    private void OnCancelStorageOperationClicked(object sender, RoutedEventArgs args)
    {
        CancelStorageOperationButton.IsEnabled = false;
        _operationCancellation?.Cancel();
        StorageInfo.Severity = InfoBarSeverity.Informational;
        StorageInfo.Title = Loc.Get("Storage.Messages.CancellingTitle");
        StorageInfo.Message = Loc.Get("Storage.Messages.CancellingMessage");
    }

    private CancellationToken BeginStorageOperation()
    {
        if (_isBusy)
        {
            throw new InvalidOperationException("Another storage operation is already running.");
        }

        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        SetBusy(true);
        return _operationCancellation.Token;
    }

    private void EndStorageOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        SetBusy(false);
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        MeasureButton.IsEnabled = !isBusy;
        RollbackButton.IsEnabled = !isBusy && _lastMigrationSnapshot is not null;
        CacheList.IsEnabled = !isBusy;
        CleanupItemPicker.IsEnabled = !isBusy;
        StorageOperationProgress.IsActive = isBusy;
        CancelStorageOperationButton.IsEnabled = isBusy && _operationCancellation is not null;
        UpdateCleanupButtons();
    }

    private void UpdateCleanupButtons()
    {
        CleanupItemRow? selected = CleanupItemPicker.SelectedItem as CleanupItemRow;
        RestoreCleanupButton.IsEnabled = !_isBusy && selected?.CanRestore == true;
        PurgeCleanupButton.IsEnabled = !_isBusy && selected?.Item.CanPurge == true;
        ToolTipService.SetToolTip(
            RestoreCleanupButton,
            selected?.RestoreToolTip ?? Loc.Get("Storage.Cleanup.Restore.DefaultToolTip"));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }

    private sealed class CacheRow : INotifyPropertyChanged
    {
        private string _sizeText;

        public CacheRow(CacheDirectoryLocation location)
        {
            Location = location;
            _sizeText = location.Exists
                ? Loc.Get("Storage.Cache.Size.NotMeasured")
                : Loc.Get("Storage.Cache.Size.Missing");
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public CacheDirectoryLocation Location { get; }

        public string Name => Location.Definition.DisplayName;

        public string DirectoryPath => Location.DirectoryPath;

        public string SourceText => Location.Warning is null
            ? Location.ConfigurationSource
            : $"{Location.ConfigurationSource}: {Location.Warning}";

        public bool CanOpen => Location.Exists;

        public bool CanMigrate => Location.Exists
            && Location.Definition.SupportsMigration
            && Location.Warning is null;

        public bool CanClean => Location.Exists
            && Location.Definition.SupportsSafeCleanup
            && Location.Warning is null;

        public string CleanupToolTip => !Location.Exists
            ? Loc.Get("Storage.Cache.CleanupToolTip.Missing")
            : !Location.Definition.SupportsSafeCleanup
                ? Loc.Get("Storage.Cache.CleanupToolTip.NotSupported")
                : Location.Warning is not null
                    ? Loc.Get("Storage.Cache.CleanupToolTip.Unsafe")
                    : Loc.Get("Storage.Cache.CleanupToolTip.Default");

        public string SizeText
        {
            get => _sizeText;
            set
            {
                if (_sizeText == value)
                {
                    return;
                }

                _sizeText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
            }
        }
    }

    private sealed class CleanupItemRow(CacheCleanupItem item)
    {
        public CacheCleanupItem Item { get; } = item;

        public bool CanRestore => Item.CanRestore;

        public string RestoreToolTip => Item.CanRestore
            ? Loc.Get("Storage.Cleanup.Restore.ToolTip")
            : Item.RestoreBlockedReason
                ?? Loc.Get("Storage.Cleanup.Restore.BlockedFallback");

        public string DisplayText
        {
            get
            {
                string state = Item.State switch
                {
                    CacheCleanupItemState.Recoverable when Item.CanRestore
                        => Loc.Get("Storage.Cleanup.State.Recoverable"),
                    CacheCleanupItemState.Recoverable
                        => Loc.Get("Storage.Cleanup.State.RestoreBlocked"),
                    CacheCleanupItemState.RestorePending when Item.CanRestore
                        => Loc.Get("Storage.Cleanup.State.RestorePending"),
                    CacheCleanupItemState.RestorePending
                        => Loc.Get("Storage.Cleanup.State.RestoreBlocked"),
                    CacheCleanupItemState.PurgePending
                        => Loc.Get("Storage.Cleanup.State.PurgePending"),
                    _ => Item.State.ToString(),
                };
                return $"{Item.DisplayName} · {FormatBytes(Item.TotalBytes)} · {state} · {Item.CreatedAtUtc.ToLocalTime():g}";
            }
        }
    }
}
