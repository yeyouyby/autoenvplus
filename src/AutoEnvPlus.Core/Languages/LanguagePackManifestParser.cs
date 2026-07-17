using System.Text.Json;

namespace AutoEnvPlus.Core.Languages;

public static class LanguagePackManifestParser
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 512 * 1024;
    public const int MaximumLanguages = 64;
    public const int MaximumTools = 256;

    private static readonly string[] ManifestProperties =
    [
        "schemaVersion", "id", "displayName", "publisher", "homepage", "license",
        "languages", "tools",
    ];

    public static LanguagePackManifest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument document = LanguageManifestJson.ParseDocument(
            utf8Json,
            MaximumManifestBytes);
        JsonElement root = document.RootElement;
        LanguageManifestJson.EnsureObject(
            root,
            "$",
            ManifestProperties,
            ManifestProperties);
        JsonElement version = LanguageManifestJson.GetRequired(
            root,
            "schemaVersion",
            "schemaVersion");
        if (version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion))
        {
            throw new LanguagePackException(
                LanguagePackErrorCode.InvalidManifest,
                "The schema version must be an integer.",
                "schemaVersion");
        }

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new LanguagePackException(
                LanguagePackErrorCode.UnsupportedSchema,
                $"Language pack schema {schemaVersion} is not supported.",
                "schemaVersion");
        }

        IReadOnlyList<LanguageDefinition> languages = LanguageManifestJson.ParseLanguages(
            LanguageManifestJson.GetRequired(root, "languages", "languages"),
            "languages",
            MaximumLanguages,
            allowDefaultEnabled: false);
        IReadOnlyList<LanguageToolDefinition> tools = LanguageManifestJson.ParseTools(
            LanguageManifestJson.GetRequired(root, "tools", "tools"),
            "tools",
            MaximumTools);
        if (languages.Count == 0 && tools.Count == 0)
        {
            throw new LanguagePackException(
                LanguagePackErrorCode.InvalidManifest,
                "A language pack must declare at least one language or language tool.",
                "languages");
        }

        return new LanguagePackManifest(
            CurrentSchemaVersion,
            LanguageManifestJson.GetId(root, "id", "id"),
            LanguageManifestJson.GetText(root, "displayName", 96, "displayName"),
            LanguageManifestJson.GetText(root, "publisher", 96, "publisher"),
            LanguageManifestJson.GetHttpsUri(root, "homepage", "homepage"),
            LanguageManifestJson.GetText(root, "license", 128, "license"),
            languages,
            tools);
    }

    public static byte[] SerializeNormalized(LanguagePackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        byte[] candidate = LanguageManifestJson.SerializePack(manifest);
        LanguagePackManifest normalized = Parse(candidate);
        return LanguageManifestJson.SerializePack(normalized);
    }
}
