using System.Security.Cryptography;
using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Networking;

public sealed record ProviderSourceNetworkSettingsLoadResult(
    NetworkSettings? Settings,
    IReadOnlyList<ProviderSourceNetworkSelection> Selections,
    IReadOnlyList<string> Errors,
    string NetworkSettingsPath,
    string? NetworkSettingsSha256,
    string ProviderSourcePreferencesPath,
    string? ProviderSourcePreferencesSha256)
{
    public bool Success => Settings is not null && Errors.Count == 0;
}

public sealed class ProviderSourceNetworkSettingsLoader
{
    private const int MaximumSnapshotAttempts = 3;

    private readonly string _managedRoot;
    private readonly NetworkSettingsStore _networkStore;
    private readonly ProviderSourcePreferenceStore _sourceStore;

    public ProviderSourceNetworkSettingsLoader(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _networkStore = new NetworkSettingsStore(_managedRoot);
        _sourceStore = new ProviderSourcePreferenceStore(_managedRoot);
    }

    public string NetworkSettingsPath => _networkStore.SettingsPath;

    public string ProviderSourcePreferencesPath => _sourceStore.PreferencesPath;

    public Task<ProviderSourceNetworkSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default) =>
        LoadWithStableSnapshotAsync(null, cancellationToken);

    public Task<ProviderSourceNetworkSettingsLoadResult> LoadForToolsAsync(
        IEnumerable<string> networkToolIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networkToolIds);
        string[] requestedTools = networkToolIds.Select(networkToolId =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(networkToolId);
            return networkToolId.Trim();
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return LoadWithStableSnapshotAsync(requestedTools, cancellationToken);
    }

    private async Task<ProviderSourceNetworkSettingsLoadResult> LoadWithStableSnapshotAsync(
        IReadOnlyList<string>? networkToolIds,
        CancellationToken cancellationToken)
    {
        FileRevision latestNetwork = FileRevision.Unreadable;
        FileRevision latestSources = FileRevision.Unreadable;
        for (int attempt = 0; attempt < MaximumSnapshotAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileRevision networkBefore = await ReadFileRevisionAsync(
                NetworkSettingsPath,
                cancellationToken).ConfigureAwait(false);
            FileRevision sourcesBefore = await ReadFileRevisionAsync(
                ProviderSourcePreferencesPath,
                cancellationToken).ConfigureAwait(false);
            ProviderSourceNetworkSettingsLoadResult loaded = await LoadCoreAsync(
                networkToolIds,
                cancellationToken).ConfigureAwait(false);
            FileRevision networkAfter = await ReadFileRevisionAsync(
                NetworkSettingsPath,
                cancellationToken).ConfigureAwait(false);
            FileRevision sourcesAfter = await ReadFileRevisionAsync(
                ProviderSourcePreferencesPath,
                cancellationToken).ConfigureAwait(false);
            latestNetwork = networkAfter;
            latestSources = sourcesAfter;
            if (networkBefore.Readable
                && sourcesBefore.Readable
                && networkAfter.Readable
                && sourcesAfter.Readable
                && networkBefore == networkAfter
                && sourcesBefore == sourcesAfter)
            {
                return loaded with
                {
                    NetworkSettingsSha256 = networkAfter.Sha256,
                    ProviderSourcePreferencesSha256 = sourcesAfter.Sha256,
                };
            }
        }

        return new ProviderSourceNetworkSettingsLoadResult(
            null,
            [],
            ["Network or Provider source settings changed while they were being loaded; retry."],
            NetworkSettingsPath,
            latestNetwork.Readable ? latestNetwork.Sha256 : null,
            ProviderSourcePreferencesPath,
            latestSources.Readable ? latestSources.Sha256 : null);
    }

    private async Task<ProviderSourceNetworkSettingsLoadResult> LoadCoreAsync(
        IReadOnlyList<string>? networkToolIds,
        CancellationToken cancellationToken)
    {
        NetworkSettingsLoadResult network = await _networkStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!network.Success || network.Settings is null)
        {
            return Failure(network.Errors.Select(error =>
                $"Network settings ({error.Path}): {error.Message}"));
        }

        LanguageCatalog catalog = await new LanguagePackStore(_managedRoot)
            .GetEffectiveCatalogAsync(cancellationToken).ConfigureAwait(false);
        ProviderSourcePreferences preferences = await _sourceStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        ProviderSourceNetworkProjectionResult projection = networkToolIds is null
            ? ProviderSourceNetworkProjection.Project(catalog, preferences, network.Settings)
            : ProviderSourceNetworkProjection.ProjectForTools(
                catalog,
                preferences,
                network.Settings,
                networkToolIds);
        if (!projection.Success || projection.Settings is null)
        {
            return Failure(projection.Errors.Select(error =>
                $"Provider source ({error.NetworkToolId}): {error.Message}"));
        }

        return new ProviderSourceNetworkSettingsLoadResult(
            projection.Settings,
            projection.Selections,
            [],
            NetworkSettingsPath,
            null,
            ProviderSourcePreferencesPath,
            null);
    }

    private ProviderSourceNetworkSettingsLoadResult Failure(IEnumerable<string> errors) => new(
        null,
        [],
        errors.ToArray(),
        NetworkSettingsPath,
        null,
        ProviderSourcePreferencesPath,
        null);

    private static async Task<FileRevision> ReadFileRevisionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new FileRevision(true, false, null);
        }

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return new FileRevision(true, true, sha256);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return FileRevision.Unreadable;
        }
    }

    private sealed record FileRevision(
        bool Readable,
        bool Exists,
        string? Sha256)
    {
        public static FileRevision Unreadable { get; } = new(false, false, null);
    }
}
