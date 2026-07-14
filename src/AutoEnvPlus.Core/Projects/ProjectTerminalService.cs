using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Projects;

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
        };

    private readonly string _managedRoot;
    private readonly IManagedRuntimeRegistryStore _registry;
    private readonly RuntimeArchitecture _architecture;
    private readonly string _shellExecutable;

    public ProjectTerminalService(
        string managedRoot,
        IManagedRuntimeRegistryStore? registry = null,
        RuntimeArchitecture? architecture = null,
        string? shellExecutable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _registry = registry ?? new ManagedRuntimeRegistry(_managedRoot);
        _architecture = architecture ?? CurrentArchitecture();
        _shellExecutable = Path.GetFullPath(shellExecutable ?? GetWindowsPowerShellPath());
    }

    public async Task<ProjectTerminalPlan> CreatePlanAsync(
        string startPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
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

        if (!File.Exists(_shellExecutable))
        {
            errors.Add($"Windows PowerShell was not found at '{_shellExecutable}'.");
        }

        environment["PATH"] = PrependPath(
            System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            shimDirectory);
        environment["AUTOENVPLUS_PROJECT_ROOT"] = projectRoot;
        environment["AUTOENVPLUS_PROJECT_MANIFEST"] = manifestPath;

        return new ProjectTerminalPlan(
            projectRoot,
            manifestPath,
            await ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
            _shellExecutable,
            ["-NoLogo", "-NoExit"],
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
                CreateNewConsole | CreateUnicodeEnvironment,
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

    private static string QuoteWindowsArgument(string value) =>
        value.Contains(' ') || value.Contains('\t') || value.Contains('"')
            ? '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : value;

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
