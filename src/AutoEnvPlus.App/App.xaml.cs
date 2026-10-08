using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Settings;
using Microsoft.UI.Xaml;

namespace AutoEnvPlus.App;

public partial class App : Application
{
    private AppDownloadManager? _downloadManager;
    private DateTimeOffset _lastRuntimeErrorDialogAtUtc = DateTimeOffset.MinValue;

    public Window? MainWindowInstance { get; private set; }

    internal AutoEnvPlusApplicationSettings CurrentSettings { get; private set; } =
        AutoEnvPlusApplicationSettings.Default;

    internal AppDownloadManager DownloadManager => _downloadManager ??=
        new AppDownloadManager(ManagedRootResolver.ResolveOrThrow());

    public App()
    {
        InitializeComponent();
        // Without this subscription any exception that escapes an async void
        // handler is swallowed by the WinUI dispatcher: the app keeps running
        // in an unknown state with no dialog and no log. Log every occurrence
        // and surface the first of each burst to the user.
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        Exception exception = args.Exception;
        string detail =
            $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "autoenvplus-runtime-error.log"),
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }

        if (DateTimeOffset.UtcNow - _lastRuntimeErrorDialogAtUtc < TimeSpan.FromSeconds(30))
        {
            args.Handled = true;
            return;
        }

        _lastRuntimeErrorDialogAtUtc = DateTimeOffset.UtcNow;
        MessageBoxW(
            System.IntPtr.Zero,
            $"AutoEnvPlus 遇到未处理的错误，界面可能处于不一致状态。{Environment.NewLine}{Environment.NewLine}"
                + $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{Environment.NewLine}"
                + "详细信息已写入临时目录的 autoenvplus-runtime-error.log。"
                + "建议保存正在进行的工作后重启应用。",
            "AutoEnvPlus",
            0x00000010u /* MB_ICONERROR */);
        args.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        EntryPoint = "MessageBoxW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern int MessageBoxW(
        System.IntPtr hWnd,
        string text,
        string caption,
        uint type);

    private static void FailFastStartup(Exception exception)
    {
        // WinUI swallows exceptions that escape the async OnLaunched callback,
        // which would otherwise leave a windowless background process. Write
        // the failure to a log file, tell the user, and exit the process.
        string detail =
            $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "autoenvplus-startup-failure.log"),
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }

        MessageBoxW(
            System.IntPtr.Zero,
            $"AutoEnvPlus 启动失败，即将退出。{Environment.NewLine}{Environment.NewLine}"
                + $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{Environment.NewLine}"
                + "详细信息已写入临时目录的 autoenvplus-startup-failure.log。",
            "AutoEnvPlus",
            0x00000010u /* MB_ICONERROR */);
        Environment.Exit(1);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
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
        catch (Exception exception)
        {
            FailFastStartup(exception);
        }
    }

    internal void UpdateCurrentSettings(AutoEnvPlusApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        CurrentSettings = settings;
    }
}
