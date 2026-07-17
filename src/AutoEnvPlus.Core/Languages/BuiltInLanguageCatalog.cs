using System.Reflection;
using System.Text.Json;

namespace AutoEnvPlus.Core.Languages;

public static class BuiltInLanguageCatalog
{
    public const int CurrentSchemaVersion = 1;

    private const string ResourceName =
        "AutoEnvPlus.Core.Languages.Catalog.language-catalog.schema1.json";

    private static readonly Lazy<LanguageCatalog> Catalog = new(
        LoadCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static LanguageCatalog Current => Catalog.Value;

    public static LanguageCatalog Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument document = LanguageManifestJson.ParseDocument(
            utf8Json,
            LanguageManifestJson.MaximumCatalogBytes);
        JsonElement root = document.RootElement;
        LanguageManifestJson.EnsureObject(
            root,
            "$",
            ["schemaVersion", "languages", "tools"],
            ["schemaVersion", "languages", "tools"]);
        JsonElement version = LanguageManifestJson.GetRequired(
            root,
            "schemaVersion",
            "schemaVersion");
        if (version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion)
            || schemaVersion != CurrentSchemaVersion)
        {
            throw new LanguagePackException(
                LanguagePackErrorCode.UnsupportedSchema,
                "The embedded language catalog schema is not supported.",
                "schemaVersion");
        }

        IReadOnlyList<LanguageDefinition> languages = LanguageManifestJson.ParseLanguages(
            LanguageManifestJson.GetRequired(root, "languages", "languages"),
            "languages",
            LanguageManifestJson.MaximumLanguages,
            allowDefaultEnabled: true);
        IReadOnlyList<LanguageToolDefinition> tools = LanguageManifestJson.ParseTools(
            LanguageManifestJson.GetRequired(root, "tools", "tools"),
            "tools",
            LanguageManifestJson.MaximumTools);
        LanguageManifestJson.ValidateReferences(languages, tools, "tools.languageIds");
        ValidateTopTen(languages);
        return new LanguageCatalog(languages, tools);
    }

    private static LanguageCatalog LoadCore()
    {
        Assembly assembly = typeof(BuiltInLanguageCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded language catalog is missing.");
        if (stream.Length is <= 0 or > LanguageManifestJson.MaximumCatalogBytes)
        {
            throw new InvalidOperationException("The embedded language catalog has an invalid size.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    private static void ValidateTopTen(IReadOnlyList<LanguageDefinition> languages)
    {
        string[] expected =
        [
            "python", "javascript", "typescript", "java", "c", "cpp", "csharp", "go",
            "rust", "php",
        ];
        string[] actual = languages.Where(language => language.DefaultEnabled)
            .Select(language => language.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new LanguagePackException(
                LanguagePackErrorCode.InvalidManifest,
                "The built-in catalog must enable exactly the product Top 10 languages.",
                "languages.defaultEnabled");
        }
    }
}
