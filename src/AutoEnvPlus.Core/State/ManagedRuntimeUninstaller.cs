using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public sealed record ManagedRuntimeUninstallPlan(
    ManagedRuntimeEntry Runtime,
    IReadOnlyList<RuntimeReference> References,
    string TrashPath)
{
    public bool IsReferenced => References.Count > 0;
}

public sealed record ManagedRuntimeUninstallResult(
    bool Success,
    bool RemovedFromRegistry,
    bool PendingTrashCleanup,
    string? Error);

public sealed class ManagedRuntimeUninstaller
{
    private readonly string _managedRoot;
    private readonly ManagedRuntimeRegistry _registry;
    private readonly GlobalRuntimeProfileStore _globalProfile;
    private readonly KnownProjectStore _knownProjects;
    private readonly ManagedStateLock _stateTransactionLock;

    public ManagedRuntimeUninstaller(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _registry = new ManagedRuntimeRegistry(_managedRoot);
        _globalProfile = new GlobalRuntimeProfileStore(_managedRoot);
        _knownProjects = new KnownProjectStore(_managedRoot);
        _stateTransactionLock = ManagedStateLock.CreateRuntimeTransaction(_managedRoot);
    }

    public async Task<ManagedRuntimeUninstallPlan> CreatePlanAsync(
        string runtimeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        RegistryLoadResult registry;
        ManagedRuntimeEntry runtime;
        RuntimeProfile global;
        {
            using ManagedStateLock.Lease transactionLock =
                await _stateTransactionLock.AcquireAsync(
                    cancellationToken).ConfigureAwait(false);
            registry = await _registry.LoadWithinTransactionAsync(
                cancellationToken).ConfigureAwait(false);
            if (registry.Errors.Count > 0)
            {
                throw new InvalidDataException(string.Join("; ", registry.Errors));
            }

            runtime = registry.Entries.FirstOrDefault(entry =>
                entry.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Managed runtime '{runtimeId}' is not registered.");
            global = await _globalProfile.LoadWithinTransactionAsync(
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<KnownProject> projects = await _knownProjects.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RuntimeReference> references = await new RuntimeReferenceScanner().ScanAsync(
            runtime,
            registry.Entries,
            global,
            projects,
            cancellationToken).ConfigureAwait(false);
        string trashPath = Path.Combine(
            _managedRoot,
            ".trash",
            $"{SanitizeFileName(runtime.Id)}-{Guid.NewGuid():N}");
        EnsureChildPath(_managedRoot, trashPath, "trash path");
        return new ManagedRuntimeUninstallPlan(runtime, references, trashPath);
    }

    public async Task<ManagedRuntimeUninstallResult> ExecuteAsync(
        ManagedRuntimeUninstallPlan plan,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsReferenced && !force)
        {
            return ReferencedFailure(plan.References);
        }

        using ManagedStateLock.Lease transactionLock = await _stateTransactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RegistryLoadResult currentRegistry = await _registry.LoadWithinTransactionAsync(
            cancellationToken).ConfigureAwait(false);
        if (currentRegistry.Errors.Count > 0)
        {
            return new ManagedRuntimeUninstallResult(
                false,
                false,
                false,
                string.Join("; ", currentRegistry.Errors));
        }

        ManagedRuntimeEntry? current = currentRegistry.Entries.FirstOrDefault(entry =>
            entry.Id.Equals(plan.Runtime.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null || !EntriesEquivalent(current, plan.Runtime))
        {
            return new ManagedRuntimeUninstallResult(
                false,
                false,
                false,
                "The managed runtime changed after the uninstall plan was created; refresh the plan.");
        }

        if (!force)
        {
            RuntimeProfile currentGlobal = await _globalProfile.LoadWithinTransactionAsync(
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<KnownProject> currentProjects = await _knownProjects.LoadAsync(
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<RuntimeReference> currentReferences =
                await new RuntimeReferenceScanner().ScanAsync(
                    current,
                    currentRegistry.Entries,
                    currentGlobal,
                    currentProjects,
                    cancellationToken).ConfigureAwait(false);
            if (currentReferences.Count > 0)
            {
                return ReferencedFailure(currentReferences);
            }
        }

        string installRoot = Path.GetFullPath(current.InstallRoot);
        EnsureChildPath(_managedRoot, installRoot, "runtime install root");
        string trashPath = Path.GetFullPath(plan.TrashPath);
        EnsureChildPath(Path.Combine(_managedRoot, ".trash"), trashPath, "trash path");
        bool moved = false;
        bool registryRemoved = false;
        try
        {
            ManagedPathSafety.EnsureOrdinaryDirectoryTree(
                _managedRoot,
                installRoot,
                "runtime install root",
                allowMissing: true);
            if (Directory.Exists(installRoot))
            {
                ManagedPathSafety.CreateOrdinaryDirectoryPath(
                    _managedRoot,
                    Path.GetDirectoryName(trashPath)!,
                    "runtime trash directory");
                ManagedPathSafety.EnsureNoReparsePointInPath(trashPath);
                Directory.Move(installRoot, trashPath);
                moved = true;
                ManagedPathSafety.EnsureOrdinaryDirectoryTree(
                    _managedRoot,
                    trashPath,
                    "quarantined runtime",
                    allowMissing: false);
            }

            RegistryLoadResult updated = await _registry.RemoveWithinTransactionAsync(
                current.Id,
                cancellationToken).ConfigureAwait(false);
            if (updated.Errors.Count > 0)
            {
                throw new InvalidDataException(string.Join("; ", updated.Errors));
            }

            registryRemoved = true;
        }
        catch (OperationCanceledException exception)
        {
            if (!TryRestoreMovedRuntime(
                    _managedRoot,
                    installRoot,
                    trashPath,
                    moved,
                    out string? restoreError))
            {
                throw new IOException(restoreError, exception);
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            bool restored = TryRestoreMovedRuntime(
                _managedRoot,
                installRoot,
                trashPath,
                moved,
                out string? restoreError);

            return new ManagedRuntimeUninstallResult(
                false,
                registryRemoved,
                Directory.Exists(trashPath),
                restored ? exception.Message : $"{exception.Message} {restoreError}");
        }

        transactionLock.Dispose();
        bool pendingCleanup = false;
        if (moved && Directory.Exists(trashPath))
        {
            try
            {
                ManagedPathSafety.EnsureOrdinaryDirectoryTree(
                    _managedRoot,
                    trashPath,
                    "quarantined runtime cleanup",
                    allowMissing: false);
                Directory.Delete(trashPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                pendingCleanup = true;
            }
        }

        return new ManagedRuntimeUninstallResult(true, true, pendingCleanup, null);
    }

    private static ManagedRuntimeUninstallResult ReferencedFailure(
        IReadOnlyList<RuntimeReference> references) => new(
        false,
        false,
        false,
        "The runtime is still referenced: "
        + string.Join("; ", references.Select(reference =>
            $"{reference.Kind} {reference.Owner} ({reference.Detail})")));

    private static bool TryRestoreMovedRuntime(
        string managedRoot,
        string installRoot,
        string trashPath,
        bool moved,
        out string? error)
    {
        error = null;
        if (!moved)
        {
            return true;
        }

        if (Directory.Exists(installRoot) || !Directory.Exists(trashPath))
        {
            error = RestoreFailure(trashPath);
            return false;
        }

        try
        {
            ManagedPathSafety.EnsureOrdinaryDirectoryTree(
                managedRoot,
                trashPath,
                "quarantined runtime restore source",
                allowMissing: false);
            ManagedPathSafety.CreateOrdinaryDirectoryPath(
                managedRoot,
                Path.GetDirectoryName(installRoot)!,
                "runtime restore parent");
            ManagedPathSafety.EnsureNoReparsePointInPath(installRoot);
            Directory.Move(trashPath, installRoot);
            return true;
        }
        catch
        {
            error = RestoreFailure(trashPath);
            return false;
        }
    }

    private static string RestoreFailure(string trashPath) =>
        "The runtime could not be restored safely; its quarantined files remain at the managed "
        + $"trash path '{trashPath}'. Resolve the path conflict before retrying.";

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static bool EntriesEquivalent(
        ManagedRuntimeEntry left,
        ManagedRuntimeEntry right) =>
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)
        && left.ProviderId.Equals(right.ProviderId, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && left.Version == right.Version
        && left.Architecture == right.Architecture
        && Path.GetFullPath(left.InstallRoot).Equals(
            Path.GetFullPath(right.InstallRoot),
            StringComparison.OrdinalIgnoreCase)
        && left.ExecutableRelativePath.Equals(right.ExecutableRelativePath, StringComparison.OrdinalIgnoreCase)
        && left.PackageHashAlgorithm == right.PackageHashAlgorithm
        && left.PackageHash.Equals(right.PackageHash, StringComparison.OrdinalIgnoreCase)
        && (left.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            (right.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

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
}
