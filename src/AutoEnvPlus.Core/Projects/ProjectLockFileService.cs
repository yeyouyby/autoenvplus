using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Projects;

public sealed record ProjectLockEntry(
    RuntimeKind Kind,
    string RequestedSelector,
    RuntimeVersion ResolvedVersion,
    RuntimeArchitecture Architecture,
    string ProviderId,
    PackageHashAlgorithm PackageHashAlgorithm,
    string PackageHash);

public sealed record ProjectLockDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string ManifestSha256,
    IReadOnlyList<ProjectLockEntry> Runtimes);

public sealed record ProjectLockResult(
    bool Success,
    string? LockPath,
    ProjectLockDocument? Document,
    IReadOnlyList<string> Errors);

public sealed class ProjectLockFileService
{
    public const int CurrentSchemaVersion = 2;
    public const string LockFileName = "autoenvplus.lock";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ProjectLockResult> CreateAsync(
        string manifestPath,
        IReadOnlyList<ManagedRuntimeEntry> installedRuntimes,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(installedRuntimes);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        ProjectManifestLoadResult manifest = new ProjectManifestService().Load(fullManifestPath);
        if (!manifest.Success)
        {
            return new ProjectLockResult(
                false,
                null,
                null,
                manifest.Errors.Select(error => $"line {error.LineNumber}: {error.Message}").ToArray());
        }

        List<ProjectLockEntry> locked = [];
        List<string> errors = [];
        foreach ((RuntimeKind kind, VersionSelector selector) in manifest.Manifest.Tools.OrderBy(pair => pair.Key))
        {
            RuntimeProfile projectProfile = manifest.Manifest.ToRuntimeProfile();
            ManagedRuntimeResolutionResult resolution =
                ManagedRuntimeResolutionService.ResolveRegistered(
                kind,
                new RuntimeResolutionContext(Project: projectProfile),
                installedRuntimes,
                architecture);
            if (!resolution.Success)
            {
                errors.AddRange(resolution.Errors);
                continue;
            }

            ManagedRuntimeEntry? installed = resolution.Entry;
            if (installed is null || !File.Exists(installed.ExecutablePath))
            {
                errors.Add($"The resolved {kind} runtime is missing from disk.");
                continue;
            }

            locked.Add(new ProjectLockEntry(
                kind,
                selector.ToString(),
                installed.Version,
                installed.Architecture,
                installed.ProviderId,
                installed.PackageHashAlgorithm,
                installed.PackageHash.ToLowerInvariant()));
        }

        if (errors.Count > 0)
        {
            return new ProjectLockResult(false, null, null, errors);
        }

        string manifestHash = await ComputeSha256Async(
            fullManifestPath,
            cancellationToken).ConfigureAwait(false);
        ProjectLockDocument document = new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            manifestHash,
            locked);
        string lockPath = Path.Combine(manifest.Manifest.ProjectRoot, LockFileName);
        string temporary = lockPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporary,
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

            File.Move(temporary, lockPath, overwrite: true);
            return new ProjectLockResult(true, lockPath, document, []);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<ProjectLockResult> LoadAsync(
        string lockPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        string fullPath = Path.GetFullPath(lockPath);
        if (!File.Exists(fullPath))
        {
            return new ProjectLockResult(false, fullPath, null, ["The project lock file does not exist."]);
        }

        try
        {
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            LockDocumentDto? serialized = await JsonSerializer.DeserializeAsync<LockDocumentDto>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (serialized is null)
            {
                return new ProjectLockResult(
                    false,
                    fullPath,
                    null,
                    ["The project lock file is empty."]);
            }

            if (serialized.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                return new ProjectLockResult(
                    false,
                    fullPath,
                    null,
                    [$"The project lock file has unsupported schema {serialized.SchemaVersion}."]);
            }

            ProjectLockDocument? document = ValidateAndConvert(serialized, out string[] errors);
            return errors.Length == 0
                ? new ProjectLockResult(true, fullPath, document, [])
                : new ProjectLockResult(false, fullPath, null, errors);
        }
        catch (JsonException exception)
        {
            return new ProjectLockResult(
                false,
                fullPath,
                null,
                [$"Invalid lock JSON: {exception.Message}"]);
        }
    }

    public async Task<bool> IsCurrentAsync(
        ProjectLockDocument document,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string hash = await ComputeSha256Async(
            Path.GetFullPath(manifestPath),
            cancellationToken).ConfigureAwait(false);
        return document.ManifestSha256.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectLockDocument? ValidateAndConvert(
        LockDocumentDto serialized,
        out string[] errors)
    {
        List<string> validationErrors = [];
        if (serialized.GeneratedAtUtc == default)
        {
            validationErrors.Add("The project lock file has an invalid generation timestamp.");
        }

        if (!PackageHashAlgorithm.Sha256.IsValidHash(serialized.ManifestSha256))
        {
            validationErrors.Add("The project lock manifest SHA-256 is invalid.");
        }

        if (serialized.Runtimes is null)
        {
            validationErrors.Add("The project lock file does not contain a runtimes array.");
            errors = validationErrors.ToArray();
            return null;
        }

        List<ProjectLockEntry> entries = [];
        HashSet<RuntimeKind> kinds = [];
        for (int index = 0; index < serialized.Runtimes.Count; index++)
        {
            LockEntryDto? item = serialized.Runtimes[index];
            string label = $"Project lock runtime entry {index + 1}";
            if (item is null)
            {
                validationErrors.Add($"{label} is null.");
                continue;
            }

            if (item.Kind is not RuntimeKind kind || !Enum.IsDefined(kind))
            {
                validationErrors.Add($"{label} has an invalid runtime kind.");
                continue;
            }

            if (!kinds.Add(kind))
            {
                validationErrors.Add($"{label} duplicates runtime kind {kind}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.RequestedSelector)
                || !VersionSelector.TryParse(item.RequestedSelector, out _))
            {
                validationErrors.Add($"{label} has an invalid requested selector.");
                continue;
            }

            if (item.ResolvedVersion is null
                || !RuntimeVersion.TryParse(
                    item.ResolvedVersion.ToString(),
                    out RuntimeVersion? normalizedVersion)
                || normalizedVersion != item.ResolvedVersion)
            {
                validationErrors.Add($"{label} has an invalid resolved version.");
                continue;
            }

            if (item.Architecture is not RuntimeArchitecture architecture
                || !Enum.IsDefined(architecture)
                || architecture == RuntimeArchitecture.Any)
            {
                validationErrors.Add($"{label} has an invalid architecture.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ProviderId))
            {
                validationErrors.Add($"{label} has an empty provider ID.");
                continue;
            }

            PackageHashAlgorithm hashAlgorithm;
            string? packageHash;
            if (serialized.SchemaVersion == 1)
            {
                hashAlgorithm = PackageHashAlgorithm.Sha256;
                packageHash = item.PackageSha256;
            }
            else if (item.PackageHashAlgorithm is PackageHashAlgorithm declaredAlgorithm
                && Enum.IsDefined(declaredAlgorithm))
            {
                hashAlgorithm = declaredAlgorithm;
                packageHash = item.PackageHash;
            }
            else
            {
                validationErrors.Add($"{label} has an invalid package hash algorithm.");
                continue;
            }

            if (!hashAlgorithm.IsValidHash(packageHash))
            {
                validationErrors.Add(
                    $"{label} has an invalid {hashAlgorithm.DisplayName()} package hash.");
                continue;
            }

            entries.Add(new ProjectLockEntry(
                kind,
                item.RequestedSelector,
                item.ResolvedVersion,
                architecture,
                item.ProviderId,
                hashAlgorithm,
                packageHash!.ToLowerInvariant()));
        }

        errors = validationErrors.ToArray();
        return errors.Length == 0
            ? new ProjectLockDocument(
                CurrentSchemaVersion,
                serialized.GeneratedAtUtc,
                serialized.ManifestSha256!.ToLowerInvariant(),
                entries)
            : null;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record LockDocumentDto(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string? ManifestSha256,
        List<LockEntryDto?>? Runtimes);

    private sealed record LockEntryDto(
        RuntimeKind? Kind,
        string? RequestedSelector,
        RuntimeVersion? ResolvedVersion,
        RuntimeArchitecture? Architecture,
        string? ProviderId,
        PackageHashAlgorithm? PackageHashAlgorithm,
        string? PackageHash,
        string? PackageSha256);
}
