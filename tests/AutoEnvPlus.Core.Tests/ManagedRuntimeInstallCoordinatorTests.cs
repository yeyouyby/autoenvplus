using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedRuntimeInstallCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-InstallCoordinator-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallAsync_RegistersAndSetsGlobalDefault()
    {
        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: true);
        FakeInstaller installer = new(InstallOutcome.Installed, createDestination: true);
        FakeRegistry registry = new();
        FakeGlobalProfile profile = new();
        ManagedRuntimeInstallCoordinator coordinator = new(
            _root,
            installer,
            registry,
            profile);

        ManagedRuntimeInstallTransactionResult result = await coordinator.InstallAsync(request);

        Assert.True(result.Success);
        Assert.True(result.Registered);
        Assert.True(result.GlobalDefaultUpdated);
        Assert.True(Directory.Exists(request.Plan.DestinationRoot));
        Assert.Same(request.Entry, registry.Entries.Single());
        Assert.Equal(
            request.Entry.Version,
            profile.Profile.Selections[RuntimeKind.NodeJs].Version);
    }

    [Fact]
    public async Task InstallAsync_RegistryFailureDeletesOnlyNewInstallation()
    {
        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: false);
        FakeInstaller installer = new(InstallOutcome.Installed, createDestination: true);
        FakeRegistry registry = new() { FailUpsert = true };
        FakeGlobalProfile profile = new();
        ManagedRuntimeInstallCoordinator coordinator = new(
            _root,
            installer,
            registry,
            profile);

        ManagedRuntimeInstallTransactionResult result = await coordinator.InstallAsync(request);

        Assert.False(result.Success);
        Assert.False(result.PendingCleanup);
        Assert.False(Directory.Exists(request.Plan.DestinationRoot));
        Assert.Empty(registry.Entries);
    }

    [Fact]
    public async Task InstallAsync_ProfileFailureRestoresRegistryProfileAndDeletesNewInstall()
    {
        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: true);
        ManagedRuntimeEntry previous = request.Entry with
        {
            Version = RuntimeVersion.Parse("20.0.0"),
            PackageHash = new string('b', 64),
        };
        RuntimeProfile originalProfile = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.NodeJs] = VersionSelector.Parse("20"),
        });
        FakeInstaller installer = new(InstallOutcome.Installed, createDestination: true);
        FakeRegistry registry = new();
        registry.Entries.Add(previous);
        FakeGlobalProfile profile = new(originalProfile) { FailSet = true };
        ManagedRuntimeInstallCoordinator coordinator = new(
            _root,
            installer,
            registry,
            profile);

        ManagedRuntimeInstallTransactionResult result = await coordinator.InstallAsync(request);

        Assert.False(result.Success);
        Assert.False(result.PendingCleanup);
        Assert.False(Directory.Exists(request.Plan.DestinationRoot));
        Assert.Equal(previous, registry.Entries.Single());
        Assert.Equal("20", profile.Profile.Selections[RuntimeKind.NodeJs].ToString());
    }

    [Fact]
    public async Task InstallAsync_DoesNotDeleteAlreadyInstalledDirectoryWhenRegistrationFails()
    {
        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: false);
        Directory.CreateDirectory(request.Plan.DestinationRoot);
        File.WriteAllText(request.Entry.ExecutablePath, "existing");
        FakeInstaller installer = new(InstallOutcome.AlreadyInstalled, createDestination: false);
        FakeRegistry registry = new() { FailUpsert = true };
        FakeGlobalProfile profile = new();
        ManagedRuntimeInstallCoordinator coordinator = new(
            _root,
            installer,
            registry,
            profile);

        ManagedRuntimeInstallTransactionResult result = await coordinator.InstallAsync(request);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(request.Plan.DestinationRoot));
        Assert.True(File.Exists(request.Entry.ExecutablePath));
    }

    [Fact]
    public async Task InstallAsync_RejectsMismatchedEntryBeforeInstallerRuns()
    {
        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: false);
        FakeInstaller installer = new(InstallOutcome.Installed, createDestination: true);
        ManagedRuntimeInstallCoordinator coordinator = new(
            _root,
            installer,
            new FakeRegistry(),
            new FakeGlobalProfile());
        request = request with
        {
            Entry = request.Entry with { PackageHash = new string('f', 64) },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.InstallAsync(request));

        Assert.Equal(0, installer.CallCount);
    }

    [Fact]
    public async Task InstallAsync_RejectsMismatchedHashAlgorithmBeforeInstallerRuns()
    {
        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: false);
        FakeInstaller installer = new(InstallOutcome.Installed, createDestination: true);
        ManagedRuntimeInstallCoordinator coordinator = new(
            _root,
            installer,
            new FakeRegistry(),
            new FakeGlobalProfile());
        request = request with
        {
            Entry = request.Entry with
            {
                PackageHashAlgorithm = PackageHashAlgorithm.Sha512,
            },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.InstallAsync(request));

        Assert.Equal(0, installer.CallCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ManagedRuntimeInstallRequest CreateRequest(bool setGlobalDefault)
    {
        RuntimeRelease release = new(
            "nodejs-official",
            "v22.17.0",
            RuntimeKind.NodeJs,
            RuntimeVersion.Parse("22.17.0"),
            RuntimeArchitecture.X64,
            "Node.js",
            new DateOnly(2025, 6, 24),
            ["lts"],
            false);
        string sha256 = new string('a', 64);
        RuntimePackageAsset asset = new(
            release,
            new Uri("https://example.test/node.zip"),
            "node.zip",
            sha256,
            RuntimePackageFormat.Zip,
            "node-v22.17.0-win-x64",
            [
                new PackageVerification(
                    PackageVerificationKind.ProviderChecksum,
                    new Uri("https://example.test/SHASUMS256.txt"),
                    "node.zip",
                    "SHA-256",
                    sha256),
            ],
            [],
            PackageAuthenticityRequirement.ChecksumEvidence);
        string destination = Path.Combine(
            _root,
            "runtimes",
            "nodejs",
            "22.17.0",
            "x64");
        ArchiveInstallPlan plan = new(
            asset,
            _root,
            destination,
            "node.exe");
        ManagedRuntimeEntry entry = new(
            "nodejs-22.17.0-x64",
            release.ProviderId,
            release.Kind,
            release.Version,
            release.Architecture,
            destination,
            "node.exe",
            sha256,
            DateTimeOffset.UtcNow,
            release.Channels);
        return new ManagedRuntimeInstallRequest(plan, entry, setGlobalDefault);
    }

    private sealed class FakeInstaller(
        InstallOutcome outcome,
        bool createDestination) : IArchiveInstaller
    {
        public int CallCount { get; private set; }

        public Task<InstallResult> InstallAsync(
            ArchiveInstallPlan plan,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (createDestination)
            {
                Directory.CreateDirectory(plan.DestinationRoot);
                File.WriteAllText(
                    Path.Combine(plan.DestinationRoot, plan.ExpectedExecutableRelativePath),
                    "runtime");
            }

            return Task.FromResult(new InstallResult(
                outcome,
                outcome == InstallOutcome.Failed ? null : plan.DestinationRoot,
                outcome == InstallOutcome.Failed ? "simulated install failure" : null));
        }
    }

    private sealed class FakeRegistry : IManagedRuntimeRegistryStore
    {
        public List<ManagedRuntimeEntry> Entries { get; } = [];

        public bool FailUpsert { get; set; }

        public Task<RegistryLoadResult> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RegistryLoadResult(Entries.ToArray(), []));
        }

        public Task<RegistryLoadResult> UpsertAsync(
            ManagedRuntimeEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailUpsert)
            {
                throw new IOException("simulated registry failure");
            }

            Entries.RemoveAll(candidate => candidate.Id.Equals(
                entry.Id,
                StringComparison.OrdinalIgnoreCase));
            Entries.Add(entry);
            return Task.FromResult(new RegistryLoadResult(Entries.ToArray(), []));
        }

        public Task<RegistryLoadResult> RemoveAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.RemoveAll(candidate => candidate.Id.Equals(
                id,
                StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new RegistryLoadResult(Entries.ToArray(), []));
        }
    }

    private sealed class FakeGlobalProfile : IGlobalRuntimeProfileStore
    {
        public FakeGlobalProfile(RuntimeProfile? profile = null)
        {
            Profile = profile ?? RuntimeProfile.Empty;
        }

        public RuntimeProfile Profile { get; private set; }

        public bool FailSet { get; set; }

        public Task<RuntimeProfile> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Profile);
        }

        public Task<RuntimeProfile> SetAsync(
            RuntimeKind kind,
            VersionSelector selector,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSet)
            {
                throw new IOException("simulated profile failure");
            }

            Dictionary<RuntimeKind, VersionSelector> selections = new(Profile.Selections)
            {
                [kind] = selector,
            };
            Profile = new RuntimeProfile(selections);
            return Task.FromResult(Profile);
        }

        public Task<RuntimeProfile> ReplaceAsync(
            RuntimeProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Profile = profile;
            return Task.FromResult(Profile);
        }
    }
}
