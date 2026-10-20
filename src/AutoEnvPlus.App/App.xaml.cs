using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.App.Services;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Settings;
using Microsoft.UI.Xaml;

namespace AutoEnvPlus.App;

public partial class App : Application
{
    private AppDownloadManager? _downloadManager;
    private OverviewMonitorService? _overviewMonitor;
    private DateTimeOffset _lastRuntimeErrorDialogAtUtc = DateTimeOffset.MinValue;
    private System.Threading.Mutex? _singleInstanceMutex;

    public Window? MainWindowInstance { get; private set; }

    internal AutoEnvPlusApplicationSettings CurrentSettings { get; private set; } =
        AutoEnvPlusApplicationSettings.Default;

    internal AppDownloadManager DownloadManager => _downloadManager ??=
        new AppDownloadManager(ManagedRootResolver.ResolveOrThrow());

    /// <summary>
    /// App-lifetime overview statistics monitor. Owns snapshot collection so
    /// the environment statistics keep refreshing in the background even
    /// when no dashboard page is visible.
    /// </summary>
    internal OverviewMonitorService OverviewMonitor =>
        _overviewMonitor ??= new OverviewMonitorService(DownloadManager);

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
            Loc.Get("App.UnhandledError.Intro")
                + Environment.NewLine + Environment.NewLine
                + $"{exception.GetType().Name}: {exception.Message}"
                + Environment.NewLine + Environment.NewLine
                + Loc.Get("App.UnhandledError.Details"),
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

    // Internal so the custom Program.Main can route pre-activation startup
    // failures through the same log + dialog + exit path.
    internal static void FailFastStartup(Exception exception)
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
            Loc.Get("App.StartupFailure.Intro")
                + Environment.NewLine + Environment.NewLine
                + $"{exception.GetType().Name}: {exception.Message}"
                + Environment.NewLine + Environment.NewLine
                + Loc.Get("App.StartupFailure.Details"),
            "AutoEnvPlus",
            0x00000010u /* MB_ICONERROR */);
        Environment.Exit(1);
    }

    /// <summary>
    /// The first launch of a freshly downloaded single-file EXE can hit a
    /// transient <see cref="Microsoft.UI.Xaml.Markup.XamlParseException"/>:
    /// security software scanning the new download briefly blocks reads of
    /// the extracted XBF/PRI payload. The identical binary then starts
    /// cleanly on the next attempt, so retry a few times with a growing
    /// delay before surfacing the failure dialog.
    /// </summary>
    private const int MainWindowCreateAttempts = 3;

    private static async Task<MainWindow> CreateMainWindowWithRetryAsync(
        AutoEnvPlusApplicationSettings settings)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new MainWindow(settings);
            }
            catch (Microsoft.UI.Xaml.Markup.XamlParseException exception)
                when (attempt < MainWindowCreateAttempts)
            {
                // Best-effort diagnostics: the same interference that broke
                // the XAML read can block this log write, and a logging
                // failure must never abort the retry itself.
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            "autoenvplus-startup-retry.log"),
                        $"[{DateTimeOffset.Now:O}] attempt {attempt} failed: "
                            + exception.Message
                            + Environment.NewLine);
                }
                catch (Exception logException) when (logException is IOException
                    or UnauthorizedAccessException)
                {
                }

                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            ManagedRootResolver.TryResolve(null, out string? managedRoot, out _);
            if (!TryAcquireSingleInstanceLock(managedRoot))
            {
                // Two instances writing the same managed root concurrently can
                // corrupt registry state and snapshots; refuse the second one.
                // When the existing window can be surfaced, do so silently -
                // the app jumping to the front is self-explanatory. Only fall
                // back to a blocking notice when no window was found.
                if (!ActivateExistingInstance())
                {
                    MessageBoxW(
                        System.IntPtr.Zero,
                        Loc.Get("App.SingleInstance.SwitchFailed")
                            + Environment.NewLine + Environment.NewLine
                            + Loc.Get("App.SingleInstance.Explanation"),
                        "AutoEnvPlus",
                        0x00000030u /* MB_ICONWARNING */);
                }

                Environment.Exit(0);
                return;
            }

            AutoEnvPlusApplicationSettings settings = AutoEnvPlusApplicationSettings.Default;
            if (managedRoot is not null)
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
            MainWindowInstance = await CreateMainWindowWithRetryAsync(settings);
            MainWindowInstance.Activate();
            if (managedRoot is not null)
            {
                // Background statistics: quick-refresh the overview snapshot
                // every 5 minutes for the lifetime of the app so the data is
                // sustainable across page switches. Full scans stay
                // user-initiated (they probe processes and walk caches).
                OverviewMonitor.StartAutoRefresh(managedRoot, TimeSpan.FromMinutes(5));
            }
        }
        catch (Exception exception)
        {
            FailFastStartup(exception);
        }
    }

    private bool TryAcquireSingleInstanceLock(string? managedRoot)
    {
        // One instance per managed root: instances targeting different roots
        // may coexist, but two writers to the same root must not.
        string rootKey = "default";
        try
        {
            if (!string.IsNullOrWhiteSpace(managedRoot))
            {
                rootKey = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(
                            System.IO.Path.GetFullPath(managedRoot))))[..16];
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or System.Security.Cryptography.CryptographicException)
        {
            rootKey = "default";
        }

        _singleInstanceMutex = new System.Threading.Mutex(
            initiallyOwned: true,
            $@"Local\AutoEnvPlus.SingleInstance.{rootKey}",
            out bool createdNew);
        if (createdNew)
        {
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private static bool ActivateExistingInstance()
    {
        IntPtr found = System.IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            System.Text.StringBuilder text = new(256);
            _ = GetWindowTextW(hwnd, text, 256);
            if (IsWindowVisible(hwnd)
                && text.ToString().StartsWith("AutoEnvPlus ", StringComparison.Ordinal))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, System.IntPtr.Zero);
        if (found == System.IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(found))
        {
            _ = ShowWindow(found, 9 /* SW_RESTORE */);
        }

        _ = SetForegroundWindow(found);
        return true;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        EntryPoint = "GetWindowTextW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    internal void UpdateCurrentSettings(AutoEnvPlusApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        CurrentSettings = settings;
    }
}
