namespace AutoEnvPlus.Core.Storage;

public enum CacheConfigurationKind
{
    EnvironmentVariable,
    MavenSettingsXml,
    PnpmRc,
}

public sealed record CacheDirectoryDefinition(
    string Id,
    string DisplayName,
    string? ConfigurationEnvironmentVariable,
    Func<CacheEnvironment, string> DefaultPathFactory,
    bool SupportsMigration,
    CacheConfigurationKind ConfigurationKind = CacheConfigurationKind.EnvironmentVariable,
    Func<CacheEnvironment, string>? ConfigurationFilePathFactory = null,
    bool SupportsSafeCleanup = false);

public sealed record CacheEnvironment(
    string LocalApplicationData,
    string UserProfile,
    IReadOnlyDictionary<string, string?> Variables)
{
    public string? GetVariable(string name) => Variables.TryGetValue(name, out string? value)
        ? value
        : null;
}

public sealed record CacheDirectoryLocation(
    CacheDirectoryDefinition Definition,
    string DirectoryPath,
    string ConfigurationSource,
    bool Exists,
    string? ConfigurationFilePath = null,
    string? Warning = null,
    string? ConfigurationValue = null,
    bool ConfigurationValueKnown = false);

public sealed record CacheDirectoryMeasurement(
    CacheDirectoryLocation Location,
    long FileCount,
    long TotalBytes,
    IReadOnlyList<string> Errors);
