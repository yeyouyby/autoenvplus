using System.Collections.Frozen;
using System.Text.Json;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Languages;

public sealed class LanguageVisibilityStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumLanguageIds = 256;
    public const int MaximumDocumentBytes = 64 * 1024;

    private readonly string _managedRoot;
    private readonly ManagedStateLock _stateLock;

    public LanguageVisibilityStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        StatePath = Path.Combine(_managedRoot, "state", "language-visibility.json");
        _stateLock = new ManagedStateLock(
            _managedRoot,
            StatePath,
            "language-visibility.lock");
    }

    public string StatePath { get; }

    public async Task<LanguageVisibilityState> LoadAsync(
        LanguageCatalog availableCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availableCatalog);
        try
        {
            using ManagedStateLock.Lease lease = await _stateLock.AcquireAsync(
                cancellationToken).ConfigureAwait(false);
            return await ReadCoreAsync(availableCatalog, cancellationToken).ConfigureAwait(false);
        }
        catch (LanguageVisibilityException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw Error(
                LanguageVisibilityErrorCode.UnsafePath,
                "The language visibility state path is not an ordinary managed path.",
                innerException: exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw Error(
                LanguageVisibilityErrorCode.IoFailure,
                "The language visibility state could not be read safely.",
                innerException: exception);
        }
    }

    public Task<LanguageVisibilityState> SetEnabledAsync(
        LanguageCatalog availableCatalog,
        string languageId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            availableCatalog,
            languageId,
            (enabledIds, hiddenIds, id) =>
            {
                if (enabled)
                {
                    enabledIds.Add(id);
                    hiddenIds.Remove(id);
                }
                else
                {
                    enabledIds.Remove(id);
                }
            },
            cancellationToken);

    public Task<LanguageVisibilityState> SetHiddenAsync(
        LanguageCatalog availableCatalog,
        string languageId,
        bool hidden,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            availableCatalog,
            languageId,
            (enabledIds, hiddenIds, id) =>
            {
                if (hidden)
                {
                    hiddenIds.Add(id);
                    enabledIds.Remove(id);
                }
                else
                {
                    hiddenIds.Remove(id);
                }
            },
            cancellationToken);

    public async Task<LanguageVisibilityState> ResetAsync(
        LanguageCatalog availableCatalog,
        string? languageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availableCatalog);
        if (languageId is not null)
        {
            return await MutateAsync(
                availableCatalog,
                languageId,
                static (enabled, hidden, id) =>
                {
                    enabled.Remove(id);
                    hidden.Remove(id);
                },
                cancellationToken).ConfigureAwait(false);
        }

        return await MutateStateAsync(
            availableCatalog,
            static (_, _) => { },
            resetAll: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LanguageVisibilityState> MutateAsync(
        LanguageCatalog catalog,
        string languageId,
        Action<HashSet<string>, HashSet<string>, string> mutation,
        CancellationToken cancellationToken)
    {
        string normalizedId = ResolveLanguageId(catalog, languageId);
        return await MutateStateAsync(
            catalog,
            (enabled, hidden) => mutation(enabled, hidden, normalizedId),
            resetAll: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LanguageVisibilityState> MutateStateAsync(
        LanguageCatalog catalog,
        Action<HashSet<string>, HashSet<string>> mutation,
        bool resetAll,
        CancellationToken cancellationToken)
    {
        try
        {
            using ManagedStateLock.Lease lease = await _stateLock.AcquireAsync(
                cancellationToken).ConfigureAwait(false);
            LanguageVisibilityState current = await ReadCoreAsync(catalog, cancellationToken)
                .ConfigureAwait(false);
            HashSet<string> enabled = resetAll
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(current.EnabledLanguageIds, StringComparer.OrdinalIgnoreCase);
            HashSet<string> hidden = resetAll
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(current.HiddenLanguageIds, StringComparer.OrdinalIgnoreCase);
            mutation(enabled, hidden);
            if (enabled.Overlaps(hidden))
            {
                throw Error(
                    LanguageVisibilityErrorCode.InvalidDocument,
                    "A language cannot be both explicitly enabled and hidden.");
            }

            LanguageVisibilityState updated = CreateState(enabled, hidden);
            await WriteCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (LanguageVisibilityException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw Error(
                LanguageVisibilityErrorCode.UnsafePath,
                "The language visibility state could not be updated through an ordinary managed path.",
                innerException: exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw Error(
                LanguageVisibilityErrorCode.IoFailure,
                "The language visibility state could not be updated safely.",
                innerException: exception);
        }
    }

    private async Task<LanguageVisibilityState> ReadCoreAsync(
        LanguageCatalog catalog,
        CancellationToken cancellationToken)
    {
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (!File.Exists(StatePath))
        {
            return LanguageVisibilityState.Empty;
        }

        FileInfo info = new(StatePath);
        if (info.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw Error(
                LanguageVisibilityErrorCode.DocumentTooLarge,
                "The language visibility state has an invalid size.");
        }

        byte[] bytes = new byte[checked((int)info.Length)];
        await using FileStream stream = new(
            StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != bytes.Length)
        {
            throw Error(
                LanguageVisibilityErrorCode.IoFailure,
                "The language visibility state changed while it was being read.");
        }

        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Parse(bytes, catalog);
    }

    private async Task WriteCoreAsync(
        LanguageVisibilityState state,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (MemoryStream stream = new())
        {
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
                WriteIds(writer, "enabledLanguageIds", state.EnabledLanguageIds);
                WriteIds(writer, "hiddenLanguageIds", state.HiddenLanguageIds);
                writer.WriteEndObject();
            }

            stream.WriteByte((byte)'\n');
            bytes = stream.ToArray();
        }

        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string temporary = StatePath + $".{Guid.NewGuid():N}.tmp";
        _stateLock.EnsureTemporaryFilePathSafe(temporary);
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
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, StatePath, overwrite: true);
            _stateLock.EnsureStatePathSafe(createDirectory: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static LanguageVisibilityState Parse(
        ReadOnlyMemory<byte> bytes,
        LanguageCatalog catalog)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 6,
                });
        }
        catch (JsonException exception)
        {
            throw Error(
                LanguageVisibilityErrorCode.MalformedJson,
                "The language visibility state is not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("$", "The language visibility state must be an object.");
            }

            string[] names = ["schemaVersion", "enabledLanguageIds", "hiddenLanguageIds"];
            HashSet<string> expected = names.ToHashSet(StringComparer.Ordinal);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !expected.Contains(property.Name))
                {
                    throw Invalid("$", "State properties must be known and unique ignoring case.");
                }
            }

            if (names.Any(name => !root.TryGetProperty(name, out _)))
            {
                throw Invalid("$", "A required state property is missing.");
            }

            JsonElement schema = root.GetProperty("schemaVersion");
            if (schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int version)
                || version != CurrentSchemaVersion)
            {
                throw Error(
                    LanguageVisibilityErrorCode.UnsupportedSchema,
                    "The language visibility state schema is not supported.",
                    "schemaVersion");
            }

            HashSet<string> enabled = ParseIds(
                root.GetProperty("enabledLanguageIds"),
                "enabledLanguageIds",
                catalog);
            HashSet<string> hidden = ParseIds(
                root.GetProperty("hiddenLanguageIds"),
                "hiddenLanguageIds",
                catalog);
            if (enabled.Overlaps(hidden))
            {
                throw Invalid("$", "A language cannot be both explicitly enabled and hidden.");
            }

            return CreateState(enabled, hidden);
        }
    }

    private static HashSet<string> ParseIds(
        JsonElement element,
        string field,
        LanguageCatalog catalog)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > MaximumLanguageIds)
        {
            throw Invalid(field, "The language ID list is invalid.");
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string? requested = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (requested is null)
            {
                throw Invalid($"{field}[{index}]", "A language ID must be a string.");
            }

            string id = ResolveLanguageId(catalog, requested);
            if (!ids.Add(id))
            {
                throw Invalid(
                    $"{field}[{index}]",
                    "Language IDs must be unique ignoring case.");
            }

            index++;
        }

        return ids;
    }

    private static string ResolveLanguageId(LanguageCatalog catalog, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)
            || !catalog.TryGetLanguage(requested.Trim(), out LanguageDefinition? language))
        {
            throw Error(
                LanguageVisibilityErrorCode.UnknownLanguage,
                "The visibility state refers to a language not available in the catalog.",
                "languageId");
        }

        return language!.Id;
    }

    private static LanguageVisibilityState CreateState(
        IEnumerable<string> enabled,
        IEnumerable<string> hidden) =>
        new(
            enabled.Order(StringComparer.Ordinal)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            hidden.Order(StringComparer.Ordinal)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    private static void WriteIds(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> ids)
    {
        writer.WriteStartArray(propertyName);
        foreach (string id in ids.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
        }
    }

    private static LanguageVisibilityException Invalid(string field, string message) =>
        Error(LanguageVisibilityErrorCode.InvalidDocument, message, field);

    private static LanguageVisibilityException Error(
        LanguageVisibilityErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null) =>
        new(code, message, field, innerException);
}
