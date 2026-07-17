using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public enum RuntimeReferenceKind
{
    GlobalProfile,
    ProjectManifest,
    ProjectLock,
}

public sealed record RuntimeReference(
    RuntimeReferenceKind Kind,
    string Owner,
    string Detail);

public sealed class RuntimeReferenceScanner
{
    public async Task<IReadOnlyList<RuntimeReference>> ScanAsync(
        ManagedRuntimeEntry runtime,
        IReadOnlyList<ManagedRuntimeEntry> installedRuntimes,
        RuntimeProfile globalProfile,
        IEnumerable<KnownProject> knownProjects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(installedRuntimes);
        ArgumentNullException.ThrowIfNull(globalProfile);
        ArgumentNullException.ThrowIfNull(knownProjects);
        List<RuntimeReference> references = [];

        if (globalProfile.Selections.TryGetValue(runtime.Kind, out VersionSelector? globalSelector))
        {
            bool exactMatch = globalProfile.ExactSelections.TryGetValue(
                    runtime.Kind,
                    out RuntimeSelectionIdentity? identity)
                && identity.RuntimeId.Equals(runtime.Id, StringComparison.OrdinalIgnoreCase)
                && identity.ProviderId.Equals(
                    runtime.ProviderId,
                    StringComparison.OrdinalIgnoreCase);
            RuntimeResolutionResult? resolvedGlobal = identity is null
                ? new RuntimeResolver().Resolve(
                    runtime.Kind,
                    new RuntimeResolutionContext(Global: globalProfile),
                    installedRuntimes.Select(entry => entry.ToRuntimeInstallation()),
                    runtime.Architecture)
                : null;
            if (exactMatch
                || resolvedGlobal?.Installation?.Id.Equals(
                    runtime.Id,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                references.Add(new RuntimeReference(
                    RuntimeReferenceKind.GlobalProfile,
                    "Global profile",
                    exactMatch
                        ? $"{runtime.Kind} = {globalSelector} ({runtime.Id} / {runtime.ProviderId})"
                        : $"{runtime.Kind} = {globalSelector}"));
            }
        }

        foreach (KnownProject project in knownProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath = Path.Combine(project.ProjectRoot, ProjectManifestService.ManifestFileName);
            if (File.Exists(manifestPath))
            {
                ProjectManifestLoadResult manifest = new ProjectManifestService().Load(manifestPath);
                if (manifest.Success
                    && manifest.Manifest.Tools.TryGetValue(runtime.Kind, out VersionSelector? selector))
                {
                    ManagedRuntimeResolutionResult resolvedProject =
                        ManagedRuntimeResolutionService.ResolveRegistered(
                        runtime.Kind,
                        new RuntimeResolutionContext(
                            Project: manifest.Manifest.ToRuntimeProfile()),
                        installedRuntimes,
                        runtime.Architecture);
                    if (resolvedProject.Entry?.Id.Equals(
                        runtime.Id,
                        StringComparison.OrdinalIgnoreCase) == true)
                    {
                        references.Add(new RuntimeReference(
                            RuntimeReferenceKind.ProjectManifest,
                            project.ProjectRoot,
                            $"{runtime.Kind} = {selector}"));
                    }
                }
            }

            string lockPath = Path.Combine(project.ProjectRoot, ProjectLockFileService.LockFileName);
            if (File.Exists(lockPath))
            {
                ProjectLockResult locked = await new ProjectLockFileService().LoadAsync(
                    lockPath,
                    cancellationToken).ConfigureAwait(false);
                if (!locked.Success)
                {
                    references.Add(new RuntimeReference(
                        RuntimeReferenceKind.ProjectLock,
                        project.ProjectRoot,
                        "unreadable lock: " + string.Join("; ", locked.Errors.Take(2))));
                }
                else if (locked.Document!.Runtimes.Any(entry =>
                        entry.Kind == runtime.Kind
                        && entry.ResolvedVersion == runtime.Version
                        && entry.Architecture == runtime.Architecture
                        && entry.ProviderId.Equals(runtime.ProviderId, StringComparison.Ordinal)
                        && entry.PackageHashAlgorithm == runtime.PackageHashAlgorithm
                        && entry.PackageHash.Equals(runtime.PackageHash, StringComparison.OrdinalIgnoreCase)))
                {
                    references.Add(new RuntimeReference(
                        RuntimeReferenceKind.ProjectLock,
                        project.ProjectRoot,
                        $"locked to {runtime.Version} ({runtime.Architecture})"));
                }
            }
        }

        return references
            .DistinctBy(reference => (reference.Kind, reference.Owner, reference.Detail))
            .ToArray();
    }
}
