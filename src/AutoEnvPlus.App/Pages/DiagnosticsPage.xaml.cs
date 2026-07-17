using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Diagnostics;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Environment;
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
    private readonly CancellationTokenSource _pageCancellation = new();
    private CancellationTokenSource? _scanCancellation;
    private EnvironmentDiagnosticReport? _lastReport;
    private bool _scanning;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        UpdateScopeControls();
        ShowPendingScopes();
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
            SummaryInfo.Title = "无法选择项目目录";
            SummaryInfo.Message = exception.Message;
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
            picker.FileTypeChoices.Add("JSON 诊断报告", [".json"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            string json = DiagnosticReportExportSerializer.Serialize(_lastReport);
            await FileIO.WriteTextAsync(file, json, UnicodeEncoding.Utf8);
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = "诊断报告已导出";
            SummaryInfo.Message = file.Path;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.DiagnosticExport,
                ActivityStatus.Succeeded,
                "已导出脱敏的结构化环境诊断 JSON。",
                [file.Path]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = "无法导出诊断报告";
            SummaryInfo.Message = exception.Message;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.DiagnosticExport,
                ActivityStatus.Failed,
                $"环境诊断 JSON 导出失败。错误类型：{exception.GetType().Name}。");
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
        SummaryInfo.Title = "正在执行所选诊断";
        SummaryInfo.Message = $"扫描域：{string.Join("、", EnumerateScopes(options.Scopes).Select(ScopeName))}。";
        try
        {
            EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
                GetManagedRoot()).InspectCurrentAsync(options, _scanCancellation.Token);
            ShowReport(report);
        }
        catch (OperationCanceledException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Informational;
            SummaryInfo.Title = "诊断已取消";
            SummaryInfo.Message = "没有修改任何环境设置。";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = "无法完成环境诊断";
            SummaryInfo.Message = exception.Message;
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
            SummaryInfo.Title = "请选择扫描范围";
            SummaryInfo.Message = "至少选择一个只读扫描域。";
            return null;
        }

        string? projectRoot = ProjectScopeCheckBox.IsChecked == true
            ? ProjectPathTextBox.Text.Trim()
            : null;
        if (ProjectScopeCheckBox.IsChecked == true
            && string.IsNullOrWhiteSpace(projectRoot))
        {
            SummaryInfo.Severity = InfoBarSeverity.Warning;
            SummaryInfo.Title = "请选择项目目录";
            SummaryInfo.Message = "项目锁与虚拟环境检查只扫描明确选择的目录。";
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
            ? "尚未选择扫描域。"
            : $"已选择 {selected.Length} 个扫描域"
                + (expensive == 0
                    ? "；不会遍历项目、缓存或访问网络。"
                    : $"，其中 {expensive} 个需要额外 I/O 或网络。");
        ScopeList.ItemsSource = selected
            .Select(scope => new ScopeRow(
                ScopeName(scope),
                "待扫描",
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
        SummaryInfo.Title = report.IsHealthy ? "所选范围状态正常" : "所选范围需要检查";
        SummaryInfo.Message = $"错误 {report.ErrorCount} · 警告 {report.WarningCount} · "
            + $"扫描域 {EnumerateScopes(report.CompletedScopes).Count()} 个 · "
            + $"时间 {report.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

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
                    ? $"{errors} 个错误"
                    : warnings > 0
                        ? $"{warnings} 个警告"
                        : "已通过";
                string detail = scoped.Length == 0
                    ? ScopeDescription(scope)
                    : $"{ScopeDescription(scope)} 共记录 {scoped.Length} 条结果。";
                return new ScopeRow(ScopeName(scope), result, detail);
            })
            .ToArray();

        IssueList.ItemsSource = report.Issues.Count == 0
            ? [new IssueRow(
                "所选范围",
                "信息",
                "未发现问题",
                "所选扫描域均通过当前只读检查。",
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
                command.WinnerPath ?? "未找到",
                command.HasConflict
                    ? $"{command.CandidateCount} 个，有遮蔽"
                    : $"{command.CandidateCount} 个"))
            .ToArray();
        RuntimeList.ItemsSource = report.Runtimes
            .Select(runtime => new RuntimeRow(
                $"{runtime.Kind} · {runtime.Command}",
                runtime.Version?.ToString() ?? "探测失败",
                runtime.ExecutablePath))
            .ToArray();
        GlobalSelectionList.ItemsSource = report.GlobalSelections
            .Select(selection => new GlobalSelectionRow(
                selection.Kind.ToString(),
                selection.Selector,
                selection.Success
                    ? $"{selection.RuntimeId} · {selection.Version} · {selection.Architecture}"
                    : selection.Error ?? "无法解析"))
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
        DiagnosticScanScope.PathAndCommands => "PATH、命令与 Shim",
        DiagnosticScanScope.ManagedTools => "托管语言工具",
        DiagnosticScanScope.ProjectEnvironment => "项目环境",
        DiagnosticScanScope.ProviderConfiguration => "Provider 与来源",
        DiagnosticScanScope.StoragePressure => "缓存、暂存与磁盘",
        DiagnosticScanScope.ProviderConnectivity => "实时连接",
        _ => "其他",
    };

    private static string ScopeDescription(DiagnosticScanScope scope) => scope switch
    {
        DiagnosticScanScope.PathAndCommands => "检查失效目录、命令遮蔽、Shim 完整性与实际版本。",
        DiagnosticScanScope.ManagedTools => "检查受管入口、库存快照、精确全局身份、重复记录及命令偏差。",
        DiagnosticScanScope.ProjectEnvironment => "检查所选目录的版本选择、精确工具身份和本地虚拟环境。",
        DiagnosticScanScope.ProviderConfiguration => "检查 Provider 插件、工具身份、来源及代理配置的可解析状态。",
        DiagnosticScanScope.StoragePressure => "检查缓存、安装/下载暂存、系统盘落点与相关磁盘容量。",
        DiagnosticScanScope.ProviderConnectivity => "对受限数量的 Provider 来源端点执行 HEAD 请求。",
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
        Brush SeverityBrush)
    {
        public Visibility PathVisibility => string.IsNullOrWhiteSpace(Path)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public static IssueRow FromIssue(DiagnosticIssue issue) => new(
            ScopeName(issue.Scope),
            issue.Severity switch
            {
                DiagnosticSeverity.Error => "错误",
                DiagnosticSeverity.Warning => "警告",
                _ => "信息",
            },
            issue.Title,
            issue.Detail,
            issue.Path ?? string.Empty,
            new SolidColorBrush(issue.Severity switch
            {
                DiagnosticSeverity.Error => Colors.Firebrick,
                DiagnosticSeverity.Warning => Colors.DarkGoldenrod,
                _ => Colors.DodgerBlue,
            }));
    }

    private sealed record CommandRow(string Command, string Winner, string CandidateText);

    private sealed record RuntimeRow(string Runtime, string Version, string Path);

    private sealed record GlobalSelectionRow(string Runtime, string Selector, string Resolution);
}
