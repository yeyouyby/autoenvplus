using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.App.Pages;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AutoEnvPlus.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowBackdropManager _backdropManager;
    private bool _suppressSelectionChanged;

    public MainWindow()
        : this(AutoEnvPlusApplicationSettings.Default)
    {
    }

    public MainWindow(AutoEnvPlusApplicationSettings applicationSettings)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        applicationSettings.Validate();
        InitializeComponent();
        _backdropManager = new WindowBackdropManager(
            this,
            RootSurface,
            applicationSettings.Backdrop);
        ApplyApplicationSettings(applicationSettings);
        AppWindow.Resize(new SizeInt32(1180, 760));
        NavigateTo(ApplicationSettingsPresentationPolicy.GetStartupNavigationTag(
            applicationSettings.StartupDestination));
    }

    internal AutoEnvPlusApplicationSettings CurrentApplicationSettings { get; private set; } =
        AutoEnvPlusApplicationSettings.Default;

    internal void ApplyApplicationSettings(AutoEnvPlusApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        PagePaddingMetrics padding = ApplicationSettingsPresentationPolicy.GetPagePadding(
            settings.Density);
        Application.Current.Resources["PagePadding"] = new Thickness(
            padding.Left,
            padding.Top,
            padding.Right,
            padding.Bottom);
        RootSurface.RequestedTheme = ApplicationSettingsPresentationPolicy.GetRequestedTheme(
            settings.Theme) switch
        {
            RequestedElementTheme.Default => ElementTheme.Default,
            RequestedElementTheme.Light => ElementTheme.Light,
            RequestedElementTheme.Dark => ElementTheme.Dark,
            _ => throw new InvalidOperationException("Unsupported application theme selection."),
        };
        _backdropManager.SetPreference(settings.Backdrop);
        CurrentApplicationSettings = settings;
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateCore("settings");
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        NavigateCore(tag);
    }

    internal void NavigateTo(string tag, string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (tag.Equals("settings", StringComparison.Ordinal))
        {
            if (!ReferenceEquals(RootNavigation.SelectedItem, RootNavigation.SettingsItem))
            {
                _suppressSelectionChanged = true;
                try
                {
                    RootNavigation.SelectedItem = RootNavigation.SettingsItem;
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }
            }

            NavigateCore(tag, context);
            return;
        }

        NavigationViewItem? item = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => candidate.Tag is string candidateTag
                && candidateTag.Equals(tag, StringComparison.Ordinal));
        if (item is not null && !ReferenceEquals(RootNavigation.SelectedItem, item))
        {
            _suppressSelectionChanged = true;
            try
            {
                RootNavigation.SelectedItem = item;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        NavigateCore(tag, context);
    }

    private void NavigateCore(string tag, string? context = null)
    {
        try
        {
            ContentFrame.Content = tag switch
            {
                "dashboard" => new DashboardPage(),
                "languages" => new LanguagesPage(),
                "path" => new PathPage(),
                "storage" => new StoragePage(),
                "projects" => new ProjectsPage(context),
                "downloads" => new DownloadsPage(),
                "doctor" => new DiagnosticsPage(),
                "activity" => new ActivityPage(),
                "settings" => new SettingsPage(_backdropManager),
                _ => new DashboardPage(),
            };
        }
        catch (InvalidOperationException) when (!ManagedRootResolver.TryResolve(
            null,
            out _,
            out _))
        {
            ContentFrame.Content = new SettingsPage(_backdropManager);
        }
    }
}
