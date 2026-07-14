using AutoEnvPlus.App.RuntimeCatalogs;

namespace AutoEnvPlus.Core.Tests;

public sealed class AdoptiumFeatureReleaseCatalogTests
{
    [Fact]
    public async Task GetAsync_UsesOfficialMetadataAndRecommendsLatestAvailableLts()
    {
        Uri? requestedUri = null;
        const string response = """
            {
              "available_lts_releases": [8, 11, 17, 21, 25],
              "available_releases": [8, 21, 17, 25, 26, 25],
              "most_recent_feature_release": 26,
              "most_recent_lts": 25
            }
            """;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return StubHttpMessageHandler.Text(response, "application/json");
        }));
        AdoptiumFeatureReleaseCatalog catalog = new(
            client,
            new Uri("https://api.example.test/v3"));

        JavaFeatureReleaseCatalogSnapshot snapshot = await catalog.GetAsync();

        Assert.Equal(new Uri("https://api.example.test/v3/info/available_releases"), requestedUri);
        Assert.Equal([26, 25, 21, 17, 8], snapshot.AvailableReleases);
        Assert.Equal(26, snapshot.LatestFeatureRelease);
        Assert.Equal(25, snapshot.RecommendedFeatureRelease);
        Assert.True(snapshot.LtsReleases.SetEquals([8, 17, 21, 25]));
    }

    [Fact]
    public async Task GetAsync_FallsBackWhenSummaryVersionsAreNotAvailable()
    {
        const string response = """
            {
              "available_lts_releases": [8, 17],
              "available_releases": [8, 17, 22],
              "most_recent_feature_release": 99,
              "most_recent_lts": 21
            }
            """;
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(response, "application/json")));

        JavaFeatureReleaseCatalogSnapshot snapshot = await new AdoptiumFeatureReleaseCatalog(
            client,
            new Uri("https://api.example.test/v3/")).GetAsync();

        Assert.Equal(22, snapshot.LatestFeatureRelease);
        Assert.Equal(17, snapshot.RecommendedFeatureRelease);
    }

    [Fact]
    public async Task GetAsync_RejectsCatalogWithoutSupportedGaVersions()
    {
        const string response = """
            {
              "available_lts_releases": [7],
              "available_releases": [1, 7]
            }
            """;
        using HttpClient client = new(new StubHttpMessageHandler(
            _ => StubHttpMessageHandler.Text(response, "application/json")));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new AdoptiumFeatureReleaseCatalog(
                client,
                new Uri("https://api.example.test/v3/")).GetAsync());

        Assert.Contains("没有可安装的 GA 版本线", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsInsecureApiBaseUri()
    {
        using HttpClient client = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new AdoptiumFeatureReleaseCatalog(
                client,
                new Uri("http://api.example.test/v3/")));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }
}
