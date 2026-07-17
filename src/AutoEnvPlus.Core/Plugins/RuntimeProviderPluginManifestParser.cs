using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Plugins;

public static partial class RuntimeProviderPluginManifestParser
{
    public const int CurrentSchemaVersion = 2;
    public const int LegacySchemaVersion = 1;
    public const int MaximumManifestBytes = 512 * 1024;
    public const int MaximumReleases = 256;
    public const int MaximumAssetsPerRelease = 8;

    private const int MaximumChannelsPerRelease = 16;

    private static readonly string[] ManifestProperties =
    [
        "schemaVersion",
        "id",
        "displayName",
        "vendor",
        "homepage",
        "license",
        "languageToolId",
        "runtimeKind",
        "releases",
    ];

    private static readonly string[] CommonManifestProperties =
    [
        "schemaVersion", "id", "displayName", "vendor", "homepage", "license", "releases",
    ];

    private static readonly string[] ReleaseProperties =
    [
        "version",
        "channels",
        "releaseDate",
        "assets",
    ];

    private static readonly string[] AssetProperties =
    [
        "architecture",
        "fileName",
        "downloadUri",
        "checksumSourceUri",
        "sha256",
        "sha512",
        "archiveRoot",
        "expectedExecutableRelativePath",
    ];

    public static RuntimeProviderPluginManifest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw Error(
                RuntimeProviderPluginErrorCode.MalformedJson,
                "The runtime provider plugin manifest is empty.");
        }

        if (utf8Json.Length > MaximumManifestBytes)
        {
            throw Error(
                RuntimeProviderPluginErrorCode.ManifestTooLarge,
                $"A runtime provider plugin manifest cannot exceed {MaximumManifestBytes} bytes.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
        }
        catch (JsonException exception)
        {
            throw Error(
                RuntimeProviderPluginErrorCode.MalformedJson,
                "The runtime provider plugin manifest is not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            EnsureObject(root, ManifestProperties, "$", CommonManifestProperties);
            int schemaVersion = GetRequiredInt32(root, "schemaVersion", "schemaVersion");
            if (schemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion)
            {
                throw Error(
                    RuntimeProviderPluginErrorCode.UnsupportedSchema,
                    $"Runtime provider plugin schema {schemaVersion} is not supported.",
                    "schemaVersion");
            }

            string id = NormalizePluginId(GetRequiredString(root, "id", "id"));
            string displayName = NormalizeText(
                GetRequiredString(root, "displayName", "displayName"),
                96,
                "displayName");
            string vendor = NormalizeText(
                GetRequiredString(root, "vendor", "vendor"),
                96,
                "vendor");
            Uri homepage = NormalizeHttpsUri(
                GetRequiredString(root, "homepage", "homepage"),
                "homepage");
            string license = NormalizeText(
                GetRequiredString(root, "license", "license"),
                128,
                "license");
            string languageToolId;
            RuntimeKind kind;
            if (schemaVersion == LegacySchemaVersion)
            {
                if (root.TryGetProperty("languageToolId", out _))
                {
                    throw Invalid(
                        "languageToolId",
                        "Schema 1 uses runtimeKind and cannot also declare languageToolId.");
                }

                kind = ParseRuntimeKind(GetRequiredString(root, "runtimeKind", "runtimeKind"));
                languageToolId = LanguageToolRuntimeBridge.Get(kind).ToolId;
            }
            else
            {
                if (root.TryGetProperty("runtimeKind", out _))
                {
                    throw Invalid(
                        "runtimeKind",
                        "Schema 2 derives the internal adapter from languageToolId.");
                }

                languageToolId = NormalizeLanguageToolId(GetRequiredString(
                    root,
                    "languageToolId",
                    "languageToolId"));
                kind = ResolveBridge(languageToolId).RuntimeKind;
            }

            IReadOnlyList<RuntimeProviderPluginRelease> releases = ParseReleases(
                GetRequiredArray(root, "releases", "releases"));

            return new RuntimeProviderPluginManifest(
                CurrentSchemaVersion,
                id,
                displayName,
                vendor,
                homepage,
                license,
                languageToolId,
                kind,
                releases);
        }
    }

    public static RuntimeProviderPluginManifest ValidateAndNormalize(
        RuntimeProviderPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Parse(SerializeNormalized(manifest));
    }

    public static byte[] SerializeNormalized(RuntimeProviderPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RuntimeProviderPluginManifest normalized = ReferenceEquals(manifest, null)
            ? throw new ArgumentNullException(nameof(manifest))
            : NormalizeModel(manifest);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(
            stream,
            new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", normalized.SchemaVersion);
            writer.WriteString("id", normalized.Id);
            writer.WriteString("displayName", normalized.DisplayName);
            writer.WriteString("vendor", normalized.Vendor);
            writer.WriteString("homepage", normalized.Homepage.AbsoluteUri);
            writer.WriteString("license", normalized.License);
            writer.WriteString("languageToolId", normalized.LanguageToolId);
            writer.WriteStartArray("releases");
            foreach (RuntimeProviderPluginRelease release in normalized.Releases)
            {
                writer.WriteStartObject();
                writer.WriteString("version", release.Version.ToString());
                writer.WriteStartArray("channels");
                foreach (string channel in release.Channels)
                {
                    writer.WriteStringValue(channel);
                }

                writer.WriteEndArray();
                if (release.ReleaseDate is DateOnly releaseDate)
                {
                    writer.WriteString(
                        "releaseDate",
                        releaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                writer.WriteStartArray("assets");
                foreach (RuntimeProviderPluginAsset asset in release.Assets)
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "architecture",
                        asset.Architecture.ToString().ToLowerInvariant());
                    writer.WriteString("fileName", asset.FileName);
                    writer.WriteString("downloadUri", asset.DownloadUri.AbsoluteUri);
                    writer.WriteString(
                        "checksumSourceUri",
                        asset.ChecksumSourceUri.AbsoluteUri);
                    writer.WriteString(
                        asset.HashAlgorithm == PackageHashAlgorithm.Sha256
                            ? "sha256"
                            : "sha512",
                        asset.PackageHash);
                    if (asset.ArchiveRoot is not null)
                    {
                        writer.WriteString("archiveRoot", asset.ArchiveRoot);
                    }

                    writer.WriteString(
                        "expectedExecutableRelativePath",
                        asset.ExpectedExecutableRelativePath);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static bool IsValidPluginId(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && PluginIdPattern().IsMatch(value);

    private static RuntimeProviderPluginManifest NormalizeModel(
        RuntimeProviderPluginManifest manifest)
    {
        if (manifest.SchemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion)
        {
            throw Error(
                RuntimeProviderPluginErrorCode.UnsupportedSchema,
                $"Runtime provider plugin schema {manifest.SchemaVersion} is not supported.",
                "schemaVersion");
        }

        string id = NormalizePluginId(manifest.Id);
        string displayName = NormalizeText(manifest.DisplayName, 96, "displayName");
        string vendor = NormalizeText(manifest.Vendor, 96, "vendor");
        Uri homepage = NormalizeHttpsUri(manifest.Homepage?.OriginalString, "homepage");
        string license = NormalizeText(manifest.License, 128, "license");
        if (!Enum.IsDefined(manifest.Kind))
        {
            throw Invalid("runtimeKind", "The runtime kind is not supported.");
        }

        string languageToolId = manifest.SchemaVersion == LegacySchemaVersion
            ? LanguageToolRuntimeBridge.Get(manifest.Kind).ToolId
            : NormalizeLanguageToolId(manifest.LanguageToolId);
        LanguageToolRuntimeBridgeDefinition bridge = ResolveBridge(languageToolId);
        if (bridge.RuntimeKind != manifest.Kind)
        {
            throw Invalid(
                "languageToolId",
                "The language tool ID does not match the selected managed adapter.");
        }

        IReadOnlyList<RuntimeProviderPluginRelease> releases = NormalizeReleases(
            manifest.Releases);
        return new RuntimeProviderPluginManifest(
            CurrentSchemaVersion,
            id,
            displayName,
            vendor,
            homepage,
            license,
            languageToolId,
            manifest.Kind,
            releases);
    }

    private static IReadOnlyList<RuntimeProviderPluginRelease> ParseReleases(
        JsonElement releasesElement)
    {
        if (releasesElement.GetArrayLength() is < 1 or > MaximumReleases)
        {
            throw Invalid(
                "releases",
                $"A plugin must declare between 1 and {MaximumReleases} releases.");
        }

        List<RuntimeProviderPluginRelease> releases = [];
        int index = 0;
        foreach (JsonElement element in releasesElement.EnumerateArray())
        {
            string path = $"releases[{index}]";
            EnsureObject(
                element,
                ReleaseProperties,
                path,
                ["version", "channels", "assets"]);
            string versionValue = GetRequiredString(element, "version", $"{path}.version");
            if (versionValue.Length > 96
                || !RuntimeVersion.TryParse(versionValue, out RuntimeVersion? version))
            {
                throw Invalid($"{path}.version", "A release version is invalid.");
            }

            IReadOnlyList<string> channels = ParseChannels(
                GetRequiredArray(element, "channels", $"{path}.channels"),
                $"{path}.channels");
            DateOnly? releaseDate = ParseOptionalDate(element, "releaseDate", path);
            IReadOnlyList<RuntimeProviderPluginAsset> assets = ParseAssets(
                GetRequiredArray(element, "assets", $"{path}.assets"),
                $"{path}.assets");
            releases.Add(new RuntimeProviderPluginRelease(
                version!,
                channels,
                releaseDate,
                assets));
            index++;
        }

        return NormalizeReleases(releases);
    }

    private static IReadOnlyList<RuntimeProviderPluginRelease> NormalizeReleases(
        IReadOnlyList<RuntimeProviderPluginRelease>? releases)
    {
        if (releases is null || releases.Count is < 1 or > MaximumReleases)
        {
            throw Invalid(
                "releases",
                $"A plugin must declare between 1 and {MaximumReleases} releases.");
        }

        HashSet<string> versions = new(StringComparer.OrdinalIgnoreCase);
        List<RuntimeVersion> versionPrecedences = [];
        HashSet<string> downloadUris = new(StringComparer.Ordinal);
        List<RuntimeProviderPluginRelease> normalized = [];
        for (int releaseIndex = 0; releaseIndex < releases.Count; releaseIndex++)
        {
            RuntimeProviderPluginRelease release = releases[releaseIndex]
                ?? throw Invalid(
                    $"releases[{releaseIndex}]",
                    "A release entry cannot be null.");
            string version = release.Version?.ToString()
                ?? throw Invalid(
                    $"releases[{releaseIndex}].version",
                    "A release version is required.");
            if (!RuntimeVersion.TryParse(version, out RuntimeVersion? parsedVersion))
            {
                throw Invalid(
                    $"releases[{releaseIndex}].version",
                    "A release version is invalid.");
            }

            ValidateVersionPathSegment(
                parsedVersion!.ToString(),
                $"releases[{releaseIndex}].version");

            if (!versions.Add(parsedVersion!.ToString()))
            {
                throw Invalid(
                    $"releases[{releaseIndex}].version",
                    "Plugin release versions must be unique.");
            }

            if (versionPrecedences.Any(existing => existing.CompareTo(parsedVersion) == 0))
            {
                throw Invalid(
                    $"releases[{releaseIndex}].version",
                    "Plugin release version precedence must be unique; build metadata cannot "
                    + "distinguish an install selector or destination.");
            }

            versionPrecedences.Add(parsedVersion);

            IReadOnlyList<string> channels = NormalizeChannels(
                release.Channels,
                $"releases[{releaseIndex}].channels");
            IReadOnlyList<RuntimeProviderPluginAsset> assets = NormalizeAssets(
                release.Assets,
                $"releases[{releaseIndex}].assets",
                downloadUris);
            normalized.Add(new RuntimeProviderPluginRelease(
                parsedVersion,
                channels,
                release.ReleaseDate,
                assets));
        }

        return Array.AsReadOnly(normalized
            .OrderByDescending(release => release.Version)
            .ToArray());
    }

    private static IReadOnlyList<string> ParseChannels(JsonElement element, string path)
    {
        if (element.GetArrayLength() is < 1 or > MaximumChannelsPerRelease)
        {
            throw Invalid(
                path,
                $"A release must declare between 1 and {MaximumChannelsPerRelease} channels.");
        }

        List<string> channels = [];
        int index = 0;
        foreach (JsonElement channel in element.EnumerateArray())
        {
            if (channel.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"{path}[{index}]", "A channel must be a string.");
            }

            channels.Add(channel.GetString()!);
            index++;
        }

        return NormalizeChannels(channels, path);
    }

    private static IReadOnlyList<string> NormalizeChannels(
        IReadOnlyList<string>? channels,
        string path)
    {
        if (channels is null || channels.Count is < 1 or > MaximumChannelsPerRelease)
        {
            throw Invalid(
                path,
                $"A release must declare between 1 and {MaximumChannelsPerRelease} channels.");
        }

        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        List<string> normalized = [];
        for (int index = 0; index < channels.Count; index++)
        {
            string channel = NormalizeText(channels[index], 48, $"{path}[{index}]")
                .ToLowerInvariant();
            if (!ChannelPattern().IsMatch(channel) || !unique.Add(channel))
            {
                throw Invalid(
                    $"{path}[{index}]",
                    "Release channels must be unique simple identifiers.");
            }

            normalized.Add(channel);
        }

        return Array.AsReadOnly(
            normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<RuntimeProviderPluginAsset> ParseAssets(
        JsonElement element,
        string path)
    {
        if (element.GetArrayLength() is < 1 or > MaximumAssetsPerRelease)
        {
            throw Invalid(
                path,
                $"A release must declare between 1 and {MaximumAssetsPerRelease} assets.");
        }

        List<RuntimeProviderPluginAsset> assets = [];
        int index = 0;
        foreach (JsonElement assetElement in element.EnumerateArray())
        {
            string assetPath = $"{path}[{index}]";
            EnsureObject(
                assetElement,
                AssetProperties,
                assetPath,
                [
                    "architecture",
                    "fileName",
                    "downloadUri",
                    "checksumSourceUri",
                    "expectedExecutableRelativePath",
                ]);
            RuntimeArchitecture architecture = ParseArchitecture(
                GetRequiredString(
                    assetElement,
                    "architecture",
                    $"{assetPath}.architecture"),
                $"{assetPath}.architecture");
            string fileName = GetRequiredString(
                assetElement,
                "fileName",
                $"{assetPath}.fileName");
            Uri downloadUri = NormalizeHttpsUri(
                GetRequiredString(
                    assetElement,
                    "downloadUri",
                    $"{assetPath}.downloadUri"),
                $"{assetPath}.downloadUri");
            Uri checksumSourceUri = NormalizeHttpsUri(
                GetRequiredString(
                    assetElement,
                    "checksumSourceUri",
                    $"{assetPath}.checksumSourceUri"),
                $"{assetPath}.checksumSourceUri");
            (PackageHashAlgorithm algorithm, string hash) = ParseHash(
                assetElement,
                assetPath);
            string? archiveRoot = GetOptionalString(
                assetElement,
                "archiveRoot",
                $"{assetPath}.archiveRoot");
            string expectedExecutable = GetRequiredString(
                assetElement,
                "expectedExecutableRelativePath",
                $"{assetPath}.expectedExecutableRelativePath");
            assets.Add(new RuntimeProviderPluginAsset(
                architecture,
                fileName,
                downloadUri,
                checksumSourceUri,
                algorithm,
                hash,
                archiveRoot,
                expectedExecutable));
            index++;
        }

        return assets;
    }

    private static IReadOnlyList<RuntimeProviderPluginAsset> NormalizeAssets(
        IReadOnlyList<RuntimeProviderPluginAsset>? assets,
        string path,
        HashSet<string> downloadUris)
    {
        if (assets is null || assets.Count is < 1 or > MaximumAssetsPerRelease)
        {
            throw Invalid(
                path,
                $"A release must declare between 1 and {MaximumAssetsPerRelease} assets.");
        }

        HashSet<RuntimeArchitecture> architectures = [];
        HashSet<string> fileNames = new(StringComparer.OrdinalIgnoreCase);
        List<RuntimeProviderPluginAsset> normalized = [];
        for (int index = 0; index < assets.Count; index++)
        {
            RuntimeProviderPluginAsset asset = assets[index]
                ?? throw Invalid($"{path}[{index}]", "An asset entry cannot be null.");
            string assetPath = $"{path}[{index}]";
            RuntimeArchitecture architecture = ValidateArchitecture(
                asset.Architecture,
                $"{assetPath}.architecture");
            if (!architectures.Add(architecture))
            {
                throw Invalid(
                    $"{assetPath}.architecture",
                    "A release cannot contain two assets for the same architecture.");
            }

            string fileName = NormalizeFileName(asset.FileName, $"{assetPath}.fileName");
            if (!fileNames.Add(fileName))
            {
                throw Invalid(
                    $"{assetPath}.fileName",
                    "Asset file names must be unique within a release.");
            }

            Uri downloadUri = NormalizeHttpsUri(
                asset.DownloadUri?.OriginalString,
                $"{assetPath}.downloadUri");
            Uri checksumSourceUri = NormalizeHttpsUri(
                asset.ChecksumSourceUri?.OriginalString,
                $"{assetPath}.checksumSourceUri");
            if (!downloadUris.Add(downloadUri.AbsoluteUri))
            {
                throw Invalid(
                    $"{assetPath}.downloadUri",
                    "A download asset cannot be declared more than once.");
            }

            if (!Enum.IsDefined(asset.HashAlgorithm)
                || !asset.HashAlgorithm.IsValidHash(asset.PackageHash))
            {
                throw Invalid(
                    $"{assetPath}.hash",
                    "An asset must contain exactly one valid SHA-256 or SHA-512 checksum.");
            }

            string? archiveRoot = asset.ArchiveRoot is null
                ? null
                : NormalizeRelativePath(
                    asset.ArchiveRoot,
                    $"{assetPath}.archiveRoot");
            string executable = NormalizeRelativePath(
                asset.ExpectedExecutableRelativePath,
                $"{assetPath}.expectedExecutableRelativePath");
            if (!Path.GetExtension(executable).Equals(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    $"{assetPath}.expectedExecutableRelativePath",
                    "A declarative runtime entry point must be a Windows .exe file.");
            }

            normalized.Add(new RuntimeProviderPluginAsset(
                architecture,
                fileName,
                downloadUri,
                checksumSourceUri,
                asset.HashAlgorithm,
                asset.PackageHash.ToLowerInvariant(),
                archiveRoot,
                executable));
        }

        return Array.AsReadOnly(
            normalized.OrderBy(asset => asset.Architecture).ToArray());
    }

    private static (PackageHashAlgorithm Algorithm, string Hash) ParseHash(
        JsonElement element,
        string path)
    {
        string? sha256 = GetOptionalString(element, "sha256", $"{path}.sha256");
        string? sha512 = GetOptionalString(element, "sha512", $"{path}.sha512");
        if ((sha256 is null) == (sha512 is null))
        {
            throw Invalid(
                $"{path}.hash",
                "An asset must declare exactly one of sha256 or sha512.");
        }

        PackageHashAlgorithm algorithm = sha256 is not null
            ? PackageHashAlgorithm.Sha256
            : PackageHashAlgorithm.Sha512;
        string hash = (sha256 ?? sha512)!;
        if (!algorithm.IsValidHash(hash))
        {
            throw Invalid(
                $"{path}.hash",
                $"The asset {algorithm.DisplayName()} checksum is invalid.");
        }

        return (algorithm, hash.ToLowerInvariant());
    }

    private static RuntimeKind ParseRuntimeKind(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out RuntimeKind kind)
            || !Enum.IsDefined(kind))
        {
            throw Invalid("runtimeKind", "The runtime kind is not supported.");
        }

        return kind;
    }

    private static string NormalizeLanguageToolId(string? value)
    {
        string id = NormalizeText(value, 128, "languageToolId").ToLowerInvariant();
        if (!IsValidPluginId(id))
        {
            throw Invalid(
                "languageToolId",
                "A language tool ID must be a lowercase, hyphen-separated identifier.");
        }

        return id;
    }

    private static LanguageToolRuntimeBridgeDefinition ResolveBridge(string languageToolId) =>
        LanguageToolRuntimeBridge.Definitions.FirstOrDefault(definition =>
            definition.ToolId.Equals(languageToolId, StringComparison.OrdinalIgnoreCase))
        ?? throw Invalid(
            "languageToolId",
            "This language tool does not have a registered managed archive adapter.");

    private static string RuntimeKindName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.NodeJs => "nodejs",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static RuntimeArchitecture ParseArchitecture(string value, string path)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out RuntimeArchitecture architecture))
        {
            throw Invalid(path, "The asset architecture is not supported.");
        }

        return ValidateArchitecture(architecture, path);
    }

    private static RuntimeArchitecture ValidateArchitecture(
        RuntimeArchitecture architecture,
        string path)
    {
        if (architecture is not RuntimeArchitecture.X86
            and not RuntimeArchitecture.X64
            and not RuntimeArchitecture.Arm64)
        {
            throw Invalid(path, "Plugin archives must declare x86, x64, or arm64.");
        }

        return architecture;
    }

    private static DateOnly? ParseOptionalDate(
        JsonElement element,
        string propertyName,
        string parentPath)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        string path = $"{parentPath}.{propertyName}";
        if (property.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(
                property.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly releaseDate))
        {
            throw Invalid(path, "A release date must use the yyyy-MM-dd format.");
        }

        return releaseDate;
    }

    private static string NormalizePluginId(string? value)
    {
        string id = NormalizeText(value, 64, "id").ToLowerInvariant();
        if (!IsValidPluginId(id))
        {
            throw Invalid(
                "id",
                "A plugin ID must be a lowercase, hyphen-separated identifier.");
        }

        if (RuntimeProviderPluginIds.BuiltInProviderIds.Contains(id))
        {
            throw Error(
                RuntimeProviderPluginErrorCode.BuiltInProviderConflict,
                "A runtime provider plugin cannot use a built-in provider ID.",
                "id");
        }

        return id;
    }

    private static string NormalizeText(string? value, int maximumLength, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(path, "A required text field is empty.");
        }

        string normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw Invalid(path, "A text field is too long or contains control characters.");
        }

        return normalized;
    }

    private static Uri NormalizeHttpsUri(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2_048
            || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.Contains('\\')
            || value.Any(char.IsWhiteSpace)
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
        {
            throw Invalid(
                path,
                "Plugin URIs must be absolute credential-free HTTPS URIs without a query or fragment.");
        }

        return uri;
    }

    private static string NormalizeFileName(string? value, string path)
    {
        string fileName = NormalizeText(value, 180, path);
        if (!Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || IsReservedWindowsName(Path.GetFileNameWithoutExtension(fileName)))
        {
            throw Invalid(path, "A plugin asset must use a plain, non-reserved .zip file name.");
        }

        return fileName;
    }

    private static string NormalizeRelativePath(string? value, string path)
    {
        string relativePath = NormalizeText(value, 240, path).Replace('\\', '/');
        if (Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.Contains(':')
            || relativePath.EndsWith('/'))
        {
            throw Invalid(path, "Plugin archive paths must be safe relative paths.");
        }

        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsName(segment))
            {
                throw Invalid(path, "Plugin archive paths must be safe relative paths.");
            }
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static void ValidateVersionPathSegment(string value, string path)
    {
        if (value.Length is < 1 or > 96
            || value.EndsWith(' ')
            || value.EndsWith('.')
            || !Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || IsReservedWindowsName(value))
        {
            throw Invalid(
                path,
                "A release version must be a safe Windows directory segment of at most 96 characters.");
        }
    }

    private static bool IsReservedWindowsName(string value)
    {
        string stem = value.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(
                stem,
                "^(?:COM|LPT)[1-9]$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void EnsureObject(
        JsonElement element,
        IReadOnlyCollection<string> allowedProperties,
        string path,
        IReadOnlyCollection<string> requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "A plugin manifest entry must be an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Invalid(path, "A plugin manifest object contains a duplicate property.");
            }

            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid(path, "A plugin manifest object contains an unsupported property.");
            }
        }

        if (requiredProperties.Any(property => !seen.Contains(property)))
        {
            throw Invalid(path, "A plugin manifest object is missing a required property.");
        }
    }

    private static string GetRequiredString(
        JsonElement element,
        string propertyName,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw Invalid(path, "A required plugin manifest field must be a string.");
        }

        return property.GetString()!;
    }

    private static string? GetOptionalString(
        JsonElement element,
        string propertyName,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw Invalid(path, "An optional plugin manifest field must be a string.");
        }

        return property.GetString();
    }

    private static int GetRequiredInt32(
        JsonElement element,
        string propertyName,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int value))
        {
            throw Invalid(path, "A required plugin manifest field must be an integer.");
        }

        return value;
    }

    private static JsonElement GetRequiredArray(
        JsonElement element,
        string propertyName,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(path, "A required plugin manifest field must be an array.");
        }

        return property;
    }

    private static RuntimeProviderPluginException Invalid(string path, string message) =>
        Error(RuntimeProviderPluginErrorCode.InvalidManifest, message, path);

    private static RuntimeProviderPluginException Error(
        RuntimeProviderPluginErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null) => new(code, message, field, innerException);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
}
