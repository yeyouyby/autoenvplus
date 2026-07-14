using System.Text.Json;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public interface IGlobalRuntimeProfileStore
{
    Task<RuntimeProfile> LoadAsync(CancellationToken cancellationToken = default);

    Task<RuntimeProfile> SetAsync(
        RuntimeKind kind,
        VersionSelector selector,
        CancellationToken cancellationToken = default);

    Task<RuntimeProfile> ReplaceAsync(
        RuntimeProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class GlobalRuntimeProfileStore : IGlobalRuntimeProfileStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _managedRoot;
    private readonly string _profilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GlobalRuntimeProfileStore(string managedRoot, string? profilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _profilePath = Path.GetFullPath(
            profilePath ?? Path.Combine(_managedRoot, "state", "global-profile.json"));
        EnsureChildPath(_managedRoot, _profilePath);
    }

    public async Task<RuntimeProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeProfile> SetAsync(
        RuntimeKind kind,
        VersionSelector selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeProfile current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<RuntimeKind, VersionSelector> selections = new(current.Selections)
            {
                [kind] = selector,
            };
            await SaveCoreAsync(selections, cancellationToken).ConfigureAwait(false);
            return new RuntimeProfile(selections);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeProfile> ReplaceAsync(
        RuntimeProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(profile.Selections, cancellationToken).ConfigureAwait(false);
            return new RuntimeProfile(new Dictionary<RuntimeKind, VersionSelector>(
                profile.Selections));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RuntimeProfile> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_profilePath))
        {
            return RuntimeProfile.Empty;
        }

        await using FileStream stream = new(
            _profilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8_192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ProfileDocument? document = await JsonSerializer.DeserializeAsync<ProfileDocument>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The global runtime profile has an unsupported schema.");
        }

        Dictionary<RuntimeKind, VersionSelector> selections = [];
        foreach ((string key, string value) in document.Selections ?? [])
        {
            if (!Enum.TryParse(key, true, out RuntimeKind kind)
                || !VersionSelector.TryParse(value, out VersionSelector? selector))
            {
                throw new InvalidDataException($"The global runtime selection '{key}={value}' is invalid.");
            }

            selections[kind] = selector!;
        }

        return new RuntimeProfile(selections);
    }

    private async Task SaveCoreAsync(
        IReadOnlyDictionary<RuntimeKind, VersionSelector> selections,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_profilePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_profilePath)}.{Guid.NewGuid():N}.tmp");
        EnsureChildPath(_managedRoot, temporaryPath);
        ProfileDocument document = new(
            CurrentSchemaVersion,
            selections.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase));

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8_192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _profilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        string rootPrefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The profile file must remain inside the managed root.");
        }
    }

    private sealed record ProfileDocument(
        int SchemaVersion,
        Dictionary<string, string>? Selections);
}
