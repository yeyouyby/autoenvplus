using System.Runtime.InteropServices;
using System.Text;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Settings;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.App.Text;
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
        ApplyLocalizedStrings();
        InitializeApplicationSettingsChoices();
        InitializeAboutSection();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateBackdropStatus();
        UpdateManagedRootStatus();
        SetBusy(false);
    }

    /// <summary>
    /// Applies every localized string for this page from the resource table.
    /// Unpackaged WinUI 3 apps cannot use x:Uid: the XAML loader resolves
    /// ms-resource property bags against the OS system language only, so it
    /// neither matches the standard dotted-key PRI layout nor honors the
    /// in-app language preference. Code-behind assignment through
    /// <see cref="Loc"/> is the only path that follows
    /// <c>Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c>.
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        PageTitleText.Text = Loc.Get("Settings.Page.Title");
        PageSubtitleText.Text = Loc.Get("Settings.Page.Subtitle");
        SaveSettingsButtonText.Text = Loc.Get("Settings.SaveButton.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SaveSettingsButton,
            Loc.Get("Settings.SaveButton.AccessibleName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SettingsProgress,
            Loc.Get("Settings.SaveProgress.AccessibleName"));
        LanguageSectionTitleText.Text = Loc.Get("Settings.Language.SectionTitle");
        LanguageSectionSubtitleText.Text = Loc.Get("Settings.Language.SectionSubtitle");
        LanguageFieldLabelText.Text = Loc.Get("Settings.Language.FieldLabel");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LanguagePicker,
            Loc.Get("Settings.Language.Picker.AccessibleName"));
        LanguageRestartHint.Text = Loc.Get("Settings.Language.RestartHint");
        AppearanceSectionTitleText.Text = Loc.Get("Settings.Appearance.SectionTitle");
        AppearanceSectionSubtitleText.Text = Loc.Get("Settings.Appearance.SectionSubtitle");
        ThemeFieldLabelText.Text = Loc.Get("Settings.Appearance.Theme.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ThemePicker,
            Loc.Get("Settings.Appearance.Theme.AccessibleName"));
        BackdropFieldLabelText.Text = Loc.Get("Settings.Appearance.Backdrop.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BackdropPicker,
            Loc.Get("Settings.Appearance.Backdrop.AccessibleName"));
        DensityFieldLabelText.Text = Loc.Get("Settings.Appearance.Density.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DensityPicker,
            Loc.Get("Settings.Appearance.Density.AccessibleName"));
        StartupSnapshotExpander.Header = Loc.Get("Settings.Startup.Header");
        StartupDestinationLabelText.Text = Loc.Get("Settings.Startup.Destination.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StartupDestinationPicker,
            Loc.Get("Settings.Startup.Destination.AccessibleName"));
        StartupScanPolicyInfoBar.Title = Loc.Get("Settings.Startup.ScanPolicy.Title");
        StartupScanPolicyInfoBar.Message = Loc.Get("Settings.Startup.ScanPolicy.Message");
        LanguageCatalogExpander.Header = Loc.Get("Settings.LanguageCatalog.Header");
        LanguageVisibilityLabelText.Text = Loc.Get("Settings.LanguageCatalog.Visibility.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LanguageVisibilityPicker,
            Loc.Get("Settings.LanguageCatalog.Visibility.AccessibleName"));
        ExperimentalToolsToggle.Header =
            Loc.Get("Settings.LanguageCatalog.ExperimentalTools.Header");
        ExperimentalToolsToggle.OffContent =
            Loc.Get("Settings.LanguageCatalog.ExperimentalTools.OffContent");
        ExperimentalToolsToggle.OnContent =
            Loc.Get("Settings.LanguageCatalog.ExperimentalTools.OnContent");
        ExperimentalToolsHintText.Text =
            Loc.Get("Settings.LanguageCatalog.ExperimentalTools.Hint");
        DownloadDefaultsExpander.Header = Loc.Get("Settings.DownloadDefaults.Header");
        DefaultConnectionsLabelText.Text =
            Loc.Get("Settings.DownloadDefaults.Connections.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DefaultConnectionsPicker,
            Loc.Get("Settings.DownloadDefaults.Connections.AccessibleName"));
        MaxDownloadGbLabelText.Text = Loc.Get("Settings.DownloadDefaults.MaxSize.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            MaxDownloadGbNumber,
            Loc.Get("Settings.DownloadDefaults.MaxSize.AccessibleName"));
        NetworkProxyExpander.Header = Loc.Get("Settings.Network.Header");
        NetworkProxyIntroText.Text = Loc.Get("Settings.Network.Intro");
        HttpProxyLabelText.Text = Loc.Get("Settings.Network.HttpProxy.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HttpProxyTextBox,
            Loc.Get("Settings.Network.HttpProxy.AccessibleName"));
        HttpProxyHintText.Text = Loc.Get("Settings.Network.ProxyHint");
        HttpsProxyLabelText.Text = Loc.Get("Settings.Network.HttpsProxy.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HttpsProxyTextBox,
            Loc.Get("Settings.Network.HttpsProxy.AccessibleName"));
        HttpsProxyHintText.Text = Loc.Get("Settings.Network.ProxyHint");
        NoProxyLabelText.Text = Loc.Get("Settings.Network.NoProxy.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            NoProxyTextBox,
            Loc.Get("Settings.Network.NoProxy.AccessibleName"));
        SaveProxyButtonText.Text = Loc.Get("Settings.Network.SaveProxy.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SaveProxyButton,
            Loc.Get("Settings.Network.SaveProxy.AccessibleName"));
        LoggingPolicyExpander.Header = Loc.Get("Settings.Logging.Header");
        SecurityContractInfoBar.Title = Loc.Get("Settings.Logging.SecurityContract.Title");
        SecurityContractInfoBar.Message = Loc.Get("Settings.Logging.SecurityContract.Message");
        LogRetentionLabelText.Text = Loc.Get("Settings.Logging.Retention.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LogRetentionNumber,
            Loc.Get("Settings.Logging.Retention.AccessibleName"));
        LogRetentionHintText.Text = Loc.Get("Settings.Logging.Retention.Hint");
        ManagedRootSectionTitleText.Text = Loc.Get("Settings.ManagedRoot.SectionTitle");
        ManagedRootSectionSubtitleText.Text =
            Loc.Get("Settings.ManagedRoot.SectionSubtitle");
        ToolTipService.SetToolTip(
            ChooseManagedRootButton,
            Loc.Get("Settings.ManagedRoot.Choose.ToolTip"));
        ChooseManagedRootButtonText.Text = Loc.Get("Settings.ManagedRoot.Choose.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ResetManagedRootButton,
            Loc.Get("Settings.ManagedRoot.Reset.AccessibleName"));
        ToolTipService.SetToolTip(
            ResetManagedRootButton,
            Loc.Get("Settings.ManagedRoot.Reset.ToolTip"));
        ResetManagedRootButtonText.Text = Loc.Get("Settings.ManagedRoot.Reset.Label");
        PowerShellSectionTitleText.Text = Loc.Get("Settings.PowerShell.SectionTitle");
        PowerShellSectionSubtitleText.Text = Loc.Get("Settings.PowerShell.SectionSubtitle");
        ToolTipService.SetToolTip(
            PreviewPowerShellButton,
            Loc.Get("Settings.PowerShell.Preview.ToolTip"));
        PreviewPowerShellButtonText.Text = Loc.Get("Settings.PowerShell.Preview.Label");
        ToolTipService.SetToolTip(
            InstallPowerShellButton,
            Loc.Get("Settings.PowerShell.Install.ToolTip"));
        InstallPowerShellButtonText.Text = Loc.Get("Settings.PowerShell.Install.Label");
        ToolTipService.SetToolTip(
            RollbackPowerShellButton,
            Loc.Get("Settings.PowerShell.Rollback.ToolTip"));
        RollbackPowerShellButtonText.Text = Loc.Get("Settings.PowerShell.Rollback.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PowerShellProgress,
            Loc.Get("Settings.PowerShell.Progress.AccessibleName"));
        PowerShellSummaryPivotItem.Header = Loc.Get("Settings.PowerShell.Summary.Header");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PowerShellSummaryText,
            Loc.Get("Settings.PowerShell.Summary.AccessibleName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PowerShellProfilePreviewText,
            Loc.Get("Settings.PowerShell.ProfilePreview.AccessibleName"));
        PowerShellModulePivotItem.Header = Loc.Get("Settings.PowerShell.Module.Header");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            PowerShellModulePreviewText,
            Loc.Get("Settings.PowerShell.ModulePreview.AccessibleName"));
        AboutSectionTitleText.Text = Loc.Get("Settings.About.SectionTitle");
        AboutTaglineText.Text = Loc.Get("Settings.About.Tagline");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            GitHubRepoLink,
            Loc.Get("Settings.About.GitHubRepo.AccessibleName"));
        GitHubRepoLink.Content = Loc.Get("Settings.About.GitHubRepo.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ReleaseNotesLink,
            Loc.Get("Settings.About.ReleaseNotes.AccessibleName"));
        ReleaseNotesLink.Content = Loc.Get("Settings.About.ReleaseNotes.Label");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            IssueFeedbackLink,
            Loc.Get("Settings.About.IssueFeedback.AccessibleName"));
        IssueFeedbackLink.Content = Loc.Get("Settings.About.IssueFeedback.Label");
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

    private void InitializeAboutSection()
    {
        try
        {
            ProductIdentityPresentation identity =
                ProductIdentityPresentationPolicy.FromAssembly(typeof(SettingsPage).Assembly);
            AboutVersionText.Text = $"AutoEnvPlus {ProductIdentityText.DisplayVersion(identity)} · "
                + (identity.IsPreview
                    ? Loc.Get("Settings.About.Stage.Preview")
                    : Loc.Get("Settings.About.Stage.Stable"));
        }
        catch (InvalidOperationException)
        {
            AboutVersionText.Text = "AutoEnvPlus";
        }

        AboutManagedRootText.Text = _managedRoot is null
            ? Loc.Get("Settings.About.ManagedRootUnset")
            : Loc.Format("Settings.About.ManagedRootSet", _managedRoot);
    }

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
            new(Loc.Get("Settings.Startup.Option.Overview"), StartupDestination.Overview),
            new(Loc.Get("Settings.Startup.Option.Languages"), StartupDestination.Languages),
            new(Loc.Get("Settings.Startup.Option.Projects"), StartupDestination.Projects),
            new(Loc.Get("Settings.Startup.Option.Diagnostics"), StartupDestination.Diagnostics),
        };
        LanguageVisibilityPicker.ItemsSource = new SettingChoice<LanguageVisibilityPolicy>[]
        {
            new(
                Loc.Get("Settings.LanguageCatalog.Visibility.Option.TopTenAndDetected"),
                LanguageVisibilityPolicy.TopTenAndDetected),
            new(
                Loc.Get("Settings.LanguageCatalog.Visibility.Option.EnabledOnly"),
                LanguageVisibilityPolicy.EnabledOnly),
            new(
                Loc.Get("Settings.LanguageCatalog.Visibility.Option.AllBuiltIn"),
                LanguageVisibilityPolicy.AllBuiltIn),
        };
        DefaultConnectionsPicker.ItemsSource = new[] { 1, 2, 4, 8, 16 }
            .Select(value => new SettingChoice<int>(
                Loc.Format("Settings.DownloadDefaults.Connections.Option", value),
                value))
            .ToArray();
        ThemePicker.ItemsSource = new SettingChoice<ApplicationThemePreference>[]
        {
            new(Loc.Get("Settings.Appearance.Theme.Option.System"), ApplicationThemePreference.System),
            new(Loc.Get("Settings.Appearance.Theme.Option.Light"), ApplicationThemePreference.Light),
            new(Loc.Get("Settings.Appearance.Theme.Option.Dark"), ApplicationThemePreference.Dark),
        };
        BackdropPicker.ItemsSource = new SettingChoice<BackdropPreference>[]
        {
            new(Loc.Get("Settings.Appearance.Backdrop.Option.Automatic"), BackdropPreference.Automatic),
            new(Loc.Get("Settings.Appearance.Backdrop.Option.Mica"), BackdropPreference.Mica),
            new(Loc.Get("Settings.Appearance.Backdrop.Option.Acrylic"), BackdropPreference.Acrylic),
            new(Loc.Get("Settings.Appearance.Backdrop.Option.Solid"), BackdropPreference.Solid),
        };
        DensityPicker.ItemsSource = new SettingChoice<InterfaceDensity>[]
        {
            new(Loc.Get("Settings.Appearance.Density.Option.Comfortable"), InterfaceDensity.Comfortable),
            new(Loc.Get("Settings.Appearance.Density.Option.Compact"), InterfaceDensity.Compact),
        };
        LanguagePicker.ItemsSource = new SettingChoice<LanguagePreference>[]
        {
            new(Loc.Get("Settings.Language.Option.System"), LanguagePreference.System),
            new(
                Loc.Get("Settings.Language.Option.ChineseSimplified"),
                LanguagePreference.ChineseSimplified),
            new(Loc.Get("Settings.Language.Option.English"), LanguagePreference.English),
        };

        // The restart hint only matters while an unsaved (or saved-but-not-
        // yet-applied) language change is pending; hide it when the picker
        // matches the persisted preference.
        LanguagePicker.SelectionChanged += OnLanguagePickerSelectionChanged;
        UpdateLanguageRestartHint();

        // Live preview: visual settings apply immediately when picked; the
        // save button persists them. Without this the user had to save (and
        // could not un-save) just to see what a theme looks like.
        ThemePicker.SelectionChanged += OnAppearancePreviewChanged;
        BackdropPicker.SelectionChanged += OnAppearancePreviewChanged;
        DensityPicker.SelectionChanged += OnAppearancePreviewChanged;

        // Inline proxy validation: a malformed endpoint is flagged while
        // typing instead of only failing at save time.
        HttpProxyTextBox.TextChanged += (_, _) => ValidateProxyInline();
        HttpsProxyTextBox.TextChanged += (_, _) => ValidateProxyInline();
    }

    private void OnLanguagePickerSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateLanguageRestartHint();

    private void UpdateLanguageRestartHint()
    {
        LanguageRestartHint.Visibility =
            LanguagePicker.SelectedItem is SettingChoice<LanguagePreference> choice
            && choice.Value != _loadedApplicationSettings.LanguagePreference
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ValidateProxyInline()
    {
        if (_applicationSettingsBusy)
        {
            // The busy state owns the button while a load or save runs; the
            // final SetApplicationSettingsBusy(false) re-evaluates validity.
            return;
        }

        SaveProxyButton.IsEnabled = IsProxyTextValid(HttpProxyTextBox.Text)
            && IsProxyTextValid(HttpsProxyTextBox.Text);
        HttpProxyHintText.Text = IsProxyTextEmpty(HttpProxyTextBox.Text)
            ? Loc.Get("Settings.Network.ProxyHint")
            : IsProxyTextValid(HttpProxyTextBox.Text)
                ? Loc.Get("Settings.Network.ProxyValid")
                : Loc.Get("Settings.Network.ProxyInvalid");
        HttpsProxyHintText.Text = IsProxyTextEmpty(HttpsProxyTextBox.Text)
            ? Loc.Get("Settings.Network.ProxyHint")
            : IsProxyTextValid(HttpsProxyTextBox.Text)
                ? Loc.Get("Settings.Network.ProxyValid")
                : Loc.Get("Settings.Network.ProxyInvalid");
    }

    private static bool IsProxyTextEmpty(string text) => string.IsNullOrWhiteSpace(text);

    private static bool IsProxyTextValid(string text) =>
        IsProxyTextEmpty(text)
        || (Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri)
            && uri is not null
            && !string.IsNullOrWhiteSpace(uri.Host));

    private void OnAppearancePreviewChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_applicationSettingsBusy
            || ((App)Application.Current).MainWindowInstance is not MainWindow mainWindow)
        {
            return;
        }

        AutoEnvPlusApplicationSettings preview = _loadedApplicationSettings;
        if (ThemePicker.SelectedItem is SettingChoice<ApplicationThemePreference> theme)
        {
            preview = preview with { Theme = theme.Value };
        }

        if (BackdropPicker.SelectedItem is SettingChoice<BackdropPreference> backdrop)
        {
            preview = preview with { Backdrop = backdrop.Value };
        }

        if (DensityPicker.SelectedItem is SettingChoice<InterfaceDensity> density)
        {
            preview = preview with { Density = density.Value };
        }

        if (preview == _loadedApplicationSettings)
        {
            return;
        }

        try
        {
            mainWindow.ApplyApplicationSettings(preview);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException)
        {
            // A preview must never break the page; saving still validates.
        }
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
            SelectChoice(LanguagePicker, settings.LanguagePreference);
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
                Loc.Get("Settings.LoadFailed.Title"),
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
                (long)Math.Round(
                    RequiredNumber(
                        MaxDownloadGbNumber,
                        Loc.Get("Settings.DownloadDefaults.MaxSize.ValidationName")))
                * 1024 * 1024 * 1024);
            StartupDestination startupDestination = SelectedChoice<StartupDestination>(
                StartupDestinationPicker);
            LanguageVisibilityPolicy languageVisibility =
                SelectedChoice<LanguageVisibilityPolicy>(LanguageVisibilityPicker);
            int downloadConnections = SelectedChoice<int>(DefaultConnectionsPicker);
            ApplicationThemePreference theme = SelectedChoice<ApplicationThemePreference>(ThemePicker);
            BackdropPreference backdrop = SelectedChoice<BackdropPreference>(BackdropPicker);
            InterfaceDensity density = SelectedChoice<InterfaceDensity>(DensityPicker);
            LanguagePreference languagePreference =
                SelectedChoice<LanguagePreference>(LanguagePicker);
            int logRetentionDays = checked((int)Math.Round(RequiredNumber(
                LogRetentionNumber,
                Loc.Get("Settings.Logging.Retention.ValidationName"))));
            bool showExperimentalTools = ExperimentalToolsToggle.IsOn;
            AutoEnvPlusApplicationSettings settings = await _settingsStore.UpdateAsync(current =>
                current with
                {
                    StartupDestination = startupDestination,
                    OverviewRefreshPolicy = OverviewRefreshPolicy.CachedOnly,
                    LanguageVisibilityPolicy = languageVisibility,
                    DefaultDownloadConnections = downloadConnections,
                    DefaultDownloadMaximumBytes = maximumBytes,
                    Theme = theme,
                    Backdrop = backdrop,
                    Density = density,
                    LogRetentionDays = logRetentionDays,
                    ShowExperimentalTools = showExperimentalTools,
                    LanguagePreference = languagePreference,
                });
            _loadedApplicationSettings = settings;
            ((App)Application.Current).UpdateCurrentSettings(settings);
            if (((App)Application.Current).MainWindowInstance is MainWindow mainWindow)
            {
                mainWindow.ApplyApplicationSettings(settings);
            }

            SetSettingsInfo(
                InfoBarSeverity.Success,
                Loc.Get("Settings.SaveSucceeded.Title"),
                Loc.Get("Settings.SaveSucceeded.Message"));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Get("Settings.ActivityLog.SettingsSaved"),
                [_settingsStore.SettingsPath]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException)
        {
            SetSettingsInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.SaveFailed.Title"),
                exception.Message);
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
                    Loc.Get("Settings.ProxyUnavailable.Title"),
                    string.Join(
                        Loc.Get("Settings.Common.ErrorSeparator"),
                        result.Errors.Select(error => error.Message)));
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
            SetSettingsInfo(
                InfoBarSeverity.Warning,
                Loc.Get("Settings.ProxyUnavailable.Title"),
                exception.Message);
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
                Loc.Get("Settings.ProxySaveSucceeded.Title"),
                Loc.Get("Settings.ProxySaveSucceeded.Message"));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            SetSettingsInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.ProxySaveFailed.Title"),
                exception.Message);
        }
        finally
        {
            SetApplicationSettingsBusy(false);
        }
    }

    private void UpdateManagedRootStatus()
    {
        // The restore affordance only makes sense while a user-level override
        // exists; hide it when the app already runs on the default root.
        string? userOverride = System.Environment.GetEnvironmentVariable(
            ManagedRootResolver.EnvironmentVariableName,
            EnvironmentVariableTarget.User);
        ResetManagedRootButton.Visibility = string.IsNullOrWhiteSpace(userOverride)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_managedRoot is null)
        {
            ManagedRootText.Text = Loc.Get("Settings.ManagedRoot.Unresolved");
            ManagedRootDescriptionText.Text = _managedRootError
                ?? Loc.Get("Settings.ManagedRoot.FixEnvHint");
            ChooseManagedRootButton.IsEnabled = true;
            SetManagedRootInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.ManagedRoot.Invalid.Title"),
                _managedRootError ?? Loc.Get("Settings.ManagedRoot.FixEnvHint"));
            return;
        }

        ManagedRootText.Text = _managedRoot;
        ManagedRootDescriptionText.Text =
            Loc.Get("Settings.ManagedRoot.CurrentProcessHint");
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
                CommitButtonText = Loc.Get("Settings.ManagedRoot.Choose.Label"),
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
                    Loc.Get("Settings.ManagedRoot.ChooseFailed.Title"),
                    normalizationError ?? Loc.Get("Settings.ManagedRoot.InvalidPath"));
                return;
            }

            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Settings.ManagedRoot.ChangeDialog.Title"),
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = BuildManagedRootChangeText(selectedRoot),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = Loc.Get("Settings.ManagedRoot.ChangeDialog.PrimaryButton"),
                CloseButtonText = Loc.Get("Settings.Common.Cancel"),
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
                Loc.Get("Settings.ManagedRoot.SaveSucceeded.Title"),
                broadcast
                    ? Loc.Format(
                        "Settings.ManagedRoot.SaveSucceeded.Message",
                        ManagedRootResolver.EnvironmentVariableName)
                    : Loc.Format(
                        "Settings.ManagedRoot.SaveSucceeded.NoBroadcastMessage",
                        ManagedRootResolver.EnvironmentVariableName));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "Settings.ManagedRoot.ActivityLog.Saved",
                    ManagedRootResolver.EnvironmentVariableName),
                _managedRoot is null ? [selectedRoot] : [_managedRoot, selectedRoot]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.Runtime.InteropServices.COMException
            or System.Security.SecurityException)
        {
            SetManagedRootInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.ManagedRoot.SaveFailed.Title"),
                exception.Message);
        }
    }

    private string BuildManagedRootChangeText(string selectedRoot)
    {
        string currentRoot = _managedRoot ?? Loc.Get("Settings.ManagedRoot.CurrentInvalid");
        return Loc.Format(
            "Settings.ManagedRoot.ChangeDialog.Message",
            currentRoot,
            selectedRoot,
            ManagedRootResolver.EnvironmentVariableName);
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

    private async void OnResetManagedRootClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            string? currentOverride = System.Environment.GetEnvironmentVariable(
                ManagedRootResolver.EnvironmentVariableName,
                EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(currentOverride))
            {
                SetManagedRootInfo(
                    InfoBarSeverity.Informational,
                    Loc.Get("Settings.ManagedRoot.ResetNothing.Title"),
                    Loc.Format(
                        "Settings.ManagedRoot.ResetNothing.Message",
                        ManagedRootResolver.EnvironmentVariableName));
                return;
            }

            string defaultRoot = Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData),
                ManagedRootResolver.DefaultDirectoryName);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Settings.ManagedRoot.ResetDialog.Title"),
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = Loc.Format(
                        "Settings.ManagedRoot.ResetDialog.Message",
                        ManagedRootResolver.EnvironmentVariableName,
                        currentOverride,
                        defaultRoot),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = Loc.Get("Settings.ManagedRoot.ResetDialog.PrimaryButton"),
                CloseButtonText = Loc.Get("Settings.Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            System.Environment.SetEnvironmentVariable(
                ManagedRootResolver.EnvironmentVariableName,
                null,
                EnvironmentVariableTarget.User);
            bool broadcast = BroadcastEnvironmentChange();
            SetManagedRootInfo(
                InfoBarSeverity.Success,
                Loc.Get("Settings.ManagedRoot.ResetSucceeded.Title"),
                broadcast
                    ? Loc.Format(
                        "Settings.ManagedRoot.ResetSucceeded.Message",
                        ManagedRootResolver.EnvironmentVariableName)
                    : Loc.Format(
                        "Settings.ManagedRoot.ResetSucceeded.NoBroadcastMessage",
                        ManagedRootResolver.EnvironmentVariableName));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "Settings.ManagedRoot.ActivityLog.Reset",
                    ManagedRootResolver.EnvironmentVariableName),
                [currentOverride, defaultRoot]);
            UpdateManagedRootStatus();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.Runtime.InteropServices.COMException
            or System.Security.SecurityException)
        {
            SetManagedRootInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.ManagedRoot.ResetFailed.Title"),
                exception.Message);
        }
    }

    private void OnPreviewPowerShellClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            ShowPreview(CreatePlan());
            SetInfo(
                InfoBarSeverity.Informational,
                Loc.Get("Settings.PowerShell.PreviewRefreshed.Title"),
                Loc.Get("Settings.PowerShell.PreviewRefreshed.Message"));
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.PowerShell.PreviewFailed.Title"),
                exception.Message);
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
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.PowerShell.PlanFailed.Title"),
                exception.Message);
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Settings.PowerShell.InstallDialog.Title"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = BuildConfirmationText(plan),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Settings.PowerShell.Install.Label"),
            CloseButtonText = Loc.Get("Settings.Common.Cancel"),
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
                    result.Error ?? Loc.Get("Settings.PowerShell.ApplyFailed"));
            }

            _lastSnapshotPath = result.SnapshotPath;
            RollbackPowerShellButton.IsEnabled = _lastSnapshotPath is not null;
            SetInfo(
                InfoBarSeverity.Success,
                Loc.Get("Settings.PowerShell.InstallSucceeded.Title"),
                result.SnapshotPath is null
                    ? Loc.Format(
                        "Settings.PowerShell.InstallSucceeded.UpToDateMessage",
                        shims.ShimFiles.Count,
                        shims.Implementation)
                    : Loc.Format(
                        "Settings.PowerShell.InstallSucceeded.Message",
                        shims.ShimFiles.Count,
                        shims.Implementation,
                        result.SnapshotPath));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "Settings.PowerShell.ActivityLog.Installed",
                    shims.ShimFiles.Count,
                    shims.Implementation),
                [plan.ProfilePath, plan.ModulePath, shims.ShimDirectory],
                result.SnapshotPath,
                result.SnapshotPath);
            ShowPreview(CreatePlan());
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.PowerShell.InstallFailed.Title"),
                exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Failed,
                Loc.Format(
                    "Settings.PowerShell.ActivityLog.InstallFailed",
                    exception.GetType().Name),
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
            Title = Loc.Get("Settings.PowerShell.RollbackDialog.Title"),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format("Settings.PowerShell.RollbackDialog.Message", snapshotPath),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("Settings.PowerShell.Rollback.Label"),
            CloseButtonText = Loc.Get("Settings.Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            PowerShellIntegrationResult result = await CreateManager().RollbackAsync(
                snapshotPath,
                _profilePath);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Error ?? Loc.Get("Settings.PowerShell.RollbackApplyFailed"));
            }

            _lastSnapshotPath = null;
            RollbackPowerShellButton.IsEnabled = false;
            SetInfo(
                InfoBarSeverity.Success,
                Loc.Get("Settings.PowerShell.RollbackSucceeded.Title"),
                Loc.Get("Settings.PowerShell.RollbackSucceeded.Message"));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Succeeded,
                Loc.Get("Settings.PowerShell.ActivityLog.RolledBack"),
                [_profilePath, snapshotPath],
                snapshotPath,
                snapshotPath);
            ShowPreview(CreatePlan());
        }
        catch (Exception exception) when (IsExpectedIntegrationException(exception))
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Settings.PowerShell.RollbackFailed.Title"),
                exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PowerShellIntegration,
                ActivityStatus.Failed,
                Loc.Format(
                    "Settings.PowerShell.ActivityLog.RollbackFailed",
                    exception.GetType().Name),
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
                Loc.Get("Settings.PowerShell.CliMissing"),
                _cliPath);
        }

        return new PowerShellIntegrationManager(managedRoot, _cliPath);
    }

    private void ShowPreview(PowerShellIntegrationPlan plan)
    {
        StringBuilder summary = new();
        summary.AppendLine($"Profile: {plan.ProfilePath}");
        summary.AppendLine(Loc.Format("Settings.PowerShell.Preview.ModulePath", plan.ModulePath));
        summary.AppendLine($"Shim: {Path.Combine(RequireManagedRoot(), "shims")}");
        summary.AppendLine();
        summary.AppendLine(plan.ProfileChanged
            ? Loc.Get("Settings.PowerShell.Preview.ProfileChanged")
            : Loc.Get("Settings.PowerShell.Preview.ProfileUnchanged"));
        summary.AppendLine(plan.ModuleChanged
            ? Loc.Get("Settings.PowerShell.Preview.ModuleChanged")
            : Loc.Get("Settings.PowerShell.Preview.ModuleUnchanged"));
        summary.AppendLine(Loc.Format(
            "Settings.PowerShell.Preview.ExistingBlocks",
            plan.ExistingProfileBlockCount));
        summary.AppendLine(Loc.Get("Settings.PowerShell.Preview.SystemConfigUnchanged"));

        PowerShellSummaryText.Text = summary.ToString();
        PowerShellProfilePreviewText.Text = plan.After;
        PowerShellModulePreviewText.Text = plan.ModuleContent;
        PowerShellPreviewPivot.Visibility = Visibility.Visible;
    }

    private static string BuildConfirmationText(PowerShellIntegrationPlan plan)
    {
        string profileChange = plan.ProfileChanged
            ? Loc.Get("Settings.PowerShell.Plan.ProfileChanged")
            : Loc.Get("Settings.PowerShell.Plan.ProfileUnchanged");
        string moduleChange = plan.ModuleChanged
            ? Loc.Get("Settings.PowerShell.Plan.ModuleChanged")
            : Loc.Get("Settings.PowerShell.Plan.ModuleUnchanged");
        return Loc.Format(
            "Settings.PowerShell.InstallDialog.Message",
            plan.ProfilePath,
            profileChange,
            plan.ModulePath,
            moduleChange);
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
        SaveProxyButton.IsEnabled = !busy
            && _managedRoot is not null
            && IsProxyTextValid(HttpProxyTextBox.Text)
            && IsProxyTextValid(HttpsProxyTextBox.Text);
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
            ? throw new InvalidDataException(
                Loc.Format("Settings.Validation.NumberRequired", description))
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
