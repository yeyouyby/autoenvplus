using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Languages;

public sealed class LanguageToolInventoryStore
{
    public const int CurrentSchemaVersion = 1;
    public const long MaximumSnapshotBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _snapshotPath;
    private readonly ManagedStateLock _stateLock;

    public LanguageToolInventoryStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        _snapshotPath = Path.Combine(fullManagedRoot, "state", "language-tool-inventory.json");
        _stateLock = new ManagedStateLock(
            fullManagedRoot,
            _snapshotPath,
            "language-tool-inventory.lock");
    }

    public string SnapshotPath => _snapshotPath;

    public async Task<LanguageToolInventorySnapshot?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease lease = await _stateLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (!File.Exists(_snapshotPath))
        {
            return null;
        }

        FileInfo info = new(_snapshotPath);
        if (info.Length is <= 0 or > MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The language tool inventory has an invalid size.");
        }

        byte[] bytes = new byte[checked((int)info.Length)];
        await using (FileStream stream = new(
            _snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length != bytes.Length)
            {
                throw new InvalidDataException("The language tool inventory changed while being read.");
            }

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
            throw new InvalidDataException("The language tool inventory contains invalid JSON.", exception);
        }

        if (document is null
            || document.SchemaVersion != CurrentSchemaVersion
            || document.Snapshot is null)
        {
            throw new InvalidDataException("The language tool inventory schema is unsupported.");
        }

        document.Snapshot.Validate();
        return document.Snapshot;
    }

    public async Task<LanguageToolInventorySnapshot> SaveAsync(
        LanguageToolInventorySnapshot snapshot,
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
                $"The language tool inventory cannot exceed {MaximumSnapshotBytes} bytes.");
        }

        using ManagedStateLock.Lease lease = await _stateLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(_snapshotPath)!,
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
                    MaxDepth = 16,
                });
            Visit(document.RootElement, "$");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The language tool inventory contains invalid JSON.", exception);
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
                            $"The language tool inventory contains duplicate property '{path}.{property.Name}'.");
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
        LanguageToolInventorySnapshot Snapshot);
}

public sealed class LanguageToolInventoryScanner
{
    public async Task<LanguageToolInventorySnapshot> ScanPathAsync(
        LanguageCatalog catalog,
        IEnumerable<string>? toolIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string>? selectedIds = toolIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds is not null
            && selectedIds.Any(id => !catalog.TryGetTool(id, out _)))
        {
            throw new ArgumentException(
                "The language tool inventory scan contains an unknown tool ID.",
                nameof(toolIds));
        }

        LanguageToolDefinition[] selectedTools = catalog.Tools
            .Where(tool => selectedIds is null || selectedIds.Contains(tool.Id))
            .ToArray();
        string[] commands = selectedTools
            .Where(tool => tool.Capabilities.Discover)
            .SelectMany(tool => tool.DiscoveryCommands)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PathInspectionReport report = await Task.Run(
            () => new PathInspector().InspectCurrentAndPersisted(commands),
            cancellationToken).ConfigureAwait(false);
        HashSet<string> detected = report.CommandResolutions
            .Where(resolution => resolution.Winner is not null)
            .Select(resolution => resolution.Command)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
        LanguageToolInventoryEntry[] tools = selectedTools
            .Where(tool => tool.Capabilities.Discover && tool.DiscoveryCommands.Count > 0)
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .Select(tool => new LanguageToolInventoryEntry(
                tool.Id,
                capturedAtUtc,
                tool.DiscoveryCommands
                    .Where(detected.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
        LanguageToolInventorySnapshot snapshot = new(
            capturedAtUtc,
            LanguageToolInventorySnapshot.ComputeCatalogFingerprint(catalog),
            tools);
        snapshot.Validate();
        return snapshot;
    }
}
