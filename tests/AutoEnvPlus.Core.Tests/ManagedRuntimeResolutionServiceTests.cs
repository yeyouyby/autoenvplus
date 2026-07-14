using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedRuntimeResolutionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Resolve-{Guid.NewGuid():N}");
    private readonly string _projectRoot;

    public ManagedRuntimeResolutionServiceTests()
    {
        _projectRoot = Directory.CreateDirectory(Path.Combine(_root, "project", "src")).Parent!.FullName;
    }

    [Fact]
    public async Task ResolveAsync_UsesSessionThenProjectThenGlobalProfiles()
    {
        await RegisterPython("3.12.9");
        await RegisterPython("3.13.5");
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));
        File.WriteAllText(
            Path.Combine(_projectRoot, "autoenvplus.toml"),
            "[tools]\npython = \"3.12\"\n");
        ManagedRuntimeResolutionService service = new(_root);

        ManagedRuntimeResolutionResult project = await service.ResolveAsync(
            RuntimeKind.Python,
            Path.Combine(_projectRoot, "src"));
        RuntimeProfile sessionProfile = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.Python] = VersionSelector.Parse("3.13"),
        });
        ManagedRuntimeResolutionResult session = await service.ResolveAsync(
            RuntimeKind.Python,
            Path.Combine(_projectRoot, "src"),
            sessionProfile);

        Assert.True(project.Success);
        Assert.Equal(ResolutionScope.Project, project.Resolution!.Scope);
        Assert.Equal(RuntimeVersion.Parse("3.12.9"), project.Entry!.Version);
        Assert.True(session.Success);
        Assert.Equal(ResolutionScope.Session, session.Resolution!.Scope);
        Assert.Equal(RuntimeVersion.Parse("3.13.5"), session.Entry!.Version);
    }

    [Fact]
    public async Task ResolveAsync_ReportsDeletedManagedExecutable()
    {
        ManagedRuntimeEntry entry = await RegisterPython("3.13.5");
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));
        File.Delete(entry.ExecutablePath);

        ManagedRuntimeResolutionResult result = await new ManagedRuntimeResolutionService(_root).ResolveAsync(
            RuntimeKind.Python,
            _projectRoot);

        Assert.False(result.Success);
        Assert.Contains("missing", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ManagedRuntimeEntry> RegisterPython(string version)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        string installRoot = Path.Combine(_root, "runtimes", "python", parsed.ToString(), "x64");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), string.Empty);
        ManagedRuntimeEntry entry = new(
            $"python-{parsed}-x64",
            "python-org",
            RuntimeKind.Python,
            parsed,
            RuntimeArchitecture.X64,
            installRoot,
            "python.exe",
            new string('a', 64),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        await new ManagedRuntimeRegistry(_root).UpsertAsync(entry);
        return entry;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
