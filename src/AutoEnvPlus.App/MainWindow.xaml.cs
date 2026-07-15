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
    private bool _suppressSelectionChanged;

    public MainWindow()
    {
        InitializeComponent();
        _backdropManager = new WindowBackdropManager(this, RootSurface);
        AppWindow.Resize(new SizeInt32(1180, 760));
        NavigateTo("dashboard");
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
            ContentFrame.Content = new SettingsPage(_backdropManager);
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
                "runtimes" => new RuntimesPage(),
                "path" => new PathPage(),
                "storage" => new StoragePage(),
                "toolchains" => new ToolchainsPage(),
                "projects" => new ProjectsPage(context),
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
