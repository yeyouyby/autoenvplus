using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Overview;

public sealed class OverviewSnapshotStore
{
    public const long MaximumSnapshotBytes = 256 * 1024;

    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _snapshotPath;
    private readonly ManagedStateLock _stateLock;

    public OverviewSnapshotStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        _snapshotPath = Path.Combine(fullManagedRoot, "state", "overview-snapshot.json");
        _stateLock = new ManagedStateLock(
            fullManagedRoot,
            _snapshotPath,
            "overview-snapshot.lock");
    }

    public string SnapshotPath => _snapshotPath;

    public async Task<OverviewSnapshot?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (!File.Exists(_snapshotPath))
        {
            return null;
        }

        byte[] bytes;
        await using (FileStream stream = new(
            _snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            _stateLock.EnsureStatePathSafe(createDirectory: false);
            if (stream.Length is <= 0 or > MaximumSnapshotBytes)
            {
                throw new InvalidDataException(
                    $"The overview snapshot must be between 1 and {MaximumSnapshotBytes} bytes.");
            }

            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        ValidateNoDuplicateProperties(bytes);
        SnapshotDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SnapshotDocument>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The overview snapshot contains invalid JSON.", exception);
        }

        if (document is null
            || document.SchemaVersion != CurrentSchemaVersion
            || document.Snapshot is null)
        {
            throw new InvalidDataException("The overview snapshot schema is unsupported.");
        }

        document.Snapshot.Validate();
        return document.Snapshot;
    }

    public async Task<OverviewSnapshot> SaveAsync(
        OverviewSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new SnapshotDocument(CurrentSchemaVersion, snapshot),
            JsonOptions);
        if (bytes.Length > MaximumSnapshotBytes)
        {
            throw new InvalidDataException(
                $"The overview snapshot cannot exceed {MaximumSnapshotBytes} bytes.");
        }

        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        string directory = Path.GetDirectoryName(_snapshotPath)!;
        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_snapshotPath)}.{Guid.NewGuid():N}.tmp");
        _stateLock.EnsureTemporaryFilePathSafe(temporaryPath);
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
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _stateLock.EnsureStatePathSafe(createDirectory: false);
            File.Move(temporaryPath, _snapshotPath, overwrite: true);
            _stateLock.EnsureStatePathSafe(createDirectory: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return snapshot;
    }

    private static void ValidateNoDuplicateProperties(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            Visit(document.RootElement, "$");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The overview snapshot contains invalid JSON.", exception);
        }

        static void Visit(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            $"The overview snapshot contains duplicate property '{path}.{property.Name}'.");
                    }

                    Visit(property.Value, $"{path}.{property.Name}");
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Visit(item, $"{path}[{index++}]");
                }
            }
        }
    }

    private sealed record SnapshotDocument(
        int SchemaVersion,
        OverviewSnapshot Snapshot);
}
