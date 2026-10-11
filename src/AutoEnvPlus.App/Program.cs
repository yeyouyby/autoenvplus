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
            // catalog resolves. For regular folder layouts this is the app
            // directory, which the loader already searches, so the call is a
            // harmless no-op.
            SetDllDirectoryW(AppContext.BaseDirectory);

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
}
