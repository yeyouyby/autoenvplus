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
            PackageHash = new string('b', 64),
        });

        ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(runtime.InstallRoot));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsGlobalReferenceAddedAfterPlanCreation()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);
        Assert.False(plan.IsReferenced);
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));

        ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("referenced", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(runtime.InstallRoot));
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(_root).LoadAsync();
        Assert.Contains(registry.Entries, entry => entry.Id == runtime.Id);
    }

    [Fact]
    public async Task ExecuteAsync_WaitsForConcurrentStateUpdateAndRevalidatesPlan()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        ManagedRuntimeRegistry registry = new(_root);
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);
        ManagedRuntimeEntry changed = runtime with
        {
            PackageHash = new string('b', 64),
        };
        string transactionLockPath = Path.Combine(
            _root,
            "state",
            "managed-runtime-install-state.lock");
        Task<RegistryLoadResult> pendingUpdate;
        Task<ManagedRuntimeUninstallResult> pendingUninstall;

        using (FileStream heldLock = new(
            transactionLockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            pendingUpdate = registry.UpsertAsync(changed);
            await Task.Delay(100);
            Assert.False(pendingUpdate.IsCompleted);
            pendingUninstall = uninstaller.ExecuteAsync(plan);
            await Task.Delay(100);
            Assert.False(pendingUninstall.IsCompleted);
        }

        await pendingUpdate;
        ManagedRuntimeUninstallResult result = await pendingUninstall;
        RegistryLoadResult loaded = await registry.LoadAsync();

        Assert.False(result.Success);
        Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
        ManagedRuntimeEntry persisted = Assert.Single(loaded.Entries);
        Assert.Equal(changed.Id, persisted.Id);
        Assert.Equal(changed.PackageHash, persisted.PackageHash);
        Assert.Equal(changed.Version, persisted.Version);
        Assert.True(Directory.Exists(runtime.InstallRoot));
    }

    [Fact]
    public async Task ExecuteAsync_CancellationAfterMoveRestoresRuntimeAndRegistry()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);
        string stateDirectory = Path.Combine(_root, "state");
        string transactionLockPath = Path.Combine(
            stateDirectory,
            "managed-runtime-install-state.lock");
        string registryLockPath = Path.Combine(
            stateDirectory,
            "managed-runtime-registry.lock");
        string profileLockPath = Path.Combine(
            stateDirectory,
            "global-runtime-profile.lock");
        using CancellationTokenSource cancellation = new();
        FileStream? heldProfileLock = null;
        FileStream? heldRegistryLock = null;
        try
        {
            heldProfileLock = new FileStream(
                profileLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Task<ManagedRuntimeUninstallResult> pending = uninstaller.ExecuteAsync(
                plan,
                cancellationToken: cancellation.Token);
            await WaitUntilLockedAsync(transactionLockPath, TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            heldRegistryLock = new FileStream(
                registryLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            heldProfileLock.Dispose();
            heldProfileLock = null;
            await WaitUntilAsync(
                () => !Directory.Exists(runtime.InstallRoot),
                TimeSpan.FromSeconds(5));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        finally
        {
            heldProfileLock?.Dispose();
            heldRegistryLock?.Dispose();
        }

        Assert.True(Directory.Exists(runtime.InstallRoot));
        Assert.False(Directory.Exists(plan.TrashPath));
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(_root).LoadAsync();
        Assert.Contains(registry.Entries, entry => entry.Id == runtime.Id);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationReportsConsistencyFailureWhenRestorePathWasRecreated()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);
        string stateDirectory = Path.Combine(_root, "state");
        string transactionLockPath = Path.Combine(
            stateDirectory,
            "managed-runtime-install-state.lock");
        string registryLockPath = Path.Combine(
            stateDirectory,
            "managed-runtime-registry.lock");
        string profileLockPath = Path.Combine(
            stateDirectory,
            "global-runtime-profile.lock");
        using CancellationTokenSource cancellation = new();
        FileStream? heldProfileLock = null;
        FileStream? heldRegistryLock = null;
        try
        {
            heldProfileLock = new FileStream(
                profileLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Task<ManagedRuntimeUninstallResult> pending = uninstaller.ExecuteAsync(
                plan,
                cancellationToken: cancellation.Token);
            await WaitUntilLockedAsync(transactionLockPath, TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            heldRegistryLock = new FileStream(
                registryLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            heldProfileLock.Dispose();
            heldProfileLock = null;
            await WaitUntilAsync(
                () => !Directory.Exists(runtime.InstallRoot),
                TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(runtime.InstallRoot);
            File.WriteAllText(Path.Combine(runtime.InstallRoot, "replacement.txt"), "replacement");

            cancellation.Cancel();
            IOException exception =
                await Assert.ThrowsAsync<IOException>(async () => await pending);

            Assert.Contains(plan.TrashPath, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("could not be restored", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            heldProfileLock?.Dispose();
            heldRegistryLock?.Dispose();
        }

        Assert.True(Directory.Exists(runtime.InstallRoot));
        Assert.True(Directory.Exists(plan.TrashPath));
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(_root).LoadAsync();
        Assert.Contains(registry.Entries, entry => entry.Id == runtime.Id);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsReparsePointInstallAncestorWithoutMovingExternalData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string externalRuntimes = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-External-Runtimes-{Guid.NewGuid():N}");
        string installRoot = Path.Combine(
            externalRuntimes,
            "python",
            "3.13.5",
            "x64");
        Directory.CreateDirectory(installRoot);
        string marker = Path.Combine(installRoot, "external-marker.txt");
        File.WriteAllText(marker, "external");
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), "runtime");
        Directory.CreateDirectory(_root);
        string runtimesLink = Path.Combine(_root, "runtimes");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(runtimesLink, externalRuntimes);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            ManagedRuntimeEntry runtime = new(
                "python-3.13.5-x64",
                "python-org",
                RuntimeKind.Python,
                RuntimeVersion.Parse("3.13.5"),
                RuntimeArchitecture.X64,
                Path.Combine(_root, "runtimes", "python", "3.13.5", "x64"),
                "python.exe",
                new string('a', 64),
                new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
                ["stable"]);
            await new ManagedRuntimeRegistry(_root).UpsertAsync(runtime);
            ManagedRuntimeUninstaller uninstaller = new(_root);
            ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);

            ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(plan);

            Assert.False(result.Success);
            Assert.Contains("reparse", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(marker));
            Assert.True(Directory.Exists(installRoot));
            Assert.Contains(
                (await new ManagedRuntimeRegistry(_root).LoadAsync()).Entries,
                entry => entry.Id == runtime.Id);
        }
        finally
        {
            if (Directory.Exists(runtimesLink))
            {
                Directory.Delete(runtimesLink);
            }

            if (Directory.Exists(externalRuntimes))
            {
                Directory.Delete(externalRuntimes, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNestedReparsePointWithoutDeletingExternalData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ManagedRuntimeEntry runtime = await RegisterPython();
        string externalDirectory = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-External-Nested-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);
        string marker = Path.Combine(externalDirectory, "external-marker.txt");
        File.WriteAllText(marker, "external");
        string nestedLink = Path.Combine(runtime.InstallRoot, "linked-cache");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(nestedLink, externalDirectory);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            ManagedRuntimeUninstaller uninstaller = new(_root);
            ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);

            ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(plan);

            Assert.False(result.Success);
            Assert.Contains("reparse", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(marker));
            Assert.True(Directory.Exists(runtime.InstallRoot));
        }
        finally
        {
            if (Directory.Exists(nestedLink))
            {
                Directory.Delete(nestedLink);
            }

            if (Directory.Exists(externalDirectory))
            {
                Directory.Delete(externalDirectory, recursive: true);
            }
        }
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

    [Fact]
    public async Task CreatePlanAsync_ProjectExactIdentityReferencesOnlyPinnedProvider()
    {
        ManagedRuntimeEntry official = await RegisterPython();
        string vendorRoot = Directory.CreateDirectory(
            Path.Combine(_root, "runtimes", "python", "vendor-3.13.5", "x64")).FullName;
        File.WriteAllText(Path.Combine(vendorRoot, "python.exe"), "runtime");
        ManagedRuntimeEntry vendor = official with
        {
            Id = "python-vendor-3.13.5-x64",
            ProviderId = "vendor-python",
            InstallRoot = vendorRoot,
            PackageHash = new string('b', 64),
        };
        await new ManagedRuntimeRegistry(_root).UpsertAsync(vendor);
        string project = Directory.CreateDirectory(Path.Combine(_root, "exact-project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, ProjectManifestService.ManifestFileName),
            $"[tools]\npython = \"3.13.5\"\n\n[tool-identities]\npython.runtime-id = \"{vendor.Id}\"\npython.provider-id = \"{vendor.ProviderId}\"\n");
        await new KnownProjectStore(_root).AddAsync(project);
        ManagedRuntimeUninstaller uninstaller = new(_root);

        ManagedRuntimeUninstallPlan officialPlan = await uninstaller.CreatePlanAsync(official.Id);
        ManagedRuntimeUninstallPlan vendorPlan = await uninstaller.CreatePlanAsync(vendor.Id);

        Assert.DoesNotContain(officialPlan.References, reference =>
            reference.Kind == RuntimeReferenceKind.ProjectManifest);
        Assert.Contains(vendorPlan.References, reference =>
            reference.Kind == RuntimeReferenceKind.ProjectManifest
            && reference.Detail.Contains("3.13.5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatePlanAsync_FailsClosedForUnreadableProjectLock()
    {
        ManagedRuntimeEntry runtime = await RegisterPython();
        string project = Directory.CreateDirectory(Path.Combine(_root, "locked-project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, ProjectLockFileService.LockFileName),
            "{ \"schemaVersion\": 999, \"runtimes\": [] }");
        await new KnownProjectStore(_root).AddAsync(project);

        ManagedRuntimeUninstallPlan plan = await new ManagedRuntimeUninstaller(_root)
            .CreatePlanAsync(runtime.Id);

        Assert.True(plan.IsReferenced);
        Assert.Contains(plan.References, reference =>
            reference.Kind == RuntimeReferenceKind.ProjectLock
            && reference.Detail.Contains("unreadable lock", StringComparison.OrdinalIgnoreCase));
        ManagedRuntimeUninstallResult result = await new ManagedRuntimeUninstaller(_root)
            .ExecuteAsync(plan);
        Assert.False(result.Success);
        Assert.True(Directory.Exists(runtime.InstallRoot));
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

    private static async Task WaitUntilLockedAsync(string path, TimeSpan timeout)
    {
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    using FileStream stream = new(
                        path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return false;
                }
                catch (IOException)
                {
                    return true;
                }
            },
            timeout);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected uninstall test state was not reached.");
            }

            await Task.Delay(10);
        }
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
