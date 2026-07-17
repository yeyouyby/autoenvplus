using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedGlobalRuntimeSelectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-GlobalSelection-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAsync_AtomicallyResolvesAndPersistsInstalledRuntime()
    {
        ManagedRuntimeEntry runtime = await RegisterPythonAsync(
            "python-org",
            "python-3.13.5-x64",
            "3.13.5");

        ManagedGlobalRuntimeSelectionResult result =
            await new ManagedGlobalRuntimeSelectionService(_root).SetAsync(
                RuntimeKind.Python,
                VersionSelector.Parse("3.13"),
                RuntimeArchitecture.X64);

        Assert.True(result.Success);
        Assert.Equal(runtime.Id, result.Entry!.Id);
        Assert.Equal(
            VersionSelector.Parse("3.13"),
            result.Profile!.Selections[RuntimeKind.Python]);
        RuntimeProfile persisted = await new GlobalRuntimeProfileStore(_root).LoadAsync();
        Assert.Equal(
            VersionSelector.Parse("3.13"),
            persisted.Selections[RuntimeKind.Python]);
        Assert.Equal(
            new RuntimeSelectionIdentity(runtime.Id, runtime.ProviderId),
            persisted.ExactSelections[RuntimeKind.Python]);
    }

    [Fact]
    public async Task SetAsync_RejectsProviderCollisionWithoutChangingProfile()
    {
        await RegisterPythonAsync("python-org", "python-official-x64", "3.13.5");
        await RegisterPythonAsync("vendor-python", "python-vendor-x64", "3.13.5");

        ManagedGlobalRuntimeSelectionResult result =
            await new ManagedGlobalRuntimeSelectionService(_root).SetAsync(
                RuntimeKind.Python,
                VersionSelector.Parse("3.13.5"),
                RuntimeArchitecture.X64);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(
            "Multiple Providers",
            StringComparison.Ordinal));
        Assert.Empty((await new GlobalRuntimeProfileStore(_root).LoadAsync()).Selections);
    }

    [Fact]
    public async Task SetAsync_WithConfirmedEntryPersistsExactProviderAcrossCollision()
    {
        await RegisterPythonAsync("python-org", "python-official-x64", "3.13.5");
        ManagedRuntimeEntry vendor = await RegisterPythonAsync(
            "vendor-python",
            "python-vendor-x64",
            "3.13.5");

        ManagedGlobalRuntimeSelectionResult result =
            await new ManagedGlobalRuntimeSelectionService(_root).SetAsync(
                RuntimeKind.Python,
                VersionSelector.Parse("3.13.5"),
                RuntimeArchitecture.X64,
                vendor);

        Assert.True(result.Success);
        Assert.Equal(vendor.Id, result.Entry!.Id);
        RuntimeProfile persisted = await new GlobalRuntimeProfileStore(_root).LoadAsync();
        Assert.Equal(
            new RuntimeSelectionIdentity(vendor.Id, vendor.ProviderId),
            persisted.ExactSelections[RuntimeKind.Python]);
    }

    [Fact]
    public async Task SetAsync_RejectsEntryChangedAfterConfirmation()
    {
        ManagedRuntimeEntry displayed = await RegisterPythonAsync(
            "python-org",
            "python-3.13.5-x64",
            "3.13.5");
        await new ManagedRuntimeRegistry(_root).UpsertAsync(displayed with
        {
            PackageHash = new string('b', 64),
        });

        ManagedGlobalRuntimeSelectionResult result =
            await new ManagedGlobalRuntimeSelectionService(_root).SetAsync(
                RuntimeKind.Python,
                VersionSelector.Parse("3.13.5"),
                RuntimeArchitecture.X64,
                displayed);

        Assert.False(result.Success);
        Assert.Contains("changed", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await new GlobalRuntimeProfileStore(_root).LoadAsync()).Selections);
    }

    [Fact]
    public async Task SetAsync_RejectsMissingExecutableWithoutChangingProfile()
    {
        ManagedRuntimeEntry runtime = await RegisterPythonAsync(
            "python-org",
            "python-3.13.5-x64",
            "3.13.5");
        File.Delete(runtime.ExecutablePath);

        ManagedGlobalRuntimeSelectionResult result =
            await new ManagedGlobalRuntimeSelectionService(_root).SetAsync(
                RuntimeKind.Python,
                VersionSelector.Parse("3.13.5"),
                RuntimeArchitecture.X64);

        Assert.False(result.Success);
        Assert.Contains("missing or unsafe", Assert.Single(result.Errors));
        Assert.Empty((await new GlobalRuntimeProfileStore(_root).LoadAsync()).Selections);
    }

    [Fact]
    public async Task SetAsync_SerializesWithUninstallAndCreatesReferenceBeforeRemoval()
    {
        ManagedRuntimeEntry runtime = await RegisterPythonAsync(
            "python-org",
            "python-3.13.5-x64",
            "3.13.5");
        ManagedRuntimeUninstaller uninstaller = new(_root);
        ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(runtime.Id);
        Assert.False(plan.IsReferenced);
        string stateDirectory = Path.Combine(_root, "state");
        string profileLockPath = Path.Combine(
            stateDirectory,
            "global-runtime-profile.lock");
        string transactionLockPath = Path.Combine(
            stateDirectory,
            "managed-runtime-install-state.lock");
        FileStream? heldProfileLock = null;
        try
        {
            heldProfileLock = new FileStream(
                profileLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Task<ManagedGlobalRuntimeSelectionResult> selection =
                new ManagedGlobalRuntimeSelectionService(_root).SetAsync(
                    RuntimeKind.Python,
                    VersionSelector.Parse("3.13.5"),
                    RuntimeArchitecture.X64,
                    runtime);
            await WaitUntilLockedAsync(transactionLockPath, TimeSpan.FromSeconds(5));
            Task<ManagedRuntimeUninstallResult> uninstall = uninstaller.ExecuteAsync(plan);
            await Task.Delay(100);
            Assert.False(selection.IsCompleted);
            Assert.False(uninstall.IsCompleted);

            heldProfileLock.Dispose();
            heldProfileLock = null;
            ManagedGlobalRuntimeSelectionResult selected = await selection;
            ManagedRuntimeUninstallResult removed = await uninstall;

            Assert.True(selected.Success);
            Assert.False(removed.Success);
            Assert.Contains("referenced", removed.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            heldProfileLock?.Dispose();
        }

        Assert.True(Directory.Exists(runtime.InstallRoot));
        Assert.Contains(
            (await new ManagedRuntimeRegistry(_root).LoadAsync()).Entries,
            entry => entry.Id == runtime.Id);
    }

    private async Task<ManagedRuntimeEntry> RegisterPythonAsync(
        string providerId,
        string runtimeId,
        string version)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        string installRoot = Path.Combine(
            _root,
            "runtimes",
            providerId,
            parsed.ToString(),
            "x64");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), "runtime");
        ManagedRuntimeEntry entry = new(
            runtimeId,
            providerId,
            RuntimeKind.Python,
            parsed,
            RuntimeArchitecture.X64,
            installRoot,
            "python.exe",
            new string(providerId == "python-org" ? 'a' : 'c', 64),
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            ["stable"]);
        await new ManagedRuntimeRegistry(_root).UpsertAsync(entry);
        return entry;
    }

    private static async Task WaitUntilLockedAsync(string path, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            try
            {
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The global selection transaction lock was not acquired.");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
