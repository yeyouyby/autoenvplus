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
        foreach (string command in new[] { "python", "node", "java" })
        {
            File.WriteAllText(Path.Combine(shims, command + ".exe"), string.Empty);
        }

        _entries.Add(CreateRuntime("python-3.12.1", RuntimeKind.Python, "3.12.1", "python.exe"));
        _entries.Add(CreateRuntime("python-3.12.8", RuntimeKind.Python, "3.12.8", "python.exe"));
        _entries.Add(CreateRuntime("node-22.17.0", RuntimeKind.NodeJs, "22.17.0", "node.exe", ["lts"]));
        _entries.Add(CreateRuntime("java-21.0.8", RuntimeKind.Java, "21.0.8", "bin/java.exe", ["lts"]));
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
        Assert.Equal(3, plan.Selections.Count);
        Assert.Equal("3.12.8", plan.EnvironmentOverrides["AUTOENVPLUS_PYTHON_VERSION"]);
        Assert.Equal("22.17.0", plan.EnvironmentOverrides["AUTOENVPLUS_NODE_VERSION"]);
        Assert.Equal("21.0.8", plan.EnvironmentOverrides["AUTOENVPLUS_JAVA_VERSION"]);
        Assert.Equal(_projectRoot, plan.EnvironmentOverrides["AUTOENVPLUS_PROJECT_ROOT"]);
        Assert.Equal(
            Path.Combine(_managedRoot, "shims"),
            plan.EnvironmentOverrides["PATH"].Split(';')[0]);
        Assert.Contains(plan.Warnings, warning => warning.Contains("DotNet", StringComparison.Ordinal));
        Assert.Equal(parentPath, System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

        ProjectTerminalSelection python = Assert.Single(
            plan.Selections,
            selection => selection.Kind == RuntimeKind.Python);
        Assert.Equal(VersionSelector.Parse("3.12"), python.RequestedSelector);
        Assert.Equal(RuntimeVersion.Parse("3.12.8"), python.ResolvedVersion);
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

    private ProjectTerminalService CreateService() => new(
        _managedRoot,
        new FakeRegistry(_entries),
        RuntimeArchitecture.X64,
        _shellExecutable);

    private ManagedRuntimeEntry CreateRuntime(
        string id,
        RuntimeKind kind,
        string version,
        string executableRelativePath,
        IReadOnlyCollection<string>? channels = null)
    {
        string installRoot = Directory.CreateDirectory(Path.Combine(_managedRoot, "runtimes", id)).FullName;
        string executable = Path.Combine(
            installRoot,
            executableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        return new ManagedRuntimeEntry(
            id,
            "test-provider",
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
