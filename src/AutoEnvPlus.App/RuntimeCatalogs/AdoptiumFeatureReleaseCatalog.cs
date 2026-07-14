using System.Text.Json;

namespace AutoEnvPlus.App.RuntimeCatalogs;

internal sealed class AdoptiumFeatureReleaseCatalog
{
    private static readonly Uri DefaultBaseUri = new("https://api.adoptium.net/v3/");
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public AdoptiumFeatureReleaseCatalog(HttpClient httpClient, Uri? baseUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = EnsureSecureBaseUri(baseUri ?? DefaultBaseUri);
    }

    public async Task<JavaFeatureReleaseCatalogSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(_baseUri, "info/available_releases"));
        request.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 16 },
            cancellationToken);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Adoptium Java 版本目录格式无效。");
        }

        HashSet<int> available = ReadFeatureVersions(root, "available_releases", required: true);
        HashSet<int> lts = ReadFeatureVersions(root, "available_lts_releases", required: false);
        lts.IntersectWith(available);

        int latestFeature = ReadKnownVersion(root, "most_recent_feature_release", available)
            ?? available.Max();
        int recommended = ReadKnownVersion(root, "most_recent_lts", lts)
            ?? lts.DefaultIfEmpty(latestFeature).Max();

        return new JavaFeatureReleaseCatalogSnapshot(
            available.OrderByDescending(version => version).ToArray(),
            lts,
            latestFeature,
            recommended);
    }

    private static HashSet<int> ReadFeatureVersions(
        JsonElement root,
        string propertyName,
        bool required)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array)
        {
            if (required)
            {
                throw new InvalidDataException($"Adoptium Java 版本目录缺少 {propertyName}。");
            }

            return [];
        }

        HashSet<int> versions = [];
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number
                && item.TryGetInt32(out int version)
                && version is >= 8 and <= 999)
            {
                versions.Add(version);
            }
        }

        if (required && versions.Count == 0)
        {
            throw new InvalidDataException("Adoptium Java 版本目录没有可安装的 GA 版本线。");
        }

        return versions;
    }

    private static int? ReadKnownVersion(
        JsonElement root,
        string propertyName,
        IReadOnlySet<int> knownVersions) =>
        root.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out int version)
        && knownVersions.Contains(version)
            ? version
            : null;

    private static Uri EnsureSecureBaseUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Adoptium API base URI must use HTTPS.", nameof(baseUri));
        }

        string absoluteUri = baseUri.AbsoluteUri;
        return absoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(absoluteUri + '/', UriKind.Absolute);
    }
}

internal sealed record JavaFeatureReleaseCatalogSnapshot(
    IReadOnlyList<int> AvailableReleases,
    IReadOnlySet<int> LtsReleases,
    int LatestFeatureRelease,
    int RecommendedFeatureRelease);
