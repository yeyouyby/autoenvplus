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

    [Fact]
    public async Task ResolveAsync_RejectsProviderAmbiguityAndHonorsExactSessionPin()
    {
        ManagedRuntimeEntry official = await RegisterPython(
            "3.13.5",
            "python-org",
            "python-3.13.5-x64");
        ManagedRuntimeEntry community = await RegisterPython(
            "3.13.5",
            "plugin:community-python",
            "plugin-community-python-3.13.5-x64");
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));
        ManagedRuntimeResolutionService service = new(_root);

        ManagedRuntimeResolutionResult ambiguous = await service.ResolveAsync(
            RuntimeKind.Python,
            _projectRoot,
            architecture: RuntimeArchitecture.X64);
        ManagedRuntimeResolutionResult pinned = await service.ResolveAsync(
            RuntimeKind.Python,
            _projectRoot,
            architecture: RuntimeArchitecture.X64,
            sessionRuntimeId: community.Id,
            sessionProviderId: community.ProviderId);
        ManagedRuntimeResolutionResult changedProvider = await service.ResolveAsync(
            RuntimeKind.Python,
            _projectRoot,
            architecture: RuntimeArchitecture.X64,
            sessionRuntimeId: community.Id,
            sessionProviderId: official.ProviderId);

        Assert.False(ambiguous.Success);
        string ambiguity = Assert.Single(ambiguous.Errors);
        Assert.Contains("Multiple Providers", ambiguity, StringComparison.Ordinal);
        Assert.Contains("exact runtime ID", ambiguity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(official.ProviderId, ambiguity, StringComparison.Ordinal);
        Assert.DoesNotContain(community.ProviderId, ambiguity, StringComparison.Ordinal);
        Assert.True(pinned.Success);
        Assert.Equal(community.Id, pinned.Entry!.Id);
        Assert.Equal(ResolutionScope.Session, pinned.Resolution!.Scope);
        Assert.False(changedProvider.Success);
        Assert.Contains("does not match", Assert.Single(changedProvider.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_HonorsExactGlobalProviderPin()
    {
        await RegisterPython("3.13.5", "python-org", "python-official-x64");
        ManagedRuntimeEntry community = await RegisterPython(
            "3.13.5",
            "plugin:community-python",
            "python-community-x64");
        await new GlobalRuntimeProfileStore(_root).SetExactAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13.5"),
            community.Id,
            community.ProviderId);

        ManagedRuntimeResolutionResult result =
            await new ManagedRuntimeResolutionService(_root).ResolveAsync(
                RuntimeKind.Python,
                _projectRoot,
                architecture: RuntimeArchitecture.X64);

        Assert.True(result.Success);
        Assert.Equal(community.Id, result.Entry!.Id);
        Assert.Equal(ResolutionScope.Global, result.Resolution!.Scope);
    }

    [Fact]
    public async Task ResolveAsync_ExactProjectPinOverridesExactGlobalPin()
    {
        ManagedRuntimeEntry official = await RegisterPython(
            "3.13.5",
            "python-org",
            "python-official-x64");
        ManagedRuntimeEntry community = await RegisterPython(
            "3.13.5",
            "plugin:community-python",
            "python-community-x64");
        await new GlobalRuntimeProfileStore(_root).SetExactAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13.5"),
            official.Id,
            official.ProviderId);
        File.WriteAllText(
            Path.Combine(_projectRoot, "autoenvplus.toml"),
            $"[tools]\npython = \"3.13.5\"\n\n[tool-identities]\npython.runtime-id = \"{community.Id}\"\npython.provider-id = \"{community.ProviderId}\"\n");

        ManagedRuntimeResolutionResult result =
            await new ManagedRuntimeResolutionService(_root).ResolveAsync(
                RuntimeKind.Python,
                Path.Combine(_projectRoot, "src"),
                architecture: RuntimeArchitecture.X64);

        Assert.True(result.Success);
        Assert.Equal(community.Id, result.Entry!.Id);
        Assert.Equal(community.ProviderId, result.Entry.ProviderId);
        Assert.Equal(ResolutionScope.Project, result.Resolution!.Scope);
    }

    private async Task<ManagedRuntimeEntry> RegisterPython(
        string version,
        string providerId = "python-org",
        string? runtimeId = null)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        runtimeId ??= $"python-{parsed}-x64";
        string installRoot = Path.Combine(_root, "runtimes", "python", runtimeId);
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), string.Empty);
        ManagedRuntimeEntry entry = new(
            runtimeId,
            providerId,
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
