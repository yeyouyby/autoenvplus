using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Tests;

public sealed class LanguagePackStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-LanguagePack-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportEnableDisableDelete_ControlsEffectiveCatalog()
    {
        string source = await WriteManifestAsync();
        LanguagePackStore store = new(_root);

        LanguagePackImportPreview preview = await store.PreviewImportAsync(source);
        LanguagePackDescriptor imported = await store.ImportAsync(preview);
        LanguagePackListResult initial = await store.ListAsync();

        Assert.False(imported.IsEnabled);
        Assert.False(Assert.Single(initial.Packs).IsEnabled);
        Assert.False((await store.GetEffectiveCatalogAsync()).TryGetLanguage("examplelang", out _));

        await store.EnableAsync(imported.Id);
        LanguageCatalog enabled = await store.GetEffectiveCatalogAsync();
        Assert.True(enabled.TryGetLanguage("examplelang", out _));
        Assert.True(enabled.TryGetTool("examplelang-compiler", out _));

        await store.DisableAsync(imported.Id);
        Assert.False((await store.GetEffectiveCatalogAsync()).TryGetLanguage("examplelang", out _));

        await store.DeleteAsync(imported.Id);
        Assert.Empty((await store.ListAsync()).Packs);
        Assert.False(File.Exists(imported.ManifestPath));
    }

    [Fact]
    public async Task Import_RejectsBuiltInLanguageOrToolReplacement()
    {
        JsonObject languageConflict = LanguagePackTestData.CreateManifest();
        languageConflict["languages"]![0]!["id"] = "python";
        languageConflict["tools"]![0]!["languageIds"]![0] = "python";
        string languageSource = await WriteManifestAsync(languageConflict, "language-conflict.json");

        LanguagePackException languageError = await Assert.ThrowsAsync<LanguagePackException>(() =>
            new LanguagePackStore(_root).ImportAsync(languageSource));

        Assert.Equal(LanguagePackErrorCode.CatalogConflict, languageError.Code);

        JsonObject toolConflict = LanguagePackTestData.CreateManifest();
        toolConflict["tools"]![0]!["id"] = "cpython";
        string toolSource = await WriteManifestAsync(toolConflict, "tool-conflict.json");
        LanguagePackException toolError = await Assert.ThrowsAsync<LanguagePackException>(() =>
            new LanguagePackStore(_root).ImportAsync(toolSource));
        Assert.Equal(LanguagePackErrorCode.CatalogConflict, toolError.Code);
    }

    [Fact]
    public async Task Import_RejectsDuplicateDefinitionsAcrossPacks()
    {
        LanguagePackStore store = new(_root);
        await store.ImportAsync(await WriteManifestAsync());
        JsonObject second = LanguagePackTestData.CreateManifest();
        second["id"] = "second-pack";
        string source = await WriteManifestAsync(second, "second.json");

        LanguagePackException exception = await Assert.ThrowsAsync<LanguagePackException>(() =>
            store.ImportAsync(source));

        Assert.Equal(LanguagePackErrorCode.CatalogConflict, exception.Code);
    }

    [Fact]
    public async Task Import_AllowsToolForExistingBuiltInLanguage()
    {
        JsonObject root = LanguagePackTestData.CreateManifest();
        root["id"] = "python-extra-tools";
        root["languages"] = new JsonArray();
        root["tools"]![0]!["id"] = "example-python-analyzer";
        root["tools"]![0]!["languageIds"]![0] = "python";
        string source = await WriteManifestAsync(root);
        LanguagePackStore store = new(_root);

        LanguagePackDescriptor imported = await store.ImportAsync(source);
        await store.EnableAsync(imported.Id);

        Assert.True((await store.GetEffectiveCatalogAsync()).TryGetTool(
            "example-python-analyzer",
            out _));
    }

    [Fact]
    public async Task List_RejectsUnknownStateField()
    {
        LanguagePackStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        await File.WriteAllTextAsync(
            store.StatePath,
            "{\"schemaVersion\":1,\"enabledPackIds\":[],\"script\":\"calc\"}");

        LanguagePackListResult result = await store.ListAsync();

        LanguagePackLoadError error = Assert.Single(result.Errors);
        Assert.Equal(LanguagePackErrorCode.InvalidManifest, error.Code);
    }

    [Fact]
    public async Task Preview_RejectsReparsePointSource()
    {
        string source = await WriteManifestAsync();
        string link = Path.Combine(_root, "source-link.json");
        try
        {
            File.CreateSymbolicLink(link, source);
        }
        catch (Exception linkError) when (linkError is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        LanguagePackException exception = await Assert.ThrowsAsync<LanguagePackException>(() =>
            new LanguagePackStore(_root).PreviewImportAsync(link));

        Assert.Equal(LanguagePackErrorCode.UnsafePath, exception.Code);
    }

    [Fact]
    public async Task List_RejectsReparsePointLockWithoutOpeningExternalTarget()
    {
        LanguagePackStore store = new(_root);
        string lockPath = Path.Combine(_root, "state", "language-packs.lock");
        string outside = Path.Combine(_root, "outside.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(outside, "external");
        try
        {
            File.CreateSymbolicLink(lockPath, outside);
        }
        catch (Exception linkError) when (linkError is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        LanguagePackListResult result = await store.ListAsync();

        LanguagePackLoadError error = Assert.Single(result.Errors);
        Assert.Equal(LanguagePackErrorCode.UnsafePath, error.Code);
        Assert.Equal("external", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task Enable_RejectsUnknownPackWithoutWritingState()
    {
        LanguagePackStore store = new(_root);

        LanguagePackException exception = await Assert.ThrowsAsync<LanguagePackException>(() =>
            store.EnableAsync("missing-pack"));

        Assert.Equal(LanguagePackErrorCode.PackNotFound, exception.Code);
        Assert.False(File.Exists(store.StatePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (string file in Directory.EnumerateFiles(
                         _root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<string> WriteManifestAsync(
        JsonObject? manifest = null,
        string fileName = "language-pack.json")
    {
        string sources = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sources);
        string path = Path.Combine(sources, fileName);
        await File.WriteAllTextAsync(
            path,
            (manifest ?? LanguagePackTestData.CreateManifest()).ToJsonString());
        return path;
    }
}
