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

    public ManagedRuntimeResolutionService(string managedRoot)
    {
        _registry = new ManagedRuntimeRegistry(managedRoot);
        _globalProfile = new GlobalRuntimeProfileStore(managedRoot);
    }

    public async Task<ManagedRuntimeResolutionResult> ResolveAsync(
        RuntimeKind kind,
        string startPath,
        RuntimeProfile? sessionProfile = null,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        RegistryLoadResult registry = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (registry.Errors.Count > 0)
        {
            return new ManagedRuntimeResolutionResult(null, null, registry.Errors);
        }

        RuntimeProfile global;
        try
        {
            global = await _globalProfile.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new ManagedRuntimeResolutionResult(
                null,
                null,
                [$"Unable to load the global runtime profile: {exception.Message}"]);
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
        RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
            kind,
            context,
            registry.Entries.Select(entry => entry.ToRuntimeInstallation()),
            architecture);
        if (!resolution.Success)
        {
            return new ManagedRuntimeResolutionResult(
                resolution,
                null,
                [resolution.Error ?? "No managed runtime matched the active profile."]);
        }

        ManagedRuntimeEntry? entry = registry.Entries.FirstOrDefault(
            candidate => candidate.Id.Equals(
                resolution.Installation!.Id,
                StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return new ManagedRuntimeResolutionResult(
                resolution,
                null,
                ["The resolved runtime disappeared from the managed registry."]);
        }

        if (!File.Exists(entry.ExecutablePath))
        {
            return new ManagedRuntimeResolutionResult(
                resolution,
                null,
                [$"The registered executable is missing: {entry.ExecutablePath}"]);
        }

        return new ManagedRuntimeResolutionResult(resolution, entry, []);
    }
}
