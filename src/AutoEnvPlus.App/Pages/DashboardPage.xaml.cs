using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        PathInspectionReport report = new PathInspector().InspectCurrent(
            ["python", "node", "npm", "java", "javac", "cl", "clang", "cmake"]);

        PathSummaryText.Text = $"{report.Entries.Count} 个目录 · {report.MissingCount} 个失效 · "
            + $"{report.DuplicateCount} 个重复 · {report.Conflicts.Count} 个命令冲突";

        PathHealthInfo.Severity = report.IsHealthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        PathHealthInfo.Title = report.IsHealthy ? "PATH 状态正常" : "PATH 需要检查";
        PathHealthInfo.Message = report.IsHealthy
            ? "没有发现失效目录、重复项或已知运行时命令冲突。"
            : "AutoEnvPlus 已完成只读扫描；在用户确认前不会修改任何环境变量。";

        IReadOnlyList<DiscoveredRuntime> runtimes = await new RuntimeDiscoveryService().DiscoverCurrentAsync();
        int healthyCount = runtimes.Count(runtime => runtime.IsHealthy);
        int kindCount = runtimes
            .Where(runtime => runtime.IsHealthy)
            .Select(runtime => runtime.Kind)
            .Distinct()
            .Count();
        RuntimeSummaryText.Text = healthyCount == 0
            ? "没有在 PATH 中发现可识别的运行时"
            : $"已识别 {healthyCount} 个运行时入口，覆盖 {kindCount} 类工具";
    }

    private void OnOpenPathClicked(object sender, RoutedEventArgs args)
    {
        Frame.Navigate(typeof(PathPage));
    }
}
