using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace AutoEnvPlus.App.Appearance;

internal sealed record WindowBackdropStatus(
    BackdropSelection Selection,
    string DisplayName,
    string Description,
    bool UsesSystemMaterial);

internal sealed class WindowBackdropManager : IDisposable
{
    private readonly Window _window;
    private readonly Panel _rootSurface;
    private readonly Brush? _solidBackground;
    private AccessibilitySettings? _accessibilitySettings;
    private UISettings? _uiSettings;
    private DispatcherQueueTimer? _settingsPollTimer;
    private bool _accessibilityEventsSubscribed;
    private bool _uiEventsSubscribed;
    private bool? _lastHighContrast;
    private bool? _lastTransparencyEffectsEnabled;
    private bool _disposed;

    public WindowBackdropManager(Window window, Panel rootSurface)
    {
        _window = window;
        _rootSurface = rootSurface;
        _solidBackground = rootSurface.Background;
        CurrentStatus = CreateStatus(BackdropSelection.SolidUnsupported);

        _rootSurface.ActualThemeChanged += OnActualThemeChanged;
        InitializeSystemSettings();
        _window.Closed += OnWindowClosed;
        ApplyCurrentPolicy();
    }

    public event EventHandler? StatusChanged;

    public WindowBackdropStatus CurrentStatus { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Closed -= OnWindowClosed;
        _rootSurface.ActualThemeChanged -= OnActualThemeChanged;

        if (_accessibilityEventsSubscribed && _accessibilitySettings is not null)
        {
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
        }

        if (_uiEventsSubscribed && _uiSettings is not null)
        {
            _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsEnabledChanged;
        }

        if (_settingsPollTimer is not null)
        {
            _settingsPollTimer.Stop();
            _settingsPollTimer.Tick -= OnSettingsPollTimerTick;
            _settingsPollTimer = null;
        }

        StatusChanged = null;
    }

    private void InitializeSystemSettings()
    {
        try
        {
            _accessibilitySettings = new AccessibilitySettings();
            _ = _accessibilitySettings.HighContrast;
        }
        catch (Exception)
        {
            _accessibilitySettings = null;
        }

        try
        {
            _uiSettings = new UISettings();
            _ = _uiSettings.AdvancedEffectsEnabled;
        }
        catch (Exception)
        {
            _uiSettings = null;
        }

        if (_accessibilitySettings is not null)
        {
            try
            {
                _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
                _accessibilityEventsSubscribed = true;
            }
            catch (Exception)
            {
                _accessibilityEventsSubscribed = false;
            }
        }

        if (_uiSettings is not null)
        {
            try
            {
                _uiSettings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsEnabledChanged;
                _uiEventsSubscribed = true;
            }
            catch (Exception)
            {
                _uiEventsSubscribed = false;
            }
        }

        if ((!_accessibilityEventsSubscribed || !_uiEventsSubscribed)
            && _accessibilitySettings is not null
            && _uiSettings is not null)
        {
            _settingsPollTimer = _rootSurface.DispatcherQueue.CreateTimer();
            _settingsPollTimer.Interval = TimeSpan.FromSeconds(2);
            _settingsPollTimer.Tick += OnSettingsPollTimerTick;
            _settingsPollTimer.Start();
        }
    }

    private void ApplyCurrentPolicy()
    {
        if (_disposed)
        {
            return;
        }

        if (_accessibilitySettings is null || _uiSettings is null)
        {
            ApplySolidFallback(
                BackdropSelection.SolidUnsupported,
                "无法读取系统外观设置，已使用可靠的纯色背景。");
            return;
        }

        BackdropSelection selection;
        try
        {
            bool highContrast = _accessibilitySettings.HighContrast;
            bool transparencyEffectsEnabled = _uiSettings.AdvancedEffectsEnabled;
            _lastHighContrast = highContrast;
            _lastTransparencyEffectsEnabled = transparencyEffectsEnabled;
            selection = BackdropSelectionPolicy.Select(
                highContrast,
                transparencyEffectsEnabled,
                IsMicaSupported(),
                IsDesktopAcrylicSupported());
        }
        catch (Exception)
        {
            ApplySolidFallback(
                BackdropSelection.SolidUnsupported,
                "无法完成背景能力检测，已使用可靠的纯色背景。");
            return;
        }

        switch (selection)
        {
            case BackdropSelection.Mica:
                if (TryApplySystemBackdrop(new MicaBackdrop(), selection))
                {
                    return;
                }

                if (IsDesktopAcrylicSupported()
                    && TryApplySystemBackdrop(
                        new DesktopAcrylicBackdrop(),
                        BackdropSelection.DesktopAcrylic))
                {
                    return;
                }

                ApplySolidFallback(
                    BackdropSelection.SolidUnsupported,
                    "系统背景材料初始化失败，已自动切换为纯色背景。");
                return;
            case BackdropSelection.DesktopAcrylic:
                if (TryApplySystemBackdrop(new DesktopAcrylicBackdrop(), selection))
                {
                    return;
                }

                ApplySolidFallback(
                    BackdropSelection.SolidUnsupported,
                    "Desktop Acrylic 初始化失败，已自动切换为纯色背景。");
                return;
            case BackdropSelection.SolidHighContrast:
            case BackdropSelection.SolidTransparencyDisabled:
            case BackdropSelection.SolidUnsupported:
                ApplySolidFallback(selection);
                return;
            default:
                throw new InvalidOperationException($"未知背景选择：{selection}");
        }
    }

    private bool TryApplySystemBackdrop(
        SystemBackdrop backdrop,
        BackdropSelection selection)
    {
        try
        {
            _window.SystemBackdrop = backdrop;
            _rootSurface.Background = null;
            UpdateStatus(CreateStatus(selection));
            return true;
        }
        catch (Exception)
        {
            _window.SystemBackdrop = null;
            _rootSurface.Background = _solidBackground;
            return false;
        }
    }

    private void ApplySolidFallback(
        BackdropSelection selection,
        string? description = null)
    {
        _window.SystemBackdrop = null;
        _rootSurface.Background = _solidBackground;

        WindowBackdropStatus status = CreateStatus(selection);
        UpdateStatus(description is null ? status : status with { Description = description });
    }

    private void UpdateStatus(WindowBackdropStatus status)
    {
        if (CurrentStatus == status)
        {
            return;
        }

        CurrentStatus = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void QueuePolicyRefresh()
    {
        if (_disposed)
        {
            return;
        }

        if (_rootSurface.DispatcherQueue.HasThreadAccess)
        {
            ApplyCurrentPolicy();
            return;
        }

        _rootSurface.DispatcherQueue.TryEnqueue(ApplyCurrentPolicy);
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args) =>
        QueuePolicyRefresh();

    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object args) =>
        QueuePolicyRefresh();

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        QueuePolicyRefresh();

    private void OnSettingsPollTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _accessibilitySettings is null || _uiSettings is null)
        {
            return;
        }

        try
        {
            bool highContrast = _accessibilitySettings.HighContrast;
            bool transparencyEffectsEnabled = _uiSettings.AdvancedEffectsEnabled;
            if (_lastHighContrast == highContrast
                && _lastTransparencyEffectsEnabled == transparencyEffectsEnabled)
            {
                return;
            }
        }
        catch (Exception)
        {
        }

        ApplyCurrentPolicy();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) => Dispose();

    private static bool IsMicaSupported()
    {
        try
        {
            return MicaController.IsSupported();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDesktopAcrylicSupported()
    {
        try
        {
            return DesktopAcrylicController.IsSupported();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static WindowBackdropStatus CreateStatus(BackdropSelection selection) => selection switch
    {
        BackdropSelection.Mica => new(
            selection,
            "Mica",
            "系统支持 Mica，且“透明效果”已开启。",
            true),
        BackdropSelection.DesktopAcrylic => new(
            selection,
            "Desktop Acrylic",
            "当前环境不支持 Mica，已使用兼容 Windows 10 的桌面亚克力背景。",
            true),
        BackdropSelection.SolidHighContrast => new(
            selection,
            "纯色（高对比度）",
            "系统高对比度已开启；为保证可读性，背景材料已停用。",
            false),
        BackdropSelection.SolidTransparencyDisabled => new(
            selection,
            "纯色（透明效果已关闭）",
            "遵循 Windows“透明效果”设置，背景材料已停用。",
            false),
        BackdropSelection.SolidUnsupported => new(
            selection,
            "纯色（兼容回退）",
            "当前系统或图形环境不支持可用的 Fluent 背景材料。",
            false),
        _ => throw new InvalidOperationException($"未知背景选择：{selection}"),
    };
}
