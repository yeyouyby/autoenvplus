using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Projects;

public sealed record ProjectEnvironmentManifest(
    string ProjectRoot,
    string ManifestPath,
    IReadOnlyDictionary<RuntimeKind, VersionSelector> Tools)
{
    public RuntimeProfile ToRuntimeProfile() => new(Tools);
}

public sealed record ProjectManifestError(int LineNumber, string Message);

public sealed record ProjectManifestLoadResult(
    ProjectEnvironmentManifest Manifest,
    IReadOnlyList<ProjectManifestError> Errors)
{
    public bool Success => Errors.Count == 0;
}
