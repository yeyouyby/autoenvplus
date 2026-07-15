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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await RefreshAsync();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _scanCancellation?.Cancel();
        _pageCancellation.Cancel();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) => await RefreshAsync();

    private void OnCancelClicked(object sender, RoutedEventArgs args) => _scanCancellation?.Cancel();

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
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        SetBusy(true);
        SummaryInfo.Severity = InfoBarSeverity.Informational;
        SummaryInfo.Title = "正在扫描当前环境";
        SummaryInfo.Message = "检查 PATH、已知命令、运行时版本、托管注册表和全局选择。";
        try
        {
            EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
                GetManagedRoot()).InspectCurrentAsync(_scanCancellation.Token);
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
            or InvalidOperationException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = "无法完成环境诊断";
            SummaryInfo.Message = exception.Message;
        }
        finally
        {
            SetBusy(false);
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            _scanning = false;
        }
    }

    private void ShowReport(EnvironmentDiagnosticReport report)
    {
        _lastReport = report;
        SummaryInfo.Severity = report.ErrorCount > 0
            ? InfoBarSeverity.Error
            : report.WarningCount > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        SummaryInfo.Title = report.IsHealthy ? "环境状态正常" : "环境需要检查";
        SummaryInfo.Message = $"错误 {report.ErrorCount} · 警告 {report.WarningCount} · "
            + $"PATH {report.Path.Entries.Count} 项 · 托管运行时 {report.ManagedRuntimeCount} 个 · "
            + $"扫描时间 {report.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        IssueList.ItemsSource = report.Issues.Count == 0
            ? [new IssueRow(
                "信息",
                "未发现问题",
                "PATH、运行时探测、托管状态和全局选择均通过当前检查。",
                string.Empty,
                new SolidColorBrush(Colors.ForestGreen))]
            : report.Issues.Select(IssueRow.FromIssue).ToArray();
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
    }

    private static string GetManagedRoot() => ManagedRootResolver.ResolveOrThrow();

    private sealed record IssueRow(
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
