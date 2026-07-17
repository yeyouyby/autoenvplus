namespace AutoEnvPlus.Core.Languages;

public sealed record LanguagePackManifest(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Publisher,
    Uri Homepage,
    string License,
    IReadOnlyList<LanguageDefinition> Languages,
    IReadOnlyList<LanguageToolDefinition> Tools);

public sealed record LanguagePackDescriptor(
    LanguagePackManifest Manifest,
    string ManifestPath,
    bool IsEnabled)
{
    public string Id => Manifest.Id;
}

public sealed record LanguagePackListResult(
    IReadOnlyList<LanguagePackDescriptor> Packs,
    IReadOnlyList<LanguagePackLoadError> Errors)
{
    public bool Success => Errors.Count == 0;

    public int EnabledCount => Packs.Count(pack => pack.IsEnabled);
}

public sealed record LanguagePackLoadError(
    LanguagePackErrorCode Code,
    string Message,
    string? PackId = null,
    bool IsEnabled = false);

public sealed class LanguagePackImportPreview
{
    private readonly byte[] _normalizedManifest;

    internal LanguagePackImportPreview(
        LanguagePackManifest manifest,
        string sourcePath,
        byte[] normalizedManifest)
    {
        Manifest = manifest;
        SourcePath = sourcePath;
        _normalizedManifest = (byte[])normalizedManifest.Clone();
    }

    public LanguagePackManifest Manifest { get; }

    public string SourcePath { get; }

    public int LanguageCount => Manifest.Languages.Count;

    public int ToolCount => Manifest.Tools.Count;

    internal byte[] GetNormalizedManifest() => (byte[])_normalizedManifest.Clone();
}

public enum LanguagePackErrorCode
{
    MalformedJson,
    UnsupportedSchema,
    ManifestTooLarge,
    InvalidManifest,
    UnsafePath,
    DuplicatePack,
    PackNotFound,
    CatalogConflict,
    InvalidState,
    IoFailure,
}

public sealed class LanguagePackException : Exception
{
    public LanguagePackException(
        LanguagePackErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Field = field;
    }

    public LanguagePackErrorCode Code { get; }

    public string? Field { get; }
}
