using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProjectTerminalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-ProjectTerminal-{Guid.NewGuid():N}");
    private readonly string _managedRoot;
    private readonly string _projectRoot;
    private readonly string _shellExecutable;
    private readonly List<ManagedRuntimeEntry> _entries = [];

    public ProjectTerminalServiceTests()
    {
        _managedRoot = Directory.CreateDirectory(Path.Combine(_root, "managed")).FullName;
        _projectRoot = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        _shellExecutable = Path.Combine(_root, "powershell.exe");
        File.WriteAllText(_shellExecutable, string.Empty);
        string shims = Directory.CreateDirectory(Path.Combine(_managedRoot, "shims")).FullName;
        foreach (string command in new[] { "python", "node", "java", "dotnet" })
        {
            File.WriteAllText(Path.Combine(shims, command + ".exe"), string.Empty);
        }

        _entries.Add(CreateRuntime("python-3.12.1", RuntimeKind.Python, "3.12.1", "python.exe"));
        _entries.Add(CreateRuntime("python-3.12.8", RuntimeKind.Python, "3.12.8", "python.exe"));
        _entries.Add(CreateRuntime("node-22.17.0", RuntimeKind.NodeJs, "22.17.0", "node.exe", ["lts"]));
        _entries.Add(CreateRuntime("java-21.0.8", RuntimeKind.Java, "21.0.8", "bin/java.exe", ["lts"]));
        _entries.Add(CreateRuntime("dotnet-10.0.200", RuntimeKind.DotNet, "10.0.200", "dotnet.exe"));
        WriteManifest(
            """
            [tools]
            python = "3.12"
            node = "22-lts"
            java = "21"
            dotnet = "10.0.200"
            """);
    }

    [Fact]
    public async Task CreatePlanAsync_ResolvesExactManagedVersionsWithoutMutatingParentEnvironment()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_projectRoot, "src", "app")).FullName;
        string parentPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(nested);

        Assert.True(plan.CanLaunch);
        Assert.Equal(_projectRoot, plan.ProjectRoot);
        Assert.Equal(4, plan.Selections.Count);
        Assert.Equal("3.12.8", plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_VERSION"]);
        Assert.Equal("22.17.0", plan.EnvironmentOverrides["AUTOENVPLUS_NODE_VERSION"]);
        Assert.Equal("21.0.8", plan.EnvironmentOverrides["AUTOENVPLUS_JAVA_VERSION"]);
        Assert.Equal("10.0.200", plan.EnvironmentOverrides["AUTOENVPLUS_DOTNET_VERSION"]);
        Assert.Equal("python-3.12.8", plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_RUNTIME_ID"]);
        Assert.Equal("node-22.17.0", plan.EnvironmentOverrides["AUTOENVPLUS_NODE_RUNTIME_ID"]);
        Assert.Equal("java-21.0.8", plan.EnvironmentOverrides["AUTOENVPLUS_JAVA_RUNTIME_ID"]);
        Assert.Equal("dotnet-10.0.200", plan.EnvironmentOverrides["AUTOENVPLUS_DOTNET_RUNTIME_ID"]);
        Assert.Equal(
            "test-provider",
            plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID"]);
        Assert.Equal(_projectRoot, plan.EnvironmentOverrides["AUTOENVPLUS_PROJECT_ROOT"]);
        Assert.Equal(
            Path.Combine(_managedRoot, "shims"),
            plan.EnvironmentOverrides["PATH"].Split(';')[0]);
        Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("DotNet", StringComparison.Ordinal));
        Assert.Equal(parentPath, System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

        ProjectTerminalSelection python = Assert.Single(
            plan.Selections,
            selection => selection.Kind == RuntimeKind.Python);
        Assert.Equal(VersionSelector.Parse("3.12"), python.RequestedSelector);
        Assert.Equal(RuntimeVersion.Parse("3.12.8"), python.ResolvedVersion);
        Assert.Equal("test-provider", python.ProviderId);
    }

    [Fact]
    public async Task CreateRuntimeSessionPlanAsync_PinsOneExactRuntimeWithoutChangingParentEnvironment()
    {
        ManagedRuntimeEntry selected = _entries.Single(entry => entry.Id == "python-3.12.8");
        string parentPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        RuntimeSessionTerminalPlan plan = await CreateService().CreateRuntimeSessionPlanAsync(
            selected,
            _projectRoot);

        Assert.True(plan.CanLaunch);
        Assert.Equal(_projectRoot, plan.WorkingDirectory);
        Assert.Equal(selected.Id, plan.Selection!.RuntimeId);
        Assert.Equal(selected.ProviderId, plan.Selection.ProviderId);
        Assert.Equal("3.12.8", plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_VERSION"]);
        Assert.Equal(selected.Id, plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_RUNTIME_ID"]);
        Assert.Equal(
            selected.ProviderId,
            plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID"]);
        Assert.Equal("exact-runtime", plan.EnvironmentOverrides["AUTOENVPLUS_SESSION_SCOPE"]);
        Assert.Equal(
            Path.Combine(_managedRoot, "shims"),
            plan.EnvironmentOverrides["PATH"].Split(';')[0]);
        Assert.Equal(parentPath, System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    }

    [Fact]
    public async Task CreateRuntimeSessionPlanAsync_UsesWindowsTerminalWhenAvailable()
    {
        string windowsTerminalExecutable = Path.Combine(_root, "wt.exe");
        File.WriteAllText(windowsTerminalExecutable, string.Empty);
        ManagedRuntimeEntry selected = _entries.Single(entry => entry.Id == "node-22.17.0");

        RuntimeSessionTerminalPlan plan = await CreateService(windowsTerminalExecutable)
            .CreateRuntimeSessionPlanAsync(
                selected,
                _projectRoot,
                ProjectTerminalHost.WindowsTerminal);

        Assert.True(plan.CanLaunch);
        Assert.Equal(ProjectTerminalHost.WindowsTerminal, plan.EffectiveHost);
        Assert.Equal(windowsTerminalExecutable, plan.ShellExecutable);
        Assert.Equal(
            [
                "new-tab",
                "--startingDirectory",
                _projectRoot,
                _shellExecutable,
                "-NoLogo",
                "-NoExit",
            ],
            plan.ShellArguments);
    }

    [Fact]
    public async Task CreateRuntimeSessionPlanAsync_SupportsManagedBuildToolShim()
    {
        ManagedRuntimeEntry cmake = CreateRuntime(
            "cmake-4.1.0",
            RuntimeKind.CMake,
            "4.1.0",
            "cmake.exe");
        _entries.Add(cmake);
        File.WriteAllText(
            Path.Combine(_managedRoot, "shims", "cmake.exe"),
            string.Empty);

        RuntimeSessionTerminalPlan plan = await CreateService().CreateRuntimeSessionPlanAsync(
            cmake,
            _projectRoot);

        Assert.True(plan.CanLaunch);
        Assert.Equal(cmake.Id, plan.Selection!.RuntimeId);
        Assert.Equal("4.1.0", plan.EnvironmentOverrides["AUTOENVPLUS_CMAKE_VERSION"]);
        Assert.Equal(cmake.Id, plan.EnvironmentOverrides["AUTOENVPLUS_CMAKE_RUNTIME_ID"]);
    }

    [Fact]
    public async Task LaunchRuntimeSessionAsync_RejectsRuntimeChangedAfterPreview()
    {
        ManagedRuntimeEntry selected = _entries.Single(entry => entry.Id == "python-3.12.8");
        ProjectTerminalService service = CreateService();
        RuntimeSessionTerminalPlan plan = await service.CreateRuntimeSessionPlanAsync(
            selected,
            _projectRoot);
        int index = _entries.IndexOf(selected);
        _entries[index] = selected with { ProviderId = "plugin:replacement" };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchRuntimeSessionAsync(plan));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_UsesFixedWindowsTerminalCommandWhenAvailable()
    {
        string windowsTerminalExecutable = Path.Combine(_root, "wt.exe");
        File.WriteAllText(windowsTerminalExecutable, string.Empty);
        ProjectTerminalService service = CreateService(windowsTerminalExecutable);

        ProjectTerminalPlan plan = await service.CreatePlanAsync(
            _projectRoot,
            ProjectTerminalHost.WindowsTerminal);

        Assert.True(service.IsHostAvailable(ProjectTerminalHost.WindowsTerminal));
        Assert.True(plan.CanLaunch);
        Assert.Equal(ProjectTerminalHost.WindowsTerminal, plan.RequestedHost);
        Assert.Equal(ProjectTerminalHost.WindowsTerminal, plan.EffectiveHost);
        Assert.Equal(windowsTerminalExecutable, plan.ShellExecutable);
        Assert.Equal(
            [
                "new-tab",
                "--startingDirectory",
                _projectRoot,
                _shellExecutable,
                "-NoLogo",
                "-NoExit",
            ],
            plan.ShellArguments);
        Assert.DoesNotContain(plan.Warnings, warning =>
            warning.Contains("fall back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePlanAsync_FallsBackDeterministicallyWhenWindowsTerminalIsUnavailable()
    {
        string missingWindowsTerminal = Path.Combine(_root, "missing", "wt.exe");
        ProjectTerminalService service = CreateService(missingWindowsTerminal);

        ProjectTerminalPlan plan = await service.CreatePlanAsync(
            _projectRoot,
            ProjectTerminalHost.WindowsTerminal);

        Assert.False(service.IsHostAvailable(ProjectTerminalHost.WindowsTerminal));
        Assert.True(plan.CanLaunch);
        Assert.Equal(ProjectTerminalHost.WindowsTerminal, plan.RequestedHost);
        Assert.Equal(ProjectTerminalHost.WindowsPowerShell, plan.EffectiveHost);
        Assert.Equal(_shellExecutable, plan.ShellExecutable);
        Assert.Equal(["-NoLogo", "-NoExit"], plan.ShellArguments);
        Assert.Contains(plan.Warnings, warning =>
            warning.Contains("fall back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePlanAsync_ActivatesManagedDotNetSelection()
    {
        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        ProjectTerminalSelection dotnet = Assert.Single(
            plan.Selections,
            selection => selection.Kind == RuntimeKind.DotNet);
        Assert.Equal(VersionSelector.Parse("10.0.200"), dotnet.RequestedSelector);
        Assert.Equal(RuntimeVersion.Parse("10.0.200"), dotnet.ResolvedVersion);
        Assert.Equal("AUTOENVPLUS_DOTNET_VERSION", dotnet.EnvironmentVariable);
        Assert.Equal("10.0.200", plan.EnvironmentOverrides[dotnet.EnvironmentVariable]);
    }

    [Fact]
    public async Task CreatePlanAsync_AppliesInheritedProxyAndSeparatePackageMirrors()
    {
        NetworkSettingsSaveResult saved = await new NetworkSettingsStore(_managedRoot).SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(
                    HttpProxy: "http://proxy.example:8080",
                    HttpsProxy: "https://proxy.example:8443",
                    NoProxy: ["localhost", ".internal.example"]),
                new Dictionary<string, ToolNetworkSettings>
                {
                    [NetworkToolIds.Pip] = new(
                        Mirror: NetworkEndpointOverride.Custom(
                            "https://legacy-pip.example/simple")),
                    [NetworkToolIds.Npm] = new(
                        Mirror: NetworkEndpointOverride.Custom(
                            "https://legacy-npm.example/registry")),
                }));
        Assert.True(saved.Success);
        ProviderSourcePreferenceStore sources = new(_managedRoot);
        await sources.SetBuiltInOverrideAsync(
            BuiltInLanguageCatalog.Current,
            new ProviderSourceOwner("pip", "bundled", "python-package-index"),
            "https://pypi.example/simple");
        await sources.SetBuiltInOverrideAsync(
            BuiltInLanguageCatalog.Current,
            new ProviderSourceOwner("npm", "bundled", "npm-registry"),
            "https://npm.example/registry");

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.True(plan.CanLaunch);
        Assert.Equal(
            Path.Combine(_managedRoot, "state", "network-settings.json"),
            plan.NetworkSettingsPath);
        Assert.Matches("^[0-9A-F]{64}$", plan.NetworkSettingsSha256!);
        Assert.Equal(sources.PreferencesPath, plan.ProviderSourcePreferencesPath);
        Assert.Matches("^[0-9A-F]{64}$", plan.ProviderSourcePreferencesSha256!);
        Assert.Equal("http://proxy.example:8080/", plan.EnvironmentOverrides["HTTP_PROXY"]);
        Assert.Equal("https://proxy.example:8443/", plan.EnvironmentOverrides["HTTPS_PROXY"]);
        Assert.Equal("localhost,.internal.example", plan.EnvironmentOverrides["NO_PROXY"]);
        Assert.Equal("https://pypi.example/simple", plan.EnvironmentOverrides["PIP_INDEX_URL"]);
        Assert.Equal("https://npm.example/registry", plan.EnvironmentOverrides["NPM_CONFIG_REGISTRY"]);
        Assert.Equal(ProjectTerminalProxySource.MatchingPackageTools, plan.NetworkSummary.ProxySource);
        Assert.True(plan.NetworkSummary.PipMirrorConfigured);
        Assert.True(plan.NetworkSummary.NpmMirrorConfigured);
        Assert.DoesNotContain("HTTP_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PIP_INDEX_URL", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_DisabledPipProxyStillUsesProviderOwnedPackageSource()
    {
        WriteManifest(
            """
            [tools]
            python = "3.12"
            """);
        NetworkSettingsSaveResult saved = await new NetworkSettingsStore(_managedRoot).SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(
                    HttpProxy: "http://global-proxy.example:8080",
                    HttpsProxy: "https://global-proxy.example:8443",
                    Mirror: "https://global-mirror.example/simple"),
                new Dictionary<string, ToolNetworkSettings>
                {
                    [NetworkToolIds.Pip] = new(
                        HttpProxy: NetworkEndpointOverride.Disabled,
                        HttpsProxy: NetworkEndpointOverride.Disabled,
                        Mirror: NetworkEndpointOverride.Disabled),
                }));
        Assert.True(saved.Success);

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.True(plan.CanLaunch);
        Assert.Equal(ProjectTerminalProxySource.Pip, plan.NetworkSummary.ProxySource);
        Assert.False(plan.NetworkSummary.HttpProxyConfigured);
        Assert.False(plan.NetworkSummary.HttpsProxyConfigured);
        Assert.True(plan.NetworkSummary.PipMirrorConfigured);
        Assert.Contains("HTTP_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("HTTPS_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("NO_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("http_proxy", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https_proxy", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ALL_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PIP_INDEX_URL", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("NPM_CONFIG_REGISTRY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.False(plan.EnvironmentOverrides.ContainsKey("HTTP_PROXY"));
        Assert.Equal("https://pypi.org/simple", plan.EnvironmentOverrides["PIP_INDEX_URL"]);
    }

    [Fact]
    public async Task CreatePlanAsync_UsesDownloadsProxyWhenPipAndNpmConflict()
    {
        NetworkSettingsSaveResult saved = await new NetworkSettingsStore(_managedRoot).SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(NoProxy: ["localhost"]),
                new Dictionary<string, ToolNetworkSettings>
                {
                    [NetworkToolIds.Pip] = new(
                        HttpProxy: NetworkEndpointOverride.Custom(
                            "http://pip-proxy.example:8080"),
                        Mirror: NetworkEndpointOverride.Custom(
                            "https://pypi.example/simple")),
                    [NetworkToolIds.Npm] = new(
                        HttpProxy: NetworkEndpointOverride.Custom(
                            "http://npm-proxy.example:8080"),
                        Mirror: NetworkEndpointOverride.Custom(
                            "https://npm.example/registry")),
                    [NetworkToolIds.Downloads] = new(
                        HttpProxy: NetworkEndpointOverride.Custom(
                            "http://shared-proxy.example:8080"),
                        HttpsProxy: NetworkEndpointOverride.Disabled),
                }));
        Assert.True(saved.Success);

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.True(plan.CanLaunch);
        Assert.Equal(ProjectTerminalProxySource.Downloads, plan.NetworkSummary.ProxySource);
        Assert.Equal("http://shared-proxy.example:8080/", plan.EnvironmentOverrides["HTTP_PROXY"]);
        Assert.Contains("HTTPS_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("https://pypi.org/simple", plan.EnvironmentOverrides["PIP_INDEX_URL"]);
        Assert.Equal("https://registry.npmjs.org/", plan.EnvironmentOverrides["NPM_CONFIG_REGISTRY"]);
        Assert.Contains(plan.Warnings, warning =>
            warning.Contains("downloads scope", StringComparison.Ordinal)
            && warning.Contains("package mirrors remain tool-specific", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_RejectsAnArbitraryWindowsTerminalCommand()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new ProjectTerminalService(
            _managedRoot,
            new FakeRegistry(_entries),
            RuntimeArchitecture.X64,
            _shellExecutable,
            Path.Combine(_root, "cmd.exe")));

        Assert.Contains("wt.exe", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsAnUndefinedHost()
    {
        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateService().CreatePlanAsync(_projectRoot, (ProjectTerminalHost)42));

        Assert.Equal("requestedHost", exception.ParamName);
    }

    [Fact]
    public async Task CreatePlanAsync_ReportsMissingShimAndUnmatchedRuntime()
    {
        File.Delete(Path.Combine(_managedRoot, "shims", "node.exe"));
        _entries.RemoveAll(entry => entry.Kind == RuntimeKind.Java);

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.False(plan.CanLaunch);
        Assert.Contains(plan.Errors, error => error.Contains("node", StringComparison.OrdinalIgnoreCase)
            && error.Contains("Shim", StringComparison.Ordinal));
        Assert.Contains(plan.Errors, error => error.Contains("No installed Java", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsSameVersionFromDistinctProviders()
    {
        _entries.Add(CreateRuntime(
            "plugin-community-python-3.12.8",
            RuntimeKind.Python,
            "3.12.8",
            "python.exe",
            providerId: "plugin:community-python"));

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.False(plan.CanLaunch);
        string ambiguity = Assert.Single(
            plan.Errors,
            error => error.Contains("Multiple Providers", StringComparison.Ordinal));
        Assert.Contains("exact runtime ID", ambiguity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-provider", ambiguity, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin:community-python", ambiguity, StringComparison.Ordinal);
        Assert.False(plan.EnvironmentOverrides.ContainsKey("AUTOENVPLUS_PYTHON_RUNTIME_ID"));
    }

    [Fact]
    public async Task LaunchAsync_RejectsProviderChangedAfterPreview()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        int index = _entries.FindIndex(entry => entry.Id == "python-3.12.8");
        _entries[index] = _entries[index] with { ProviderId = "plugin:replacement" };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(plan));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_RejectsTamperedExactRuntimeIdPin()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        Dictionary<string, string> environment = plan.EnvironmentOverrides.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        environment["AUTOENVPLUS_PYTHON_RUNTIME_ID"] = "another-runtime-id";
        ProjectTerminalPlan tampered = plan with { EnvironmentOverrides = environment };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(tampered));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_RejectsManifestChangedAfterPreviewBeforeStartingProcess()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        File.AppendAllText(Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName), "\n# changed after preview\n");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(plan));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_RejectsNetworkSettingsChangedAfterPreviewBeforeStartingProcess()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        NetworkSettingsSaveResult saved = await new NetworkSettingsStore(_managedRoot).SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(HttpProxy: "http://new-proxy.example:8080")));
        Assert.True(saved.Success);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(plan));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_RejectsProviderSourceChangedAfterPreviewBeforeStartingProcess()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        await new ProviderSourcePreferenceStore(_managedRoot).SetBuiltInOverrideAsync(
            BuiltInLanguageCatalog.Current,
            new ProviderSourceOwner("pip", "bundled", "python-package-index"),
            "https://changed.example/simple");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(plan));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_RejectsTamperedEnvironmentRemovalPlan()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        Assert.Contains("ALL_PROXY", plan.EnvironmentRemovals, StringComparer.OrdinalIgnoreCase);
        ProjectTerminalPlan tampered = plan with
        {
            EnvironmentRemovals = plan.EnvironmentRemovals
                .Where(name => !name.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(tampered));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchAsync_RejectsTamperedNetworkSummary()
    {
        ProjectTerminalService service = CreateService();
        ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
        ProjectTerminalPlan tampered = plan with
        {
            NetworkSummary = plan.NetworkSummary with
            {
                HttpProxyConfigured = !plan.NetworkSummary.HttpProxyConfigured,
            },
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LaunchAsync(tampered));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePlanAsync_DoesNotEchoRejectedProxyCredentials()
    {
        string settingsDirectory = Directory.CreateDirectory(
            Path.Combine(_managedRoot, "state")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectory, "network-settings.json"),
            """
            {
              "schemaVersion": 1,
              "global": {
                "httpProxy": "https://project-user:project-secret@proxy.example"
              },
              "tools": {}
            }
            """);

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.False(plan.CanLaunch);
        Assert.Contains(plan.Errors, error =>
            error.Contains("Credentials in proxy URIs are not accepted", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Errors.Concat(plan.Warnings), message =>
            message.Contains("project-user", StringComparison.Ordinal)
            || message.Contains("project-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatePlanAsync_PropagatesManifestErrorsAndDisablesLaunch()
    {
        WriteManifest(
            """
            [tools]
            python = "3.12"
            python = "3.13"
            """);

        ProjectTerminalPlan plan = await CreateService().CreatePlanAsync(_projectRoot);

        Assert.False(plan.CanLaunch);
        Assert.Contains(plan.Errors, error => error.Contains("declared more than once", StringComparison.Ordinal));
    }

    private ProjectTerminalService CreateService(string? windowsTerminalExecutable = null) => new(
        _managedRoot,
        new FakeRegistry(_entries),
        RuntimeArchitecture.X64,
        _shellExecutable,
        windowsTerminalExecutable);

    private ManagedRuntimeEntry CreateRuntime(
        string id,
        RuntimeKind kind,
        string version,
        string executableRelativePath,
        IReadOnlyCollection<string>? channels = null,
        string providerId = "test-provider")
    {
        string installRoot = Directory.CreateDirectory(Path.Combine(_managedRoot, "runtimes", id)).FullName;
        string executable = Path.Combine(
            installRoot,
            executableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        return new ManagedRuntimeEntry(
            id,
            providerId,
            kind,
            RuntimeVersion.Parse(version),
            RuntimeArchitecture.X64,
            installRoot,
            executableRelativePath,
            new string('a', 64),
            DateTimeOffset.UtcNow,
            channels);
    }

    private void WriteManifest(string content) => File.WriteAllText(
        Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName),
        content);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRegistry(IReadOnlyList<ManagedRuntimeEntry> entries)
        : IManagedRuntimeRegistryStore
    {
        public Task<RegistryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RegistryLoadResult(entries, []));
        }

        public Task<RegistryLoadResult> UpsertAsync(
            ManagedRuntimeEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RegistryLoadResult> RemoveAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
