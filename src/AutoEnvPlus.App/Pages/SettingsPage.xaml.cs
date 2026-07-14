using System.Text;
using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.Core.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly WindowBackdropManager _backdropManager;
    private readonly string _managedRoot;
    private readonly string _cliPath;
    private readonly string _profilePath;
    private readonly string _nativeShimPath;
    private string? _lastSnapshotPath;

    internal SettingsPage(WindowBackdropManager backdropManager)
    {
        InitializeComponent();
        _backdropManager = backdropManager;
        _managedRoot = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "AutoEnvPlus");
        _cliPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus.exe");
        _nativeShimPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus-shim.exe");
        _profilePath = PowerShellIntegrationManager.GetDefaultWindowsPowerShellProfilePath();
        ProfilePathText.Text = _profilePath;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateBackdropStatus();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _backdropManager.StatusChanged -= OnBackdropStatusChanged;
        _backdropManager.StatusChanged += OnBackdropStatusChanged;
        UpdateBackdropStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        _backdropManager.StatusChanged -= OnBackdropStatusChanged;

    private void OnBackdropStatusChanged(object? sender, EventArgs args) =>
        UpdateBackdropStatus();

    private void UpdateBackdropStatus()
    {
        WindowBackdropStatus status = _backdropManager.CurrentStatus;
        EffectiveBackdropText.Text = status.DisplayName;
        BackdropDescriptionText.Text = status.Description;
    }

    private void OnPreviewPowerShellClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            ShowPreview(CreatePlan());
            SetInfo(
                InfoBarSeverity.Informational,
                "预览已刷新",
                "尚未写入 Profile、模块或环境变量。");
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(InfoBarSeverity.Error, "无法生成 PowerShell 预览", exception.Message);
        }
    }

    private async void OnInstallPowerShellClicked(object sender, RoutedEventArgs args)
    {
        PowerShellIntegrationPlan plan;
        try
        {
            plan = CreatePlan();
            ShowPreview(plan);
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(InfoBarSeverity.Error, "无法生成 PowerShell 计划", exception.Message);
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = "安装 PowerShell 会话集成",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = BuildConfirmationText(plan),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "安装",
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
            PowerShellIntegrationResult result = await CreateManager().ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? "PowerShell Profile 修改失败。");
            }

            _lastSnapshotPath = result.SnapshotPath;
            RollbackPowerShellButton.IsEnabled = _lastSnapshotPath is not null;
            SetInfo(
                InfoBarSeverity.Success,
                "PowerShell 集成已安装",
                result.SnapshotPath is null
                    ? $"模块与 {shims.ShimFiles.Count} 个 {shims.Implementation} 会话 Shim 已是最新状态。"
                    : $"已生成 {shims.ShimFiles.Count} 个 {shims.Implementation} 会话 Shim；Profile 快照：{result.SnapshotPath}");
            ShowPreview(CreatePlan());
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(InfoBarSeverity.Error, "PowerShell 集成安装失败", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRollbackPowerShellClicked(object sender, RoutedEventArgs args)
    {
        if (_lastSnapshotPath is null)
        {
            return;
        }

        string snapshotPath = _lastSnapshotPath;
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = "回滚 PowerShell Profile",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"将恢复安装前的 Profile。\n\n快照\n{snapshotPath}\n\n如果 Profile 在安装后被修改，AutoEnvPlus 会拒绝覆盖。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "回滚",
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
            PowerShellIntegrationResult result = await CreateManager().RollbackAsync(snapshotPath);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? "PowerShell Profile 回滚失败。");
            }

            _lastSnapshotPath = null;
            RollbackPowerShellButton.IsEnabled = false;
            SetInfo(
                InfoBarSeverity.Success,
                "PowerShell Profile 已回滚",
                "模块与 Shim 保留在 AutoEnvPlus 受管目录中，不会再由 Profile 自动加载。");
            ShowPreview(CreatePlan());
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(InfoBarSeverity.Error, "PowerShell Profile 回滚失败", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private PowerShellIntegrationPlan CreatePlan() => CreateManager().PlanInstall(_profilePath);

    private PowerShellIntegrationManager CreateManager()
    {
        if (!File.Exists(_cliPath))
        {
            throw new FileNotFoundException(
                "缺少 AutoEnvPlus CLI 组件，请重新构建或安装应用。",
                _cliPath);
        }

        return new PowerShellIntegrationManager(_managedRoot, _cliPath);
    }

    private void ShowPreview(PowerShellIntegrationPlan plan)
    {
        StringBuilder summary = new();
        summary.AppendLine($"Profile: {plan.ProfilePath}");
        summary.AppendLine($"模块: {plan.ModulePath}");
        summary.AppendLine($"Shim: {Path.Combine(_managedRoot, "shims")}");
        summary.AppendLine();
        summary.AppendLine(plan.ProfileChanged
            ? "Profile: 创建或更新；写入前保存快照"
            : "Profile: 管理块已是最新状态");
        summary.AppendLine(plan.ModuleChanged
            ? "模块: 原子创建或更新"
            : "模块: 已是最新状态");
        summary.AppendLine($"现有 AutoEnvPlus 管理块: {plan.ExistingProfileBlockCount}");
        summary.AppendLine("用户 Profile 之外的系统配置: 不修改");

        PowerShellSummaryText.Text = summary.ToString();
        PowerShellProfilePreviewText.Text = plan.After;
        PowerShellModulePreviewText.Text = plan.ModuleContent;
        PowerShellPreviewPivot.Visibility = Visibility.Visible;
    }

    private static string BuildConfirmationText(PowerShellIntegrationPlan plan)
    {
        string profileChange = plan.ProfileChanged
            ? "写入受管块，并在修改前创建快照"
            : "受管块已是最新状态";
        string moduleChange = plan.ModuleChanged ? "原子创建或更新" : "已是最新状态";
        return $"Profile\n{plan.ProfilePath}\n{profileChange}\n\n模块\n{plan.ModulePath}\n{moduleChange}\n\n不会修改系统 PATH。";
    }

    private void SetBusy(bool isBusy)
    {
        PreviewPowerShellButton.IsEnabled = !isBusy;
        InstallPowerShellButton.IsEnabled = !isBusy;
        RollbackPowerShellButton.IsEnabled = !isBusy && _lastSnapshotPath is not null;
        PowerShellProgress.IsActive = isBusy;
    }

    private void SetInfo(InfoBarSeverity severity, string title, string message)
    {
        PowerShellInfo.Severity = severity;
        PowerShellInfo.Title = title;
        PowerShellInfo.Message = message;
        PowerShellInfo.IsOpen = true;
    }

    private static bool IsExpectedIntegrationException(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or InvalidDataException
        or ArgumentException
        or NotSupportedException;
}
