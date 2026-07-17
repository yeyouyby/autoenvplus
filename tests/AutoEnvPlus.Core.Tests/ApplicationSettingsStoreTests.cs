using AutoEnvPlus.Core.Settings;

namespace AutoEnvPlus.Core.Tests;

public sealed class ApplicationSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-ApplicationSettings-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_ReturnsConservativeDefaultsWhenMissing()
    {
        AutoEnvPlusApplicationSettings settings =
            await new AutoEnvPlusApplicationSettingsStore(_root).LoadAsync();

        Assert.Equal(OverviewRefreshPolicy.CachedOnly, settings.OverviewRefreshPolicy);
        Assert.Equal(LanguageVisibilityPolicy.TopTenAndDetected, settings.LanguageVisibilityPolicy);
        Assert.Equal(8, settings.DefaultDownloadConnections);
        Assert.True(settings.UseManagedShims);
        Assert.True(settings.RequireDestructiveActionConfirmation);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsEverySetting()
    {
        AutoEnvPlusApplicationSettings expected = AutoEnvPlusApplicationSettings.Default with
        {
            StartupDestination = StartupDestination.Languages,
            OverviewRefreshPolicy = OverviewRefreshPolicy.Quick,
            DeepScanIntervalHours = 24,
            LanguageVisibilityPolicy = LanguageVisibilityPolicy.AllBuiltIn,
            DefaultDownloadConnections = 16,
            DefaultDownloadMaximumBytes = 16L * 1024 * 1024 * 1024,
            Theme = ApplicationThemePreference.Dark,
            Backdrop = BackdropPreference.Solid,
            Density = InterfaceDensity.Compact,
            ShellAutoActivation = false,
            UseManagedShims = false,
            CatalogUpdatePolicy = CatalogUpdatePolicy.Daily,
            LogRetentionDays = 90,
            RequireDestructiveActionConfirmation = false,
            ShowExperimentalTools = true,
        };
        AutoEnvPlusApplicationSettingsStore store = new(_root);

        await store.SaveAsync(expected);

        Assert.Equal(expected, await store.LoadAsync());
    }

    [Fact]
    public async Task UpdateAsync_SerializesConcurrentReadModifyWriteOperations()
    {
        AutoEnvPlusApplicationSettingsStore first = new(_root);
        AutoEnvPlusApplicationSettingsStore second = new(_root);

        await Task.WhenAll(
            first.UpdateAsync(settings => settings with { DefaultDownloadConnections = 16 }),
            second.UpdateAsync(settings => settings with { LogRetentionDays = 120 }));

        AutoEnvPlusApplicationSettings actual = await first.LoadAsync();
        Assert.Equal(16, actual.DefaultDownloadConnections);
        Assert.Equal(120, actual.LogRetentionDays);
    }

    [Fact]
    public async Task LoadAsync_RejectsUnknownAndDuplicateFields()
    {
        AutoEnvPlusApplicationSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"unknown\":true}");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync());

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedDocumentBeforeParsing()
    {
        AutoEnvPlusApplicationSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await using (FileStream stream = new(
            store.SettingsPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(AutoEnvPlusApplicationSettingsStore.MaximumSettingsBytes + 1);
        }

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync());

        Assert.Contains("bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_RejectsUnsupportedDownloadConnections()
    {
        AutoEnvPlusApplicationSettings invalid = AutoEnvPlusApplicationSettings.Default with
        {
            DefaultDownloadConnections = 3,
        };

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new AutoEnvPlusApplicationSettingsStore(_root).SaveAsync(invalid));

        Assert.Contains("connections", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
