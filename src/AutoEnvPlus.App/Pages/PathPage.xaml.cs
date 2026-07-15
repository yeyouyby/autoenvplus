using System.Globalization;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class PathPage : Page
{
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
        Loaded += OnPageLoaded;
        LoadReport();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs args)
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

    private void LoadReport()
    {
        PathInspectionReport report = new PathInspector().InspectCurrent(
            ["python", "node", "npm", "java", "javac", "cl", "clang", "cmake"]);

        PathList.ItemsSource = report.Entries.Select(entry => new PathEntryRow(
            (entry.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ScopeLabel(entry.Scope),
            entry.ExpandedValue,
            StatusLabel(entry))).ToArray();

        SummaryInfo.Severity = report.IsHealthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        SummaryInfo.Title = report.IsHealthy ? "PATH 状态正常" : "发现需要处理的 PATH 项";
        SummaryInfo.Message = $"失效 {report.MissingCount} · 重复 {report.DuplicateCount} · "
            + $"已知命令冲突 {report.Conflicts.Count}";
    }

    private async void OnActivateShimClicked(object sender, RoutedEventArgs args)
    {
        if (!File.Exists(_cliPath))
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = "CLI 组件缺失";
            SummaryInfo.Message = $"找不到 {_cliPath}，请重新构建或安装 AutoEnvPlus。";
            return;
        }

        string shimDirectory = Path.Combine(_managedRoot, "shims");
        UserPathMutationPlan plan = _pathManager.PlanEnsureFirst(shimDirectory);
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = "启用命令版本切换",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"将生成命令\npython、python3、pip、pip3、node、npm、npx、java、javac、jar\n\nShim 目录\n{shimDirectory}\n\nPATH 变化\n{(plan.Changed ? "添加到用户 PATH 第一位，并保存回滚快照" : "Shim 已位于用户 PATH 第一位")}\n\n系统 PATH 不会修改。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "生成 Shim 并应用",
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
            CommandShimInstallResult shims = await new CommandShimManager().InstallAsync(
                _managedRoot,
                _cliPath,
                [],
                File.Exists(_nativeShimPath) ? _nativeShimPath : null);
            plan = _pathManager.PlanEnsureFirst(shims.ShimDirectory);
            UserPathMutationResult result = await _pathManager.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "用户 PATH 修改失败。");
            }

            LoadReport();
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = "命令版本切换已启用";
            string implementation = shims.Implementation == CommandShimImplementation.NativeWin32
                ? "Win32 原生 Shim"
                : "CMD 回退 Shim";
            SummaryInfo.Message = result.Changed
                ? $"已安装 {implementation} 并保存 PATH 快照：{result.SnapshotPath}。请打开新终端。"
                : $"{implementation} 已安装，Shim 已经处于用户 PATH 第一位。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Succeeded,
                result.Changed
                    ? $"已安装 {implementation}，并把 AutoEnvPlus Shim 置于用户 PATH 第一位。"
                    : $"已确认 {implementation} 与用户 PATH 无需变更。",
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
            SummaryInfo.Title = "无法启用命令切换";
            SummaryInfo.Message = exception.Message;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathChange,
                ActivityStatus.Failed,
                $"启用 AutoEnvPlus Shim 与用户 PATH 失败。错误类型：{exception.GetType().Name}。",
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
            Title = "回滚用户 PATH",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"快照时间\n{selected.CreatedAt}\n\n当时加入的目录\n{selected.AddedDirectory}\n\n将把用户 PATH 恢复到 AutoEnvPlus 写入前的完整值。系统 PATH 和 Shim 文件不会修改。\n\n执行前会再次检查当前 PATH；如果快照之后已有其他修改，AutoEnvPlus 将拒绝覆盖。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "回滚用户 PATH",
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
            UserPathMutationResult result = await _pathManager.RollbackAsync(
                selected.Snapshot.SnapshotPath);
            if (!result.Success)
            {
                SummaryInfo.Severity = InfoBarSeverity.Error;
                SummaryInfo.Title = "无法回滚用户 PATH";
                SummaryInfo.Message = result.Error ?? "PATH 回滚失败。";
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PathRollback,
                    ActivityStatus.Failed,
                    "用户 PATH 回滚在执行时复检阶段被拒绝。",
                    [selected.AddedDirectory],
                    selected.Snapshot.SnapshotPath,
                    selected.Snapshot.SnapshotPath);
                await LoadSnapshotsAsync();
                return;
            }

            LoadReport();
            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = "用户 PATH 已回滚";
            SummaryInfo.Message = "已恢复快照写入前的用户 PATH；系统 PATH 和 Shim 文件未修改。请打开新终端。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathRollback,
                ActivityStatus.Succeeded,
                "已恢复 AutoEnvPlus 修改前的用户 PATH；系统 PATH 与 Shim 文件保持不变。",
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
            SummaryInfo.Title = "无法回滚用户 PATH";
            SummaryInfo.Message = exception.Message;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PathRollback,
                ActivityStatus.Failed,
                $"用户 PATH 回滚失败。错误类型：{exception.GetType().Name}。",
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
                    snapshot.AddedDirectory,
                    SnapshotStateLabel(snapshot.State)))
                .ToArray();
            SnapshotList.ItemsSource = rows;
            SnapshotList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            SnapshotEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SnapshotEmptyText.Text = "尚未创建 PATH 快照。";

            int availableCount = rows.Count(row => row.CanRollback);
            SnapshotSummaryText.Text = rows.Length == 0
                ? "启用命令版本切换并修改用户 PATH 后，会在这里保留可审计的回滚快照。"
                : $"共 {rows.Length} 个有效快照，其中 {availableCount} 个与当前用户 PATH 匹配。请选择快照后回滚。";
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
            SnapshotEmptyText.Text = "无法读取 PATH 快照。";
            SnapshotSummaryText.Text = exception.Message;
        }

        UpdateRollbackButton();
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ActivateShimButton.IsEnabled = !isBusy;
        RefreshSnapshotsButton.IsEnabled = !isBusy;
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
        PathEntryScope.User => "用户",
        PathEntryScope.Machine => "系统",
        PathEntryScope.UserAndMachine => "用户 + 系统",
        _ => "进程",
    };

    private static string StatusLabel(PathInspectionEntry entry)
    {
        if (!entry.Exists)
        {
            return "目录不存在";
        }

        return entry.IsDuplicate ? "重复" : "正常";
    }

    private static string SnapshotStateLabel(UserPathSnapshotState state) => state switch
    {
        UserPathSnapshotState.RollbackAvailable => "可回滚",
        UserPathSnapshotState.AlreadyRolledBack => "已回滚",
        _ => "PATH 已变化",
    };

    private sealed record PathEntryRow(string Index, string Scope, string Path, string Status);

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
