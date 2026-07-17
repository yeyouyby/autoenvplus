using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Installation;

public sealed record ManagedRuntimeInstallRequest(
    ArchiveInstallPlan Plan,
    ManagedRuntimeEntry Entry,
    bool SetGlobalDefault);

public sealed record ManagedRuntimeInstallTransactionResult(
    bool Success,
    InstallOutcome InstallOutcome,
    bool Registered,
    bool GlobalDefaultUpdated,
    bool PendingCleanup,
    string? InstallRoot,
    string? Error);

public sealed class ManagedRuntimeInstallCoordinator
{
    private readonly string _managedRoot;
    private readonly IArchiveInstaller _installer;
    private readonly IManagedRuntimeRegistryStore _registry;
    private readonly IGlobalRuntimeProfileStore _globalProfile;
    private readonly ManagedStateLock _stateTransactionLock;

    public ManagedRuntimeInstallCoordinator(string managedRoot, HttpClient httpClient)
        : this(
            managedRoot,
            new ManagedArchiveInstaller(httpClient),
            new ManagedRuntimeRegistry(managedRoot),
            new GlobalRuntimeProfileStore(managedRoot))
    {
    }

    public ManagedRuntimeInstallCoordinator(
        string managedRoot,
        IArchiveInstaller installer,
        IManagedRuntimeRegistryStore registry,
        IGlobalRuntimeProfileStore globalProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _globalProfile = globalProfile ?? throw new ArgumentNullException(nameof(globalProfile));
        _stateTransactionLock = ManagedStateLock.CreateRuntimeTransaction(_managedRoot);
    }

    public async Task<ManagedRuntimeInstallTransactionResult> InstallAsync(
        ManagedRuntimeInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        using ManagedStateLock.Lease transactionLock = await _stateTransactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);

        RegistryLoadResult registryBefore = await LoadRegistryAsync(
            cancellationToken).ConfigureAwait(false);
        if (registryBefore.Errors.Count > 0)
        {
            return Failure(
                InstallOutcome.Failed,
                string.Join("; ", registryBefore.Errors));
        }

        ManagedRuntimeEntry? previousEntry = registryBefore.Entries.FirstOrDefault(entry =>
            entry.Id.Equals(request.Entry.Id, StringComparison.OrdinalIgnoreCase));
        RuntimeProfile profileBefore = request.SetGlobalDefault
            ? await LoadGlobalProfileAsync(cancellationToken).ConfigureAwait(false)
            : RuntimeProfile.Empty;
        InstallResult install = await _installer.InstallAsync(
            request.Plan,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (!install.Success)
        {
            return Failure(install.Outcome, install.Error ?? "The runtime installation failed.");
        }

        bool registryChanged = false;
        bool profileAttempted = false;
        try
        {
            RegistryLoadResult registered = await UpsertRegistryAsync(
                request.Entry,
                cancellationToken).ConfigureAwait(false);
            if (registered.Errors.Count > 0)
            {
                throw new InvalidDataException(string.Join("; ", registered.Errors));
            }

            registryChanged = true;
            if (request.SetGlobalDefault)
            {
                profileAttempted = true;
                await SetGlobalProfileAsync(
                    request.Entry,
                    new VersionSelector(
                        VersionSelectorKind.Exact,
                        request.Entry.Version),
                    cancellationToken).ConfigureAwait(false);
            }

            return new ManagedRuntimeInstallTransactionResult(
                true,
                install.Outcome,
                true,
                request.SetGlobalDefault,
                false,
                install.InstallRoot,
                null);
        }
        catch (OperationCanceledException)
        {
            bool pendingCleanup = await CompensateAsync(
                request,
                previousEntry,
                profileBefore,
                registryChanged,
                profileAttempted,
                install.Outcome).ConfigureAwait(false);
            if (pendingCleanup)
            {
                return new ManagedRuntimeInstallTransactionResult(
                    false,
                    install.Outcome,
                    false,
                    false,
                    true,
                    install.InstallRoot,
                    "The runtime install was cancelled, but its state or files could not be fully restored. Manual recovery is required.");
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            bool pendingCleanup = await CompensateAsync(
                request,
                previousEntry,
                profileBefore,
                registryChanged,
                profileAttempted,
                install.Outcome).ConfigureAwait(false);
            return new ManagedRuntimeInstallTransactionResult(
                false,
                install.Outcome,
                false,
                false,
                pendingCleanup,
                pendingCleanup ? install.InstallRoot : null,
                exception.Message);
        }
    }

    private async Task<bool> CompensateAsync(
        ManagedRuntimeInstallRequest request,
        ManagedRuntimeEntry? previousEntry,
        RuntimeProfile profileBefore,
        bool registryChanged,
        bool profileAttempted,
        InstallOutcome installOutcome)
    {
        bool stateRestored = true;
        if (profileAttempted)
        {
            try
            {
                await ReplaceGlobalProfileAsync(
                    profileBefore,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                stateRestored = false;
            }
        }

        if (registryChanged)
        {
            try
            {
                if (previousEntry is null)
                {
                    RegistryLoadResult removed = await RemoveRegistryAsync(
                        request.Entry.Id,
                        CancellationToken.None).ConfigureAwait(false);
                    if (removed.Errors.Count > 0)
                    {
                        stateRestored = false;
                    }
                }
                else
                {
                    RegistryLoadResult restored = await UpsertRegistryAsync(
                        previousEntry,
                        CancellationToken.None).ConfigureAwait(false);
                    if (restored.Errors.Count > 0)
                    {
                        stateRestored = false;
                    }
                }
            }
            catch
            {
                stateRestored = false;
            }
        }

        if (!stateRestored)
        {
            return true;
        }

        if (installOutcome != InstallOutcome.Installed)
        {
            return false;
        }

        return !TryDeleteNewInstall(request.Plan.DestinationRoot);
    }

    private Task<RegistryLoadResult> LoadRegistryAsync(CancellationToken cancellationToken) =>
        _registry is ManagedRuntimeRegistry registry
            ? registry.LoadWithinTransactionAsync(cancellationToken)
            : _registry.LoadAsync(cancellationToken);

    private Task<RegistryLoadResult> UpsertRegistryAsync(
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken) =>
        _registry is ManagedRuntimeRegistry registry
            ? registry.UpsertWithinTransactionAsync(entry, cancellationToken)
            : _registry.UpsertAsync(entry, cancellationToken);

    private Task<RegistryLoadResult> RemoveRegistryAsync(
        string id,
        CancellationToken cancellationToken) =>
        _registry is ManagedRuntimeRegistry registry
            ? registry.RemoveWithinTransactionAsync(id, cancellationToken)
            : _registry.RemoveAsync(id, cancellationToken);

    private Task<RuntimeProfile> LoadGlobalProfileAsync(CancellationToken cancellationToken) =>
        _globalProfile is GlobalRuntimeProfileStore profile
            ? profile.LoadWithinTransactionAsync(cancellationToken)
            : _globalProfile.LoadAsync(cancellationToken);

    private Task<RuntimeProfile> SetGlobalProfileAsync(
        ManagedRuntimeEntry entry,
        VersionSelector selector,
        CancellationToken cancellationToken) =>
        _globalProfile is GlobalRuntimeProfileStore profile
            ? profile.SetExactWithinTransactionAsync(
                entry.Kind,
                selector,
                entry.Id,
                entry.ProviderId,
                cancellationToken)
            : _globalProfile.SetAsync(entry.Kind, selector, cancellationToken);

    private Task<RuntimeProfile> ReplaceGlobalProfileAsync(
        RuntimeProfile profile,
        CancellationToken cancellationToken) =>
        _globalProfile is GlobalRuntimeProfileStore concreteProfile
            ? concreteProfile.ReplaceWithinTransactionAsync(profile, cancellationToken)
            : _globalProfile.ReplaceAsync(profile, cancellationToken);

    private bool TryDeleteNewInstall(string installRoot)
    {
        try
        {
            EnsureChildPath(_managedRoot, installRoot, "install cleanup root");
            ManagedPathSafety.EnsureOrdinaryDirectoryTree(
                _managedRoot,
                installRoot,
                "install cleanup root",
                allowMissing: true);
            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ValidateRequest(ManagedRuntimeInstallRequest request)
    {
        string planManagedRoot = Path.GetFullPath(request.Plan.ManagedRoot);
        string planDestination = Path.GetFullPath(request.Plan.DestinationRoot);
        string entryInstallRoot = Path.GetFullPath(request.Entry.InstallRoot);
        if (!planManagedRoot.Equals(_managedRoot, StringComparison.OrdinalIgnoreCase)
            || !planDestination.Equals(entryInstallRoot, StringComparison.OrdinalIgnoreCase)
            || !request.Plan.ExpectedExecutableRelativePath.Equals(
                request.Entry.ExecutableRelativePath,
                StringComparison.OrdinalIgnoreCase)
            || !request.Plan.Asset.PackageHash.Equals(
                request.Entry.PackageHash,
                StringComparison.OrdinalIgnoreCase)
            || request.Plan.Asset.HashAlgorithm != request.Entry.PackageHashAlgorithm
            || request.Plan.Asset.Release.Kind != request.Entry.Kind
            || request.Plan.Asset.Release.Version != request.Entry.Version
            || request.Plan.Asset.Release.Architecture != request.Entry.Architecture
            || !request.Plan.Asset.Release.ProviderId.Equals(
                request.Entry.ProviderId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The archive plan and managed runtime registry entry do not describe the same installation.",
                nameof(request));
        }

        EnsureChildPath(_managedRoot, planDestination, "install destination");
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} must remain inside the managed root.");
        }
    }

    private static ManagedRuntimeInstallTransactionResult Failure(
        InstallOutcome outcome,
        string error) => new(
            false,
            outcome,
            false,
            false,
            false,
            null,
            error);
}
