using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Settings;
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
    private BackdropPreference _preference;
    private bool _disposed;

    public WindowBackdropManager(
        Window window,
        Panel rootSurface,
        BackdropPreference preference = BackdropPreference.Automatic)
    {
        _window = window;
        _rootSurface = rootSurface;
        _solidBackground = rootSurface.Background;
        _preference = preference;
        CurrentStatus = CreateStatus(BackdropSelection.SolidUnsupported);

        _rootSurface.ActualThemeChanged += OnActualThemeChanged;
        InitializeSystemSettings();
        _window.Closed += OnWindowClosed;
        ApplyCurrentPolicy();
    }

    public event EventHandler? StatusChanged;

    public WindowBackdropStatus CurrentStatus { get; private set; }

    public void SetPreference(BackdropPreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (_preference == preference)
        {
            return;
        }

        _preference = preference;
        ApplyCurrentPolicy();
    }

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
                Loc.Get("Backdrop.Fallback.SettingsUnavailable"));
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
                IsDesktopAcrylicSupported(),
                _preference);
        }
        catch (Exception)
        {
            ApplySolidFallback(
                BackdropSelection.SolidUnsupported,
                Loc.Get("Backdrop.Fallback.CapabilityProbeFailed"));
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
                    Loc.Get("Backdrop.Fallback.SystemBackdropInitFailed"));
                return;
            case BackdropSelection.DesktopAcrylic:
                if (TryApplySystemBackdrop(new DesktopAcrylicBackdrop(), selection))
                {
                    return;
                }

                ApplySolidFallback(
                    BackdropSelection.SolidUnsupported,
                    Loc.Get("Backdrop.Fallback.DesktopAcrylicInitFailed"));
                return;
            case BackdropSelection.SolidHighContrast:
            case BackdropSelection.SolidTransparencyDisabled:
            case BackdropSelection.SolidPreferred:
            case BackdropSelection.SolidUnsupported:
                ApplySolidFallback(selection);
                return;
            default:
                throw new InvalidOperationException(Loc.Format("Backdrop.Selection.Unknown", selection));
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
            _rootSurface.Background = ResolveSolidBackgroundBrush();
            return false;
        }
    }

    private void ApplySolidFallback(
        BackdropSelection selection,
        string? description = null)
    {
        _window.SystemBackdrop = null;
        _rootSurface.Background = ResolveSolidBackgroundBrush();

        WindowBackdropStatus status = CreateStatus(selection);
        UpdateStatus(description is null ? status : status with { Description = description });
    }

    private Brush ResolveSolidBackgroundBrush()
    {
        // The brush captured at startup freezes the theme it was created
        // under; re-resolve on every application so a theme change while a
        // solid background is active picks up the new palette. High-contrast
        // palettes come from system settings, so keep the captured brush.
        bool highContrast = false;
        try
        {
            highContrast = _accessibilitySettings?.HighContrast == true;
        }
        catch (Exception)
        {
        }

        if (highContrast && _solidBackground is not null)
        {
            return _solidBackground;
        }

        return _rootSurface.ActualTheme == ElementTheme.Dark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
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
            Loc.Get("Backdrop.Status.Mica.Description"),
            true),
        BackdropSelection.DesktopAcrylic => new(
            selection,
            "Desktop Acrylic",
            Loc.Get("Backdrop.Status.DesktopAcrylic.Description"),
            true),
        BackdropSelection.SolidHighContrast => new(
            selection,
            Loc.Get("Backdrop.Status.SolidHighContrast.Name"),
            Loc.Get("Backdrop.Status.SolidHighContrast.Description"),
            false),
        BackdropSelection.SolidTransparencyDisabled => new(
            selection,
            Loc.Get("Backdrop.Status.SolidTransparencyDisabled.Name"),
            Loc.Get("Backdrop.Status.SolidTransparencyDisabled.Description"),
            false),
        BackdropSelection.SolidPreferred => new(
            selection,
            Loc.Get("Backdrop.Status.SolidPreferred.Name"),
            Loc.Get("Backdrop.Status.SolidPreferred.Description"),
            false),
        BackdropSelection.SolidUnsupported => new(
            selection,
            Loc.Get("Backdrop.Status.SolidUnsupported.Name"),
            Loc.Get("Backdrop.Status.SolidUnsupported.Description"),
            false),
        _ => throw new InvalidOperationException(Loc.Format("Backdrop.Selection.Unknown", selection)),
    };
}
