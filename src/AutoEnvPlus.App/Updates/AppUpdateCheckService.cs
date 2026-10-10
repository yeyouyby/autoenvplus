using System.IO;
using System.Net.Http;
using System.Text.Json;
using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.App.Updates;

internal sealed record AppUpdateInfo(
    string LatestVersion,
    Uri ReleaseUrl);

internal static class AppUpdateCheckService
{
    private const string ReleasesListUrl =
        "https://api.github.com/repos/yeyouyby/autoenvplus/releases?per_page=1";

    private static readonly Uri FallbackReleaseUrl = new(
        "https://github.com/yeyouyby/autoenvplus/releases/latest");

    public static async Task<AppUpdateInfo?> CheckForUpdateAsync(
        string managedRoot,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        try
        {
            NetworkSettingsLoadResult loaded = await new NetworkSettingsStore(managedRoot)
                .LoadAsync(cancellationToken);
            EffectiveNetworkSettings network = BuildNetworkSettings(loaded.Settings);
            using HttpClient httpClient = NetworkHttpClientFactory.Create(
                network,
                TimeSpan.FromSeconds(15));
            using HttpResponseMessage response = await httpClient.GetAsync(
                ReleasesListUrl,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;
            // The list endpoint is used instead of releases/latest because
            // this repo publishes preview builds as prereleases, which the
            // "latest" endpoint excludes.
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement release = root[0];
            if (!release.TryGetProperty("tag_name", out JsonElement tag)
                || tag.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string latestVersion = tag.GetString()?.Trim().TrimStart('v', 'V')
                ?? string.Empty;
            if (!Version.TryParse(latestVersion, out Version? latest)
                || !Version.TryParse(currentVersion.Trim(), out Version? current)
                || latest <= current)
            {
                return null;
            }

            Uri releaseUrl = release.TryGetProperty("html_url", out JsonElement url)
                && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? parsed)
                && parsed is not null
                    ? parsed
                    : FallbackReleaseUrl;
            return new AppUpdateInfo(latestVersion, releaseUrl);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            // The update check is best-effort: an offline machine, a blocked
            // endpoint, or a malformed response simply means no notice.
            return null;
        }
    }

    private static EffectiveNetworkSettings BuildNetworkSettings(NetworkSettings? settings)
    {
        // The update check has no tool scope; it only honors the global proxy
        // configuration. Malformed proxy values fall back to a direct
        // connection instead of failing the whole check.
        GlobalNetworkSettings global = settings?.Global ?? new GlobalNetworkSettings();
        return new EffectiveNetworkSettings(
            "app-update-check",
            TryParseEndpoint(global.HttpProxy),
            TryParseEndpoint(global.HttpsProxy),
            global.NoProxy ?? [],
            null);
    }

    private static Uri? TryParseEndpoint(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
                && uri is not null
                && !string.IsNullOrWhiteSpace(uri.Host)
                    ? uri
                    : null;
}
