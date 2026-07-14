using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Projects;

public sealed record ProjectLockEntry(
    RuntimeKind Kind,
    string RequestedSelector,
    RuntimeVersion ResolvedVersion,
    RuntimeArchitecture Architecture,
    string ProviderId,
    string PackageSha256);

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
    public const int CurrentSchemaVersion = 1;
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
            RuntimeProfile projectProfile = new(new Dictionary<RuntimeKind, VersionSelector>
            {
                [kind] = selector,
            });
            RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
                kind,
                new RuntimeResolutionContext(Project: projectProfile),
                installedRuntimes.Select(entry => entry.ToRuntimeInstallation()),
                architecture);
            if (!resolution.Success)
            {
                errors.Add(resolution.Error ?? $"No installed {kind} runtime matches {selector}.");
                continue;
            }

            ManagedRuntimeEntry? installed = installedRuntimes.FirstOrDefault(entry =>
                entry.Id.Equals(resolution.Installation!.Id, StringComparison.OrdinalIgnoreCase));
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
                installed.PackageSha256.ToLowerInvariant()));
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
            ProjectLockDocument? document = await JsonSerializer.DeserializeAsync<ProjectLockDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                return new ProjectLockResult(
                    false,
                    fullPath,
                    document,
                    ["The project lock file has an unsupported schema."]);
            }

            return new ProjectLockResult(true, fullPath, document, []);
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
}
