using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Projects;

public sealed record ProjectEnvironmentManifest(
    string ProjectRoot,
    string ManifestPath,
    IReadOnlyDictionary<RuntimeKind, VersionSelector> Tools,
    IReadOnlyDictionary<RuntimeKind, RuntimeSelectionIdentity>? ToolIdentities = null)
{
    public IReadOnlyDictionary<RuntimeKind, RuntimeSelectionIdentity> ExactSelections =>
        ToolIdentities ?? EmptyIdentities;

    public RuntimeProfile ToRuntimeProfile() => new(Tools)
    {
        ExactSelections = ExactSelections,
    };

    private static IReadOnlyDictionary<RuntimeKind, RuntimeSelectionIdentity> EmptyIdentities
    {
        get;
    } = new Dictionary<RuntimeKind, RuntimeSelectionIdentity>();
}

public sealed record ProjectManifestError(int LineNumber, string Message);

public sealed record ProjectManifestLoadResult(
    ProjectEnvironmentManifest Manifest,
    IReadOnlyList<ProjectManifestError> Errors)
{
    public bool Success => Errors.Count == 0;
}
