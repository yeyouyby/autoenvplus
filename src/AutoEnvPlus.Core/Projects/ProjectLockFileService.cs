using System.Security.Cryptography;
using System.Text;
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
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumLockFileBytes = 1024 * 1024;
    public const int MaximumRuntimeEntries = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<ProjectLockManifestSnapshot, CancellationToken, Task>? _snapshotObserver;

    public ProjectLockFileService()
    {
    }

    internal ProjectLockFileService(
        Func<ProjectLockManifestSnapshot, CancellationToken, Task> snapshotObserver)
    {
        _snapshotObserver = snapshotObserver ?? throw new ArgumentNullException(nameof(snapshotObserver));
    }

    public async Task<ProjectLockResult> CreateAsync(
        string manifestPath,
        IReadOnlyList<ManagedRuntimeEntry> installedRuntimes,
        RuntimeArchitecture? architecture = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(installedRuntimes);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        RuntimeArchitecture effectiveArchitecture = architecture ?? ProjectRuntimeArchitecture.Current;
        if (!Enum.IsDefined(effectiveArchitecture)
            || effectiveArchitecture == RuntimeArchitecture.Any)
        {
            throw new ArgumentOutOfRangeException(
                nameof(architecture),
                architecture,
                "Project lock creation requires a concrete runtime architecture.");
        }

        ProjectLockManifestSnapshot snapshot = await ReadManifestSnapshotAsync(
            fullManifestPath,
            cancellationToken).ConfigureAwait(false);
        if (_snapshotObserver is not null)
        {
            await _snapshotObserver(snapshot, cancellationToken).ConfigureAwait(false);
        }

        ProjectManifestLoadResult manifest = snapshot.Manifest;
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
        RuntimeProfile projectProfile = manifest.Manifest.ToRuntimeProfile();
        foreach ((RuntimeKind kind, VersionSelector selector) in manifest.Manifest.Tools.OrderBy(pair => pair.Key))
        {
            RuntimeArchitecture resolutionArchitecture = SelectResolutionArchitecture(
                kind,
                manifest.Manifest,
                installedRuntimes,
                effectiveArchitecture);
            ManagedRuntimeResolutionResult resolution =
                ManagedRuntimeResolutionService.ResolveRegistered(
                kind,
                new RuntimeResolutionContext(Project: projectProfile),
                installedRuntimes,
                resolutionArchitecture);
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

        ProjectLockDocument document = new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            snapshot.Sha256,
            locked);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (serialized.Length > MaximumLockFileBytes)
        {
            return new ProjectLockResult(
                false,
                null,
                null,
                [$"The generated project lock file exceeds the {MaximumLockFileBytes}-byte limit."]);
        }

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
                await stream.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
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

    private static RuntimeArchitecture SelectResolutionArchitecture(
        RuntimeKind kind,
        ProjectEnvironmentManifest manifest,
        IReadOnlyList<ManagedRuntimeEntry> installedRuntimes,
        RuntimeArchitecture defaultArchitecture)
    {
        if (!manifest.ExactSelections.TryGetValue(
                kind,
                out RuntimeSelectionIdentity? identity))
        {
            return defaultArchitecture;
        }

        ManagedRuntimeEntry[] pinned = installedRuntimes
            .Where(entry => entry.Id.Equals(
                identity.RuntimeId,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (pinned.Length != 1
            || !Enum.IsDefined(pinned[0].Architecture)
            || pinned[0].Architecture == RuntimeArchitecture.Any)
        {
            // ResolveRegistered owns the detailed missing, duplicate, and invalid pin errors.
            return defaultArchitecture;
        }

        return pinned[0].Architecture;
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
            byte[] bytes = await ReadBoundedFileAsync(
                fullPath,
                MaximumLockFileBytes,
                "project lock file",
                cancellationToken).ConfigureAwait(false);
            LockDocumentDto? serialized = JsonSerializer.Deserialize<LockDocumentDto>(
                bytes,
                JsonOptions);
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
        catch (InvalidDataException exception)
        {
            return new ProjectLockResult(false, fullPath, null, [exception.Message]);
        }
    }

    public async Task<bool> IsCurrentAsync(
        ProjectLockDocument document,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ProjectLockManifestSnapshot snapshot = await ReadManifestSnapshotAsync(
            Path.GetFullPath(manifestPath),
            cancellationToken).ConfigureAwait(false);
        return document.ManifestSha256.Equals(
            snapshot.Sha256,
            StringComparison.OrdinalIgnoreCase);
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

        if (serialized.Runtimes.Count > MaximumRuntimeEntries)
        {
            validationErrors.Add(
                $"The project lock file contains {serialized.Runtimes.Count} runtime entries; at most {MaximumRuntimeEntries} are allowed.");
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

    internal static async Task<ProjectLockManifestSnapshot> ReadManifestSnapshotAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath = Path.GetFullPath(manifestPath);
        byte[] bytes = await ReadBoundedFileAsync(
            fullPath,
            MaximumManifestBytes,
            "project manifest",
            cancellationToken).ConfigureAwait(false);
        bool utf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        int textOffset = utf8Bom ? Encoding.UTF8.Preamble.Length : 0;
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes, textOffset, bytes.Length - textOffset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The project manifest must use valid UTF-8.", exception);
        }

        ProjectManifestLoadResult manifest = new ProjectManifestService().LoadContent(
            fullPath,
            content);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ProjectLockManifestSnapshot(fullPath, bytes, sha256, manifest);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {description} exceeds the {maximumBytes}-byte limit.");
        }

        byte[] buffer = new byte[checked((int)stream.Length)];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"The {description} changed while it was being read.");
            }

            total += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"The {description} changed while it was being read.");
        }

        return buffer;
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

internal sealed record ProjectLockManifestSnapshot(
    string FullPath,
    byte[] Bytes,
    string Sha256,
    ProjectManifestLoadResult Manifest);
