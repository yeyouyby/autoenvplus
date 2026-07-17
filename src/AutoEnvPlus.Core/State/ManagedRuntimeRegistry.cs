using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public sealed record ManagedRuntimeEntry(
    string Id,
    string ProviderId,
    RuntimeKind Kind,
    RuntimeVersion Version,
    RuntimeArchitecture Architecture,
    string InstallRoot,
    string ExecutableRelativePath,
    string PackageHash,
    DateTimeOffset InstalledAtUtc,
    IReadOnlyCollection<string>? Channels = null,
    PackageHashAlgorithm PackageHashAlgorithm = PackageHashAlgorithm.Sha256)
{
    public string ExecutablePath => Path.GetFullPath(
        Path.Combine(InstallRoot, ExecutableRelativePath));

    public RuntimeInstallation ToRuntimeInstallation() => new(
        Id,
        Kind,
        Version,
        Architecture,
        InstallRoot,
        RuntimeOwnership.Managed,
        Channels ?? []);
}

public sealed record RegistryLoadResult(
    IReadOnlyList<ManagedRuntimeEntry> Entries,
    IReadOnlyList<string> Errors);

public interface IManagedRuntimeRegistryStore
{
    Task<RegistryLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<RegistryLoadResult> UpsertAsync(
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken = default);

    Task<RegistryLoadResult> RemoveAsync(
        string id,
        CancellationToken cancellationToken = default);
}

public sealed class ManagedRuntimeRegistry : IManagedRuntimeRegistryStore
{
    public const long MaximumRegistryBytes = 4 * 1024 * 1024;
    public const int MaximumRegistryEntries = 4_096;

    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _managedRoot;
    private readonly string _registryPath;
    private readonly ManagedStateLock _transactionLock;
    private readonly ManagedStateLock _stateLock;

    public ManagedRuntimeRegistry(string managedRoot, string? registryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _registryPath = Path.GetFullPath(
            registryPath ?? Path.Combine(_managedRoot, "state", "installations.json"));
        EnsureChildPath(_managedRoot, _registryPath, "registry file");
        _transactionLock = ManagedStateLock.CreateRuntimeTransaction(_managedRoot);
        _stateLock = new ManagedStateLock(
            _managedRoot,
            _registryPath,
            "managed-runtime-registry.lock");
    }

    public string RegistryPath => _registryPath;

    public async Task<RegistryLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await LoadWithinTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RegistryLoadResult> LoadWithinTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RegistryLoadResult> UpsertAsync(
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await UpsertWithinTransactionAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RegistryLoadResult> UpsertWithinTransactionAsync(
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RegistryLoadResult existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Errors.Count > 0)
        {
            throw new InvalidDataException(
                "The managed runtime registry contains invalid entries: "
                + string.Join("; ", existing.Errors));
        }

        ManagedRuntimeEntry? precedenceConflict = existing.Entries.FirstOrDefault(item =>
            !item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)
            && item.Kind == entry.Kind
            && item.Architecture == entry.Architecture
            && item.ProviderId.Equals(entry.ProviderId, StringComparison.OrdinalIgnoreCase)
            && item.Version.CompareTo(entry.Version) == 0);
        if (precedenceConflict is not null)
        {
            throw new InvalidDataException(
                "The managed runtime registry already contains an equivalent version from "
                + "the same Provider, runtime kind, and architecture. Remove it before "
                + "registering a replacement package identity.");
        }

        List<ManagedRuntimeEntry> entries = existing.Entries
            .Where(item => !item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase))
            .Append(entry)
            .OrderBy(item => item.Kind)
            .ThenByDescending(item => item.Version)
            .ThenBy(item => item.Architecture)
            .ToList();
        await SaveCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        return new RegistryLoadResult(entries, []);
    }

    public async Task<RegistryLoadResult> RemoveAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease transactionLock = await _transactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await RemoveWithinTransactionAsync(id, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RegistryLoadResult> RemoveWithinTransactionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RegistryLoadResult existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Errors.Count > 0)
        {
            throw new InvalidDataException(
                "The managed runtime registry contains invalid entries: "
                + string.Join("; ", existing.Errors));
        }

        List<ManagedRuntimeEntry> entries = existing.Entries
            .Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count == existing.Entries.Count)
        {
            return existing;
        }

        await SaveCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        return new RegistryLoadResult(entries, []);
    }

    private async Task<RegistryLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (!File.Exists(_registryPath))
        {
            return new RegistryLoadResult([], []);
        }

        await using FileStream stream = new(
            _registryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (stream.Length > MaximumRegistryBytes)
        {
            return new RegistryLoadResult(
                [],
                [$"The managed runtime registry exceeds the {MaximumRegistryBytes}-byte limit."]);
        }

        RegistryDocument? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<RegistryDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            return new RegistryLoadResult([], [$"Invalid registry JSON: {exception.Message}"]);
        }

        if (document is null)
        {
            return new RegistryLoadResult([], ["The registry document is empty."]);
        }

        if (document.SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            return new RegistryLoadResult(
                [],
                [$"Unsupported registry schema version {document.SchemaVersion}."]);
        }

        if (document.Installations is { Count: > MaximumRegistryEntries })
        {
            return new RegistryLoadResult(
                [],
                [$"The managed runtime registry exceeds the {MaximumRegistryEntries}-entry limit."]);
        }

        List<ManagedRuntimeEntry> entries = [];
        List<string> errors = [];
        foreach (RegistryItem item in document.Installations ?? [])
        {
            if (!TryConvert(
                    item,
                    document.SchemaVersion,
                    out ManagedRuntimeEntry? entry,
                    out string? error))
            {
                errors.Add(error!);
                continue;
            }

            entries.Add(entry!);
        }

        HashSet<string> runtimeIds = new(StringComparer.OrdinalIgnoreCase);
        List<ManagedRuntimeEntry> preceding = [];
        foreach (ManagedRuntimeEntry candidate in entries)
        {
            if (!runtimeIds.Add(candidate.Id))
            {
                errors.Add(
                    "The managed runtime registry contains a duplicate runtime ID.");
                break;
            }

            bool hasPrecedenceConflict = preceding.Any(existing =>
                !existing.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase)
                && existing.Kind == candidate.Kind
                && existing.Architecture == candidate.Architecture
                && existing.ProviderId.Equals(
                    candidate.ProviderId,
                    StringComparison.OrdinalIgnoreCase)
                && existing.Version.CompareTo(candidate.Version) == 0);
            if (hasPrecedenceConflict)
            {
                errors.Add(
                    "The managed runtime registry contains equivalent versions from the same "
                    + "Provider, runtime kind, and architecture under different runtime IDs.");
                break;
            }

            preceding.Add(candidate);
        }

        return new RegistryLoadResult(entries, errors);
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<ManagedRuntimeEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count > MaximumRegistryEntries)
        {
            throw new InvalidDataException(
                $"The managed runtime registry cannot exceed {MaximumRegistryEntries} entries.");
        }

        string directory = Path.GetDirectoryName(_registryPath)!;
        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_registryPath)}.{Guid.NewGuid():N}.tmp");
        EnsureChildPath(_managedRoot, temporaryPath, "temporary registry file");
        _stateLock.EnsureTemporaryFilePathSafe(temporaryPath);

        RegistryDocument document = new(
            CurrentSchemaVersion,
            entries.Select(RegistryItem.FromEntry).ToList());
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                _stateLock.EnsureTemporaryFilePathSafe(temporaryPath);
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumRegistryBytes)
                {
                    throw new InvalidDataException(
                        $"The managed runtime registry cannot exceed {MaximumRegistryBytes} bytes.");
                }
            }

            _stateLock.EnsureStatePathSafe(createDirectory: false);
            File.Move(temporaryPath, _registryPath, overwrite: true);
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

    private bool TryConvert(
        RegistryItem item,
        int schemaVersion,
        out ManagedRuntimeEntry? entry,
        out string? error)
    {
        entry = null;
        error = null;
        string? packageHash;
        PackageHashAlgorithm packageHashAlgorithm;
        if (schemaVersion == 1)
        {
            packageHash = item.PackageSha256;
            packageHashAlgorithm = PackageHashAlgorithm.Sha256;
        }
        else if (item.PackageHashAlgorithm is PackageHashAlgorithm declaredAlgorithm)
        {
            packageHash = item.PackageHash;
            packageHashAlgorithm = declaredAlgorithm;
        }
        else
        {
            error = $"Registry entry '{item.Id ?? "<unknown>"}' is missing its package hash algorithm.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.ProviderId)
            || !Enum.TryParse(item.Kind, true, out RuntimeKind kind)
            || !RuntimeVersion.TryParse(item.Version, out RuntimeVersion? version)
            || !Enum.TryParse(item.Architecture, true, out RuntimeArchitecture architecture)
            || architecture == RuntimeArchitecture.Any
            || string.IsNullOrWhiteSpace(item.InstallRoot)
            || string.IsNullOrWhiteSpace(item.ExecutableRelativePath)
            || string.IsNullOrWhiteSpace(packageHash))
        {
            error = $"Registry entry '{item.Id ?? "<unknown>"}' has missing or invalid fields.";
            return false;
        }

        ManagedRuntimeEntry candidate = new(
            item.Id,
            item.ProviderId,
            kind,
            version!,
            architecture,
            Path.GetFullPath(item.InstallRoot),
            item.ExecutableRelativePath,
            packageHash,
            item.InstalledAtUtc,
            item.Channels ?? [],
            packageHashAlgorithm);
        try
        {
            ValidateEntry(candidate);
        }
        catch (ArgumentException exception)
        {
            error = $"Registry entry '{item.Id}' is unsafe: {exception.Message}";
            return false;
        }

        entry = candidate;
        return true;
    }

    private void ValidateEntry(ManagedRuntimeEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)
            || string.IsNullOrWhiteSpace(entry.ProviderId)
            || string.IsNullOrWhiteSpace(entry.ExecutableRelativePath))
        {
            throw new ArgumentException("Managed runtime identity fields cannot be empty.", nameof(entry));
        }

        if (!entry.PackageHashAlgorithm.IsValidHash(entry.PackageHash))
        {
            throw new ArgumentException(
                $"Managed runtime package {entry.PackageHashAlgorithm.DisplayName()} is invalid.",
                nameof(entry));
        }

        string installRoot = Path.GetFullPath(entry.InstallRoot);
        EnsureChildPath(_managedRoot, installRoot, "runtime install root");
        if (Path.IsPathRooted(entry.ExecutableRelativePath))
        {
            throw new ArgumentException("The runtime executable path must be relative.", nameof(entry));
        }

        string executablePath = Path.GetFullPath(
            Path.Combine(installRoot, entry.ExecutableRelativePath));
        EnsureChildPath(installRoot, executablePath, "runtime executable");
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string rootPrefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} must remain inside the managed root.");
        }
    }

    private sealed record RegistryDocument(
        int SchemaVersion,
        List<RegistryItem>? Installations);

    private sealed record RegistryItem(
        string? Id,
        string? ProviderId,
        string? Kind,
        string? Version,
        string? Architecture,
        string? InstallRoot,
        string? ExecutableRelativePath,
        string? PackageHash,
        PackageHashAlgorithm? PackageHashAlgorithm,
        string? PackageSha256,
        DateTimeOffset InstalledAtUtc,
        List<string>? Channels)
    {
        public static RegistryItem FromEntry(ManagedRuntimeEntry entry) => new(
            entry.Id,
            entry.ProviderId,
            entry.Kind.ToString(),
            entry.Version.ToString(),
            entry.Architecture.ToString(),
            entry.InstallRoot,
            entry.ExecutableRelativePath,
            entry.PackageHash,
            entry.PackageHashAlgorithm,
            null,
            entry.InstalledAtUtc,
            entry.Channels?.ToList());
    }
}
