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
    public async Task InstallAsync_CompensationRefusesNestedReparsePointCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: false);
        Directory.CreateDirectory(request.Plan.DestinationRoot);
        File.WriteAllText(request.Entry.ExecutablePath, "runtime");
        string externalDirectory = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Install-External-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);
        string marker = Path.Combine(externalDirectory, "external-marker.txt");
        File.WriteAllText(marker, "external");
        string nestedLink = Path.Combine(request.Plan.DestinationRoot, "linked-cache");
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

            FakeRegistry registry = new() { FailUpsert = true };
            ManagedRuntimeInstallCoordinator coordinator = new(
                _root,
                new FakeInstaller(InstallOutcome.Installed, createDestination: false),
                registry,
                new FakeGlobalProfile());

            ManagedRuntimeInstallTransactionResult result = await coordinator.InstallAsync(request);

            Assert.False(result.Success);
            Assert.True(result.PendingCleanup);
            Assert.True(File.Exists(marker));
            Assert.True(Directory.Exists(request.Plan.DestinationRoot));
            Assert.Empty(registry.Entries);
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
    public async Task InstallAsync_CancellationReturnsConsistencyFailureWhenCleanupIsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ManagedRuntimeInstallRequest request = CreateRequest(setGlobalDefault: true);
        Directory.CreateDirectory(request.Plan.DestinationRoot);
        File.WriteAllText(request.Entry.ExecutablePath, "runtime");
        string externalDirectory = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Cancel-External-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);
        string marker = Path.Combine(externalDirectory, "external-marker.txt");
        File.WriteAllText(marker, "external");
        string nestedLink = Path.Combine(request.Plan.DestinationRoot, "linked-cache");
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

            FakeRegistry registry = new();
            FakeGlobalProfile profile = new() { CancelSet = true };
            ManagedRuntimeInstallCoordinator coordinator = new(
                _root,
                new FakeInstaller(InstallOutcome.Installed, createDestination: false),
                registry,
                profile);

            ManagedRuntimeInstallTransactionResult result = await coordinator.InstallAsync(request);

            Assert.False(result.Success);
            Assert.True(result.PendingCleanup);
            Assert.Equal(request.Plan.DestinationRoot, result.InstallRoot);
            Assert.Contains("Manual recovery", result.Error, StringComparison.Ordinal);
            Assert.True(File.Exists(marker));
            Assert.True(Directory.Exists(request.Plan.DestinationRoot));
            Assert.Empty(registry.Entries);
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
    public async Task InstallAsync_ConcurrentFailureCannotRollbackLaterSuccessfulCommit()
    {
        const string sharedRuntimeId = "nodejs-shared-x64";
        ManagedRuntimeInstallRequest failingRequest = CreateRequest(
            setGlobalDefault: true,
            version: "22.17.0",
            runtimeId: sharedRuntimeId);
        ManagedRuntimeInstallRequest successfulRequest = CreateRequest(
            setGlobalDefault: true,
            version: "24.4.1",
            runtimeId: sharedRuntimeId);
        FakeRegistry registry = new();
        BlockingFailureProfile profile = new(failingRequest.Entry.Version);
        ManagedRuntimeInstallCoordinator failingCoordinator = new(
            _root,
            new FakeInstaller(InstallOutcome.Installed, createDestination: true),
            registry,
            profile);
        ManagedRuntimeInstallCoordinator successfulCoordinator = new(
            _root,
            new FakeInstaller(InstallOutcome.Installed, createDestination: true),
            registry,
            profile);

        Task<ManagedRuntimeInstallTransactionResult> failing =
            failingCoordinator.InstallAsync(failingRequest);
        await profile.FailingSetReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ManagedRuntimeInstallTransactionResult> successful =
            successfulCoordinator.InstallAsync(successfulRequest);
        await Task.Delay(150);
        Assert.False(successful.IsCompleted);

        profile.ReleaseFailingSet.TrySetResult(true);
        ManagedRuntimeInstallTransactionResult[] results = await Task.WhenAll(
            failing,
            successful);

        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
        Assert.Equal(successfulRequest.Entry, Assert.Single(registry.Entries));
        Assert.Equal(
            successfulRequest.Entry.Version,
            profile.Profile.Selections[RuntimeKind.NodeJs].Version);
        Assert.True(Directory.Exists(successfulRequest.Plan.DestinationRoot));
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

    [Fact]
    public async Task InstallAsync_RejectsReparsePointTransactionLockBeforeInstallerRuns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string stateDirectory = Path.Combine(_root, "state");
        Directory.CreateDirectory(stateDirectory);
        string lockPath = Path.Combine(
            stateDirectory,
            "managed-runtime-install-state.lock");
        string externalLock = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Install-State-Lock-{Guid.NewGuid():N}.lock");
        await File.WriteAllTextAsync(externalLock, "lock");
        FakeInstaller installer = new(InstallOutcome.Installed, createDestination: true);
        try
        {
            try
            {
                File.CreateSymbolicLink(lockPath, externalLock);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            ManagedRuntimeInstallCoordinator coordinator = new(
                _root,
                installer,
                new FakeRegistry(),
                new FakeGlobalProfile());

            IOException unsafePathException = await Assert.ThrowsAnyAsync<IOException>(() =>
                coordinator.InstallAsync(CreateRequest(setGlobalDefault: true)));

            Assert.Contains(
                "reparse",
                unsafePathException.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, installer.CallCount);
        }
        finally
        {
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }

            if (File.Exists(externalLock))
            {
                File.Delete(externalLock);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ManagedRuntimeInstallRequest CreateRequest(
        bool setGlobalDefault,
        string version = "22.17.0",
        string? runtimeId = null)
    {
        RuntimeVersion parsedVersion = RuntimeVersion.Parse(version);
        RuntimeRelease release = new(
            "nodejs-official",
            $"v{version}",
            RuntimeKind.NodeJs,
            parsedVersion,
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
            version,
            "x64");
        ArchiveInstallPlan plan = new(
            asset,
            _root,
            destination,
            "node.exe");
        ManagedRuntimeEntry entry = new(
            runtimeId ?? $"nodejs-{version}-x64",
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
            lock (Entries)
            {
                return Task.FromResult(new RegistryLoadResult(Entries.ToArray(), []));
            }
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

            lock (Entries)
            {
                Entries.RemoveAll(candidate => candidate.Id.Equals(
                    entry.Id,
                    StringComparison.OrdinalIgnoreCase));
                Entries.Add(entry);
                return Task.FromResult(new RegistryLoadResult(Entries.ToArray(), []));
            }
        }

        public Task<RegistryLoadResult> RemoveAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Entries)
            {
                Entries.RemoveAll(candidate => candidate.Id.Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(new RegistryLoadResult(Entries.ToArray(), []));
            }
        }
    }

    private sealed class BlockingFailureProfile(
        RuntimeVersion failingVersion) : IGlobalRuntimeProfileStore
    {
        private readonly object _sync = new();
        private RuntimeProfile _profile = RuntimeProfile.Empty;
        private int _failureStarted;

        public TaskCompletionSource<bool> FailingSetReached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFailingSet { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeProfile Profile
        {
            get
            {
                lock (_sync)
                {
                    return _profile;
                }
            }
        }

        public Task<RuntimeProfile> LoadAsync(
            CancellationToken cancellationToken = default)
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
            if (selector.Version == failingVersion
                && Interlocked.CompareExchange(ref _failureStarted, 1, 0) == 0)
            {
                return FailSetAsync(cancellationToken);
            }

            lock (_sync)
            {
                Dictionary<RuntimeKind, VersionSelector> selections = new(
                    _profile.Selections)
                {
                    [kind] = selector,
                };
                _profile = new RuntimeProfile(selections);
                return Task.FromResult(_profile);
            }
        }

        public Task<RuntimeProfile> ReplaceAsync(
            RuntimeProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _profile = profile;
                return Task.FromResult(_profile);
            }
        }

        private async Task<RuntimeProfile> FailSetAsync(CancellationToken cancellationToken)
        {
            FailingSetReached.TrySetResult(true);
            await ReleaseFailingSet.Task.WaitAsync(cancellationToken);
            throw new IOException("simulated profile failure");
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

        public bool CancelSet { get; set; }

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
            if (CancelSet)
            {
                throw new OperationCanceledException("simulated profile cancellation");
            }

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
