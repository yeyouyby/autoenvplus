using System.Text.Json;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public sealed record ManagedRuntimeResolutionResult(
    RuntimeResolutionResult? Resolution,
    ManagedRuntimeEntry? Entry,
    IReadOnlyList<string> Errors)
{
    public bool Success => Entry is not null && Errors.Count == 0;
}

public sealed class ManagedRuntimeResolutionService
{
    private readonly ManagedRuntimeRegistry _registry;
    private readonly GlobalRuntimeProfileStore _globalProfile;
    private readonly ProjectManifestService _projectManifest = new();
    private readonly ManagedStateLock _stateTransactionLock;

    public ManagedRuntimeResolutionService(string managedRoot)
    {
        _registry = new ManagedRuntimeRegistry(managedRoot);
        _globalProfile = new GlobalRuntimeProfileStore(managedRoot);
        _stateTransactionLock = ManagedStateLock.CreateRuntimeTransaction(managedRoot);
    }

    public async Task<ManagedRuntimeResolutionResult> ResolveAsync(
        RuntimeKind kind,
        string startPath,
        RuntimeProfile? sessionProfile = null,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        string? sessionRuntimeId = null,
        string? sessionProviderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        RegistryLoadResult registry;
        RuntimeProfile global;
        {
            using ManagedStateLock.Lease transactionLock =
                await _stateTransactionLock.AcquireAsync(
                    cancellationToken).ConfigureAwait(false);
            registry = await _registry.LoadWithinTransactionAsync(
                cancellationToken).ConfigureAwait(false);
            if (registry.Errors.Count > 0)
            {
                return new ManagedRuntimeResolutionResult(null, null, registry.Errors);
            }

            try
            {
                global = await _globalProfile.LoadWithinTransactionAsync(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                return new ManagedRuntimeResolutionResult(
                    null,
                    null,
                    [$"Unable to load the global runtime profile: {exception.Message}"]);
            }
        }

        ProjectManifestLoadResult? project = _projectManifest.FindAndLoad(startPath);
        if (project is not null && project.Errors.Count > 0)
        {
            return new ManagedRuntimeResolutionResult(
                null,
                null,
                project.Errors
                    .Select(error => $"{project.Manifest.ManifestPath}:{error.LineNumber}: {error.Message}")
                    .ToArray());
        }

        RuntimeResolutionContext context = new(
            sessionProfile,
            project?.Manifest.ToRuntimeProfile(),
            global);
        ManagedRuntimeResolutionResult selected = ResolveRegistered(
            kind,
            context,
            registry.Entries,
            architecture,
            sessionRuntimeId,
            sessionProviderId);
        if (!selected.Success)
        {
            return selected;
        }

        ManagedRuntimeEntry entry = selected.Entry!;
        if (!File.Exists(entry.ExecutablePath))
        {
            return new ManagedRuntimeResolutionResult(
                selected.Resolution,
                null,
                [$"The registered executable is missing: {entry.ExecutablePath}"]);
        }

        return selected;
    }

    public static ManagedRuntimeResolutionResult ResolveRegistered(
        RuntimeKind kind,
        RuntimeResolutionContext context,
        IReadOnlyList<ManagedRuntimeEntry> entries,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        string? sessionRuntimeId = null,
        string? sessionProviderId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);

        RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
            kind,
            context,
            entries.Select(entry => entry.ToRuntimeInstallation()),
            architecture);

        string? pinnedRuntimeId = sessionRuntimeId;
        string? pinnedProviderId = sessionProviderId;
        ResolutionScope pinnedScope = ResolutionScope.Session;
        if (string.IsNullOrWhiteSpace(pinnedRuntimeId))
        {
            RuntimeProfile? activeProfile = resolution.Scope switch
            {
                ResolutionScope.Session => context.Session,
                ResolutionScope.Project => context.Project,
                ResolutionScope.Global => context.Global,
                _ => null,
            };
            if (activeProfile?.ExactSelections.TryGetValue(
                    kind,
                    out RuntimeSelectionIdentity? identity) == true)
            {
                pinnedRuntimeId = identity.RuntimeId;
                pinnedProviderId = identity.ProviderId;
                pinnedScope = resolution.Scope;
            }
        }

        if (!string.IsNullOrWhiteSpace(pinnedProviderId)
            && string.IsNullOrWhiteSpace(pinnedRuntimeId))
        {
            return Failure(
                resolution,
                $"The {kind} Provider pin requires an exact runtime ID pin.");
        }

        if (!string.IsNullOrWhiteSpace(pinnedRuntimeId))
        {
            ManagedRuntimeEntry[] pinned = entries
                .Where(entry => entry.Id.Equals(
                    pinnedRuntimeId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (pinned.Length != 1)
            {
                return Failure(
                    resolution,
                    pinned.Length == 0
                        ? $"The pinned {kind} runtime is no longer registered. Refresh the session selection."
                        : $"The managed runtime registry contains more than one entry for the pinned {kind} runtime ID.");
            }

            ManagedRuntimeEntry entry = pinned[0];
            RuntimeInstallation installation = entry.ToRuntimeInstallation();
            bool providerMatches = string.IsNullOrWhiteSpace(pinnedProviderId)
                || entry.ProviderId.Equals(
                    pinnedProviderId,
                    StringComparison.OrdinalIgnoreCase);
            if (entry.Kind != kind
                || !ArchitectureMatches(entry.Architecture, architecture)
                || !resolution.Selector.Matches(installation)
                || !providerMatches)
            {
                return Failure(
                    resolution,
                    $"The pinned {kind} runtime does not match the requested kind, architecture, active version selector, or Provider. Refresh the session selection.");
            }

            return new ManagedRuntimeResolutionResult(
                resolution with
                {
                    Scope = pinnedScope,
                    Installation = installation,
                    Error = null,
                },
                entry,
                []);
        }

        if (!resolution.Success)
        {
            return Failure(
                resolution,
                resolution.Error ?? "No managed runtime matched the active profile.");
        }

        ManagedRuntimeEntry[] resolvedEntries = entries
            .Where(entry => entry.Id.Equals(
                resolution.Installation!.Id,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (resolvedEntries.Length != 1)
        {
            return Failure(
                resolution,
                "The resolved runtime identity is missing or duplicated in the managed registry.");
        }

        ManagedRuntimeEntry selected = resolvedEntries[0];
        int providerCount = entries
            .Where(entry => entry.Kind == kind)
            .Where(entry => ArchitectureMatches(entry.Architecture, architecture))
            .Where(entry => entry.Architecture == selected.Architecture)
            .Where(entry => entry.Version.CompareTo(selected.Version) == 0)
            .Where(entry => resolution.Selector.Matches(entry.ToRuntimeInstallation()))
            .Select(entry => entry.ProviderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Count();
        if (providerCount > 1)
        {
            return Failure(
                resolution,
                $"Multiple Providers supply {kind} {selected.Version} for {selected.Architecture}. Select an exact runtime ID before execution.");
        }

        return new ManagedRuntimeResolutionResult(resolution, selected, []);
    }

    private static bool ArchitectureMatches(
        RuntimeArchitecture candidate,
        RuntimeArchitecture requested) =>
        requested == RuntimeArchitecture.Any
        || candidate == RuntimeArchitecture.Any
        || candidate == requested;

    private static ManagedRuntimeResolutionResult Failure(
        RuntimeResolutionResult resolution,
        string error) => new(resolution, null, [error]);
}
