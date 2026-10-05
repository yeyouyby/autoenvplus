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
    private static readonly TimeSpan CleanupOwnershipTimeout = TimeSpan.FromSeconds(5);

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
        // Validate the transaction-lock path before any installer side effect,
        // but release it immediately so package preparation does not block
        // read-only runtime resolution for the duration of a download.
        using (ManagedStateLock.Lease preflightLock = await _stateTransactionLock.AcquireAsync(
                   cancellationToken).ConfigureAwait(false))
        {
        }

        InstallResult install = await _installer.InstallAsync(
            request.Plan,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (!install.Success)
        {
            return Failure(install.Outcome, install.Error ?? "The runtime installation failed.");
        }

        // Download, signature verification, and staging extraction do not
        // mutate the registry/profile and can take minutes. Keep them outside
        // the short global state transaction so existing shims and read-only
        // resolution remain available while a package is prepared.
        ManagedStateLock.Lease acquiredTransactionLock;
        try
        {
            acquiredTransactionLock = await _stateTransactionLock.AcquireAsync(
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            InstallCleanupDecision recoveryCleanupDecision =
                await ResolveCleanupDecisionAfterLockFailureAsync(
                    request,
                    install).ConfigureAwait(false);
            ManagedRuntimeInstallTransactionResult? consistencyFailure =
                CancellationFailureAfterInstall(request, install, recoveryCleanupDecision);
            if (consistencyFailure is not null)
            {
                return consistencyFailure;
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedStateFailure(exception))
        {
            InstallCleanupDecision recoveryCleanupDecision =
                await ResolveCleanupDecisionAfterLockFailureAsync(
                    request,
                    install).ConfigureAwait(false);
            return FailureAfterInstall(
                request,
                install,
                recoveryCleanupDecision,
                exception.Message);
        }

        using ManagedStateLock.Lease transactionLock = acquiredTransactionLock;
        RegistryLoadResult registryBefore;
        try
        {
            registryBefore = await LoadRegistryAsync(
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            InstallCleanupDecision loadCleanupDecision =
                await ResolveCleanupDecisionWithinTransactionAsync(
                    request,
                    install).ConfigureAwait(false);
            ManagedRuntimeInstallTransactionResult? consistencyFailure =
                CancellationFailureAfterInstall(request, install, loadCleanupDecision);
            if (consistencyFailure is not null)
            {
                return consistencyFailure;
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedStateFailure(exception))
        {
            return FailureAfterInstall(
                request,
                install,
                InstallCleanupDecision.Unknown,
                exception.Message);
        }

        if (registryBefore.Errors.Count > 0)
        {
            return FailureAfterInstall(
                request,
                install,
                InstallCleanupDecision.Unknown,
                string.Join("; ", registryBefore.Errors));
        }

        InstallCleanupDecision cleanupDecision = DetermineCleanupDecision(
            request,
            install,
            registryBefore);
        try
        {
            // The installer intentionally prepares and promotes outside the
            // global state lock. Revalidate after acquiring it so a concurrent
            // uninstall or path replacement cannot leave a missing or unsafe
            // installation registered as the transaction's final state.
            ValidateInstalledRuntime(request, install);
        }
        catch (Exception exception) when (IsExpectedStateFailure(exception))
        {
            return FailureAfterInstall(
                request,
                install,
                cleanupDecision,
                exception.Message);
        }

        ManagedRuntimeEntry? previousEntry = registryBefore.Entries.FirstOrDefault(entry =>
            entry.Id.Equals(request.Entry.Id, StringComparison.OrdinalIgnoreCase));
        RuntimeProfile profileBefore;
        try
        {
            profileBefore = request.SetGlobalDefault
                ? await LoadGlobalProfileAsync(cancellationToken).ConfigureAwait(false)
                : RuntimeProfile.Empty;
        }
        catch (OperationCanceledException)
        {
            ManagedRuntimeInstallTransactionResult? consistencyFailure =
                CancellationFailureAfterInstall(request, install, cleanupDecision);
            if (consistencyFailure is not null)
            {
                return consistencyFailure;
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedStateFailure(exception))
        {
            return FailureAfterInstall(
                request,
                install,
                cleanupDecision,
                exception.Message);
        }

        bool registryAttempted = false;
        bool profileAttempted = false;
        try
        {
            registryAttempted = true;
            RegistryLoadResult registered;
            try
            {
                registered = await UpsertRegistryAsync(
                    request.Entry,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The concrete registry observes cancellation before its
                // synchronous atomic replace. An injected store has no such
                // contract, so its commit state remains uncertain and must be
                // compensated like any other attempted write.
                if (_registry is ManagedRuntimeRegistry)
                {
                    registryAttempted = false;
                }

                throw;
            }

            if (registered.Errors.Count > 0)
            {
                throw new InvalidDataException(string.Join("; ", registered.Errors));
            }
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
                registryAttempted,
                profileAttempted,
                cleanupDecision).ConfigureAwait(false);
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
        catch (Exception exception) when (IsExpectedStateFailure(exception))
        {
            bool pendingCleanup = await CompensateAsync(
                request,
                previousEntry,
                profileBefore,
                registryAttempted,
                profileAttempted,
                cleanupDecision).ConfigureAwait(false);
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
        bool registryAttempted,
        bool profileAttempted,
        InstallCleanupDecision cleanupDecision)
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

        // A registry write can replace the authoritative file and then fail a
        // post-commit safety check. Restore the pre-transaction state whenever
        // a write was attempted instead of inferring commit state from whether
        // the async call returned normally.
        if (registryAttempted)
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

        if (cleanupDecision != InstallCleanupDecision.Delete)
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

    private void ValidateInstalledRuntime(
        ManagedRuntimeInstallRequest request,
        InstallResult install)
    {
        string destinationRoot = Path.GetFullPath(request.Plan.DestinationRoot);
        if (string.IsNullOrWhiteSpace(install.InstallRoot)
            || !Path.GetFullPath(install.InstallRoot).Equals(
                destinationRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The archive installer returned an unexpected installation root.");
        }

        ManagedPathSafety.EnsureNoReparsePointInPath(destinationRoot);
        if (!Directory.Exists(destinationRoot))
        {
            throw new IOException(
                "The installed runtime disappeared before its state could be committed.");
        }

        ManagedPathSafety.EnsureOrdinaryFile(
            _managedRoot,
            request.Entry.ExecutablePath,
            "installed runtime entry point");
    }

    private ManagedRuntimeInstallTransactionResult FailureAfterInstall(
        ManagedRuntimeInstallRequest request,
        InstallResult install,
        InstallCleanupDecision cleanupDecision,
        string error)
    {
        bool pendingCleanup = cleanupDecision switch
        {
            InstallCleanupDecision.Delete =>
                !TryDeleteNewInstall(request.Plan.DestinationRoot),
            InstallCleanupDecision.Unknown =>
                install.Outcome == InstallOutcome.Installed,
            _ => false,
        };
        if (cleanupDecision == InstallCleanupDecision.Unknown
            && install.Outcome == InstallOutcome.Installed)
        {
            error += " The newly installed files were left in place because their registry "
                + "ownership could not be determined safely. Manual recovery is required.";
        }

        return new ManagedRuntimeInstallTransactionResult(
            false,
            install.Outcome,
            false,
            false,
            pendingCleanup,
            pendingCleanup ? request.Plan.DestinationRoot : null,
            error);
    }

    private ManagedRuntimeInstallTransactionResult? CancellationFailureAfterInstall(
        ManagedRuntimeInstallRequest request,
        InstallResult install,
        InstallCleanupDecision cleanupDecision)
    {
        if (cleanupDecision is InstallCleanupDecision.NotRequired
            or InstallCleanupDecision.Preserve)
        {
            return null;
        }

        if (cleanupDecision == InstallCleanupDecision.Delete
            && TryDeleteNewInstall(request.Plan.DestinationRoot))
        {
            return null;
        }

        return new ManagedRuntimeInstallTransactionResult(
            false,
            install.Outcome,
            false,
            false,
            true,
            request.Plan.DestinationRoot,
            "The runtime install was cancelled before state commit, but the newly installed files could not be safely removed. Manual recovery is required.");
    }

    private async Task<InstallCleanupDecision> ResolveCleanupDecisionAfterLockFailureAsync(
        ManagedRuntimeInstallRequest request,
        InstallResult install)
    {
        if (install.Outcome != InstallOutcome.Installed)
        {
            return InstallCleanupDecision.NotRequired;
        }

        try
        {
            using CancellationTokenSource timeout = new(CleanupOwnershipTimeout);
            using ManagedStateLock.Lease recoveryLock =
                await _stateTransactionLock.AcquireAsync(
                    timeout.Token).ConfigureAwait(false);
            return await ResolveCleanupDecisionWithinTransactionAsync(
                request,
                install).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedStateFailure(exception)
            || exception is OperationCanceledException)
        {
            return InstallCleanupDecision.Unknown;
        }
    }

    private async Task<InstallCleanupDecision> ResolveCleanupDecisionWithinTransactionAsync(
        ManagedRuntimeInstallRequest request,
        InstallResult install)
    {
        if (install.Outcome != InstallOutcome.Installed)
        {
            return InstallCleanupDecision.NotRequired;
        }

        try
        {
            using CancellationTokenSource timeout = new(CleanupOwnershipTimeout);
            RegistryLoadResult registry = await LoadRegistryAsync(
                timeout.Token).ConfigureAwait(false);
            return registry.Errors.Count == 0
                ? DetermineCleanupDecision(request, install, registry)
                : InstallCleanupDecision.Unknown;
        }
        catch (Exception exception) when (IsExpectedStateFailure(exception)
            || exception is OperationCanceledException)
        {
            return InstallCleanupDecision.Unknown;
        }
    }

    private static InstallCleanupDecision DetermineCleanupDecision(
        ManagedRuntimeInstallRequest request,
        InstallResult install,
        RegistryLoadResult registry)
    {
        if (install.Outcome != InstallOutcome.Installed)
        {
            return InstallCleanupDecision.NotRequired;
        }

        string destinationRoot = Path.GetFullPath(request.Plan.DestinationRoot);
        bool registered = registry.Entries.Any(entry =>
            Path.GetFullPath(entry.InstallRoot).Equals(
                destinationRoot,
                StringComparison.OrdinalIgnoreCase));
        return registered
            ? InstallCleanupDecision.Preserve
            : InstallCleanupDecision.Delete;
    }

    private static bool IsExpectedStateFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or System.Text.Json.JsonException;

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

    private enum InstallCleanupDecision
    {
        NotRequired,
        Delete,
        Preserve,
        Unknown,
    }
}
