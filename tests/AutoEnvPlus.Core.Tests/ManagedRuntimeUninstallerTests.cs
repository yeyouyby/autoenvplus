using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedRuntimeUninstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Uninstall-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreatePlanAsync_FindsGlobalManifestAndLockReferences()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));
        string project = Directory.CreateDirectory(Path.Combine(_root, "external-project")).FullName;
        string manifest = Path.Combine(project, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n");
        ProjectLockResult locked = await new ProjectLockFileService().CreateAsync(manifest, [runtime]);
        Assert.True(locked.Success);
        await new KnownProjectStore(_root).AddAsync(project);

        ManagedRuntimeUninstallPlan plan = await new ManagedRuntimeUninstaller(_root).CreatePlanAsync(runtime.Id);

        Assert.True(plan.IsReferenced);
        Assert.Contains(plan.References, reference => reference.Kind == RuntimeReferenceKind.GlobalProfile);
        Assert.Contains(plan.References, reference => reference.Kind == RuntimeReferenceKind.ProjectManifest);
        Assert.Contains(plan.References, reference => reference.Kind == RuntimeReferenceKind.ProjectLock);

        ManagedRuntimeUninstallResult blocked = await new ManagedRuntimeUninstaller(_root).ExecuteAsync(plan);
        Assert.False(blocked.Success);
        Assert.True(Directory.Exists(runtime.InstallRoot));
    }

    [Fact]
    public async Task ExecuteAsync_AtomicallyRemovesUnreferencedRuntimeAndRegistryEntry()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);

        ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.True(result.RemovedFromRegistry);
        Assert.False(result.PendingTrashCleanup);
        Assert.False(Directory.Exists(runtime.InstallRoot));
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(_root).LoadAsync();
        Assert.DoesNotContain(registry.Entries, entry => entry.Id == runtime.Id);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsStalePlanAfterRegistryChange()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);
        await new ManagedRuntimeRegistry(_root).UpsertAsync(runtime with
        {
            PackageSha256 = new string('b', 64),
        });

        ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(runtime.InstallRoot));
    }

    [Fact]
    public async Task CreatePlanAsync_ChannelProfileReferencesOnlyResolvedHighestVersion()
    {
        ManagedRuntimeEntry oldLts = await RegisterNode("22.17.0");
        ManagedRuntimeEntry currentLts = await RegisterNode("24.18.0");
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.NodeJs,
            VersionSelector.Parse("lts"));
        ManagedRuntimeUninstaller uninstaller = new(_root);

        ManagedRuntimeUninstallPlan oldPlan = await uninstaller.CreatePlanAsync(oldLts.Id);
        ManagedRuntimeUninstallPlan currentPlan = await uninstaller.CreatePlanAsync(currentLts.Id);

        Assert.DoesNotContain(oldPlan.References, reference =>
            reference.Kind == RuntimeReferenceKind.GlobalProfile);
        Assert.Contains(currentPlan.References, reference =>
            reference.Kind == RuntimeReferenceKind.GlobalProfile);
    }

    private async Task<ManagedRuntimeEntry> RegisterPython()
    {
        string installRoot = Path.Combine(_root, "runtimes", "python", "3.13.5", "x64");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), "runtime");
        ManagedRuntimeEntry runtime = new(
            "python-3.13.5-x64",
            "python-org",
            RuntimeKind.Python,
            RuntimeVersion.Parse("3.13.5"),
            RuntimeArchitecture.X64,
            installRoot,
            "python.exe",
            new string('a', 64),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            ["stable"]);
        await new ManagedRuntimeRegistry(_root).UpsertAsync(runtime);
        return runtime;
    }

    private async Task<ManagedRuntimeEntry> RegisterNode(string version)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        string installRoot = Path.Combine(_root, "runtimes", "node", parsed.ToString(), "x64");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "node.exe"), "runtime");
        ManagedRuntimeEntry runtime = new(
            $"nodejs-{parsed}-x64",
            "nodejs-official",
            RuntimeKind.NodeJs,
            parsed,
            RuntimeArchitecture.X64,
            installRoot,
            "node.exe",
            new string(parsed.Major == 22 ? 'c' : 'd', 64),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            ["lts"]);
        await new ManagedRuntimeRegistry(_root).UpsertAsync(runtime);
        return runtime;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
