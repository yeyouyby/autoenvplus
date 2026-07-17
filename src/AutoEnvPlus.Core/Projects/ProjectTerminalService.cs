using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AutoEnvPlus.Core.Networking;
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
    string ProviderId,
    RuntimeVersion ResolvedVersion,
    string ExecutablePath,
    string EnvironmentVariable);

public enum ProjectTerminalProxySource
{
    None,
    Pip,
    Npm,
    MatchingPackageTools,
    Downloads,
}

public sealed record ProjectTerminalNetworkSummary(
    bool Applied,
    ProjectTerminalProxySource ProxySource,
    bool HttpProxyConfigured,
    bool HttpsProxyConfigured,
    int NoProxyEntryCount,
    bool PipEnvironmentApplied,
    bool PipMirrorConfigured,
    bool NpmEnvironmentApplied,
    bool NpmMirrorConfigured);

public sealed record ProjectTerminalPlan(
    string ProjectRoot,
    string ManifestPath,
    string ManifestSha256,
    string NetworkSettingsPath,
    string? NetworkSettingsSha256,
    string ProviderSourcePreferencesPath,
    string? ProviderSourcePreferencesSha256,
    ProjectTerminalHost RequestedHost,
    ProjectTerminalHost EffectiveHost,
    string ShellExecutable,
    IReadOnlyList<string> ShellArguments,
    string ShimDirectory,
    IReadOnlyDictionary<string, string> EnvironmentOverrides,
    IReadOnlyList<string> EnvironmentRemovals,
    ProjectTerminalNetworkSummary NetworkSummary,
    IReadOnlyList<ProjectTerminalSelection> Selections,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanLaunch => Errors.Count == 0;
}

public sealed record RuntimeSessionTerminalPlan(
    string WorkingDirectory,
    ManagedRuntimeEntry ExpectedEntry,
    string NetworkSettingsPath,
    string? NetworkSettingsSha256,
    string ProviderSourcePreferencesPath,
    string? ProviderSourcePreferencesSha256,
    ProjectTerminalHost RequestedHost,
    ProjectTerminalHost EffectiveHost,
    string ShellExecutable,
    IReadOnlyList<string> ShellArguments,
    string ShimDirectory,
    IReadOnlyDictionary<string, string> EnvironmentOverrides,
    IReadOnlyList<string> EnvironmentRemovals,
    ProjectTerminalNetworkSummary NetworkSummary,
    ProjectTerminalSelection? Selection,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanLaunch => Errors.Count == 0 && Selection is not null;
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
            [RuntimeKind.Msvc] = ("AUTOENVPLUS_MSVC_VERSION", "cl"),
            [RuntimeKind.Llvm] = ("AUTOENVPLUS_LLVM_VERSION", "clang"),
            [RuntimeKind.Mingw] = ("AUTOENVPLUS_MINGW_VERSION", "gcc"),
            [RuntimeKind.CMake] = ("AUTOENVPLUS_CMAKE_VERSION", "cmake"),
            [RuntimeKind.Ninja] = ("AUTOENVPLUS_NINJA_VERSION", "ninja"),
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

    public async Task<RuntimeSessionTerminalPlan> CreateRuntimeSessionPlanAsync(
        ManagedRuntimeEntry expectedEntry,
        string workingDirectory,
        ProjectTerminalHost requestedHost = ProjectTerminalHost.WindowsPowerShell,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Enum.IsDefined(requestedHost))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedHost),
                requestedHost,
                "Unsupported project terminal host.");
        }

        string fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(fullWorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The terminal working directory does not exist: {fullWorkingDirectory}");
        }

        List<string> errors = [];
        List<string> warnings = [];
        List<ProjectTerminalSelection> selections = [];
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> environmentRemovals = new(StringComparer.OrdinalIgnoreCase);
        string shimDirectory = Path.Combine(_managedRoot, "shims");

        RegistryLoadResult registry = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        errors.AddRange(registry.Errors.Select(error => "Managed runtime registry: " + error));
        ManagedRuntimeEntry? currentEntry = registry.Entries.SingleOrDefault(entry =>
            entry.Id.Equals(expectedEntry.Id, StringComparison.OrdinalIgnoreCase));
        if (currentEntry is null || !EntriesEquivalent(currentEntry, expectedEntry))
        {
            errors.Add(
                "The selected managed runtime changed or was removed after it was displayed; refresh and select it again.");
        }
        else if (!SupportedKinds.TryGetValue(
                     currentEntry.Kind,
                     out (string Variable, string Command) supported))
        {
            errors.Add(
                $"{currentEntry.Kind} does not currently have an AutoEnvPlus command Shim for a new terminal session.");
        }
        else
        {
            try
            {
                ManagedPathSafety.EnsureOrdinaryFile(
                    _managedRoot,
                    currentEntry.ExecutablePath,
                    "selected managed runtime executable");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add("The selected managed runtime executable is missing or unsafe.");
            }

            if (!HasShim(shimDirectory, supported.Command))
            {
                errors.Add(
                    $"The '{supported.Command}' Shim is not installed in '{shimDirectory}'. Install PATH integration first.");
            }

            VersionSelector selector = new(VersionSelectorKind.Exact, currentEntry.Version);
            environment[supported.Variable] = currentEntry.Version.ToString();
            environment[ManagedRuntimeSessionPin.GetRuntimeIdVariableName(currentEntry.Kind)] =
                currentEntry.Id;
            environment[ManagedRuntimeSessionPin.GetProviderIdVariableName(currentEntry.Kind)] =
                currentEntry.ProviderId;
            selections.Add(new ProjectTerminalSelection(
                currentEntry.Kind,
                selector,
                currentEntry.Id,
                currentEntry.ProviderId,
                currentEntry.Version,
                currentEntry.ExecutablePath,
                supported.Variable));
        }

        ProviderSourceNetworkSettingsLoadResult network =
            await new ProviderSourceNetworkSettingsLoader(_managedRoot)
                .LoadForToolsAsync(
                    selections.Select(selection => selection.Kind switch
                    {
                        RuntimeKind.Python => NetworkToolIds.Pip,
                        RuntimeKind.NodeJs => NetworkToolIds.Npm,
                        _ => string.Empty,
                    }).Where(networkToolId => networkToolId.Length > 0),
                    cancellationToken).ConfigureAwait(false);
        errors.AddRange(network.Errors);
        ProjectTerminalNetworkSummary networkSummary = network.Success
            && network.Settings is not null
                ? ApplyNetworkEnvironment(
                    network.Settings,
                    selections,
                    environment,
                    environmentRemovals,
                    warnings,
                    errors)
                : EmptyNetworkSummary;

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
        environment["AUTOENVPLUS_SESSION_SCOPE"] = "exact-runtime";
        string shellExecutable = effectiveHost == ProjectTerminalHost.WindowsTerminal
            ? _windowsTerminalExecutable!
            : _windowsPowerShellExecutable;
        IReadOnlyList<string> shellArguments = effectiveHost == ProjectTerminalHost.WindowsTerminal
            ? CreateWindowsTerminalArguments(fullWorkingDirectory, _windowsPowerShellExecutable)
            : ["-NoLogo", "-NoExit"];

        return new RuntimeSessionTerminalPlan(
            fullWorkingDirectory,
            expectedEntry,
            network.NetworkSettingsPath,
            network.NetworkSettingsSha256,
            network.ProviderSourcePreferencesPath,
            network.ProviderSourcePreferencesSha256,
            requestedHost,
            effectiveHost,
            shellExecutable,
            shellArguments,
            shimDirectory,
            environment,
            environmentRemovals.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            networkSummary,
            selections.SingleOrDefault(),
            warnings,
            errors);
    }

    public async Task<int> LaunchRuntimeSessionAsync(
        RuntimeSessionTerminalPlan reviewedPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedPlan);
        if (!reviewedPlan.CanLaunch)
        {
            throw new InvalidOperationException(
                "A runtime session terminal plan with errors cannot be launched.");
        }

        RuntimeSessionTerminalPlan current = await CreateRuntimeSessionPlanAsync(
            reviewedPlan.ExpectedEntry,
            reviewedPlan.WorkingDirectory,
            reviewedPlan.RequestedHost,
            cancellationToken).ConfigureAwait(false);
        if (!current.CanLaunch || !RuntimeSessionPlansMatch(reviewedPlan, current))
        {
            throw new InvalidOperationException(
                "The selected runtime, Provider sources, network settings, Shims, or terminal environment changed after preview; refresh and review a new plan.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return LaunchNewConsole(
            current.WorkingDirectory,
            current.EffectiveHost,
            current.ShellExecutable,
            current.ShellArguments,
            current.EnvironmentOverrides,
            current.EnvironmentRemovals);
    }

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
        HashSet<string> environmentRemovals = new(StringComparer.OrdinalIgnoreCase);
        string shimDirectory = Path.Combine(_managedRoot, "shims");

        RegistryLoadResult registry = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        errors.AddRange(registry.Errors.Select(error => "Managed runtime registry: " + error));
        foreach ((RuntimeKind kind, VersionSelector requestedSelector) in loaded.Manifest.Tools.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupportedKinds.TryGetValue(kind, out (string Variable, string Command) supported))
            {
                warnings.Add($"{kind} does not currently have an AutoEnvPlus command Shim; its project selector is not activated in this terminal.");
                continue;
            }

            ManagedRuntimeResolutionResult resolution = ManagedRuntimeResolutionService.ResolveRegistered(
                kind,
                new RuntimeResolutionContext(Project: loaded.Manifest.ToRuntimeProfile()),
                registry.Entries,
                _architecture);
            if (!resolution.Success)
            {
                errors.AddRange(resolution.Errors);
                continue;
            }

            ManagedRuntimeEntry entry = resolution.Entry!;
            if (!File.Exists(entry.ExecutablePath))
            {
                errors.Add($"Resolved {kind} runtime '{entry.Id}' does not contain its registered executable.");
                continue;
            }

            if (!HasShim(shimDirectory, supported.Command))
            {
                errors.Add($"The '{supported.Command}' Shim is not installed in '{shimDirectory}'. Install PATH integration first.");
                continue;
            }

            string resolvedVersion = entry.Version.ToString();
            environment[supported.Variable] = resolvedVersion;
            environment[ManagedRuntimeSessionPin.GetRuntimeIdVariableName(kind)] = entry.Id;
            environment[ManagedRuntimeSessionPin.GetProviderIdVariableName(kind)] = entry.ProviderId;
            selections.Add(new ProjectTerminalSelection(
                kind,
                requestedSelector,
                entry.Id,
                entry.ProviderId,
                entry.Version,
                entry.ExecutablePath,
                supported.Variable));
        }

        ProviderSourceNetworkSettingsLoadResult network =
            await new ProviderSourceNetworkSettingsLoader(_managedRoot)
                .LoadForToolsAsync(
                    selections.Select(selection => selection.Kind switch
                    {
                        RuntimeKind.Python => NetworkToolIds.Pip,
                        RuntimeKind.NodeJs => NetworkToolIds.Npm,
                        _ => string.Empty,
                    }).Where(networkToolId => networkToolId.Length > 0),
                    cancellationToken).ConfigureAwait(false);
        errors.AddRange(network.Errors);
        ProjectTerminalNetworkSummary networkSummary = network.Success
            && network.Settings is not null
                ? ApplyNetworkEnvironment(
                    network.Settings,
                    selections,
                    environment,
                    environmentRemovals,
                    warnings,
                    errors)
                : EmptyNetworkSummary;

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
            network.NetworkSettingsPath,
            network.NetworkSettingsSha256,
            network.ProviderSourcePreferencesPath,
            network.ProviderSourcePreferencesSha256,
            requestedHost,
            effectiveHost,
            shellExecutable,
            shellArguments,
            shimDirectory,
            environment,
            environmentRemovals.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            networkSummary,
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
                "The project manifest, network settings, Provider sources, managed runtimes, Shims, or terminal environment changed after preview; refresh and review a new plan.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return LaunchNewConsole(current);
    }

    private static bool PlansMatch(ProjectTerminalPlan reviewed, ProjectTerminalPlan current) =>
        reviewed.ProjectRoot.Equals(current.ProjectRoot, StringComparison.OrdinalIgnoreCase)
        && reviewed.ManifestPath.Equals(current.ManifestPath, StringComparison.OrdinalIgnoreCase)
        && reviewed.ManifestSha256.Equals(current.ManifestSha256, StringComparison.Ordinal)
        && reviewed.NetworkSettingsPath.Equals(
            current.NetworkSettingsPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            reviewed.NetworkSettingsSha256,
            current.NetworkSettingsSha256,
            StringComparison.Ordinal)
        && reviewed.ProviderSourcePreferencesPath.Equals(
            current.ProviderSourcePreferencesPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            reviewed.ProviderSourcePreferencesSha256,
            current.ProviderSourcePreferencesSha256,
            StringComparison.Ordinal)
        && reviewed.RequestedHost == current.RequestedHost
        && reviewed.EffectiveHost == current.EffectiveHost
        && reviewed.ShellExecutable.Equals(current.ShellExecutable, StringComparison.OrdinalIgnoreCase)
        && reviewed.ShellArguments.SequenceEqual(current.ShellArguments, StringComparer.Ordinal)
        && reviewed.ShimDirectory.Equals(current.ShimDirectory, StringComparison.OrdinalIgnoreCase)
        && DictionariesEqual(reviewed.EnvironmentOverrides, current.EnvironmentOverrides)
        && reviewed.EnvironmentRemovals.SequenceEqual(
            current.EnvironmentRemovals,
            StringComparer.OrdinalIgnoreCase)
        && reviewed.NetworkSummary == current.NetworkSummary
        && reviewed.Selections.SequenceEqual(current.Selections)
        && reviewed.Warnings.SequenceEqual(current.Warnings, StringComparer.Ordinal);

    private static bool RuntimeSessionPlansMatch(
        RuntimeSessionTerminalPlan reviewed,
        RuntimeSessionTerminalPlan current) =>
        reviewed.WorkingDirectory.Equals(
            current.WorkingDirectory,
            StringComparison.OrdinalIgnoreCase)
        && EntriesEquivalent(reviewed.ExpectedEntry, current.ExpectedEntry)
        && reviewed.NetworkSettingsPath.Equals(
            current.NetworkSettingsPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            reviewed.NetworkSettingsSha256,
            current.NetworkSettingsSha256,
            StringComparison.Ordinal)
        && reviewed.ProviderSourcePreferencesPath.Equals(
            current.ProviderSourcePreferencesPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            reviewed.ProviderSourcePreferencesSha256,
            current.ProviderSourcePreferencesSha256,
            StringComparison.Ordinal)
        && reviewed.RequestedHost == current.RequestedHost
        && reviewed.EffectiveHost == current.EffectiveHost
        && reviewed.ShellExecutable.Equals(
            current.ShellExecutable,
            StringComparison.OrdinalIgnoreCase)
        && reviewed.ShellArguments.SequenceEqual(current.ShellArguments, StringComparer.Ordinal)
        && reviewed.ShimDirectory.Equals(current.ShimDirectory, StringComparison.OrdinalIgnoreCase)
        && DictionariesEqual(reviewed.EnvironmentOverrides, current.EnvironmentOverrides)
        && reviewed.EnvironmentRemovals.SequenceEqual(
            current.EnvironmentRemovals,
            StringComparer.OrdinalIgnoreCase)
        && reviewed.NetworkSummary == current.NetworkSummary
        && reviewed.Selection == current.Selection
        && reviewed.Warnings.SequenceEqual(current.Warnings, StringComparer.Ordinal);

    private static bool EntriesEquivalent(
        ManagedRuntimeEntry left,
        ManagedRuntimeEntry right) =>
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)
        && left.ProviderId.Equals(right.ProviderId, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && left.Version == right.Version
        && left.Architecture == right.Architecture
        && Path.GetFullPath(left.InstallRoot).Equals(
            Path.GetFullPath(right.InstallRoot),
            StringComparison.OrdinalIgnoreCase)
        && left.ExecutableRelativePath.Equals(
            right.ExecutableRelativePath,
            StringComparison.OrdinalIgnoreCase)
        && left.PackageHashAlgorithm == right.PackageHashAlgorithm
        && left.PackageHash.Equals(right.PackageHash, StringComparison.OrdinalIgnoreCase)
        && left.InstalledAtUtc == right.InstalledAtUtc
        && (left.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            (right.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out string? value)
            && value.Equals(pair.Value, StringComparison.Ordinal));

    private static bool HasShim(string shimDirectory, string command) =>
        File.Exists(Path.Combine(shimDirectory, command + ".exe"))
        || File.Exists(Path.Combine(shimDirectory, command + ".cmd"));

    private static readonly ProjectTerminalNetworkSummary EmptyNetworkSummary = new(
        false,
        ProjectTerminalProxySource.None,
        false,
        false,
        0,
        false,
        false,
        false,
        false);

    private static ProjectTerminalNetworkSummary ApplyNetworkEnvironment(
        NetworkSettings settings,
        IReadOnlyList<ProjectTerminalSelection> selections,
        IDictionary<string, string> environment,
        ISet<string> removals,
        ICollection<string> warnings,
        ICollection<string> errors)
    {
        bool hasPython = selections.Any(selection => selection.Kind == RuntimeKind.Python);
        bool hasNode = selections.Any(selection => selection.Kind == RuntimeKind.NodeJs);
        if (!hasPython && !hasNode)
        {
            return EmptyNetworkSummary;
        }

        EffectiveNetworkSettings? pip = hasPython
            ? ResolveNetworkSettings(settings, NetworkToolIds.Pip, errors)
            : null;
        EffectiveNetworkSettings? npm = hasNode
            ? ResolveNetworkSettings(settings, NetworkToolIds.Npm, errors)
            : null;
        if ((hasPython && pip is null) || (hasNode && npm is null))
        {
            return EmptyNetworkSummary;
        }

        EffectiveNetworkSettings proxySettings;
        ProjectTerminalProxySource proxySource;
        if (pip is not null && npm is null)
        {
            proxySettings = pip;
            proxySource = ProjectTerminalProxySource.Pip;
        }
        else if (npm is not null && pip is null)
        {
            proxySettings = npm;
            proxySource = ProjectTerminalProxySource.Npm;
        }
        else if (ProxySettingsEqual(pip!, npm!))
        {
            proxySettings = pip!;
            proxySource = ProjectTerminalProxySource.MatchingPackageTools;
        }
        else
        {
            EffectiveNetworkSettings? downloads = ResolveNetworkSettings(
                settings,
                NetworkToolIds.Downloads,
                errors);
            if (downloads is null)
            {
                return EmptyNetworkSummary;
            }

            proxySettings = downloads;
            proxySource = ProjectTerminalProxySource.Downloads;
            warnings.Add(
                "pip and npm proxy settings differ. Because a terminal cannot assign different HTTP_PROXY values per command, the shared proxy uses the downloads scope; package mirrors remain tool-specific.");
        }

        SetOrRemoveEnvironmentVariable(
            environment,
            removals,
            "HTTP_PROXY",
            proxySettings.HttpProxy?.AbsoluteUri);
        SetOrRemoveEnvironmentVariable(
            environment,
            removals,
            "http_proxy",
            proxySettings.HttpProxy?.AbsoluteUri);
        SetOrRemoveEnvironmentVariable(
            environment,
            removals,
            "HTTPS_PROXY",
            proxySettings.HttpsProxy?.AbsoluteUri);
        SetOrRemoveEnvironmentVariable(
            environment,
            removals,
            "https_proxy",
            proxySettings.HttpsProxy?.AbsoluteUri);
        SetOrRemoveEnvironmentVariable(
            environment,
            removals,
            "NO_PROXY",
            proxySettings.NoProxy.Count == 0
                ? null
                : string.Join(',', proxySettings.NoProxy));
        SetOrRemoveEnvironmentVariable(
            environment,
            removals,
            "no_proxy",
            proxySettings.NoProxy.Count == 0
                ? null
                : string.Join(',', proxySettings.NoProxy));
        SetOrRemoveEnvironmentVariable(environment, removals, "ALL_PROXY", null);
        SetOrRemoveEnvironmentVariable(environment, removals, "all_proxy", null);

        if (pip is not null)
        {
            SetOrRemoveEnvironmentVariable(
                environment,
                removals,
                "PIP_INDEX_URL",
                pip.Mirror?.AbsoluteUri);
        }

        if (npm is not null)
        {
            SetOrRemoveEnvironmentVariable(
                environment,
                removals,
                "NPM_CONFIG_REGISTRY",
                npm.Mirror?.AbsoluteUri);
        }

        if (pip is not null
            && npm is not null
            && pip.Mirror is not null
            && npm.Mirror is not null
            && pip.Mirror.AbsoluteUri.Equals(npm.Mirror.AbsoluteUri, StringComparison.Ordinal)
            && ToolInheritsGlobalMirror(settings, NetworkToolIds.Pip)
            && ToolInheritsGlobalMirror(settings, NetworkToolIds.Npm))
        {
            warnings.Add(
                "pip and npm inherit the same global mirror endpoint. Verify that the endpoint supports both package protocols, or configure separate tool mirrors.");
        }

        return new ProjectTerminalNetworkSummary(
            true,
            proxySource,
            proxySettings.HttpProxy is not null,
            proxySettings.HttpsProxy is not null,
            proxySettings.NoProxy.Count,
            pip is not null,
            pip?.Mirror is not null,
            npm is not null,
            npm?.Mirror is not null);
    }

    private static bool ToolInheritsGlobalMirror(NetworkSettings settings, string toolId)
    {
        if (string.IsNullOrWhiteSpace(settings.Global?.Mirror))
        {
            return false;
        }

        return settings.Tools is null
            || !settings.Tools.TryGetValue(toolId, out ToolNetworkSettings? tool)
            || tool?.Mirror is null
            || tool.Mirror.Mode == NetworkEndpointOverrideMode.Inherit;
    }

    private static EffectiveNetworkSettings? ResolveNetworkSettings(
        NetworkSettings settings,
        string toolId,
        ICollection<string> errors)
    {
        NetworkSettingsResolutionResult resolution = NetworkSettingsResolver.Resolve(settings, toolId);
        if (resolution.Success && resolution.EffectiveSettings is not null)
        {
            return resolution.EffectiveSettings;
        }

        foreach (NetworkSettingsError error in resolution.Errors)
        {
            errors.Add($"Network settings ({error.Path}): {error.Message}");
        }

        return null;
    }

    private static bool ProxySettingsEqual(
        EffectiveNetworkSettings left,
        EffectiveNetworkSettings right) =>
        UriEquals(left.HttpProxy, right.HttpProxy)
        && UriEquals(left.HttpsProxy, right.HttpsProxy)
        && left.NoProxy.SequenceEqual(right.NoProxy, StringComparer.OrdinalIgnoreCase);

    private static bool UriEquals(Uri? left, Uri? right) => string.Equals(
        left?.AbsoluteUri,
        right?.AbsoluteUri,
        StringComparison.Ordinal);

    private static void SetOrRemoveEnvironmentVariable(
        IDictionary<string, string> environment,
        ISet<string> removals,
        string name,
        string? value)
    {
        if (value is null)
        {
            environment.Remove(name);
            removals.Add(name);
            return;
        }

        removals.Remove(name);
        environment[name] = value;
    }

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

    private static int LaunchNewConsole(ProjectTerminalPlan plan) => LaunchNewConsole(
        plan.ProjectRoot,
        plan.EffectiveHost,
        plan.ShellExecutable,
        plan.ShellArguments,
        plan.EnvironmentOverrides,
        plan.EnvironmentRemovals);

    private static int LaunchNewConsole(
        string workingDirectory,
        ProjectTerminalHost effectiveHost,
        string shellExecutable,
        IReadOnlyList<string> shellArguments,
        IReadOnlyDictionary<string, string> environmentOverrides,
        IReadOnlyList<string> environmentRemovals)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Project terminal launch is only supported on Windows.");
        }

        const uint CreateNewConsole = 0x00000010;
        const uint CreateUnicodeEnvironment = 0x00000400;
        uint creationFlags = CreateUnicodeEnvironment;
        if (effectiveHost == ProjectTerminalHost.WindowsPowerShell)
        {
            creationFlags |= CreateNewConsole;
        }

        string commandLine = string.Join(
            ' ',
            new[] { QuoteWindowsArgument(shellExecutable) }
                .Concat(shellArguments.Select(QuoteWindowsArgument)));
        StringBuilder mutableCommandLine = new(commandLine);
        IntPtr environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(
            environmentOverrides,
            environmentRemovals));
        StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>() };
        try
        {
            if (!CreateProcess(
                shellExecutable,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                environmentBlock,
                workingDirectory,
                ref startup,
                out ProcessInformation process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start the AutoEnvPlus terminal.");
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

    private static string BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyList<string> removals)
    {
        SortedDictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        foreach (string key in removals)
        {
            ValidateEnvironmentName(key);
            environment.Remove(key);
        }

        foreach ((string key, string value) in overrides)
        {
            ValidateEnvironmentName(key);
            if (value.Contains('\0'))
            {
                throw new InvalidOperationException(
                    "The project terminal environment contains an invalid entry.");
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

    private static void ValidateEnvironmentName(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Contains('\0') || key.Contains('='))
        {
            throw new InvalidOperationException(
                "The project terminal environment contains an invalid entry.");
        }
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
