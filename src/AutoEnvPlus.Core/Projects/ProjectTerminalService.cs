using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Projects;

public enum ProjectTerminalHost
{
    WindowsPowerShell,
    WindowsTerminal,
}

public sealed record ProjectTerminalSelection(
    RuntimeKind Kind,
    VersionSelector RequestedSelector,
    string RuntimeId,
    RuntimeVersion ResolvedVersion,
    string ExecutablePath,
    string EnvironmentVariable);

public sealed record ProjectTerminalPlan(
    string ProjectRoot,
    string ManifestPath,
    string ManifestSha256,
    ProjectTerminalHost RequestedHost,
    ProjectTerminalHost EffectiveHost,
    string ShellExecutable,
    IReadOnlyList<string> ShellArguments,
    string ShimDirectory,
    IReadOnlyDictionary<string, string> EnvironmentOverrides,
    IReadOnlyList<ProjectTerminalSelection> Selections,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanLaunch => Errors.Count == 0;
}

public sealed class ProjectTerminalService
{
    private static readonly IReadOnlyDictionary<RuntimeKind, (string Variable, string Command)> SupportedKinds =
        new Dictionary<RuntimeKind, (string Variable, string Command)>
        {
            [RuntimeKind.Python] = ("AUTOENVPLUS_PYTHON_VERSION", "python"),
            [RuntimeKind.NodeJs] = ("AUTOENVPLUS_NODE_VERSION", "node"),
            [RuntimeKind.Java] = ("AUTOENVPLUS_JAVA_VERSION", "java"),
            [RuntimeKind.DotNet] = ("AUTOENVPLUS_DOTNET_VERSION", "dotnet"),
        };

    private readonly string _managedRoot;
    private readonly IManagedRuntimeRegistryStore _registry;
    private readonly RuntimeArchitecture _architecture;
    private readonly string _windowsPowerShellExecutable;
    private readonly string? _windowsTerminalExecutable;

    public ProjectTerminalService(
        string managedRoot,
        IManagedRuntimeRegistryStore? registry = null,
        RuntimeArchitecture? architecture = null,
        string? shellExecutable = null,
        string? windowsTerminalExecutable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _registry = registry ?? new ManagedRuntimeRegistry(_managedRoot);
        _architecture = architecture ?? CurrentArchitecture();
        _windowsPowerShellExecutable = Path.GetFullPath(
            shellExecutable ?? GetWindowsPowerShellPath());
        if (!Path.GetFileName(_windowsPowerShellExecutable)
            .Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Windows PowerShell executable must be named 'powershell.exe'.",
                nameof(shellExecutable));
        }

        _windowsTerminalExecutable = ResolveWindowsTerminalPath(windowsTerminalExecutable);
    }

    public bool IsHostAvailable(ProjectTerminalHost host) => host switch
    {
        ProjectTerminalHost.WindowsPowerShell => File.Exists(_windowsPowerShellExecutable),
        ProjectTerminalHost.WindowsTerminal => _windowsTerminalExecutable is not null
            && File.Exists(_windowsTerminalExecutable),
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unsupported project terminal host."),
    };

    public Task<ProjectTerminalPlan> CreatePlanAsync(
        string startPath,
        CancellationToken cancellationToken = default) =>
        CreatePlanAsync(startPath, ProjectTerminalHost.WindowsPowerShell, cancellationToken);

    public async Task<ProjectTerminalPlan> CreatePlanAsync(
        string startPath,
        ProjectTerminalHost requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        if (!Enum.IsDefined(requestedHost))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedHost),
                requestedHost,
                "Unsupported project terminal host.");
        }

        string fullStartPath = Path.GetFullPath(startPath);
        string? manifestPath = new ProjectManifestService().FindManifest(fullStartPath);
        if (manifestPath is null)
        {
            throw new FileNotFoundException(
                $"No {ProjectManifestService.ManifestFileName} was found from '{fullStartPath}'.");
        }

        ProjectManifestLoadResult loaded = new ProjectManifestService().Load(manifestPath);
        string projectRoot = loaded.Manifest.ProjectRoot;
        List<string> errors = loaded.Errors
            .Select(error => $"line {error.LineNumber}: {error.Message}")
            .ToList();
        List<string> warnings = [];
        List<ProjectTerminalSelection> selections = [];
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        string shimDirectory = Path.Combine(_managedRoot, "shims");

        RegistryLoadResult registry = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        errors.AddRange(registry.Errors.Select(error => "Managed runtime registry: " + error));
        RuntimeInstallation[] installations = registry.Entries
            .Select(entry => entry.ToRuntimeInstallation())
            .ToArray();

        foreach ((RuntimeKind kind, VersionSelector requestedSelector) in loaded.Manifest.Tools.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupportedKinds.TryGetValue(kind, out (string Variable, string Command) supported))
            {
                warnings.Add($"{kind} does not currently have an AutoEnvPlus command Shim; its project selector is not activated in this terminal.");
                continue;
            }

            RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
                kind,
                new RuntimeResolutionContext(Project: loaded.Manifest.ToRuntimeProfile()),
                installations,
                _architecture);
            if (!resolution.Success)
            {
                errors.Add(resolution.Error!);
                continue;
            }

            ManagedRuntimeEntry? entry = registry.Entries.FirstOrDefault(candidate =>
                candidate.Id.Equals(resolution.Installation!.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !File.Exists(entry.ExecutablePath))
            {
                errors.Add($"Resolved {kind} runtime '{resolution.Installation!.Id}' does not contain its registered executable.");
                continue;
            }

            if (!HasShim(shimDirectory, supported.Command))
            {
                errors.Add($"The '{supported.Command}' Shim is not installed in '{shimDirectory}'. Install PATH integration first.");
                continue;
            }

            string resolvedVersion = entry.Version.ToString();
            environment[supported.Variable] = resolvedVersion;
            selections.Add(new ProjectTerminalSelection(
                kind,
                requestedSelector,
                entry.Id,
                entry.Version,
                entry.ExecutablePath,
                supported.Variable));
        }

        ProjectTerminalHost effectiveHost = requestedHost;
        if (requestedHost == ProjectTerminalHost.WindowsTerminal
            && !IsHostAvailable(ProjectTerminalHost.WindowsTerminal))
        {
            effectiveHost = ProjectTerminalHost.WindowsPowerShell;
            warnings.Add("Windows Terminal (wt.exe) is unavailable; this plan will fall back to Windows PowerShell.");
        }

        if (!File.Exists(_windowsPowerShellExecutable))
        {
            string role = effectiveHost == ProjectTerminalHost.WindowsTerminal
                ? "Windows PowerShell child shell"
                : "Windows PowerShell";
            errors.Add($"{role} was not found at '{_windowsPowerShellExecutable}'.");
        }

        environment["PATH"] = PrependPath(
            System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            shimDirectory);
        environment["AUTOENVPLUS_PROJECT_ROOT"] = projectRoot;
        environment["AUTOENVPLUS_PROJECT_MANIFEST"] = manifestPath;

        string shellExecutable = effectiveHost == ProjectTerminalHost.WindowsTerminal
            ? _windowsTerminalExecutable!
            : _windowsPowerShellExecutable;
        IReadOnlyList<string> shellArguments = effectiveHost == ProjectTerminalHost.WindowsTerminal
            ? CreateWindowsTerminalArguments(projectRoot, _windowsPowerShellExecutable)
            : ["-NoLogo", "-NoExit"];

        return new ProjectTerminalPlan(
            projectRoot,
            manifestPath,
            await ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
            requestedHost,
            effectiveHost,
            shellExecutable,
            shellArguments,
            shimDirectory,
            environment,
            selections,
            warnings,
            errors);
    }

    public async Task<int> LaunchAsync(
        ProjectTerminalPlan reviewedPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedPlan);
        if (!reviewedPlan.CanLaunch)
        {
            throw new InvalidOperationException("A project terminal plan with errors cannot be launched.");
        }

        ProjectTerminalPlan current = await CreatePlanAsync(
            reviewedPlan.ProjectRoot,
            reviewedPlan.RequestedHost,
            cancellationToken).ConfigureAwait(false);
        if (!current.CanLaunch || !PlansMatch(reviewedPlan, current))
        {
            throw new InvalidOperationException(
                "The project manifest, managed runtimes, Shims, or terminal environment changed after preview; refresh and review a new plan.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return LaunchNewConsole(current);
    }

    private static bool PlansMatch(ProjectTerminalPlan reviewed, ProjectTerminalPlan current) =>
        reviewed.ProjectRoot.Equals(current.ProjectRoot, StringComparison.OrdinalIgnoreCase)
        && reviewed.ManifestPath.Equals(current.ManifestPath, StringComparison.OrdinalIgnoreCase)
        && reviewed.ManifestSha256.Equals(current.ManifestSha256, StringComparison.Ordinal)
        && reviewed.RequestedHost == current.RequestedHost
        && reviewed.EffectiveHost == current.EffectiveHost
        && reviewed.ShellExecutable.Equals(current.ShellExecutable, StringComparison.OrdinalIgnoreCase)
        && reviewed.ShellArguments.SequenceEqual(current.ShellArguments, StringComparer.Ordinal)
        && reviewed.ShimDirectory.Equals(current.ShimDirectory, StringComparison.OrdinalIgnoreCase)
        && DictionariesEqual(reviewed.EnvironmentOverrides, current.EnvironmentOverrides)
        && reviewed.Selections.SequenceEqual(current.Selections);

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out string? value)
            && value.Equals(pair.Value, StringComparison.Ordinal));

    private static bool HasShim(string shimDirectory, string command) =>
        File.Exists(Path.Combine(shimDirectory, command + ".exe"))
        || File.Exists(Path.Combine(shimDirectory, command + ".cmd"));

    private static string PrependPath(string currentPath, string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        IEnumerable<string> remaining = currentPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !PathsEqual(entry, fullDirectory));
        return string.Join(';', new[] { fullDirectory }.Concat(remaining));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(left.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string GetWindowsPowerShellPath() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private static IReadOnlyList<string> CreateWindowsTerminalArguments(
        string projectRoot,
        string windowsPowerShellExecutable) =>
        [
            "new-tab",
            "--startingDirectory",
            projectRoot,
            windowsPowerShellExecutable,
            "-NoLogo",
            "-NoExit",
        ];

    private static string? ResolveWindowsTerminalPath(string? configuredPath)
    {
        if (configuredPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
            string fullPath = Path.GetFullPath(configuredPath);
            if (!Path.GetFileName(fullPath).Equals("wt.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The Windows Terminal executable must be named 'wt.exe'.",
                    nameof(configuredPath));
            }

            return fullPath;
        }

        string windowsAppsPath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");
        if (File.Exists(windowsAppsPath))
        {
            return windowsAppsPath;
        }

        foreach (string pathEntry in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(
                    System.Environment.ExpandEnvironmentVariables(pathEntry.Trim('"')),
                    "wt.exe"));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue deterministic discovery.
            }
        }

        return null;
    }

    private static RuntimeArchitecture CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => RuntimeArchitecture.X86,
        Architecture.Arm64 => RuntimeArchitecture.Arm64,
        _ => RuntimeArchitecture.X64,
    };

    private static int LaunchNewConsole(ProjectTerminalPlan plan)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Project terminal launch is only supported on Windows.");
        }

        const uint CreateNewConsole = 0x00000010;
        const uint CreateUnicodeEnvironment = 0x00000400;
        uint creationFlags = CreateUnicodeEnvironment;
        if (plan.EffectiveHost == ProjectTerminalHost.WindowsPowerShell)
        {
            creationFlags |= CreateNewConsole;
        }

        string commandLine = string.Join(
            ' ',
            new[] { QuoteWindowsArgument(plan.ShellExecutable) }
                .Concat(plan.ShellArguments.Select(QuoteWindowsArgument)));
        StringBuilder mutableCommandLine = new(commandLine);
        IntPtr environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(plan.EnvironmentOverrides));
        StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>() };
        try
        {
            if (!CreateProcess(
                plan.ShellExecutable,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                environmentBlock,
                plan.ProjectRoot,
                ref startup,
                out ProcessInformation process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start the project terminal.");
            }

            try
            {
                return checked((int)process.ProcessId);
            }
            finally
            {
                CloseHandle(process.ThreadHandle);
                CloseHandle(process.ProcessHandle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        SortedDictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        foreach ((string key, string value) in overrides)
        {
            if (key.Contains('\0') || value.Contains('\0') || key.Contains('='))
            {
                throw new InvalidOperationException("The project terminal environment contains an invalid entry.");
            }

            environment[key] = value;
        }

        StringBuilder block = new();
        foreach ((string key, string value) in environment)
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        return block.ToString();
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0
            && !value.Any(character => character is ' ' or '\t' or '"'))
        {
            return value;
        }

        StringBuilder quoted = new(value.Length + 2);
        quoted.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes);
            quoted.Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal int Flags;
        internal short ShowWindow;
        internal short Reserved2;
        internal IntPtr ReservedPointer;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr ProcessHandle;
        internal IntPtr ThreadHandle;
        internal uint ProcessId;
        internal uint ThreadId;
    }
}
