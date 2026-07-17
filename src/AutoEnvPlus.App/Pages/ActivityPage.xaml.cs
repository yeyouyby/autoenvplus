using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AutoEnvPlus.App.Pages;

public sealed partial class ActivityPage : Page
{
    private readonly ObservableCollection<ActivityRow> _rows = [];
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly ActivityLogStore? _store;
    private readonly string? _rootError;
    private IReadOnlyList<ActivityLogEntry> _entries = [];

    public ActivityPage(string? managedRoot = null)
    {
        InitializeComponent();
        ActivityList.ItemsSource = _rows;
        TypeFilter.Items.Clear();
        TypeFilter.Items.Add(new ComboBoxItem { Content = "全部操作" });
        foreach (ActivityOperationType type in Enum.GetValues<ActivityOperationType>())
        {
            TypeFilter.Items.Add(new ComboBoxItem
            {
                Content = GetOperationText(type),
                Tag = type,
            });
        }

        StatusFilter.SelectedIndex = 0;
        TypeFilter.SelectedIndex = 0;
        if (managedRoot is null)
        {
            if (!ManagedRootResolver.TryResolve(
                    null,
                    out managedRoot,
                    out string? error)
                || managedRoot is null)
            {
                _rootError = error ?? "无法确定 AutoEnvPlus 数据根。";
            }
        }

        if (_rootError is null && managedRoot is not null)
        {
            try
            {
                int retentionDays = Application.Current is App app
                    ? app.CurrentSettings.LogRetentionDays
                    : ActivityLogStore.DefaultRetentionDays;
                _store = new ActivityLogStore(managedRoot, retentionDays: retentionDays);
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                _rootError = exception.Message;
            }
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _pageCancellation.Cancel();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        ApplyFilter();
    }

    private async Task RefreshAsync()
    {
        if (_rootError is not null)
        {
            SetInfo(InfoBarSeverity.Error, "无法读取活动记录", _rootError);
            return;
        }

        if (_store is null)
        {
            SetInfo(InfoBarSeverity.Error, "无法读取活动记录", "日志存储未初始化。");
            return;
        }

        RefreshButton.IsEnabled = false;
        try
        {
            ActivityLogLoadResult result = await _store.LoadAsync(_pageCancellation.Token);
            _entries = result.Entries;
            ApplyFilter();
            if (result.Errors.Count == 0)
            {
                SetInfo(
                    InfoBarSeverity.Informational,
                    "活动记录已刷新",
                    $"共 {_entries.Count} 条记录；日志：{_store.LogPath}。回滚路径仅供查看。");
            }
            else
            {
                SetInfo(
                    InfoBarSeverity.Warning,
                    "活动记录已部分读取",
                    $"已加载 {_entries.Count} 条有效记录，跳过 {result.Errors.Count} 条损坏或不兼容记录。日志：{_store.LogPath}。");
            }
        }
        catch (OperationCanceledException)
        {
            SetInfo(InfoBarSeverity.Informational, "读取已取消", string.Empty);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SetInfo(InfoBarSeverity.Error, "无法读取活动记录", exception.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        ActivityStatus? status = StatusFilter.SelectedIndex switch
        {
            1 => ActivityStatus.Succeeded,
            2 => ActivityStatus.Failed,
            3 => ActivityStatus.Cancelled,
            _ => null,
        };
        ActivityOperationType? operation = TypeFilter.SelectedItem is ComboBoxItem
        {
            Tag: ActivityOperationType selectedType,
        }
            ? selectedType
            : null;

        _rows.Clear();
        foreach (ActivityLogEntry entry in _entries
            .Where(entry => status is null || entry.Status == status)
            .Where(entry => operation is null || entry.OperationType == operation))
        {
            _rows.Add(new ActivityRow(entry));
        }

        EmptyText.Visibility = _rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnCopyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ActivityRow row })
        {
            return;
        }

        try
        {
            DataPackage package = new();
            package.SetText(row.CopyText);
            Clipboard.SetContent(package);
            SetInfo(InfoBarSeverity.Success, "摘要已复制", "已复制脱敏活动摘要；回滚路径不会在此页自动执行。");
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            SetInfo(InfoBarSeverity.Error, "无法复制摘要", exception.Message);
        }
    }

    private void SetInfo(InfoBarSeverity severity, string title, string message)
    {
        ActivityInfo.Severity = severity;
        ActivityInfo.Title = title;
        ActivityInfo.Message = message;
        ActivityInfo.IsOpen = true;
    }

    private static string GetOperationText(ActivityOperationType type) => type switch
    {
        ActivityOperationType.RuntimeInstall => "语言工具安装",
        ActivityOperationType.RuntimeUninstall => "语言工具卸载",
        ActivityOperationType.RuntimeSwitch => "语言工具切换",
        ActivityOperationType.PathChange => "PATH 修改",
        ActivityOperationType.PathRollback => "PATH 回滚",
        ActivityOperationType.CacheMigration => "缓存迁移",
        ActivityOperationType.CacheRollback => "缓存回滚",
        ActivityOperationType.CacheCleanup => "缓存清理",
        ActivityOperationType.ToolchainInstall => "编译与构建工具安装",
        ActivityOperationType.PowerShellIntegration => "PowerShell 集成",
        ActivityOperationType.ProjectImport => "项目导入",
        ActivityOperationType.DiagnosticExport => "诊断导出",
        ActivityOperationType.CMakePreset => "CMake Preset",
        ActivityOperationType.SettingsChange => "设置变更",
        ActivityOperationType.PackageDownload => "包下载",
        ActivityOperationType.PackageImport => "包导入",
        ActivityOperationType.PackageInstall => "包安装",
        ActivityOperationType.ProviderPluginImport => "Provider 插件导入",
        ActivityOperationType.ProviderPluginStateChange => "Provider 插件状态",
        ActivityOperationType.ProviderPluginDelete => "Provider 插件删除",
        _ => "其他操作",
    };

    private static string GetStatusText(ActivityStatus status) => status switch
    {
        ActivityStatus.Succeeded => "成功",
        ActivityStatus.Failed => "失败",
        ActivityStatus.Cancelled => "已取消",
        _ => status.ToString(),
    };

    private sealed class ActivityRow
    {
        public ActivityRow(ActivityLogEntry entry)
        {
            Entry = entry;
            TimestampText = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            OperationText = GetOperationText(entry.OperationType);
            StatusText = GetStatusText(entry.Status);
            Summary = entry.Summary;
            AffectedPathsText = entry.AffectedPaths.Count == 0
                ? "受影响路径：无"
                : $"受影响路径：{string.Join("；", entry.AffectedPaths)}";
            SnapshotText = entry.SnapshotPath is null
                ? "快照路径：无"
                : $"快照路径：{entry.SnapshotPath}";
            RollbackText = entry.RollbackPath is null
                ? "回滚路径（仅供查看，不自动执行）：无"
                : $"回滚路径（仅供查看，不自动执行）：{entry.RollbackPath}";
            CopyText = string.Join(
                System.Environment.NewLine,
                TimestampText,
                $"操作：{OperationText}",
                $"状态：{StatusText}",
                $"摘要：{Summary}",
                AffectedPathsText,
                SnapshotText,
                RollbackText);
        }

        public ActivityLogEntry Entry { get; }

        public string TimestampText { get; }

        public string OperationText { get; }

        public string StatusText { get; }

        public string Summary { get; }

        public string AffectedPathsText { get; }

        public string SnapshotText { get; }

        public string RollbackText { get; }

        public string CopyText { get; }
    }
}
