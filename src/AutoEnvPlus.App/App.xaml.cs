using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Settings;
using Microsoft.UI.Xaml;

namespace AutoEnvPlus.App;

public partial class App : Application
{
    private AppDownloadManager? _downloadManager;

    public Window? MainWindowInstance { get; private set; }

    internal AutoEnvPlusApplicationSettings CurrentSettings { get; private set; } =
        AutoEnvPlusApplicationSettings.Default;

    internal AppDownloadManager DownloadManager => _downloadManager ??=
        new AppDownloadManager(ManagedRootResolver.ResolveOrThrow());

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AutoEnvPlusApplicationSettings settings = AutoEnvPlusApplicationSettings.Default;
        if (ManagedRootResolver.TryResolve(
                null,
                out string? managedRoot,
                out _)
            && managedRoot is not null)
        {
            using CancellationTokenSource startupLoad = new(TimeSpan.FromSeconds(2));
            try
            {
                settings = await new AutoEnvPlusApplicationSettingsStore(managedRoot)
                    .LoadAsync(startupLoad.Token);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or OperationCanceledException)
            {
                settings = AutoEnvPlusApplicationSettings.Default;
            }
        }

        CurrentSettings = settings;
        MainWindowInstance = new MainWindow(settings);
        MainWindowInstance.Activate();
    }

    internal void UpdateCurrentSettings(AutoEnvPlusApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        CurrentSettings = settings;
    }
}
