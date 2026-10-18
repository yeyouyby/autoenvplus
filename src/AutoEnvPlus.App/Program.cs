namespace AutoEnvPlus.App;

/// <summary>
/// Custom entry point. The XAML compiler's generated <c>Program.Main</c> is
/// disabled via the DISABLE_XAML_GENERATED_MAIN define so startup can install
/// a DLL search path fix and a pre-activation failure handler before the very
/// first WinRT activation inside <c>Application.Start</c>.
/// </summary>
internal static class Program
{
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string? path);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern System.IntPtr AddDllDirectory(string newDirectory);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern System.IntPtr LoadLibraryExW(string fileName, System.IntPtr hFile, uint flags);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern bool FreeLibrary(System.IntPtr module);

    /// <summary>
    /// Release diagnostics for the single-file launch failures that only
    /// reproduce on CI-built bundles: dump the startup environment and the
    /// LoadLibraryExW search behavior for the WinUI native DLL to
    /// %TEMP%\autoenvplus-startup-env.log before the first WinRT activation.
    /// The release smoke test collects every autoenvplus-*.log on failure, so
    /// this turns an opaque 0x80040111 into an attributable report. Best
    /// effort: diagnostics must never block or break startup.
    /// </summary>
    private static void WriteStartupEnvironmentDiagnostics()
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            string TryProbe(uint flags)
            {
                try
                {
                    System.IntPtr module = LoadLibraryExW("Microsoft.UI.Xaml.dll", System.IntPtr.Zero, flags);
                    if (module == System.IntPtr.Zero)
                    {
                        return $"err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
                    }

                    _ = FreeLibrary(module);
                    return "ok";
                }
                catch (System.Exception exception)
                {
                    return $"ex={exception.GetType().Name}";
                }
            }

            string report = string.Join(
                System.Environment.NewLine,
                $"time={System.DateTimeOffset.Now:O}",
                $"processPath={System.Environment.ProcessPath}",
                $"baseDirectory={baseDirectory}",
                $"xamlDllExists={System.IO.File.Exists(System.IO.Path.Combine(baseDirectory, "Microsoft.UI.Xaml.dll"))}",
                $"windowsAppRuntimeDllExists={System.IO.File.Exists(System.IO.Path.Combine(baseDirectory, "Microsoft.WindowsAppRuntime.dll"))}",
                $"modulePriExists={System.IO.File.Exists(System.IO.Path.Combine(baseDirectory, "AutoEnvPlus-win-x64.pri"))}",
                $"setDllDirectory={SetDllDirectoryW(baseDirectory)}",
                $"addDllDirectory={(long)AddDllDirectory(baseDirectory)}",
                $"loadLibraryLegacy={TryProbe(0)}",
                $"loadLibraryUserDirs={TryProbe(0x00000400)}",
                $"loadLibraryDefaultDirs={TryProbe(0x00001000)}");
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autoenvplus-startup-env.log"),
                report + System.Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never block startup.
        }
    }

    [System.STAThreadAttribute]
    private static void Main(string[] args)
    {
        try
        {
            // Windows App SDK's undocked reg-free WinRT catalog loads the WinUI
            // native DLLs by bare file name through LoadLibraryExW. In a
            // single-file bundle those DLLs live in the extraction directory,
            // which .NET 8.0.27+ single-file hosts no longer pre-load eagerly;
            // a bare-name search then misses them and the first
            // Microsoft.UI.Xaml activation fails with 0x80040111
            // (CLASS_E_CLASSNOTAVAILABLE), killing the app before a window or
            // any log is produced. Put the app base directory (the extraction
            // directory for single-file apps) on the DLL search path so the
            // catalog resolves. Both APIs are needed: SetDllDirectoryW covers
            // the legacy search order, while AddDllDirectory covers callers
            // that pass LOAD_LIBRARY_SEARCH_USER_DIRS/DEFAULT_DIRS, which
            // ignore the SetDllDirectory entry. For regular folder layouts
            // this is the app directory, which the loader already searches,
            // so both calls are harmless no-ops.
            SetDllDirectoryW(AppContext.BaseDirectory);
            // AddDllDirectory can fail with ERROR_ACCESS_DENIED on directories
            // with restrictive ACLs; SetDllDirectoryW above still covers the
            // legacy search order, so a failure here is non-fatal by design.
            _ = AddDllDirectory(AppContext.BaseDirectory);

            WriteStartupEnvironmentDiagnostics();

            ApplyPersistedInterfaceLanguage();

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (System.Exception exception)
        {
            // Failures this early escape every handler installed in App's
            // constructor, which previously left a windowless process that
            // silently exited. Reuse the startup failure path so the cause is
            // logged to %TEMP%\autoenvplus-startup-failure.log and surfaced.
            App.FailFastStartup(exception);
        }
    }

    /// <summary>
    /// Applies the persisted interface language preference before any XAML
    /// resource is resolved. Must run before Application.Start so the first
    /// page picks up the right language. Best-effort: any failure (missing
    /// managed root, unreadable settings) falls back to the system language
    /// rather than blocking the launch.
    /// </summary>
    private static void ApplyPersistedInterfaceLanguage()
    {
        try
        {
            if (!AutoEnvPlus.Core.Environment.ManagedRootResolver.TryResolve(
                    null,
                    out string? managedRoot,
                    out _)
                || string.IsNullOrEmpty(managedRoot))
            {
                return;
            }

            AutoEnvPlus.Core.Settings.AutoEnvPlusApplicationSettings settings =
                new AutoEnvPlus.Core.Settings.AutoEnvPlusApplicationSettingsStore(managedRoot)
                    .LoadAsync()
                    .GetAwaiter()
                    .GetResult();

            string? languageTag = settings.LanguagePreference switch
            {
                AutoEnvPlus.Core.Settings.LanguagePreference.ChineseSimplified => "zh-CN",
                AutoEnvPlus.Core.Settings.LanguagePreference.English => "en-US",
                _ => null,
            };

            if (languageTag is not null)
            {
                // MRT Core (the Windows App SDK resource stack used by
                // unpackaged WinUI apps) reads this per-process override;
                // the OS-level Windows.Globalization copy does not apply.
                global::Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride =
                    languageTag;
            }
        }
        catch
        {
            // Language selection never blocks startup; the system language
            // remains in effect for this session.
        }
    }
}
