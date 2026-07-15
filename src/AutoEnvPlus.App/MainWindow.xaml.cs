using AutoEnvPlus.App.Pages;
using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.Core.Environment;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AutoEnvPlus.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowBackdropManager _backdropManager;

    public MainWindow()
    {
        InitializeComponent();
        _backdropManager = new WindowBackdropManager(this, RootSurface);
        AppWindow.Resize(new SizeInt32(1180, 760));
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Content = new DashboardPage();
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Content = new SettingsPage(_backdropManager);
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        try
        {
            ContentFrame.Content = tag switch
            {
                "dashboard" => new DashboardPage(),
                "runtimes" => new RuntimesPage(),
                "path" => new PathPage(),
                "storage" => new StoragePage(),
                "toolchains" => new ToolchainsPage(),
                "projects" => new ProjectsPage(),
                "doctor" => new DiagnosticsPage(),
                "activity" => new ActivityPage(),
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
