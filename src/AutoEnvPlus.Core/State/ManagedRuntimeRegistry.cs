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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedRuntimeRegistry(string managedRoot, string? registryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _registryPath = Path.GetFullPath(
            registryPath ?? Path.Combine(_managedRoot, "state", "installations.json"));
        EnsureChildPath(_managedRoot, _registryPath, "registry file");
    }

    public string RegistryPath => _registryPath;

    public async Task<RegistryLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
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

    public async Task<RegistryLoadResult> UpsertAsync(
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RegistryLoadResult existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Errors.Count > 0)
            {
                throw new InvalidDataException(
                    "The managed runtime registry contains invalid entries: "
                    + string.Join("; ", existing.Errors));
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
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RegistryLoadResult> RemoveAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RegistryLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
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

        return new RegistryLoadResult(entries, errors);
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<ManagedRuntimeEntry> entries,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_registryPath)}.{Guid.NewGuid():N}.tmp");
        EnsureChildPath(_managedRoot, temporaryPath, "temporary registry file");

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
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _registryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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
