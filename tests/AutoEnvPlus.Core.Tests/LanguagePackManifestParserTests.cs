using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Tests;

public sealed class LanguagePackManifestParserTests
{
    [Fact]
    public void PublishedSchemaAndTemplate_AreValidAndDataOnly()
    {
        string templatePath = FindRepositoryFile("examples", "language-pack.template.json");
        string schemaPath = FindRepositoryFile("schemas", "language-pack.schema.json");

        LanguagePackManifest template = LanguagePackManifestParser.Parse(
            File.ReadAllBytes(templatePath));
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        string schemaText = File.ReadAllText(schemaPath);

        Assert.Equal(LanguagePackManifestParser.CurrentSchemaVersion, template.SchemaVersion);
        Assert.All(template.Languages, language => Assert.False(language.DefaultEnabled));
        Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        Assert.DoesNotContain("installCommand", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environmentHook", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("registryWrite", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assemblyPath", schemaText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AcceptsDataOnlyLanguageAndTool()
    {
        LanguagePackManifest manifest = Parse(LanguagePackTestData.CreateManifest());

        Assert.Equal("example-zig-extension", manifest.Id);
        Assert.Equal("examplelang", Assert.Single(manifest.Languages).Id);
        LanguageToolDefinition tool = Assert.Single(manifest.Tools);
        Assert.Equal("examplelang-compiler", tool.Id);
        Assert.Contains(LanguageToolRole.Compiler, tool.Roles);
        Assert.False(tool.Capabilities.Install);
        Assert.Empty(Assert.Single(tool.Providers).MirrorSlots);
    }

    [Fact]
    public void SerializeNormalized_RoundTrips()
    {
        LanguagePackManifest manifest = Parse(LanguagePackTestData.CreateManifest());

        byte[] normalized = LanguagePackManifestParser.SerializeNormalized(manifest);
        LanguagePackManifest reparsed = LanguagePackManifestParser.Parse(normalized);

        Assert.Equal(manifest.Id, reparsed.Id);
        Assert.Equal(manifest.Tools[0].Capabilities, reparsed.Tools[0].Capabilities);
        Assert.EndsWith("\n", Encoding.UTF8.GetString(normalized), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("script")]
    [InlineData("dll")]
    [InlineData("installCommand")]
    [InlineData("environmentHooks")]
    [InlineData("registryWrites")]
    public void Parse_RejectsExecutableOrUnknownHooks(string propertyName)
    {
        JsonObject root = LanguagePackTestData.CreateManifest();
        root[propertyName] = "malicious";

        LanguagePackException exception = Assert.Throws<LanguagePackException>(() => Parse(root));

        Assert.Equal(LanguagePackErrorCode.InvalidManifest, exception.Code);
    }

    [Fact]
    public void Parse_RejectsDuplicatePropertyIgnoringCase()
    {
        string json = LanguagePackTestData.CreateManifest().ToJsonString();
        json = json.Replace(
            "\"displayName\":\"Example language pack\"",
            "\"displayName\":\"Example language pack\",\"DisplayName\":\"Duplicate\"",
            StringComparison.Ordinal);

        LanguagePackException exception = Assert.Throws<LanguagePackException>(() =>
            LanguagePackManifestParser.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(LanguagePackErrorCode.InvalidManifest, exception.Code);
    }

    [Fact]
    public void Parse_RejectsHttpCredentialsQueriesAndFragments()
    {
        foreach (string uri in new[]
        {
            "http://example.com/", "https://user:secret@example.com/",
            "https://example.com/?token=secret", "https://example.com/#fragment",
        })
        {
            JsonObject root = LanguagePackTestData.CreateManifest();
            root["homepage"] = uri;
            LanguagePackException exception = Assert.Throws<LanguagePackException>(() => Parse(root));
            Assert.Equal(LanguagePackErrorCode.InvalidManifest, exception.Code);
        }
    }

    [Fact]
    public void Parse_RejectsAutomaticActivation()
    {
        JsonObject root = LanguagePackTestData.CreateManifest();
        root["languages"]![0]!["defaultEnabled"] = true;

        LanguagePackException exception = Assert.Throws<LanguagePackException>(() => Parse(root));

        Assert.Equal("languages[0].defaultEnabled", exception.Field);
    }

    [Fact]
    public void Parse_RejectsPathOrShellTextAsDiscoveryCommand()
    {
        foreach (string command in new[] { "..\\tool.exe", "tool.exe --run", "cmd&calc" })
        {
            JsonObject root = LanguagePackTestData.CreateManifest();
            root["tools"]![0]!["discoveryCommands"] = new JsonArray(command);
            Assert.Throws<LanguagePackException>(() => Parse(root));
        }
    }

    [Fact]
    public void Parse_RejectsCapabilityNotBackedByProvider()
    {
        JsonObject root = LanguagePackTestData.CreateManifest();
        root["tools"]![0]!["capabilities"]!["install"] = true;

        LanguagePackException exception = Assert.Throws<LanguagePackException>(() => Parse(root));

        Assert.Contains("provider", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsMirrorCapabilityMismatch()
    {
        JsonObject root = LanguagePackTestData.CreateManifest();
        JsonObject provider = root["tools"]![0]!["providers"]![0]!.AsObject();
        provider["mirrorSlots"] = new JsonArray(LanguagePackTestData.CreateMirrorSlot());

        LanguagePackException exception = Assert.Throws<LanguagePackException>(() => Parse(root));

        Assert.Contains("Mirror configuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsOversizedManifest()
    {
        byte[] bytes = new byte[LanguagePackManifestParser.MaximumManifestBytes + 1];

        LanguagePackException exception = Assert.Throws<LanguagePackException>(() =>
            LanguagePackManifestParser.Parse(bytes));

        Assert.Equal(LanguagePackErrorCode.ManifestTooLarge, exception.Code);
    }

    private static LanguagePackManifest Parse(JsonObject root) =>
        LanguagePackManifestParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));

    private static string FindRepositoryFile(params string[] segments)
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            string candidate = Path.Combine([current, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException("A repository language-pack artifact is missing.");
    }
}

internal static class LanguagePackTestData
{
    public static JsonObject CreateManifest() => new()
    {
        ["schemaVersion"] = 1,
        ["id"] = "example-zig-extension",
        ["displayName"] = "Example language pack",
        ["publisher"] = "Example publisher",
        ["homepage"] = "https://example.com/languages/",
        ["license"] = "MIT",
        ["languages"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "examplelang",
                ["displayName"] = "ExampleLang",
                ["aliases"] = new JsonArray("ExampleLang"),
                ["fileExtensions"] = new JsonArray(".example"),
                ["defaultEnabled"] = false,
                ["homepage"] = "https://example.com/languages/examplelang/",
            },
        },
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "examplelang-compiler",
                ["displayName"] = "ExampleLang Compiler",
                ["vendor"] = "Example publisher",
                ["homepage"] = "https://example.com/tools/examplelang/",
                ["license"] = "MIT",
                ["languageIds"] = new JsonArray("examplelang"),
                ["roles"] = new JsonArray("compiler", "build"),
                ["discoveryCommands"] = new JsonArray("examplec.exe"),
                ["windowsSupport"] = "conditional",
                ["capabilities"] = CreateCapabilities(),
                ["providers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "example-manual",
                        ["displayName"] = "Example manual distribution",
                        ["distributionKind"] = "manual",
                        ["managedInstallSupported"] = false,
                        ["mirrorSlots"] = new JsonArray(),
                    },
                },
            },
        },
    };

    public static JsonObject CreateMirrorSlot() => new()
    {
        ["id"] = "package-index",
        ["displayName"] = "Package index",
        ["defaultEndpoint"] = "https://packages.example.com/simple/",
        ["endpointKind"] = "generic-download",
        ["purpose"] = "Resolve ExampleLang packages.",
        ["userOverridable"] = true,
    };

    public static JsonObject CreateCapabilities() => new()
    {
        ["discover"] = true,
        ["install"] = false,
        ["versionSwitch"] = false,
        ["projectPin"] = false,
        ["sessionActivation"] = false,
        ["packageManagement"] = false,
        ["virtualEnvironment"] = false,
        ["cacheManagement"] = false,
        ["mirrorConfiguration"] = false,
        ["debug"] = false,
        ["format"] = false,
        ["lint"] = false,
    };
}
