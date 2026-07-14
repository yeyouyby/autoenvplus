using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class PathPage : Page
{
    public PathPage()
    {
        InitializeComponent();
        LoadReport();
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
        string managedRoot = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "AutoEnvPlus");
        string cliPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus.exe");
        string nativeShimPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus-shim.exe");
        if (!File.Exists(cliPath))
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = "CLI 组件缺失";
            SummaryInfo.Message = $"找不到 {cliPath}，请重新构建或安装 AutoEnvPlus。";
            return;
        }

        UserPathManager manager = new(managedRoot, new WindowsUserEnvironmentVariableStore());
        string shimDirectory = Path.Combine(managedRoot, "shims");
        UserPathMutationPlan plan = manager.PlanEnsureFirst(shimDirectory);
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

        ActivateShimButton.IsEnabled = false;
        try
        {
            CommandShimInstallResult shims = await new CommandShimManager().InstallAsync(
                managedRoot,
                cliPath,
                [],
                File.Exists(nativeShimPath) ? nativeShimPath : null);
            plan = manager.PlanEnsureFirst(shims.ShimDirectory);
            UserPathMutationResult result = await manager.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "用户 PATH 修改失败。");
            }

            SummaryInfo.Severity = InfoBarSeverity.Success;
            SummaryInfo.Title = "命令版本切换已启用";
            string implementation = shims.Implementation == CommandShimImplementation.NativeWin32
                ? "Win32 原生 Shim"
                : "CMD 回退 Shim";
            SummaryInfo.Message = result.Changed
                ? $"已安装 {implementation} 并保存 PATH 快照：{result.SnapshotPath}。请打开新终端。"
                : $"{implementation} 已安装，Shim 已经处于用户 PATH 第一位。";
            LoadReport();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SummaryInfo.Severity = InfoBarSeverity.Error;
            SummaryInfo.Title = "无法启用命令切换";
            SummaryInfo.Message = exception.Message;
        }
        finally
        {
            ActivateShimButton.IsEnabled = true;
        }
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

    private sealed record PathEntryRow(string Index, string Scope, string Path, string Status);
}
