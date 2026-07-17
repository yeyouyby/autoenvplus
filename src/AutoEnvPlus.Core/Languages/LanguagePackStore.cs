using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Languages;

public sealed class LanguagePackStore
{
    public const int MaximumInstalledPacks = 128;
    public const int CurrentStateSchemaVersion = 1;

    private const int MaximumStateBytes = 64 * 1024;
    private const int LockTimeoutMilliseconds = 5_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _managedRoot;
    private readonly string _lockPath;
    private readonly string _packRootPrefix;
    private readonly SemaphoreSlim _gate;
    private readonly LanguageCatalog _builtInCatalog;

    public LanguagePackStore(string managedRoot, LanguageCatalog? builtInCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        PackRoot = Path.Combine(_managedRoot, "plugins", "language-packs");
        StatePath = Path.Combine(_managedRoot, "state", "language-packs.json");
        _lockPath = Path.Combine(_managedRoot, "state", "language-packs.lock");
        _packRootPrefix = PackRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _builtInCatalog = builtInCatalog ?? BuiltInLanguageCatalog.Current;
        _gate = Gates.GetOrAdd(_lockPath, static _ => new SemaphoreSlim(1, 1));
        ManagedPathSafety.EnsureNoReparsePointInPath(_managedRoot);
    }

    public string PackRoot { get; }

    public string StatePath { get; }

    public async Task<LanguagePackImportPreview> PreviewImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw Error(
                LanguagePackErrorCode.UnsafePath,
                "The language pack source path is invalid.",
                innerException: exception);
        }

        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(
                LanguagePackErrorCode.UnsafePath,
                "Only a local JSON language pack may be imported.");
        }

        try
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        }
        catch (IOException exception)
        {
            throw Error(
                LanguagePackErrorCode.UnsafePath,
                "The language pack source path crosses a reparse point.",
                innerException: exception);
        }

        try
        {
            FileInfo file = new(fullPath);
            if (!file.Exists
                || (file.Attributes & (FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0)
            {
                throw Error(
                    LanguagePackErrorCode.UnsafePath,
                    "The language pack source must be an ordinary file.");
            }

            if (file.Length is <= 0 or > LanguagePackManifestParser.MaximumManifestBytes)
            {
                throw Error(
                    LanguagePackErrorCode.ManifestTooLarge,
                    $"A language pack cannot exceed "
                    + $"{LanguagePackManifestParser.MaximumManifestBytes} bytes.");
            }

            byte[] bytes = new byte[checked((int)file.Length)];
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != bytes.Length)
            {
                throw Error(
                    LanguagePackErrorCode.IoFailure,
                    "The language pack changed while it was being read.");
            }

            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            LanguagePackManifest manifest = LanguagePackManifestParser.Parse(bytes);
            ValidatePackAgainstBase(manifest);
            byte[] normalized = LanguagePackManifestParser.SerializeNormalized(manifest);
            return new LanguagePackImportPreview(manifest, fullPath, normalized);
        }
        catch (LanguagePackException)
        {
            throw;
        }
        catch (Exception exception) when (IsIo(exception))
        {
            throw Error(
                LanguagePackErrorCode.IoFailure,
                "The language pack could not be read safely.",
                innerException: exception);
        }
    }

    public async Task<LanguagePackDescriptor> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        LanguagePackImportPreview preview = await PreviewImportAsync(
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        return await ImportAsync(preview, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LanguagePackDescriptor> ImportAsync(
        LanguagePackImportPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        byte[] normalized = preview.GetNormalizedManifest();
        LanguagePackManifest manifest = LanguagePackManifestParser.Parse(normalized);
        ValidatePackAgainstBase(manifest);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using FileStream operationLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            LanguagePackListResult existing = await ListCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            ThrowOnErrors(existing);
            if (existing.Packs.Count >= MaximumInstalledPacks)
            {
                throw Error(
                    LanguagePackErrorCode.InvalidState,
                    $"At most {MaximumInstalledPacks} language packs may be imported.");
            }

            if (existing.Packs.Any(pack => pack.Id.Equals(
                    manifest.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw Error(
                    LanguagePackErrorCode.DuplicatePack,
                    "A language pack with this ID is already imported.",
                    "id");
            }

            ValidateDefinitionConflicts(manifest, existing.Packs.Select(pack => pack.Manifest));
            PackState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.EnabledPackIds.Contains(manifest.Id))
            {
                throw Error(
                    LanguagePackErrorCode.InvalidState,
                    "The activation state refers to a missing language pack.");
            }

            string destination = GetManifestPath(manifest.Id);
            string temporary = Path.Combine(PackRoot, $".{manifest.Id}.{Guid.NewGuid():N}.tmp");
            EnsurePackChild(temporary);
            try
            {
                await WriteNewFileAsync(temporary, normalized, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: false);
                EnsureOrdinaryFile(destination);
            }
            catch (Exception exception) when (exception is not LanguagePackException && IsIo(exception))
            {
                throw Error(
                    LanguagePackErrorCode.IoFailure,
                    "The language pack could not be imported atomically.",
                    innerException: exception);
            }
            finally
            {
                TryDelete(temporary);
            }

            return new LanguagePackDescriptor(manifest, destination, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LanguagePackListResult> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using FileStream operationLock = await AcquireInterprocessLockAsync(
                    cancellationToken).ConfigureAwait(false);
                EnsureDirectories();
                return await ListCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (LanguagePackException exception)
            {
                return new LanguagePackListResult(
                    [],
                    [new LanguagePackLoadError(exception.Code, exception.Message)]);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                return new LanguagePackListResult(
                    [],
                    [new LanguagePackLoadError(
                        LanguagePackErrorCode.IoFailure,
                        "The language pack store could not be read safely.")]);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task EnableAsync(string packId, CancellationToken cancellationToken = default) =>
        SetEnabledAsync(packId, true, cancellationToken);

    public Task DisableAsync(string packId, CancellationToken cancellationToken = default) =>
        SetEnabledAsync(packId, false, cancellationToken);

    public async Task DeleteAsync(
        string packId,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = NormalizeId(packId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using FileStream operationLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            LanguagePackListResult existing = await ListCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            ThrowOnErrors(existing);
            LanguagePackDescriptor descriptor = existing.Packs.FirstOrDefault(pack =>
                pack.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
                ?? throw Error(LanguagePackErrorCode.PackNotFound, "The language pack is not imported.");
            PackState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            string quarantine = Path.Combine(PackRoot, $".{normalizedId}.{Guid.NewGuid():N}.delete");
            EnsurePackChild(quarantine);
            File.Move(descriptor.ManifestPath, quarantine, overwrite: false);
            try
            {
                state.EnabledPackIds.Remove(normalizedId);
                await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
                File.Delete(quarantine);
            }
            catch
            {
                if (File.Exists(quarantine) && !File.Exists(descriptor.ManifestPath))
                {
                    File.Move(quarantine, descriptor.ManifestPath, overwrite: false);
                }

                throw;
            }
        }
        catch (LanguagePackException)
        {
            throw;
        }
        catch (Exception exception) when (IsIo(exception))
        {
            throw Error(
                LanguagePackErrorCode.IoFailure,
                "The language pack could not be deleted safely.",
                innerException: exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LanguageCatalog> GetEffectiveCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        LanguagePackListResult result = await ListAsync(cancellationToken).ConfigureAwait(false);
        ThrowOnErrors(result);
        LanguagePackManifest[] enabled = result.Packs.Where(pack => pack.IsEnabled)
            .Select(pack => pack.Manifest)
            .ToArray();
        IEnumerable<LanguageDefinition> languages = _builtInCatalog.Languages.Concat(
            enabled.SelectMany(pack => pack.Languages));
        IEnumerable<LanguageToolDefinition> tools = _builtInCatalog.Tools.Concat(
            enabled.SelectMany(pack => pack.Tools));
        return new LanguageCatalog(languages, tools);
    }

    public async Task<LanguageCatalog> GetAvailableCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        LanguagePackListResult result = await ListAsync(cancellationToken).ConfigureAwait(false);
        ThrowOnErrors(result);
        IEnumerable<LanguageDefinition> languages = _builtInCatalog.Languages.Concat(
            result.Packs.SelectMany(pack => pack.Manifest.Languages));
        IEnumerable<LanguageToolDefinition> tools = _builtInCatalog.Tools.Concat(
            result.Packs.SelectMany(pack => pack.Manifest.Tools));
        return new LanguageCatalog(languages, tools);
    }

    private async Task SetEnabledAsync(
        string packId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        string normalizedId = NormalizeId(packId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using FileStream operationLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            LanguagePackListResult existing = await ListCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            ThrowOnErrors(existing);
            if (!existing.Packs.Any(pack => pack.Id.Equals(
                    normalizedId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw Error(LanguagePackErrorCode.PackNotFound, "The language pack is not imported.");
            }

            PackState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            bool changed = enabled
                ? state.EnabledPackIds.Add(normalizedId)
                : state.EnabledPackIds.Remove(normalizedId);
            if (changed)
            {
                await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LanguagePackListResult> ListCoreAsync(CancellationToken cancellationToken)
    {
        PackState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        List<LanguagePackDescriptor> packs = [];
        List<LanguagePackLoadError> errors = [];
        foreach (string path in Directory.EnumerateFiles(PackRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            string expectedId = Path.GetFileNameWithoutExtension(path);
            bool isEnabled = state.EnabledPackIds.Contains(expectedId);
            try
            {
                EnsurePackChild(path);
                EnsureOrdinaryFile(path);
                FileInfo info = new(path);
                if (info.Length is <= 0 or > LanguagePackManifestParser.MaximumManifestBytes)
                {
                    throw Error(LanguagePackErrorCode.ManifestTooLarge, "An imported language pack has an invalid size.");
                }

                byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                LanguagePackManifest manifest = LanguagePackManifestParser.Parse(bytes);
                if (!manifest.Id.Equals(expectedId, StringComparison.Ordinal))
                {
                    throw Error(LanguagePackErrorCode.InvalidManifest, "A language pack ID does not match its file name.");
                }

                ValidatePackAgainstBase(manifest);
                packs.Add(new LanguagePackDescriptor(manifest, path, isEnabled));
            }
            catch (LanguagePackException exception)
            {
                errors.Add(new LanguagePackLoadError(
                    exception.Code,
                    "An imported language pack is invalid.",
                    expectedId,
                    isEnabled));
            }
            catch (Exception exception) when (IsIo(exception))
            {
                errors.Add(new LanguagePackLoadError(
                    LanguagePackErrorCode.IoFailure,
                    "An imported language pack could not be read safely.",
                    expectedId,
                    isEnabled));
            }
        }

        foreach (string enabledId in state.EnabledPackIds)
        {
            if (!packs.Any(pack => pack.Id.Equals(enabledId, StringComparison.OrdinalIgnoreCase))
                && !errors.Any(error => error.PackId?.Equals(
                    enabledId,
                    StringComparison.OrdinalIgnoreCase) == true))
            {
                errors.Add(new LanguagePackLoadError(
                    LanguagePackErrorCode.InvalidState,
                    "The activation state refers to a missing language pack.",
                    enabledId,
                    true));
            }
        }

        return new LanguagePackListResult(
            packs.OrderBy(pack => pack.Id, StringComparer.Ordinal).ToArray(),
            errors);
    }

    private void ValidatePackAgainstBase(LanguagePackManifest manifest)
    {
        HashSet<string> availableLanguages = _builtInCatalog.Languages
            .Select(language => language.Id)
            .Concat(manifest.Languages.Select(language => language.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageToolDefinition tool in manifest.Tools)
        {
            if (tool.LanguageIds.Any(languageId => !availableLanguages.Contains(languageId)))
            {
                throw Error(
                    LanguagePackErrorCode.CatalogConflict,
                    "A language pack tool refers to a language not present in the built-in catalog or the same pack.",
                    "tools.languageIds");
            }
        }

        if (manifest.Languages.Any(language => _builtInCatalog.TryGetLanguage(language.Id, out _))
            || manifest.Tools.Any(tool => _builtInCatalog.TryGetTool(tool.Id, out _)))
        {
            throw Error(
                LanguagePackErrorCode.CatalogConflict,
                "A language pack cannot replace a built-in language or language tool.");
        }
    }

    private static void ValidateDefinitionConflicts(
        LanguagePackManifest candidate,
        IEnumerable<LanguagePackManifest> existing)
    {
        HashSet<string> languageIds = existing.SelectMany(pack => pack.Languages)
            .Select(language => language.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> toolIds = existing.SelectMany(pack => pack.Tools)
            .Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidate.Languages.Any(language => !languageIds.Add(language.Id))
            || candidate.Tools.Any(tool => !toolIds.Add(tool.Id)))
        {
            throw Error(
                LanguagePackErrorCode.CatalogConflict,
                "Language and tool IDs must be unique across all imported packs.");
        }
    }

    private void EnsureDirectories()
    {
        ManagedPathSafety.CreateOrdinaryDirectoryPath(_managedRoot, PackRoot, "language pack root");
        string stateRoot = Path.GetDirectoryName(StatePath)!;
        ManagedPathSafety.CreateOrdinaryDirectoryPath(_managedRoot, stateRoot, "language pack state root");
        ManagedPathSafety.EnsureOrdinaryDirectoryTree(
            _managedRoot,
            PackRoot,
            "language pack root",
            allowMissing: false);
    }

    private async Task<PackState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return new PackState();
        }

        ManagedPathSafety.EnsureOrdinaryFile(_managedRoot, StatePath, "language pack state");
        FileInfo info = new(StatePath);
        if (info.Length is <= 0 or > MaximumStateBytes)
        {
            throw Error(LanguagePackErrorCode.InvalidState, "The language pack state has an invalid size.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = LanguageManifestJson.ParseDocument(bytes, MaximumStateBytes);
        JsonElement root = document.RootElement;
        LanguageManifestJson.EnsureObject(
            root,
            "$",
            ["schemaVersion", "enabledPackIds"],
            ["schemaVersion", "enabledPackIds"]);
        JsonElement version = LanguageManifestJson.GetRequired(root, "schemaVersion", "schemaVersion");
        if (version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion)
            || schemaVersion != CurrentStateSchemaVersion)
        {
            throw Error(LanguagePackErrorCode.InvalidState, "The language pack state schema is invalid.");
        }

        JsonElement enabled = LanguageManifestJson.GetRequired(root, "enabledPackIds", "enabledPackIds");
        if (enabled.ValueKind != JsonValueKind.Array
            || enabled.GetArrayLength() > MaximumInstalledPacks)
        {
            throw Error(LanguagePackErrorCode.InvalidState, "The enabled language pack list is invalid.");
        }

        PackState state = new();
        foreach (JsonElement item in enabled.EnumerateArray())
        {
            string? id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (id is null
                || !id.Equals(id.ToLowerInvariant(), StringComparison.Ordinal)
                || !state.EnabledPackIds.Add(NormalizeId(id)))
            {
                throw Error(LanguagePackErrorCode.InvalidState, "The enabled language pack list is invalid.");
            }
        }

        return state;
    }

    private async Task WriteStateAsync(PackState state, CancellationToken cancellationToken)
    {
        string temporary = StatePath + $".{Guid.NewGuid():N}.tmp";
        byte[] bytes;
        using (MemoryStream stream = new())
        {
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentStateSchemaVersion);
                writer.WriteStartArray("enabledPackIds");
                foreach (string id in state.EnabledPackIds.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            stream.WriteByte((byte)'\n');
            bytes = stream.ToArray();
        }

        try
        {
            await WriteNewFileAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, StatePath, overwrite: true);
            ManagedPathSafety.EnsureOrdinaryFile(_managedRoot, StatePath, "language pack state");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<FileStream> AcquireInterprocessLockAsync(CancellationToken cancellationToken)
    {
        string stateRoot = Path.GetDirectoryName(_lockPath)!;
        ManagedPathSafety.CreateOrdinaryDirectoryPath(_managedRoot, stateRoot, "language pack state root");
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(LockTimeoutMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ManagedPathSafety.EnsureNoReparsePointInPath(_lockPath);
            }
            catch (IOException exception)
            {
                throw Error(
                    LanguagePackErrorCode.UnsafePath,
                    "The language pack lock path crosses a reparse point.",
                    innerException: exception);
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
                ManagedPathSafety.EnsureNoReparsePointInPath(_lockPath);
                FileAttributes attributes = File.GetAttributes(_lockPath);
                if ((attributes & (FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException("The language pack lock is not an ordinary file.");
                }

                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                stream?.Dispose();
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                stream?.Dispose();
                throw Error(
                    LanguagePackErrorCode.IoFailure,
                    "The language pack store is busy in another AutoEnvPlus process.",
                    innerException: exception);
            }
        }
    }

    private string GetManifestPath(string packId)
    {
        string path = Path.Combine(PackRoot, packId + ".json");
        EnsurePackChild(path);
        return path;
    }

    private void EnsurePackChild(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_packRootPrefix, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(fullPath)?.Equals(
                PackRoot,
                StringComparison.OrdinalIgnoreCase) != true)
        {
            throw Error(LanguagePackErrorCode.UnsafePath, "A language pack path escaped its store.");
        }

        ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
    }

    private void EnsureOrdinaryFile(string path)
    {
        EnsurePackChild(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw Error(LanguagePackErrorCode.UnsafePath, "A language pack must be an ordinary file.");
        }
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeId(string packId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        string normalized = packId.Trim();
        using JsonDocument document = JsonDocument.Parse(
            $"{{\"id\":{JsonSerializer.Serialize(normalized)}}}");
        string id = LanguageManifestJson.GetId(document.RootElement, "id", "id");
        return id;
    }

    private static void ThrowOnErrors(LanguagePackListResult result)
    {
        LanguagePackLoadError? error = result.Errors.FirstOrDefault();
        if (error is not null)
        {
            throw Error(error.Code, error.Message);
        }
    }

    private static bool IsIo(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or System.Security.SecurityException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsIo(exception))
        {
        }
    }

    private static LanguagePackException Error(
        LanguagePackErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null) =>
        new(code, message, field, innerException);

    private sealed class PackState
    {
        public HashSet<string> EnabledPackIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
