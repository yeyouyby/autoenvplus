using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly string _managedRoot = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "AutoEnvPlus");
    private string? _lastMigrationSnapshot;

    public StoragePage()
    {
        InitializeComponent();
        CacheList.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _pageCancellation.Cancel();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        LoadLocations();
    }

    private void LoadLocations()
    {
        _rows.Clear();
        foreach (CacheDirectoryLocation location in _service.DiscoverCurrent())
        {
            _rows.Add(new CacheRow(location));
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
            LoadLocations();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = "迁移已取消";
            StorageInfo.Message = "原目录和原配置保持不变。";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = "缓存迁移失败";
            StorageInfo.Message = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

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
            LoadLocations();
        }
        catch (OperationCanceledException)
        {
            StorageInfo.Severity = InfoBarSeverity.Informational;
            StorageInfo.Title = "回滚已取消";
            StorageInfo.Message = string.Empty;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StorageInfo.Severity = InfoBarSeverity.Error;
            StorageInfo.Title = "存储配置回滚失败";
            StorageInfo.Message = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        MeasureButton.IsEnabled = !isBusy;
        RollbackButton.IsEnabled = !isBusy && _lastMigrationSnapshot is not null;
        CacheList.IsEnabled = !isBusy;
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
}
