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
    }

    public async Task<ManagedRuntimeInstallTransactionResult> InstallAsync(
        ManagedRuntimeInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        RegistryLoadResult registryBefore = await _registry.LoadAsync(
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
            ? await _globalProfile.LoadAsync(cancellationToken).ConfigureAwait(false)
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
            RegistryLoadResult registered = await _registry.UpsertAsync(
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
                await _globalProfile.SetAsync(
                    request.Entry.Kind,
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
            await CompensateAsync(
                request,
                previousEntry,
                profileBefore,
                registryChanged,
                profileAttempted,
                install.Outcome).ConfigureAwait(false);
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
                await _globalProfile.ReplaceAsync(
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
                    RegistryLoadResult removed = await _registry.RemoveAsync(
                        request.Entry.Id,
                        CancellationToken.None).ConfigureAwait(false);
                    if (removed.Errors.Count > 0)
                    {
                        stateRestored = false;
                    }
                }
                else
                {
                    RegistryLoadResult restored = await _registry.UpsertAsync(
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

        if (installOutcome != InstallOutcome.Installed || !stateRestored)
        {
            return installOutcome == InstallOutcome.Installed && !stateRestored;
        }

        return !TryDeleteNewInstall(request.Plan.DestinationRoot);
    }

    private bool TryDeleteNewInstall(string installRoot)
    {
        try
        {
            EnsureChildPath(_managedRoot, installRoot, "install cleanup root");
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
