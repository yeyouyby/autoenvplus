using System.Runtime.InteropServices;
using System.Text;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Settings;
using AutoEnvPlus.Core.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly WindowBackdropManager _backdropManager;
    private readonly string? _managedRoot;
    private readonly string? _managedRootError;
    private readonly string _cliPath;
    private readonly string _profilePath;
    private readonly string _nativeShimPath;
    private readonly AutoEnvPlusApplicationSettingsStore? _settingsStore;
    private AutoEnvPlusApplicationSettings _loadedApplicationSettings =
        AutoEnvPlusApplicationSettings.Default;
    private string? _lastSnapshotPath;
    private bool _applicationSettingsBusy;

    internal SettingsPage(WindowBackdropManager backdropManager)
    {
        InitializeComponent();
        _backdropManager = backdropManager;
        if (!ManagedRootResolver.TryResolve(
                null,
                out string? managedRoot,
                out string? managedRootError))
        {
            _managedRootError = managedRootError;
        }

        _managedRoot = managedRoot;
        _settingsStore = managedRoot is null
            ? null
            : new AutoEnvPlusApplicationSettingsStore(managedRoot);
        _cliPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus.exe");
        _nativeShimPath = Path.Combine(AppContext.BaseDirectory, "cli", "autoenvplus-shim.exe");
        _profilePath = PowerShellIntegrationManager.GetDefaultWindowsPowerShellProfilePath();
        ProfilePathText.Text = _profilePath;
        InitializeApplicationSettingsChoices();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateBackdropStatus();
        UpdateManagedRootStatus();
        SetBusy(false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _backdropManager.StatusChanged -= OnBackdropStatusChanged;
        _backdropManager.StatusChanged += OnBackdropStatusChanged;
        UpdateBackdropStatus();
        await LoadApplicationSettingsAsync();
        await LoadProxySettingsAsync();
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

    private void InitializeApplicationSettingsChoices()
    {
        StartupDestinationPicker.ItemsSource = new SettingChoice<StartupDestination>[]
        {
            new("概览", StartupDestination.Overview),
            new("语言", StartupDestination.Languages),
            new("项目环境", StartupDestination.Projects),
            new("环境诊断", StartupDestination.Diagnostics),
        };
        LanguageVisibilityPicker.ItemsSource = new SettingChoice<LanguageVisibilityPolicy>[]
        {
            new("Top 10 与本机已发现（推荐）", LanguageVisibilityPolicy.TopTenAndDetected),
            new("只显示手动启用", LanguageVisibilityPolicy.EnabledOnly),
            new("显示全部内置语言", LanguageVisibilityPolicy.AllBuiltIn),
        };
        DefaultConnectionsPicker.ItemsSource = new[] { 1, 2, 4, 8, 16 }
            .Select(value => new SettingChoice<int>($"{value} 个连接", value))
            .ToArray();
        ThemePicker.ItemsSource = new SettingChoice<ApplicationThemePreference>[]
        {
            new("跟随系统", ApplicationThemePreference.System),
            new("浅色", ApplicationThemePreference.Light),
            new("深色", ApplicationThemePreference.Dark),
        };
        BackdropPicker.ItemsSource = new SettingChoice<BackdropPreference>[]
        {
            new("自动选择", BackdropPreference.Automatic),
            new("优先 Mica", BackdropPreference.Mica),
            new("优先 Acrylic", BackdropPreference.Acrylic),
            new("纯色", BackdropPreference.Solid),
        };
        DensityPicker.ItemsSource = new SettingChoice<InterfaceDensity>[]
        {
            new("舒适", InterfaceDensity.Comfortable),
            new("紧凑", InterfaceDensity.Compact),
        };
    }

    private async Task LoadApplicationSettingsAsync()
    {
        if (_settingsStore is null)
        {
            SaveSettingsButton.IsEnabled = false;
            SaveProxyButton.IsEnabled = false;
            return;
        }

        SetApplicationSettingsBusy(true);
        try
        {
            AutoEnvPlusApplicationSettings settings = await _settingsStore.LoadAsync();
            _loadedApplicationSettings = settings;
            SelectChoice(StartupDestinationPicker, settings.StartupDestination);
            SelectChoice(LanguageVisibilityPicker, settings.LanguageVisibilityPolicy);
            SelectChoice(DefaultConnectionsPicker, settings.DefaultDownloadConnections);
            SelectChoice(ThemePicker, settings.Theme);
            SelectChoice(BackdropPicker, settings.Backdrop);
            SelectChoice(DensityPicker, settings.Density);
            MaxDownloadGbNumber.Value = settings.DefaultDownloadMaximumBytes
                / (double)(1024L * 1024 * 1024);
            LogRetentionNumber.Value = settings.LogRetentionDays;
            ExperimentalToolsToggle.IsOn = settings.ShowExperimentalTools;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            SetSettingsInfo(
                InfoBarSeverity.Error,
                "无法读取应用设置",
                exception.Message);
        }
        finally
        {
            SetApplicationSettingsBusy(false);
        }
    }

    private async void OnSaveSettingsClicked(object sender, RoutedEventArgs args)
    {
        if (_settingsStore is null || _applicationSettingsBusy)
        {
            return;
        }

        SetApplicationSettingsBusy(true);
        try
        {
            long maximumBytes = checked(
                (long)Math.Round(RequiredNumber(MaxDownloadGbNumber, "单文件默认上限"))
                * 1024 * 1024 * 1024);
            AutoEnvPlusApplicationSettings settings = _loadedApplicationSettings with
            {
                StartupDestination = SelectedChoice<StartupDestination>(
                    StartupDestinationPicker),
                OverviewRefreshPolicy = OverviewRefreshPolicy.CachedOnly,
                LanguageVisibilityPolicy = SelectedChoice<LanguageVisibilityPolicy>(
                    LanguageVisibilityPicker),
                DefaultDownloadConnections = SelectedChoice<int>(DefaultConnectionsPicker),
                DefaultDownloadMaximumBytes = maximumBytes,
                Theme = SelectedChoice<ApplicationThemePreference>(ThemePicker),
                Backdrop = SelectedChoice<BackdropPreference>(BackdropPicker),
                Density = SelectedChoice<InterfaceDensity>(DensityPicker),
                LogRetentionDays = checked((int)Math.Round(RequiredNumber(
                    LogRetentionNumber,
                    "日志保留天数"))),
                ShowExperimentalTools = ExperimentalToolsToggle.IsOn,
            };
            await _settingsStore.SaveAsync(settings);
            _loadedApplicationSettings = settings;
            ((App)Application.Current).UpdateCurrentSettings(settings);
            if (((App)Application.Current).MainWindowInstance is MainWindow mainWindow)
            {
                mainWindow.ApplyApplicationSettings(settings);
            }

            SetSettingsInfo(
                InfoBarSeverity.Success,
                "应用设置已保存",
                "主题、背景材质和界面密度已应用；启动页、语言、下载默认值和日志保留策略将在对应功能中生效。首页始终只读快照。");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                "已更新启动页、语言可见性、下载、外观和日志保留设置。",
                [_settingsStore.SettingsPath]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException)
        {
            SetSettingsInfo(InfoBarSeverity.Error, "无法保存应用设置", exception.Message);
        }
        finally
        {
            SetApplicationSettingsBusy(false);
        }
    }

    private async Task LoadProxySettingsAsync()
    {
        if (_managedRoot is null)
        {
            return;
        }

        try
        {
            NetworkSettingsLoadResult result = await new NetworkSettingsStore(_managedRoot)
                .LoadAsync();
            if (!result.Success || result.Settings is null)
            {
                SetSettingsInfo(
                    InfoBarSeverity.Warning,
                    "代理设置不可用",
                    string.Join("；", result.Errors.Select(error => error.Message)));
                return;
            }

            GlobalNetworkSettings global = result.Settings.Global ?? new GlobalNetworkSettings();
            HttpProxyTextBox.Text = global.HttpProxy ?? string.Empty;
            HttpsProxyTextBox.Text = global.HttpsProxy ?? string.Empty;
            NoProxyTextBox.Text = string.Join(", ", global.NoProxy ?? []);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            SetSettingsInfo(InfoBarSeverity.Warning, "代理设置不可用", exception.Message);
        }
    }

    private async void OnSaveProxyClicked(object sender, RoutedEventArgs args)
    {
        if (_managedRoot is null || _applicationSettingsBusy)
        {
            return;
        }

        SetApplicationSettingsBusy(true);
        try
        {
            NetworkSettingsStore store = new(_managedRoot);
            NetworkSettingsLoadResult loaded = await store.LoadAsync();
            if (!loaded.Success || loaded.Settings is null)
            {
                throw new InvalidDataException(string.Join(
                    "; ",
                    loaded.Errors.Select(error => error.Message)));
            }

            string[] noProxy = NoProxyTextBox.Text.Split(
                [',', ';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            GlobalNetworkSettings previous = loaded.Settings.Global
                ?? new GlobalNetworkSettings();
            NetworkSettings updated = new(
                new GlobalNetworkSettings(
                    EmptyToNull(HttpProxyTextBox.Text),
                    EmptyToNull(HttpsProxyTextBox.Text),
                    noProxy,
                    previous.Mirror),
                loaded.Settings.Tools);
            NetworkSettingsSaveResult saved = await store.SaveAsync(updated);
            if (!saved.Success)
            {
                throw new InvalidDataException(string.Join(
                    "; ",
                    saved.Errors.Select(error => error.Message)));
            }

            SetSettingsInfo(
                InfoBarSeverity.Success,
                "全局代理已保存",
                "语言工具镜像仍由对应 Provider 单独管理；这里没有修改任何镜像地址。");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            SetSettingsInfo(InfoBarSeverity.Error, "无法保存代理", exception.Message);
        }
        finally
        {
            SetApplicationSettingsBusy(false);
        }
    }

    private void UpdateManagedRootStatus()
    {
        if (_managedRoot is null)
        {
            ManagedRootText.Text = "无法解析受管数据根";
            ManagedRootDescriptionText.Text = _managedRootError
                ?? "请修正 AUTOENVPLUS_HOME 后重新启动 AutoEnvPlus。";
            ChooseManagedRootButton.IsEnabled = true;
            SetManagedRootInfo(
                InfoBarSeverity.Error,
                "受管数据根配置无效",
                _managedRootError ?? "请修正 AUTOENVPLUS_HOME 后重新启动 AutoEnvPlus。");
            return;
        }

        ManagedRootText.Text = _managedRoot;
        ManagedRootDescriptionText.Text =
            "当前进程启动时使用此目录。修改 AUTOENVPLUS_HOME 后，重启 AutoEnvPlus 才会生效。";
        ChooseManagedRootButton.IsEnabled = true;
        ManagedRootInfo.IsOpen = false;
    }

    private async void OnChooseManagedRootClicked(object sender, RoutedEventArgs args)
    {
        if ((Application.Current as App)?.MainWindowInstance is not Window window)
        {
            return;
        }

        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
                CommitButtonText = "选择数据根",
            };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            if (!ManagedRootResolver.TryNormalize(
                    folder.Path,
                    out string? selectedRoot,
                    out string? normalizationError)
                || selectedRoot is null)
            {
                SetManagedRootInfo(
                    InfoBarSeverity.Error,
                    "无法使用此目录",
                    normalizationError ?? "目录路径无效。");
                return;
            }

            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = "更改受管数据根",
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = BuildManagedRootChangeText(selectedRoot),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "保存并提示重启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            System.Environment.SetEnvironmentVariable(
                ManagedRootResolver.EnvironmentVariableName,
                selectedRoot,
                EnvironmentVariableTarget.User);
            bool broadcast = BroadcastEnvironmentChange();
            SetManagedRootInfo(
                InfoBarSeverity.Success,
                "受管数据根已保存",
                broadcast
                    ? $"已写入用户级 {ManagedRootResolver.EnvironmentVariableName}。不会迁移或删除旧目录；请重启 AutoEnvPlus 后使用新目录。"
                    : $"已写入用户级 {ManagedRootResolver.EnvironmentVariableName}。环境变更广播未确认；请重启 AutoEnvPlus 后使用新目录。");
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                $"已保存新的用户级 {ManagedRootResolver.EnvironmentVariableName}；重启后生效，旧目录未迁移或删除。",
                _managedRoot is null ? [selectedRoot] : [_managedRoot, selectedRoot]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.Runtime.InteropServices.COMException
            or System.Security.SecurityException)
        {
            SetManagedRootInfo(InfoBarSeverity.Error, "保存受管数据根失败", exception.Message);
        }
    }

    private string BuildManagedRootChangeText(string selectedRoot)
    {
        string currentRoot = _managedRoot ?? "当前配置无效";
        return $"当前目录\n{currentRoot}\n\n新目录\n{selectedRoot}\n\nAutoEnvPlus 只会写入用户级 {ManagedRootResolver.EnvironmentVariableName}。不会自动迁移语言工具、缓存、快照或删除旧目录；请重启应用后使新目录生效。";
    }

    private void SetManagedRootInfo(InfoBarSeverity severity, string title, string message)
    {
        ManagedRootInfo.Severity = severity;
        ManagedRootInfo.Title = title;
        ManagedRootInfo.Message = message;
        ManagedRootInfo.IsOpen = true;
    }

    private static bool BroadcastEnvironmentChange()
    {
        return SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            0,
            "Environment",
            SmtoAbortIfHung,
            5000,
            out _) != 0;
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
                RequireManagedRoot(),
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
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Succeeded,
                $"已安装 PowerShell 会话集成与 {shims.ShimFiles.Count} 个 {shims.Implementation} Shim。",
                [plan.ProfilePath, plan.ModulePath, shims.ShimDirectory],
                result.SnapshotPath,
                result.SnapshotPath);
            ShowPreview(CreatePlan());
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(InfoBarSeverity.Error, "PowerShell 集成安装失败", exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Failed,
                $"PowerShell 会话集成安装失败。错误类型：{exception.GetType().Name}。",
                [plan.ProfilePath, plan.ModulePath]);
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
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Succeeded,
                "已回滚 PowerShell Profile；受管模块与 Shim 保持不变。",
                [_profilePath, snapshotPath],
                snapshotPath,
                snapshotPath);
            ShowPreview(CreatePlan());
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(InfoBarSeverity.Error, "PowerShell Profile 回滚失败", exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Failed,
                $"PowerShell Profile 回滚失败。错误类型：{exception.GetType().Name}。",
                [_profilePath, snapshotPath],
                snapshotPath,
                snapshotPath);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private PowerShellIntegrationPlan CreatePlan() => CreateManager().PlanInstall(_profilePath);

    private PowerShellIntegrationManager CreateManager()
    {
        string managedRoot = RequireManagedRoot();
        if (!File.Exists(_cliPath))
        {
            throw new FileNotFoundException(
                "缺少 AutoEnvPlus CLI 组件，请重新构建或安装应用。",
                _cliPath);
        }

        return new PowerShellIntegrationManager(managedRoot, _cliPath);
    }

    private void ShowPreview(PowerShellIntegrationPlan plan)
    {
        StringBuilder summary = new();
        summary.AppendLine($"Profile: {plan.ProfilePath}");
        summary.AppendLine($"模块: {plan.ModulePath}");
        summary.AppendLine($"Shim: {Path.Combine(RequireManagedRoot(), "shims")}");
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
        bool canUseManagedRoot = _managedRoot is not null;
        PreviewPowerShellButton.IsEnabled = !isBusy && canUseManagedRoot;
        InstallPowerShellButton.IsEnabled = !isBusy && canUseManagedRoot;
        RollbackPowerShellButton.IsEnabled = !isBusy
            && canUseManagedRoot
            && _lastSnapshotPath is not null;
        PowerShellProgress.IsActive = isBusy;
    }

    private void SetApplicationSettingsBusy(bool busy)
    {
        _applicationSettingsBusy = busy;
        SettingsProgress.IsActive = busy;
        SaveSettingsButton.IsEnabled = !busy && _settingsStore is not null;
        SaveProxyButton.IsEnabled = !busy && _managedRoot is not null;
    }

    private void SetSettingsInfo(InfoBarSeverity severity, string title, string message)
    {
        SettingsInfo.Severity = severity;
        SettingsInfo.Title = title;
        SettingsInfo.Message = message;
        SettingsInfo.IsOpen = true;
    }

    private static void SelectChoice<T>(ComboBox picker, T value)
    {
        if (picker.ItemsSource is not IEnumerable<SettingChoice<T>> choices)
        {
            throw new InvalidOperationException("A settings picker has no compatible choices.");
        }

        picker.SelectedItem = choices.FirstOrDefault(choice =>
            EqualityComparer<T>.Default.Equals(choice.Value, value))
            ?? throw new InvalidDataException("A saved setting is not available in this build.");
    }

    private static T SelectedChoice<T>(ComboBox picker) =>
        picker.SelectedItem is SettingChoice<T> choice
            ? choice.Value
            : throw new InvalidDataException("Select a value for every application setting.");

    private static double RequiredNumber(NumberBox input, string description) =>
        double.IsNaN(input.Value) || double.IsInfinity(input.Value)
            ? throw new InvalidDataException($"{description}必须是有效数字。")
            : input.Value;

    private static string? EmptyToNull(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private string RequireManagedRoot() => _managedRoot
        ?? throw new InvalidOperationException(
            _managedRootError ?? "AutoEnvPlus could not resolve its managed root.");

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

    private sealed record SettingChoice<T>(string Label, T Value);

    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint message,
        nuint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nuint result);
}
