using System.Collections.Concurrent;
using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Tests;

public sealed class LanguageVisibilityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-LanguageVisibility-{Guid.NewGuid():N}");

    [Fact]
    public async Task DefaultState_EvaluatesTopTenPlusDetectedLanguages()
    {
        LanguageVisibilityStore store = new(_root);
        LanguageVisibilityState state = await store.LoadAsync(BuiltInLanguageCatalog.Current);

        LanguageVisibilitySnapshot snapshot = LanguageVisibilityEvaluator.Evaluate(
            BuiltInLanguageCatalog.Current,
            state,
            ["ruby"]);

        Assert.Equal(11, snapshot.VisibleLanguages.Count);
        Assert.Contains(snapshot.VisibleLanguages, entry => entry.Language.Id == "ruby"
            && entry.IsDetected);
        Assert.All(
            BuiltInLanguageCatalog.Current.DefaultLanguages,
            language => Assert.Contains(
                snapshot.VisibleLanguages,
                entry => entry.Language.Id == language.Id));
    }

    [Fact]
    public async Task ExplicitHideWinsAndExplicitEnableClearsHide()
    {
        LanguageVisibilityStore store = new(_root);

        LanguageVisibilityState hidden = await store.SetHiddenAsync(
            BuiltInLanguageCatalog.Current,
            "python",
            true);
        LanguageVisibilitySnapshot hiddenSnapshot = LanguageVisibilityEvaluator.Evaluate(
            BuiltInLanguageCatalog.Current,
            hidden,
            ["python"]);
        LanguageVisibilityEntry pythonHidden = Assert.Single(
            hiddenSnapshot.Entries,
            entry => entry.Language.Id == "python");
        Assert.False(pythonHidden.IsVisible);
        Assert.True(pythonHidden.IsExplicitlyHidden);

        LanguageVisibilityState enabled = await store.SetEnabledAsync(
            BuiltInLanguageCatalog.Current,
            "python",
            true);
        LanguageVisibilityEntry pythonEnabled = Assert.Single(
            LanguageVisibilityEvaluator.Evaluate(
                BuiltInLanguageCatalog.Current,
                enabled,
                []).Entries,
            entry => entry.Language.Id == "python");
        Assert.True(pythonEnabled.IsVisible);
        Assert.True(pythonEnabled.IsExplicitlyEnabled);
        Assert.False(pythonEnabled.IsExplicitlyHidden);
    }

    [Fact]
    public async Task StatePersistsAcrossInstancesAndCanResetOneLanguage()
    {
        LanguageVisibilityStore first = new(_root);
        await first.SetEnabledAsync(BuiltInLanguageCatalog.Current, "ruby", true);
        await first.SetHiddenAsync(BuiltInLanguageCatalog.Current, "java", true);

        LanguageVisibilityStore second = new(_root);
        LanguageVisibilityState loaded = await second.LoadAsync(BuiltInLanguageCatalog.Current);
        Assert.Contains("ruby", loaded.EnabledLanguageIds);
        Assert.Contains("java", loaded.HiddenLanguageIds);

        LanguageVisibilityState reset = await second.ResetAsync(
            BuiltInLanguageCatalog.Current,
            "java");
        Assert.DoesNotContain("java", reset.HiddenLanguageIds);
        Assert.Contains("ruby", reset.EnabledLanguageIds);

        LanguageVisibilityState resetAll = await second.ResetAsync(
            BuiltInLanguageCatalog.Current);
        Assert.Empty(resetAll.EnabledLanguageIds);
        Assert.Empty(resetAll.HiddenLanguageIds);
    }

    [Fact]
    public async Task TwoInstances_DoNotLoseConcurrentUpdates()
    {
        LanguageVisibilityStore first = new(_root);
        LanguageVisibilityStore second = new(_root);
        ConcurrentBag<Exception> failures = [];

        await Task.WhenAll(
            Task.Run(async () =>
            {
                try
                {
                    await first.SetEnabledAsync(BuiltInLanguageCatalog.Current, "ruby", true);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }),
            Task.Run(async () =>
            {
                try
                {
                    await second.SetEnabledAsync(BuiltInLanguageCatalog.Current, "kotlin", true);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }));

        Assert.Empty(failures);
        LanguageVisibilityState state = await first.LoadAsync(BuiltInLanguageCatalog.Current);
        Assert.Contains("ruby", state.EnabledLanguageIds);
        Assert.Contains("kotlin", state.EnabledLanguageIds);
    }

    [Fact]
    public async Task Load_RejectsUnknownDuplicateAndOverlappingIds()
    {
        foreach (string json in new[]
        {
            "{\"schemaVersion\":1,\"enabledLanguageIds\":[\"missing\"],\"hiddenLanguageIds\":[]}",
            "{\"schemaVersion\":1,\"enabledLanguageIds\":[\"ruby\",\"RUBY\"],\"hiddenLanguageIds\":[]}",
            "{\"schemaVersion\":1,\"enabledLanguageIds\":[\"ruby\"],\"hiddenLanguageIds\":[\"ruby\"]}",
            "{\"schemaVersion\":1,\"SchemaVersion\":1,\"enabledLanguageIds\":[],\"hiddenLanguageIds\":[]}",
            "{\"schemaVersion\":1,\"enabledLanguageIds\":[],\"hiddenLanguageIds\":[],\"script\":\"calc\"}",
        })
        {
            string caseRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            LanguageVisibilityStore store = new(caseRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
            await File.WriteAllTextAsync(store.StatePath, json);

            await Assert.ThrowsAsync<LanguageVisibilityException>(() =>
                store.LoadAsync(BuiltInLanguageCatalog.Current));
        }
    }

    [Fact]
    public async Task Load_RejectsReparseState()
    {
        LanguageVisibilityStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        string outside = Path.Combine(_root, "outside.json");
        await File.WriteAllTextAsync(
            outside,
            "{\"schemaVersion\":1,\"enabledLanguageIds\":[],\"hiddenLanguageIds\":[]}");
        try
        {
            File.CreateSymbolicLink(store.StatePath, outside);
        }
        catch (Exception linkError) when (linkError is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        LanguageVisibilityException exception = await Assert.ThrowsAsync<
            LanguageVisibilityException>(() => store.LoadAsync(BuiltInLanguageCatalog.Current));

        Assert.Equal(LanguageVisibilityErrorCode.UnsafePath, exception.Code);
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
}
