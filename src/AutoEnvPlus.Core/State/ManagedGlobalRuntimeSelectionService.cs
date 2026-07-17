using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public sealed record ManagedGlobalRuntimeSelectionResult(
    RuntimeProfile? Profile,
    ManagedRuntimeEntry? Entry,
    IReadOnlyList<string> Errors)
{
    public bool Success => Profile is not null && Entry is not null && Errors.Count == 0;
}

public sealed class ManagedGlobalRuntimeSelectionService
{
    private readonly string _managedRoot;
    private readonly ManagedRuntimeRegistry _registry;
    private readonly GlobalRuntimeProfileStore _globalProfile;
    private readonly ManagedStateLock _stateTransactionLock;

    public ManagedGlobalRuntimeSelectionService(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _registry = new ManagedRuntimeRegistry(_managedRoot);
        _globalProfile = new GlobalRuntimeProfileStore(_managedRoot);
        _stateTransactionLock = ManagedStateLock.CreateRuntimeTransaction(_managedRoot);
    }

    public async Task<ManagedGlobalRuntimeSelectionResult> SetAsync(
        RuntimeKind kind,
        VersionSelector selector,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        ManagedRuntimeEntry? expectedEntry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (expectedEntry is not null && expectedEntry.Kind != kind)
        {
            throw new ArgumentException(
                "The expected runtime entry must have the requested runtime kind.",
                nameof(expectedEntry));
        }

        using ManagedStateLock.Lease transactionLock = await _stateTransactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RegistryLoadResult registry = await _registry.LoadWithinTransactionAsync(
            cancellationToken).ConfigureAwait(false);
        if (registry.Errors.Count > 0)
        {
            return Failure(registry.Errors);
        }

        RuntimeProfile requested = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [kind] = selector,
        });
        ManagedRuntimeResolutionResult resolution =
            ManagedRuntimeResolutionService.ResolveRegistered(
                kind,
                new RuntimeResolutionContext(Global: requested),
                registry.Entries,
                architecture,
                expectedEntry?.Id,
                expectedEntry?.ProviderId);
        if (!resolution.Success)
        {
            return Failure(resolution.Errors);
        }

        ManagedRuntimeEntry entry = resolution.Entry!;
        if (expectedEntry is not null && !EntriesEquivalent(expectedEntry, entry))
        {
            return Failure(
                "The selected managed runtime changed after it was displayed; refresh and confirm it again.");
        }

        try
        {
            ManagedPathSafety.EnsureOrdinaryFile(
                _managedRoot,
                entry.ExecutablePath,
                "selected managed runtime executable");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return Failure(
                "The selected managed runtime executable is missing or unsafe.");
        }

        RuntimeProfile profile = await _globalProfile.SetExactWithinTransactionAsync(
            kind,
            selector,
            entry.Id,
            entry.ProviderId,
            cancellationToken).ConfigureAwait(false);
        return new ManagedGlobalRuntimeSelectionResult(profile, entry, []);
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
        && left.ExecutableRelativePath.Equals(
            right.ExecutableRelativePath,
            StringComparison.OrdinalIgnoreCase)
        && left.PackageHashAlgorithm == right.PackageHashAlgorithm
        && left.PackageHash.Equals(right.PackageHash, StringComparison.OrdinalIgnoreCase)
        && left.InstalledAtUtc == right.InstalledAtUtc
        && (left.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            (right.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static ManagedGlobalRuntimeSelectionResult Failure(
        IReadOnlyList<string> errors) => new(null, null, errors);

    private static ManagedGlobalRuntimeSelectionResult Failure(string error) =>
        Failure([error]);
}
