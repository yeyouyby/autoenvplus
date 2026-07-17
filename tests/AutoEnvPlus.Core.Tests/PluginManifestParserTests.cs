using System.Text;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class PluginManifestParserTests
{
    [Fact]
    public void Parse_ValidSchemaNormalizesDeclarativeManifest()
    {
        JsonObject document = PluginTestData.CreateManifestNode("Community-Python");
        JsonArray channels = (JsonArray)document["releases"]![0]!["channels"]!;
        channels[0] = "Stable";

        RuntimeProviderPluginManifest manifest = RuntimeProviderPluginManifestParser.Parse(
            Encoding.UTF8.GetBytes(document.ToJsonString()));

        Assert.Equal(RuntimeProviderPluginManifestParser.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal("community-python", manifest.Id);
        Assert.Equal("plugin:community-python", manifest.ProviderId);
        Assert.Equal("cpython", manifest.LanguageToolId);
        Assert.Equal(RuntimeKind.Python, manifest.Kind);
        RuntimeProviderPluginRelease release = Assert.Single(manifest.Releases);
        Assert.Equal(new RuntimeVersion(3, 13, 5), release.Version);
        Assert.Equal(["3.13", "stable"], release.Channels);
        Assert.Equal(new DateOnly(2026, 6, 11), release.ReleaseDate);
        RuntimeProviderPluginAsset asset = Assert.Single(release.Assets);
        Assert.Equal(RuntimeArchitecture.X64, asset.Architecture);
        Assert.Equal(PackageHashAlgorithm.Sha256, asset.HashAlgorithm);
        Assert.Equal(PluginTestData.Sha256, asset.PackageHash);
        Assert.Equal("python.exe", asset.ExpectedExecutableRelativePath);
        Assert.Equal("python-3.13.5", asset.ArchiveRoot);
        Assert.Equal("checksums.example", asset.ChecksumSourceUri.Host);
        Assert.DoesNotContain("https://", manifest.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", asset.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeNormalized_RoundTripsCanonicalSchemaWithoutExecutionFields()
    {
        RuntimeProviderPluginManifest manifest = PluginTestData.ParseManifest();

        byte[] serialized = RuntimeProviderPluginManifestParser.SerializeNormalized(manifest);
        RuntimeProviderPluginManifest reparsed =
            RuntimeProviderPluginManifestParser.Parse(serialized);
        string json = Encoding.UTF8.GetString(serialized);

        Assert.Equal(manifest.Id, reparsed.Id);
        Assert.Equal(manifest.DisplayName, reparsed.DisplayName);
        Assert.Equal(manifest.Kind, reparsed.Kind);
        Assert.Equal(manifest.Releases.Count, reparsed.Releases.Count);
        Assert.Equal(
            manifest.Releases[0].Assets[0].PackageHash,
            reparsed.Releases[0].Assets[0].PackageHash);
        Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assembly", json, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("script")]
    [InlineData("dll")]
    [InlineData("installArguments")]
    public void Parse_RejectsUnknownExecutionOrExtensionField(string fieldName)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document[fieldName] = "untrusted-content";

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.DoesNotContain("untrusted-content", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://downloads.example/python.zip")]
    [InlineData("ftp://downloads.example/python.zip")]
    [InlineData("https://user:secret@downloads.example/python.zip")]
    [InlineData("https://downloads.example/python.zip?token=secret")]
    [InlineData("https://downloads.example/python.zip#fragment")]
    [InlineData("/python.zip")]
    public void Parse_RejectsUnsafeDownloadUriWithoutLeakingIt(string uri)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["downloadUri"] = uri;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://checksums.example/SHA256SUMS")]
    [InlineData("https://user:secret@checksums.example/SHA256SUMS")]
    [InlineData("https://checksums.example/SHA256SUMS?token=secret")]
    [InlineData("https://checksums.example/SHA256SUMS#fragment")]
    public void Parse_RejectsUnsafeChecksumEvidenceUri(string uri)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["checksumSourceUri"] = uri;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AllowsButMarksChecksumSourceMatchingDownload()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        JsonObject asset = PluginTestData.FirstAsset(document);
        asset["checksumSourceUri"] = asset["downloadUri"]!.GetValue<string>();

        RuntimeProviderPluginAsset parsedAsset = Assert.Single(
            Assert.Single(PluginTestData.Parse(document).Releases).Assets);

        Assert.True(parsedAsset.ChecksumSourceMatchesDownload);
    }

    [Theory]
    [InlineData("../python.exe")]
    [InlineData("bin/../../python.exe")]
    [InlineData("C:/Python/python.exe")]
    [InlineData("/python.exe")]
    [InlineData("bin//python.exe")]
    [InlineData("bin/python.exe/")]
    [InlineData("bin/CON")]
    public void Parse_RejectsUnsafeArchiveExecutablePath(string path)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["expectedExecutableRelativePath"] = path;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("relative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("runtime.dll")]
    [InlineData("install.cmd")]
    [InlineData("setup.ps1")]
    [InlineData("bin/runtime")]
    public void Parse_RejectsNonExecutableOrScriptEntryPoint(string path)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["expectedExecutableRelativePath"] = path;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains(".exe", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDllAssetInsteadOfArchive()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["fileName"] = "runtime-provider.dll";

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains(".zip", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../payload")]
    [InlineData("C:\\payload")]
    [InlineData("/payload")]
    [InlineData("payload/../bin")]
    public void Parse_RejectsUnsafeArchiveRoot(string path)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["archiveRoot"] = path;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
    }

    [Fact]
    public void Parse_RejectsPresentButEmptyArchiveRoot()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["archiveRoot"] = "   ";

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
    }

    [Fact]
    public void Parse_RejectsMissingHash()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document).Remove("sha256");

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsTwoHashes()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["sha512"] = PluginTestData.Sha512;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Parse_RejectsInvalidHash(string hash)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        PluginTestData.FirstAsset(document)["sha256"] = hash;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
    }

    [Fact]
    public void Parse_RejectsDuplicateVersion()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        JsonArray releases = (JsonArray)document["releases"]!;
        releases.Add(releases[0]!.DeepClone());

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsVersionsWithEqualPrecedenceButDifferentBuildMetadata()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        JsonArray releases = (JsonArray)document["releases"]!;
        JsonObject duplicate = (JsonObject)releases[0]!.DeepClone();
        duplicate["version"] = "3.13.5+alternate-build";
        releases.Add(duplicate);

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("precedence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.0.0-preview.")]
    [InlineData("1.0.0+build.")]
    public void Parse_RejectsVersionThatIsUnsafeAsAWindowsDirectory(string version)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["releases"]![0]!["version"] = version;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsVersionLongerThanPublicSchemaLimit()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["releases"]![0]!["version"] = "1.0.0-" + new string('a', 91);

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
    }

    [Theory]
    [InlineData("cpython", RuntimeKind.Python)]
    [InlineData("nodejs", RuntimeKind.NodeJs)]
    [InlineData("eclipse-temurin", RuntimeKind.Java)]
    [InlineData("dotnet-sdk", RuntimeKind.DotNet)]
    [InlineData("msvc-build-tools", RuntimeKind.Msvc)]
    [InlineData("clang", RuntimeKind.Llvm)]
    [InlineData("gcc", RuntimeKind.Mingw)]
    [InlineData("cmake", RuntimeKind.CMake)]
    [InlineData("ninja", RuntimeKind.Ninja)]
    public void Parse_AcceptsEveryPublishedSchemaTwoLanguageTool(
        string toolId,
        RuntimeKind expected)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["languageToolId"] = toolId;

        RuntimeProviderPluginManifest manifest = PluginTestData.Parse(document);

        Assert.Equal(toolId, manifest.LanguageToolId);
        Assert.Equal(expected, manifest.Kind);
    }

    [Fact]
    public void Parse_LegacySchemaOneMapsRuntimeKindToExactToolAndNormalizesToSchemaTwo()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["schemaVersion"] = 1;
        document.Remove("languageToolId");
        document["runtimeKind"] = "python";

        RuntimeProviderPluginManifest manifest = PluginTestData.Parse(document);
        string normalized = Encoding.UTF8.GetString(
            RuntimeProviderPluginManifestParser.SerializeNormalized(manifest));

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal("cpython", manifest.LanguageToolId);
        Assert.Contains("\"languageToolId\": \"cpython\"", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeKind", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SchemaTwoRejectsLegacyRuntimeKindField()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["runtimeKind"] = "python";

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Equal("runtimeKind", exception.Field);
    }

    [Theory]
    [InlineData("pypy")]
    [InlineData("pip")]
    [InlineData("unknown-tool")]
    public void Parse_RejectsToolWithoutManagedArchiveAdapter(string toolId)
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["languageToolId"] = toolId;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Equal("languageToolId", exception.Field);
    }

    [Fact]
    public void Parse_RejectsDuplicateArchitectureAsset()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        JsonArray assets = (JsonArray)document["releases"]![0]!["assets"]!;
        JsonObject duplicate = (JsonObject)assets[0]!.DeepClone();
        duplicate["fileName"] = "other-python.zip";
        duplicate["downloadUri"] = "https://downloads.example/other-python.zip";
        assets.Add(duplicate);

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("architecture", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateJsonProperty()
    {
        string json = PluginTestData.CreateManifestNode().ToJsonString();
        json = json.Replace(
            "\"id\":\"community-python\"",
            "\"id\":\"community-python\",\"id\":\"shadow-plugin\"",
            StringComparison.Ordinal);

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => RuntimeProviderPluginManifestParser.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(RuntimeProviderPluginErrorCode.InvalidManifest, exception.Code);
        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsFutureSchema()
    {
        JsonObject document = PluginTestData.CreateManifestNode();
        document["schemaVersion"] = 999;

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(document));

        Assert.Equal(RuntimeProviderPluginErrorCode.UnsupportedSchema, exception.Code);
    }

    [Theory]
    [InlineData("python-org")]
    [InlineData("nodejs-official")]
    [InlineData("adoptium-temurin")]
    [InlineData("microsoft-dotnet-sdk")]
    public void Parse_RejectsBuiltInProviderId(string id)
    {
        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => PluginTestData.Parse(PluginTestData.CreateManifestNode(id)));

        Assert.Equal(RuntimeProviderPluginErrorCode.BuiltInProviderConflict, exception.Code);
    }

    [Fact]
    public void Parse_RejectsOversizedManifestBeforeJsonParsing()
    {
        byte[] content = new byte[RuntimeProviderPluginManifestParser.MaximumManifestBytes + 1];

        RuntimeProviderPluginException exception = Assert.Throws<RuntimeProviderPluginException>(
            () => RuntimeProviderPluginManifestParser.Parse(content));

        Assert.Equal(RuntimeProviderPluginErrorCode.ManifestTooLarge, exception.Code);
    }
}

internal static class PluginTestData
{
    public static readonly string Sha256 = new('a', 64);
    public static readonly string Sha512 = new('b', 128);

    public static JsonObject CreateManifestNode(string id = "community-python") => new()
    {
        ["schemaVersion"] = 2,
        ["id"] = id,
        ["displayName"] = "Community Python",
        ["vendor"] = "Community Builders",
        ["homepage"] = "https://community.example/python",
        ["license"] = "PSF-2.0",
        ["languageToolId"] = "cpython",
        ["releases"] = new JsonArray
        {
            new JsonObject
            {
                ["version"] = "3.13.5",
                ["channels"] = new JsonArray("stable", "3.13"),
                ["releaseDate"] = "2026-06-11",
                ["assets"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["architecture"] = "x64",
                        ["fileName"] = "community-python-3.13.5-win-x64.zip",
                        ["downloadUri"] =
                            "https://downloads.example/community-python-3.13.5-win-x64.zip",
                        ["checksumSourceUri"] =
                            "https://checksums.example/community-python-3.13.5/SHA256SUMS",
                        ["sha256"] = Sha256,
                        ["archiveRoot"] = "python-3.13.5",
                        ["expectedExecutableRelativePath"] = "python.exe",
                    },
                },
            },
        },
    };

    public static RuntimeProviderPluginManifest ParseManifest(string id = "community-python") =>
        Parse(CreateManifestNode(id));

    public static RuntimeProviderPluginManifest Parse(JsonObject document) =>
        RuntimeProviderPluginManifestParser.Parse(
            Encoding.UTF8.GetBytes(document.ToJsonString()));

    public static JsonObject FirstAsset(JsonObject document) =>
        (JsonObject)document["releases"]![0]!["assets"]![0]!;

    public static string LanguageToolIdForKindName(string kindName) => kindName switch
    {
        "python" => "cpython",
        "nodejs" => "nodejs",
        "java" => "eclipse-temurin",
        "dotnet" => "dotnet-sdk",
        "msvc" => "msvc-build-tools",
        "llvm" => "clang",
        "mingw" => "gcc",
        "cmake" => "cmake",
        "ninja" => "ninja",
        _ => throw new ArgumentOutOfRangeException(nameof(kindName)),
    };

    public static async Task<string> WriteManifestAsync(
        string directory,
        string id = "community-python",
        Action<JsonObject>? mutate = null)
    {
        Directory.CreateDirectory(directory);
        JsonObject document = CreateManifestNode(id);
        mutate?.Invoke(document);
        string path = Path.Combine(directory, id + "-source.json");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        return path;
    }
}
