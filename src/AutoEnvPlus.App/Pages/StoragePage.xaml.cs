using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AutoEnvPlus.App.Activity;
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
    private readonly CancellationTokenSource _pageCancellation = new();
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
        CacheList.ItemsSource = _rows;
        CleanupItemPicker.ItemsSource = _cleanupItems;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _operationCancellation?.Cancel();
            _pageCancellation.Cancel();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
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
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = "无法刷新缓存与隔离区";
            StorageInfo.Message = exception.Message;
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
        await RefreshCleanupItemsAsync();
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
            StorageInfo.Title = $"{completedTitle}，但列表刷新失败";
            StorageInfo.Message = $"{completedMessage} 刷新错误：{exception.Message}";
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
            ? "无待处理项"
            : $"{_cleanupItems.Count:N0} 项";
        CleanupItemPicker.SelectedIndex = _cleanupItems.Count > 0 ? 0 : -1;
        UpdateCleanupButtons();
        if (catalog.Errors.Count > 0)
        {
            StorageInfo.Severity = InfoBarSeverity.Warning;
            StorageInfo.Title = "部分隔离项未通过安全复检";
            StorageInfo.Message = string.Join("；", catalog.Errors.Take(3));
        }
    }

    private async void OnMeasureClicked(object sender, RoutedEventArgs args)
    {
        SetBusy(true);
        StorageInfo.Severity = InfoBarSeverity.Informational;
        StorageInfo.Title = "正在计算缓存大小";
        try
        {
            foreach (CacheRow row in _rows)
            {
                row.SizeText = row.Location.Exists ? "计算中…" : "目录不存在";
                CacheDirectoryMeasurement measurement = await _service.MeasureAsync(
                    row.Location,
                    _pageCancellation.Token);
                row.SizeText = measurement.Location.Exists
                    ? $"{FormatBytes(measurement.TotalBytes)} · {measurement.FileCount:N0} 文件"
                    : "目录不存在";
            }

            StorageInfo.Severity = InfoBarSeverity.Success;
            StorageInfo.Title = "缓存统计完成";
            StorageInfo.Message = "迁移事务将在复制和校验完成后才切换工具配置，原目录不会自动删除。";
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Title = "统计已取消";
            StorageInfo.Message = string.Empty;
        }
        finally
        {
            SetBusy(false);
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
            CommitButtonText = "选择缓存目标磁盘或父目录",
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
            StorageInfo.Title = "无法创建迁移计划";
            StorageInfo.Message = exception.Message;
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"迁移 {row.Name} 缓存",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"源目录\n{plan.Source.DirectoryPath}\n\n目标目录\n{plan.DestinationPath}\n\n配置\n{plan.ConfigurationDescription}\n\n文件会逐个校验；切换前保存配置快照，原目录不会自动删除。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "复制、校验并切换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        bool terminalActivityWritten = false;
        try
        {
            Progress<CacheMigrationProgress> progress = new(value =>
            {
                StorageInfo.Severity = InfoBarSeverity.Informational;
                StorageInfo.Title = value.Stage switch
                {
                    "measure" => "正在检查源目录",
                    "copy" => "正在复制并校验",
                    "commit" => "正在提交新目录",
                    "configure" => "正在切换工具配置",
                    "complete" => "迁移完成",
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
                _pageCancellation.Token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "迁移失败。");
            }

            _lastMigrationSnapshot = result.SnapshotPath;
            RollbackButton.IsEnabled = _lastMigrationSnapshot is not null;
            StorageInfo.Severity = InfoBarSeverity.Success;
            StorageInfo.Title = "缓存迁移完成";
            StorageInfo.Message = result.SnapshotPath is null
                ? $"新目录：{result.DestinationPath}。原目录仍保留，请确认工具工作正常后手动清理。"
                : $"新目录：{result.DestinationPath}。配置快照：{result.SnapshotPath}。原目录仍保留。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheMigration,
                ActivityStatus.Succeeded,
                $"已迁移 {row.Name} 缓存并切换工具配置；原目录保持不变。",
                [plan.Source.DirectoryPath, result.DestinationPath ?? plan.DestinationPath],
                result.SnapshotPath,
                result.SnapshotPath);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = "迁移已取消";
            StorageInfo.Message = "原目录和原配置保持不变。";
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheMigration,
                    ActivityStatus.Cancelled,
                    $"{row.Name} 缓存迁移已取消；原目录和原配置保持不变。",
                    [plan.Source.DirectoryPath, plan.DestinationPath]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = "缓存迁移失败";
            StorageInfo.Message = exception.Message;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheMigration,
                    ActivityStatus.Failed,
                    $"{row.Name} 缓存迁移失败。错误类型：{exception.GetType().Name}。",
                    [plan.Source.DirectoryPath, plan.DestinationPath]);
            }
        }
        finally
        {
            SetBusy(false);
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
        StorageInfo.Title = $"正在检查 {row.Name} 缓存";
        StorageInfo.Message = "正在复检目录边界、重解析点、文件数量和大小。";
        try
        {
            plan = await _cleanupService.CreatePlanAsync(
                row.Location,
                planCancellation);
            row.SizeText = $"{FormatBytes(plan.TotalBytes)} · {plan.FileCount:N0} 文件";
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Title = "清理计划已取消";
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
            StorageInfo.Title = "无法创建安全清理计划";
            StorageInfo.Message = exception.Message;
            return;
        }
        finally
        {
            EndStorageOperation();
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"隔离 {row.Name} 缓存内容",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"缓存目录（保留）\n{plan.Source.DirectoryPath}\n\n安全隔离区\n{plan.TrashPath}\n\n影响\n{plan.FileCount:N0} 个文件 · {FormatBytes(plan.TotalBytes)} · {plan.TopLevelEntryCount:N0} 个顶层项\n\n内容将通过同卷重命名移入隔离区，缓存目录和工具配置保持不变。此阶段不会释放磁盘空间；可先恢复，或稍后单独确认永久清空。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "移入安全隔离区",
            CloseButtonText = "取消",
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
            Progress<CacheCleanupProgress> progress = CreateCleanupProgress("正在隔离缓存内容");
            CacheCleanupOperationResult result = await _cleanupService.CleanupAsync(
                plan,
                progress,
                cleanupCancellation);
            if (result.Success)
            {
                StorageInfo.Severity = InfoBarSeverity.Success;
                StorageInfo.Title = "缓存内容已安全隔离";
                StorageInfo.Message = $"已隔离 {result.FileCount:N0} 个文件（{FormatBytes(result.TotalBytes)}）。缓存根目录仍保留；可从安全隔离区恢复或永久清空。";
            }
            else
            {
                StorageInfo.Severity = result.Cancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
                StorageInfo.Title = result.Cancelled ? "缓存清理已取消" : "缓存清理未完成";
                StorageInfo.Message = result.Error ?? "缓存内容保持在原目录或受控隔离区。";
            }

            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheCleanup,
                result.Success
                    ? ActivityStatus.Succeeded
                    : result.Cancelled
                        ? ActivityStatus.Cancelled
                        : ActivityStatus.Failed,
                result.Success
                    ? $"已把 {row.Name} 纯缓存移入同卷安全隔离区；尚未永久释放空间。"
                    : result.Cancelled
                        ? $"{row.Name} 缓存隔离已取消，并保存为一致状态。"
                        : $"{row.Name} 缓存隔离未完成。",
                [plan.Source.DirectoryPath, plan.TrashPath],
                result.ManifestPath,
                result.RecoveryAvailable ? result.ManifestPath : null);
            terminalActivityWritten = true;

            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = "缓存清理已取消";
            StorageInfo.Message = "缓存内容保持在原目录或受控隔离区。";
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Cancelled,
                    $"{row.Name} 缓存隔离已取消。",
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
            StorageInfo.Title = "缓存清理失败";
            StorageInfo.Message = exception.Message;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Failed,
                    $"{row.Name} 缓存隔离失败。错误类型：{exception.GetType().Name}。",
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
            Title = $"恢复 {row.Item.DisplayName} 缓存",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"原缓存目录\n{row.Item.SourcePath}\n\n隔离目录\n{row.Item.TrashPath}\n\n将恢复 {row.Item.FileCount:N0} 个文件（{FormatBytes(row.Item.TotalBytes)}）。执行前会再次复检；原目录出现任何后续数据时都拒绝覆盖。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "恢复到原目录",
            CloseButtonText = "取消",
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
                CreateCleanupProgress("正在恢复缓存内容"),
                restoreCancellation);
            StorageInfo.Severity = result.Success
                ? InfoBarSeverity.Success
                : result.Cancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
            StorageInfo.Title = result.Success
                ? "缓存内容已恢复"
                : result.Cancelled
                    ? "缓存恢复已取消"
                    : "缓存恢复被拒绝";
            StorageInfo.Message = result.Success
                ? $"已将 {result.FileCount:N0} 个文件（{FormatBytes(result.TotalBytes)}）恢复到原缓存目录。"
                : result.Error ?? "隔离内容仍保留。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheCleanup,
                result.Success
                    ? ActivityStatus.Succeeded
                    : result.Cancelled
                        ? ActivityStatus.Cancelled
                        : ActivityStatus.Failed,
                result.Success
                    ? $"已恢复 {row.Item.DisplayName} 的隔离缓存内容。"
                    : result.Cancelled
                        ? $"恢复 {row.Item.DisplayName} 隔离缓存已取消。"
                        : $"恢复 {row.Item.DisplayName} 隔离缓存被拒绝。",
                [row.Item.SourcePath, row.Item.TrashPath],
                row.Item.ManifestPath,
                row.Item.ManifestPath);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = "缓存恢复已取消";
            StorageInfo.Message = "隔离项已保存为可继续处理的一致状态。";
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Cancelled,
                    $"恢复 {row.Item.DisplayName} 隔离缓存已取消。",
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
            StorageInfo.Title = "缓存恢复失败";
            StorageInfo.Message = exception.Message;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Failed,
                    $"恢复 {row.Item.DisplayName} 隔离缓存失败。错误类型：{exception.GetType().Name}。",
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
            Title = $"永久清空 {row.Item.DisplayName} 隔离内容",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"受信隔离目录\n{row.Item.TrashPath}\n\n将永久删除 {row.Item.FileCount:N0} 个文件（{FormatBytes(row.Item.TotalBytes)}）。此操作不可撤销；开始后该隔离项不能再恢复。原缓存目录和工具配置不会修改。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
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
                CreateCleanupProgress("正在永久清空隔离内容"),
                purgeCancellation);
            StorageInfo.Severity = result.Success
                ? InfoBarSeverity.Success
                : result.Cancelled
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error;
            StorageInfo.Title = result.Success
                ? "隔离内容已永久清空"
                : result.Cancelled
                    ? "永久清空已中断"
                    : "永久清空未完成";
            StorageInfo.Message = result.Success
                ? $"已释放隔离内容占用的 {FormatBytes(result.TotalBytes)}；原缓存目录保持不变。"
                : $"{result.Error ?? "仍有隔离内容待处理。"} 已开始永久清空的项目不能恢复，可再次选择“永久清空”继续。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheCleanup,
                result.Success
                    ? ActivityStatus.Succeeded
                    : result.Cancelled
                        ? ActivityStatus.Cancelled
                        : ActivityStatus.Failed,
                result.Success
                    ? $"已永久清空 {row.Item.DisplayName} 的受信隔离内容并释放空间。"
                    : result.Cancelled
                        ? $"永久清空 {row.Item.DisplayName} 隔离内容已中断，只能继续清空。"
                        : $"永久清空 {row.Item.DisplayName} 隔离内容未完成。",
                [row.Item.SourcePath, row.Item.TrashPath],
                row.Item.ManifestPath);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Warning;
            StorageInfo.Title = "永久清空已中断";
            StorageInfo.Message = "已开始永久清空的项目不能恢复，可再次选择“永久清空”继续。";
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Cancelled,
                    $"永久清空 {row.Item.DisplayName} 隔离内容已中断，只能继续清空。",
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
            StorageInfo.Title = "永久清空失败";
            StorageInfo.Message = exception.Message;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheCleanup,
                    ActivityStatus.Failed,
                    $"永久清空 {row.Item.DisplayName} 隔离内容失败。错误类型：{exception.GetType().Name}。",
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
            StorageInfo.Title = "无法打开缓存目录";
            StorageInfo.Message = exception.Message;
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
            Title = "回滚最近一次存储配置",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"快照\n{snapshot}\n\n只恢复工具配置；已复制的新目录会保留。如果配置后来被修改，AutoEnvPlus 会拒绝覆盖。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "恢复配置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        bool terminalActivityWritten = false;
        try
        {
            CacheMigrationResult result = await new CacheMigrationService(_managedRoot).RollbackAsync(
                snapshot,
                new WindowsUserEnvironmentVariableStore(),
                _pageCancellation.Token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "配置回滚失败。");
            }

            _lastMigrationSnapshot = null;
            StorageInfo.Severity = InfoBarSeverity.Success;
            StorageInfo.Title = "存储配置已回滚";
            StorageInfo.Message = $"工具已重新指向原目录。迁移副本仍保留在 {result.DestinationPath}。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CacheRollback,
                ActivityStatus.Succeeded,
                "已回滚最近一次缓存迁移配置；迁移副本保持不变。",
                result.DestinationPath is null ? [snapshot] : [snapshot, result.DestinationPath],
                snapshot,
                snapshot);
            terminalActivityWritten = true;
            await RefreshAfterMutationAsync();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = "回滚已取消";
            StorageInfo.Message = string.Empty;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheRollback,
                    ActivityStatus.Cancelled,
                    "缓存迁移配置回滚已取消。",
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
            StorageInfo.Title = "存储配置回滚失败";
            StorageInfo.Message = exception.Message;
            if (!terminalActivityWritten)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CacheRollback,
                    ActivityStatus.Failed,
                    $"缓存迁移配置回滚失败。错误类型：{exception.GetType().Name}。",
                    [snapshot],
                    snapshot,
                    snapshot);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCancelStorageOperationClicked(object sender, RoutedEventArgs args)
    {
        CancelStorageOperationButton.IsEnabled = false;
        _operationCancellation?.Cancel();
        StorageInfo.Severity = InfoBarSeverity.Informational;
        StorageInfo.Title = "正在取消当前存储操作";
        StorageInfo.Message = "正在完成当前原子步骤并保存一致状态。";
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
            selected?.RestoreToolTip ?? "选择一个可恢复的隔离项");
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
            _sizeText = location.Exists ? "尚未计算" : "目录不存在";
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
            ? "缓存目录不存在"
            : !Location.Definition.SupportsSafeCleanup
                ? "该根目录包含配置或其他非缓存数据，仅支持迁移，不提供整目录清理"
                : Location.Warning is not null
                    ? "当前配置无法安全确认，请刷新或修复配置后再清理"
                    : "先将缓存内容同卷移入可恢复的安全隔离区";

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
            ? "将隔离内容移回原缓存目录"
            : Item.RestoreBlockedReason
                ?? "永久清空已经开始，不能再恢复";

        public string DisplayText
        {
            get
            {
                string state = Item.State switch
                {
                    CacheCleanupItemState.Recoverable when Item.CanRestore => "可恢复",
                    CacheCleanupItemState.Recoverable => "恢复已阻止",
                    CacheCleanupItemState.RestorePending when Item.CanRestore => "待继续恢复",
                    CacheCleanupItemState.RestorePending => "恢复已阻止",
                    CacheCleanupItemState.PurgePending => "待继续永久清空",
                    _ => Item.State.ToString(),
                };
                return $"{Item.DisplayName} · {FormatBytes(Item.TotalBytes)} · {state} · {Item.CreatedAtUtc.ToLocalTime():g}";
            }
        }
    }
}
