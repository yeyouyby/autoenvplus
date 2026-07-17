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
    public const long MaximumProfileBytes = 256 * 1024;
    public const int MaximumProfileSelections = 64;

    private const int CurrentSchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _managedRoot;
    private readonly string _profilePath;
    private readonly ManagedStateLock _transactionLock;
    private readonly ManagedStateLock _stateLock;

    public GlobalRuntimeProfileStore(string managedRoot, string? profilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _profilePath = Path.GetFullPath(
            profilePath ?? Path.Combine(_managedRoot, "state", "global-profile.json"));
        EnsureChildPath(_managedRoot, _profilePath);
        _transactionLock = ManagedStateLock.CreateRuntimeTransaction(_managedRoot);
        _stateLock = new ManagedStateLock(
            _managedRoot,
            _profilePath,
            "global-runtime-profile.lock");
    }

    public string ProfilePath => _profilePath;

    public async Task<RuntimeProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await LoadWithinTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeProfile> LoadWithinTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeProfile> SetAsync(
        RuntimeKind kind,
        VersionSelector selector,
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await SetWithinTransactionAsync(kind, selector, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RuntimeProfile> SetWithinTransactionAsync(
        RuntimeKind kind,
        VersionSelector selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RuntimeProfile current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<RuntimeKind, VersionSelector> selections = new(current.Selections)
        {
            [kind] = selector,
        };
        Dictionary<RuntimeKind, RuntimeSelectionIdentity> exactSelections = new(
            current.ExactSelections);
        exactSelections.Remove(kind);
        RuntimeProfile updated = new(selections) { ExactSelections = exactSelections };
        await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<RuntimeProfile> SetExactAsync(
        RuntimeKind kind,
        VersionSelector selector,
        string runtimeId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await SetExactWithinTransactionAsync(
            kind,
            selector,
            runtimeId,
            providerId,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeProfile> SetExactWithinTransactionAsync(
        RuntimeKind kind,
        VersionSelector selector,
        string runtimeId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ValidateIdentity(runtimeId, providerId);
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RuntimeProfile current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<RuntimeKind, VersionSelector> selections = new(current.Selections)
        {
            [kind] = selector,
        };
        Dictionary<RuntimeKind, RuntimeSelectionIdentity> exactSelections = new(
            current.ExactSelections)
        {
            [kind] = new RuntimeSelectionIdentity(runtimeId.Trim(), providerId.Trim()),
        };
        RuntimeProfile updated = new(selections) { ExactSelections = exactSelections };
        await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<RuntimeProfile> ReplaceAsync(
        RuntimeProfile profile,
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await ReplaceWithinTransactionAsync(profile, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RuntimeProfile> ReplaceWithinTransactionAsync(
        RuntimeProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RuntimeProfile copy = new(new Dictionary<RuntimeKind, VersionSelector>(
            profile.Selections))
        {
            ExactSelections = new Dictionary<RuntimeKind, RuntimeSelectionIdentity>(
                profile.ExactSelections),
        };
        await SaveCoreAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy;
    }

    private async Task<RuntimeProfile> LoadCoreAsync(CancellationToken cancellationToken)
    {
        _stateLock.EnsureStatePathSafe(createDirectory: false);
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
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (stream.Length > MaximumProfileBytes)
        {
            throw new InvalidDataException(
                $"The global runtime profile exceeds the {MaximumProfileBytes}-byte limit.");
        }

        ProfileDocument? document = await JsonSerializer.DeserializeAsync<ProfileDocument>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (document is null
            || document.SchemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion)
        {
            throw new InvalidDataException("The global runtime profile has an unsupported schema.");
        }

        if (document.Selections is { Count: > MaximumProfileSelections }
            || document.ExactSelections is { Count: > MaximumProfileSelections })
        {
            throw new InvalidDataException(
                $"The global runtime profile exceeds the {MaximumProfileSelections}-selection limit.");
        }

        Dictionary<RuntimeKind, VersionSelector> selections = [];
        foreach ((string key, string value) in document.Selections ?? [])
        {
            if (!Enum.TryParse(key, true, out RuntimeKind kind)
                || !VersionSelector.TryParse(value, out VersionSelector? selector))
            {
                throw new InvalidDataException($"The global runtime selection '{key}={value}' is invalid.");
            }

            if (!selections.TryAdd(kind, selector!))
            {
                throw new InvalidDataException(
                    "The global runtime profile contains duplicate selection kinds.");
            }
        }

        Dictionary<RuntimeKind, RuntimeSelectionIdentity> exactSelections = [];
        if (document.SchemaVersion == LegacySchemaVersion
            && document.ExactSelections is { Count: > 0 })
        {
            throw new InvalidDataException(
                "A schema 1 global runtime profile cannot contain exact selections.");
        }

        foreach ((string key, ExactSelectionDocument value) in
                 document.ExactSelections ?? [])
        {
            if (!Enum.TryParse(key, true, out RuntimeKind kind)
                || !selections.ContainsKey(kind))
            {
                throw new InvalidDataException(
                    "An exact global runtime selection has no matching version selector.");
            }

            ValidateIdentity(value.RuntimeId, value.ProviderId);
            if (!exactSelections.TryAdd(
                    kind,
                    new RuntimeSelectionIdentity(
                        value.RuntimeId.Trim(),
                        value.ProviderId.Trim())))
            {
                throw new InvalidDataException(
                    "The global runtime profile contains duplicate exact selection kinds.");
            }
        }

        return new RuntimeProfile(selections) { ExactSelections = exactSelections };
    }

    private async Task SaveCoreAsync(
        RuntimeProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Selections.Count > MaximumProfileSelections
            || profile.ExactSelections.Count > MaximumProfileSelections)
        {
            throw new InvalidDataException(
                $"The global runtime profile cannot exceed {MaximumProfileSelections} selections.");
        }

        string directory = Path.GetDirectoryName(_profilePath)!;
        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_profilePath)}.{Guid.NewGuid():N}.tmp");
        EnsureChildPath(_managedRoot, temporaryPath);
        _stateLock.EnsureTemporaryFilePathSafe(temporaryPath);
        foreach ((RuntimeKind kind, RuntimeSelectionIdentity identity) in
                 profile.ExactSelections)
        {
            if (!profile.Selections.ContainsKey(kind))
            {
                throw new InvalidDataException(
                    "An exact global selection requires a matching version selector.");
            }

            ValidateIdentity(identity.RuntimeId, identity.ProviderId);
        }

        ProfileDocument document = new(
            CurrentSchemaVersion,
            profile.Selections.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
            profile.ExactSelections.ToDictionary(
                pair => pair.Key.ToString(),
                pair => new ExactSelectionDocument(
                    pair.Value.RuntimeId,
                    pair.Value.ProviderId),
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
                _stateLock.EnsureTemporaryFilePathSafe(temporaryPath);
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumProfileBytes)
                {
                    throw new InvalidDataException(
                        $"The global runtime profile cannot exceed {MaximumProfileBytes} bytes.");
                }
            }

            _stateLock.EnsureStatePathSafe(createDirectory: false);
            File.Move(temporaryPath, _profilePath, overwrite: true);
            _stateLock.EnsureStatePathSafe(createDirectory: false);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateIdentity(string? runtimeId, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId)
            || runtimeId.Length > 256
            || runtimeId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > 256
            || providerId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "A global runtime identity pin contains an invalid runtime or Provider ID.");
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
        Dictionary<string, string>? Selections,
        Dictionary<string, ExactSelectionDocument>? ExactSelections = null);

    private sealed record ExactSelectionDocument(
        string RuntimeId,
        string ProviderId);
}
