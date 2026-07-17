using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Languages;

internal static partial class LanguageManifestJson
{
    internal const int MaximumCatalogBytes = 2 * 1024 * 1024;
    internal const int MaximumLanguages = 256;
    internal const int MaximumTools = 2_048;
    internal const int MaximumProvidersPerTool = 16;
    internal const int MaximumMirrorSlotsPerProvider = 16;

    private static readonly string[] LanguageProperties =
    [
        "id", "displayName", "aliases", "fileExtensions", "defaultEnabled", "homepage",
    ];

    private static readonly string[] ToolProperties =
    [
        "id", "displayName", "vendor", "homepage", "license", "languageIds", "roles",
        "discoveryCommands", "windowsSupport", "capabilities", "providers",
    ];

    private static readonly string[] CapabilityProperties =
    [
        "discover", "install", "versionSwitch", "projectPin", "sessionActivation",
        "packageManagement", "virtualEnvironment", "cacheManagement",
        "mirrorConfiguration", "debug", "format", "lint",
    ];

    private static readonly string[] ProviderProperties =
    [
        "id", "displayName", "distributionKind", "managedInstallSupported", "mirrorSlots",
    ];

    private static readonly string[] MirrorSlotProperties =
    [
        "id", "displayName", "defaultEndpoint", "endpointKind", "purpose", "userOverridable",
    ];

    internal static JsonDocument ParseDocument(ReadOnlyMemory<byte> utf8Json, int maximumBytes)
    {
        if (utf8Json.Length == 0)
        {
            throw Error(LanguagePackErrorCode.MalformedJson, "The JSON document is empty.");
        }

        if (utf8Json.Length > maximumBytes)
        {
            throw Error(
                LanguagePackErrorCode.ManifestTooLarge,
                $"The JSON document cannot exceed {maximumBytes} bytes.");
        }

        try
        {
            return JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 20,
                });
        }
        catch (JsonException exception)
        {
            throw Error(
                LanguagePackErrorCode.MalformedJson,
                "The document is not valid JSON.",
                innerException: exception);
        }
    }

    internal static IReadOnlyList<LanguageDefinition> ParseLanguages(
        JsonElement element,
        string path,
        int maximum,
        bool allowDefaultEnabled)
    {
        EnsureArray(element, path);
        if (element.GetArrayLength() > maximum)
        {
            throw Invalid(path, $"At most {maximum} languages may be declared.");
        }

        List<LanguageDefinition> languages = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            EnsureObject(item, itemPath, LanguageProperties, LanguageProperties);
            string id = GetId(item, "id", $"{itemPath}.id");
            if (!ids.Add(id))
            {
                throw Invalid($"{itemPath}.id", "Language IDs must be unique ignoring case.");
            }

            bool defaultEnabled = GetBoolean(
                item,
                "defaultEnabled",
                $"{itemPath}.defaultEnabled");
            if (defaultEnabled && !allowDefaultEnabled)
            {
                throw Invalid(
                    $"{itemPath}.defaultEnabled",
                    "Imported language packs cannot enable a language automatically.");
            }

            languages.Add(new LanguageDefinition(
                id,
                GetText(item, "displayName", 96, $"{itemPath}.displayName"),
                GetStringArray(item, "aliases", 32, 64, $"{itemPath}.aliases", AliasPattern()),
                GetStringArray(
                    item,
                    "fileExtensions",
                    64,
                    24,
                    $"{itemPath}.fileExtensions",
                    FileExtensionPattern()),
                defaultEnabled,
                GetHttpsUri(item, "homepage", $"{itemPath}.homepage")));
            index++;
        }

        return languages.AsReadOnly();
    }

    internal static IReadOnlyList<LanguageToolDefinition> ParseTools(
        JsonElement element,
        string path,
        int maximum)
    {
        EnsureArray(element, path);
        if (element.GetArrayLength() > maximum)
        {
            throw Invalid(path, $"At most {maximum} language tools may be declared.");
        }

        List<LanguageToolDefinition> tools = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            EnsureObject(item, itemPath, ToolProperties, ToolProperties);
            string id = GetId(item, "id", $"{itemPath}.id");
            if (!ids.Add(id))
            {
                throw Invalid($"{itemPath}.id", "Language tool IDs must be unique ignoring case.");
            }

            IReadOnlyList<string> languageIds = GetIdArray(
                item,
                "languageIds",
                16,
                $"{itemPath}.languageIds",
                requireNonEmpty: true);
            IReadOnlySet<LanguageToolRole> roles = ParseRoles(
                GetRequired(item, "roles", $"{itemPath}.roles"),
                $"{itemPath}.roles");
            IReadOnlyList<string> discoveryCommands = GetStringArray(
                item,
                "discoveryCommands",
                24,
                64,
                $"{itemPath}.discoveryCommands",
                CommandPattern());
            LanguageToolWindowsSupport windowsSupport = ParseWindowsSupport(
                GetString(item, "windowsSupport", $"{itemPath}.windowsSupport"),
                $"{itemPath}.windowsSupport");
            LanguageToolCapabilities capabilities = ParseCapabilities(
                GetRequired(item, "capabilities", $"{itemPath}.capabilities"),
                $"{itemPath}.capabilities");
            IReadOnlyList<LanguageToolProviderDefinition> providers = ParseProviders(
                GetRequired(item, "providers", $"{itemPath}.providers"),
                $"{itemPath}.providers");
            ValidateToolSemantics(
                roles,
                windowsSupport,
                capabilities,
                providers,
                itemPath);

            tools.Add(new LanguageToolDefinition(
                id,
                GetText(item, "displayName", 96, $"{itemPath}.displayName"),
                GetText(item, "vendor", 96, $"{itemPath}.vendor"),
                GetHttpsUri(item, "homepage", $"{itemPath}.homepage"),
                GetText(item, "license", 128, $"{itemPath}.license"),
                languageIds,
                roles,
                discoveryCommands,
                windowsSupport,
                capabilities,
                providers));
            index++;
        }

        return tools.AsReadOnly();
    }

    internal static void ValidateReferences(
        IEnumerable<LanguageDefinition> languages,
        IEnumerable<LanguageToolDefinition> tools,
        string field)
    {
        HashSet<string> languageIds = languages.Select(language => language.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageToolDefinition tool in tools)
        {
            foreach (string languageId in tool.LanguageIds)
            {
                if (!languageIds.Contains(languageId))
                {
                    throw Invalid(
                        field,
                        "Every language tool must reference a language available in the catalog.");
                }
            }
        }
    }

    internal static byte[] SerializePack(LanguagePackManifest manifest)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("id", manifest.Id);
            writer.WriteString("displayName", manifest.DisplayName);
            writer.WriteString("publisher", manifest.Publisher);
            writer.WriteString("homepage", manifest.Homepage.AbsoluteUri);
            writer.WriteString("license", manifest.License);
            WriteLanguages(writer, manifest.Languages);
            WriteTools(writer, manifest.Tools);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    internal static void WriteLanguages(
        Utf8JsonWriter writer,
        IReadOnlyList<LanguageDefinition> languages)
    {
        writer.WriteStartArray("languages");
        foreach (LanguageDefinition language in languages)
        {
            writer.WriteStartObject();
            writer.WriteString("id", language.Id);
            writer.WriteString("displayName", language.DisplayName);
            WriteStrings(writer, "aliases", language.Aliases);
            WriteStrings(writer, "fileExtensions", language.FileExtensions);
            writer.WriteBoolean("defaultEnabled", language.DefaultEnabled);
            writer.WriteString("homepage", language.Homepage.AbsoluteUri);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static void WriteTools(
        Utf8JsonWriter writer,
        IReadOnlyList<LanguageToolDefinition> tools)
    {
        writer.WriteStartArray("tools");
        foreach (LanguageToolDefinition tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("id", tool.Id);
            writer.WriteString("displayName", tool.DisplayName);
            writer.WriteString("vendor", tool.Vendor);
            writer.WriteString("homepage", tool.Homepage.AbsoluteUri);
            writer.WriteString("license", tool.License);
            WriteStrings(writer, "languageIds", tool.LanguageIds);
            WriteStrings(writer, "roles", tool.Roles.Select(RoleName));
            WriteStrings(writer, "discoveryCommands", tool.DiscoveryCommands);
            writer.WriteString("windowsSupport", WindowsSupportName(tool.WindowsSupport));
            WriteCapabilities(writer, tool.Capabilities);
            WriteProviders(writer, tool.Providers);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static void EnsureObject(
        JsonElement element,
        string path,
        IEnumerable<string> allowed,
        IEnumerable<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "The value must be an object.");
        }

        HashSet<string> allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Invalid(path, "Object property names must be unique ignoring case.");
            }

            if (!allowedSet.Contains(property.Name))
            {
                throw Invalid($"{path}.{property.Name}", "Unknown properties are not allowed.");
            }
        }

        foreach (string name in required)
        {
            if (!element.TryGetProperty(name, out _))
            {
                throw Invalid($"{path}.{name}", "A required property is missing.");
            }
        }
    }

    internal static JsonElement GetRequired(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw Invalid(path, "A required property is missing.");
        }

        return value;
    }

    internal static string GetId(JsonElement element, string name, string path)
    {
        string value = GetString(element, name, path);
        if (!IdPattern().IsMatch(value))
        {
            throw Invalid(path, "The ID must be a lowercase kebab-case identifier.");
        }

        return value;
    }

    internal static string GetText(
        JsonElement element,
        string name,
        int maximumLength,
        string path)
    {
        string value = GetString(element, name, path).Trim();
        if (value.Length is < 1 || value.Length > maximumLength || ContainsControl(value))
        {
            throw Invalid(path, $"Text must contain 1 to {maximumLength} safe characters.");
        }

        return value;
    }

    internal static Uri GetHttpsUri(JsonElement element, string name, string path) =>
        ParseHttpsUri(GetString(element, name, path), path);

    internal static Uri ParseHttpsUri(string value, string path)
    {
        if (value.Length > 2_048
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Invalid(
                path,
                "The URI must be an absolute HTTPS URI without credentials, query, or fragment.");
        }

        return uri;
    }

    internal static LanguagePackException Invalid(string field, string message) =>
        Error(LanguagePackErrorCode.InvalidManifest, message, field);

    internal static LanguagePackException Error(
        LanguagePackErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null) =>
        new(code, message, field, innerException);

    private static IReadOnlySet<LanguageToolRole> ParseRoles(JsonElement element, string path)
    {
        EnsureArray(element, path);
        if (element.GetArrayLength() is < 1 or > 14)
        {
            throw Invalid(path, "A tool must declare between 1 and 14 roles.");
        }

        HashSet<LanguageToolRole> roles = [];
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"{path}[{index}]", "A role must be a string.");
            }

            LanguageToolRole role = item.GetString() switch
            {
                "compiler" => LanguageToolRole.Compiler,
                "interpreter" => LanguageToolRole.Interpreter,
                "runtime" => LanguageToolRole.Runtime,
                "sdk" => LanguageToolRole.Sdk,
                "package-manager" => LanguageToolRole.PackageManager,
                "build" => LanguageToolRole.Build,
                "debugger" => LanguageToolRole.Debugger,
                "formatter" => LanguageToolRole.Formatter,
                "linter" => LanguageToolRole.Linter,
                "version-manager" => LanguageToolRole.VersionManager,
                "virtual-env" => LanguageToolRole.VirtualEnvironment,
                "repl" => LanguageToolRole.Repl,
                "language-server" => LanguageToolRole.LanguageServer,
                "test-runner" => LanguageToolRole.TestRunner,
                _ => throw Invalid($"{path}[{index}]", "The language tool role is unknown."),
            };
            if (!roles.Add(role))
            {
                throw Invalid($"{path}[{index}]", "Tool roles must be unique.");
            }

            index++;
        }

        return roles;
    }

    private static LanguageToolWindowsSupport ParseWindowsSupport(string value, string path) =>
        value switch
        {
            "native" => LanguageToolWindowsSupport.Native,
            "wsl" => LanguageToolWindowsSupport.Wsl,
            "conditional" => LanguageToolWindowsSupport.Conditional,
            "unsupported" => LanguageToolWindowsSupport.Unsupported,
            _ => throw Invalid(path, "The Windows support value is unknown."),
        };

    private static LanguageToolCapabilities ParseCapabilities(JsonElement element, string path)
    {
        EnsureObject(element, path, CapabilityProperties, CapabilityProperties);
        return new LanguageToolCapabilities(
            GetBoolean(element, "discover", $"{path}.discover"),
            GetBoolean(element, "install", $"{path}.install"),
            GetBoolean(element, "versionSwitch", $"{path}.versionSwitch"),
            GetBoolean(element, "projectPin", $"{path}.projectPin"),
            GetBoolean(element, "sessionActivation", $"{path}.sessionActivation"),
            GetBoolean(element, "packageManagement", $"{path}.packageManagement"),
            GetBoolean(element, "virtualEnvironment", $"{path}.virtualEnvironment"),
            GetBoolean(element, "cacheManagement", $"{path}.cacheManagement"),
            GetBoolean(element, "mirrorConfiguration", $"{path}.mirrorConfiguration"),
            GetBoolean(element, "debug", $"{path}.debug"),
            GetBoolean(element, "format", $"{path}.format"),
            GetBoolean(element, "lint", $"{path}.lint"));
    }

    private static IReadOnlyList<LanguageToolProviderDefinition> ParseProviders(
        JsonElement element,
        string path)
    {
        EnsureArray(element, path);
        if (element.GetArrayLength() is < 1 or > MaximumProvidersPerTool)
        {
            throw Invalid(
                path,
                $"A tool must declare between 1 and {MaximumProvidersPerTool} providers.");
        }

        List<LanguageToolProviderDefinition> providers = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            EnsureObject(item, itemPath, ProviderProperties, ProviderProperties);
            string id = GetId(item, "id", $"{itemPath}.id");
            if (!ids.Add(id))
            {
                throw Invalid($"{itemPath}.id", "Provider IDs must be unique within a tool.");
            }

            LanguageToolProviderDistributionKind distributionKind = GetString(
                item,
                "distributionKind",
                $"{itemPath}.distributionKind") switch
            {
                "managed-archive" => LanguageToolProviderDistributionKind.ManagedArchive,
                "manual" => LanguageToolProviderDistributionKind.Manual,
                "system" => LanguageToolProviderDistributionKind.System,
                "winget" => LanguageToolProviderDistributionKind.WinGet,
                "external" => LanguageToolProviderDistributionKind.External,
                _ => throw Invalid(
                    $"{itemPath}.distributionKind",
                    "The provider distribution kind is unknown."),
            };
            bool managedInstallSupported = GetBoolean(
                item,
                "managedInstallSupported",
                $"{itemPath}.managedInstallSupported");
            if (managedInstallSupported
                && distributionKind is not LanguageToolProviderDistributionKind.ManagedArchive
                    and not LanguageToolProviderDistributionKind.WinGet)
            {
                throw Invalid(
                    $"{itemPath}.managedInstallSupported",
                    "Only managed archive and WinGet providers may claim managed installation.");
            }

            providers.Add(new LanguageToolProviderDefinition(
                id,
                GetText(item, "displayName", 96, $"{itemPath}.displayName"),
                distributionKind,
                managedInstallSupported,
                ParseMirrorSlots(
                    GetRequired(item, "mirrorSlots", $"{itemPath}.mirrorSlots"),
                    $"{itemPath}.mirrorSlots")));
            index++;
        }

        return providers.AsReadOnly();
    }

    private static IReadOnlyList<ProviderMirrorSlotDefinition> ParseMirrorSlots(
        JsonElement element,
        string path)
    {
        EnsureArray(element, path);
        if (element.GetArrayLength() > MaximumMirrorSlotsPerProvider)
        {
            throw Invalid(path, $"At most {MaximumMirrorSlotsPerProvider} mirror slots may be declared.");
        }

        List<ProviderMirrorSlotDefinition> slots = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            EnsureObject(item, itemPath, MirrorSlotProperties, MirrorSlotProperties);
            string id = GetId(item, "id", $"{itemPath}.id");
            if (!ids.Add(id))
            {
                throw Invalid($"{itemPath}.id", "Mirror slot IDs must be unique within a provider.");
            }

            ProviderMirrorEndpointKind endpointKind = GetString(
                item,
                "endpointKind",
                $"{itemPath}.endpointKind") switch
            {
                "pypi" => ProviderMirrorEndpointKind.PyPi,
                "npm" => ProviderMirrorEndpointKind.Npm,
                "nuget" => ProviderMirrorEndpointKind.NuGet,
                "maven" => ProviderMirrorEndpointKind.Maven,
                "gradle" => ProviderMirrorEndpointKind.Gradle,
                "go-proxy" => ProviderMirrorEndpointKind.GoProxy,
                "crates" => ProviderMirrorEndpointKind.Crates,
                "rubygems" => ProviderMirrorEndpointKind.RubyGems,
                "composer" => ProviderMirrorEndpointKind.Composer,
                "generic-download" => ProviderMirrorEndpointKind.GenericDownload,
                _ => throw Invalid(
                    $"{itemPath}.endpointKind",
                    "The mirror endpoint kind is unknown."),
            };
            slots.Add(new ProviderMirrorSlotDefinition(
                id,
                GetText(item, "displayName", 96, $"{itemPath}.displayName"),
                GetHttpsUri(item, "defaultEndpoint", $"{itemPath}.defaultEndpoint"),
                endpointKind,
                GetText(item, "purpose", 256, $"{itemPath}.purpose"),
                GetBoolean(item, "userOverridable", $"{itemPath}.userOverridable")));
            index++;
        }

        return slots.AsReadOnly();
    }

    private static void ValidateToolSemantics(
        IReadOnlySet<LanguageToolRole> roles,
        LanguageToolWindowsSupport windowsSupport,
        LanguageToolCapabilities capabilities,
        IReadOnlyList<LanguageToolProviderDefinition> providers,
        string path)
    {
        bool hasManagedInstall = providers.Any(provider => provider.ManagedInstallSupported);
        bool hasMirrorSlots = providers.Any(provider => provider.MirrorSlots.Count > 0);
        if (capabilities.Install != hasManagedInstall)
        {
            throw Invalid(
                $"{path}.capabilities.install",
                "Install capability must exactly reflect whether a provider supports managed installation.");
        }

        if (capabilities.MirrorConfiguration != hasMirrorSlots)
        {
            throw Invalid(
                $"{path}.capabilities.mirrorConfiguration",
                "Mirror configuration capability must exactly reflect declared provider mirror slots.");
        }

        if (capabilities.PackageManagement && !roles.Contains(LanguageToolRole.PackageManager))
        {
            throw Invalid(path, "Package management capability requires the package-manager role.");
        }

        if (capabilities.VirtualEnvironment && !roles.Contains(LanguageToolRole.VirtualEnvironment))
        {
            throw Invalid(path, "Virtual environment capability requires the virtual-env role.");
        }

        if (capabilities.Debug && !roles.Contains(LanguageToolRole.Debugger)
            || capabilities.Format && !roles.Contains(LanguageToolRole.Formatter)
            || capabilities.Lint && !roles.Contains(LanguageToolRole.Linter))
        {
            throw Invalid(path, "Debug, format, and lint capabilities require their matching roles.");
        }

        if (windowsSupport == LanguageToolWindowsSupport.Unsupported
            && (capabilities.Install || capabilities.SessionActivation))
        {
            throw Invalid(
                path,
                "A Windows-unsupported tool cannot claim installation or session activation.");
        }
    }

    private static IReadOnlyList<string> GetIdArray(
        JsonElement element,
        string name,
        int maximumCount,
        string path,
        bool requireNonEmpty)
    {
        JsonElement array = GetRequired(element, name, path);
        EnsureArray(array, path);
        if (array.GetArrayLength() > maximumCount
            || requireNonEmpty && array.GetArrayLength() == 0)
        {
            throw Invalid(path, $"The array must contain at most {maximumCount} values.");
        }

        List<string> values = [];
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not string value
                || !IdPattern().IsMatch(value))
            {
                throw Invalid($"{path}[{index}]", "The value must be a lowercase kebab-case ID.");
            }

            if (!unique.Add(value))
            {
                throw Invalid($"{path}[{index}]", "Array values must be unique ignoring case.");
            }

            values.Add(value);
            index++;
        }

        return values.AsReadOnly();
    }

    private static IReadOnlyList<string> GetStringArray(
        JsonElement element,
        string name,
        int maximumCount,
        int maximumLength,
        string path,
        Regex pattern)
    {
        JsonElement array = GetRequired(element, name, path);
        EnsureArray(array, path);
        if (array.GetArrayLength() > maximumCount)
        {
            throw Invalid(path, $"The array may contain at most {maximumCount} values.");
        }

        List<string> values = [];
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (value is null
                || value.Length is < 1
                || value.Length > maximumLength
                || !pattern.IsMatch(value))
            {
                throw Invalid($"{path}[{index}]", "The array contains an invalid value.");
            }

            if (!unique.Add(value))
            {
                throw Invalid($"{path}[{index}]", "Array values must be unique ignoring case.");
            }

            values.Add(value);
            index++;
        }

        return values.AsReadOnly();
    }

    private static string GetString(JsonElement element, string name, string path)
    {
        JsonElement value = GetRequired(element, name, path);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw Invalid(path, "The value must be a string.");
        }

        return text;
    }

    private static bool GetBoolean(JsonElement element, string name, string path)
    {
        JsonElement value = GetRequired(element, name, path);
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw Invalid(path, "The value must be a Boolean.");
        }

        return value.GetBoolean();
    }

    private static void EnsureArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(path, "The value must be an array.");
        }
    }

    private static bool ContainsControl(string value) => value.Any(char.IsControl);

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteCapabilities(
        Utf8JsonWriter writer,
        LanguageToolCapabilities capabilities)
    {
        writer.WriteStartObject("capabilities");
        writer.WriteBoolean("discover", capabilities.Discover);
        writer.WriteBoolean("install", capabilities.Install);
        writer.WriteBoolean("versionSwitch", capabilities.VersionSwitch);
        writer.WriteBoolean("projectPin", capabilities.ProjectPin);
        writer.WriteBoolean("sessionActivation", capabilities.SessionActivation);
        writer.WriteBoolean("packageManagement", capabilities.PackageManagement);
        writer.WriteBoolean("virtualEnvironment", capabilities.VirtualEnvironment);
        writer.WriteBoolean("cacheManagement", capabilities.CacheManagement);
        writer.WriteBoolean("mirrorConfiguration", capabilities.MirrorConfiguration);
        writer.WriteBoolean("debug", capabilities.Debug);
        writer.WriteBoolean("format", capabilities.Format);
        writer.WriteBoolean("lint", capabilities.Lint);
        writer.WriteEndObject();
    }

    private static void WriteProviders(
        Utf8JsonWriter writer,
        IReadOnlyList<LanguageToolProviderDefinition> providers)
    {
        writer.WriteStartArray("providers");
        foreach (LanguageToolProviderDefinition provider in providers)
        {
            writer.WriteStartObject();
            writer.WriteString("id", provider.Id);
            writer.WriteString("displayName", provider.DisplayName);
            writer.WriteString("distributionKind", DistributionKindName(provider.DistributionKind));
            writer.WriteBoolean("managedInstallSupported", provider.ManagedInstallSupported);
            writer.WriteStartArray("mirrorSlots");
            foreach (ProviderMirrorSlotDefinition slot in provider.MirrorSlots)
            {
                writer.WriteStartObject();
                writer.WriteString("id", slot.Id);
                writer.WriteString("displayName", slot.DisplayName);
                writer.WriteString("defaultEndpoint", slot.DefaultEndpoint.AbsoluteUri);
                writer.WriteString("endpointKind", EndpointKindName(slot.EndpointKind));
                writer.WriteString("purpose", slot.Purpose);
                writer.WriteBoolean("userOverridable", slot.UserOverridable);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string RoleName(LanguageToolRole role) => role switch
    {
        LanguageToolRole.Compiler => "compiler",
        LanguageToolRole.Interpreter => "interpreter",
        LanguageToolRole.Runtime => "runtime",
        LanguageToolRole.Sdk => "sdk",
        LanguageToolRole.PackageManager => "package-manager",
        LanguageToolRole.Build => "build",
        LanguageToolRole.Debugger => "debugger",
        LanguageToolRole.Formatter => "formatter",
        LanguageToolRole.Linter => "linter",
        LanguageToolRole.VersionManager => "version-manager",
        LanguageToolRole.VirtualEnvironment => "virtual-env",
        LanguageToolRole.Repl => "repl",
        LanguageToolRole.LanguageServer => "language-server",
        LanguageToolRole.TestRunner => "test-runner",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static string WindowsSupportName(LanguageToolWindowsSupport support) => support switch
    {
        LanguageToolWindowsSupport.Native => "native",
        LanguageToolWindowsSupport.Wsl => "wsl",
        LanguageToolWindowsSupport.Conditional => "conditional",
        LanguageToolWindowsSupport.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(support)),
    };

    private static string DistributionKindName(LanguageToolProviderDistributionKind kind) =>
        kind switch
        {
            LanguageToolProviderDistributionKind.ManagedArchive => "managed-archive",
            LanguageToolProviderDistributionKind.Manual => "manual",
            LanguageToolProviderDistributionKind.System => "system",
            LanguageToolProviderDistributionKind.WinGet => "winget",
            LanguageToolProviderDistributionKind.External => "external",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string EndpointKindName(ProviderMirrorEndpointKind kind) => kind switch
    {
        ProviderMirrorEndpointKind.PyPi => "pypi",
        ProviderMirrorEndpointKind.Npm => "npm",
        ProviderMirrorEndpointKind.NuGet => "nuget",
        ProviderMirrorEndpointKind.Maven => "maven",
        ProviderMirrorEndpointKind.Gradle => "gradle",
        ProviderMirrorEndpointKind.GoProxy => "go-proxy",
        ProviderMirrorEndpointKind.Crates => "crates",
        ProviderMirrorEndpointKind.RubyGems => "rubygems",
        ProviderMirrorEndpointKind.Composer => "composer",
        ProviderMirrorEndpointKind.GenericDownload => "generic-download",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._+#-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    [GeneratedRegex("^\\.[A-Za-z0-9][A-Za-z0-9._+-]{0,22}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileExtensionPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();
}
