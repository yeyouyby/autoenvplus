using System.Collections.Concurrent;
using System.Security;
using System.Text;
using System.Text.Json;

namespace AutoEnvPlus.Core.Plugins;

public sealed class RuntimeProviderPluginStore
{
    public const int CurrentStateSchemaVersion = 1;
    public const int MaximumInstalledPlugins = 256;

    private const int MaximumStateBytes = 64 * 1024;
    private const int LockRetryMilliseconds = 25;
    private const int LockTimeoutMilliseconds = 5_000;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _managedRoot;
    private readonly string _lockPath;
    private readonly string _pluginRootPrefix;
    private readonly SemaphoreSlim _gate;

    public RuntimeProviderPluginStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        PluginRoot = Path.Combine(_managedRoot, "plugins", "runtime-providers");
        StatePath = Path.Combine(_managedRoot, "state", "runtime-provider-plugins.json");
        _lockPath = Path.Combine(_managedRoot, "state", "runtime-provider-plugins.lock");
        EnsureChildPath(_managedRoot, PluginRoot, "runtime provider plugin root");
        EnsureChildPath(_managedRoot, StatePath, "runtime provider plugin state");
        EnsureChildPath(_managedRoot, _lockPath, "runtime provider plugin lock");
        _pluginRootPrefix = PluginRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _gate = Gates.GetOrAdd(_lockPath, static _ => new SemaphoreSlim(1, 1));
        EnsureNoReparsePointInPath(_managedRoot);
    }

    public string PluginRoot { get; }

    public string StatePath { get; }

    public async Task<RuntimeProviderPluginImportPreview> PreviewImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string fullSourcePath;
        try
        {
            fullSourcePath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.UnsafePath,
                "The plugin import source path is invalid.",
                innerException: exception);
        }

        if (!Path.GetExtension(fullSourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.UnsafePath,
                "Only a local JSON manifest can be imported as a runtime provider plugin.");
        }

        try
        {
            EnsureNoReparsePointInPath(fullSourcePath);
            FileInfo source = new(fullSourcePath);
            if (!source.Exists
                || (source.Attributes & (FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.UnsafePath,
                    "The plugin import source must be an existing ordinary JSON file.");
            }

            if (source.Length is <= 0 or > RuntimeProviderPluginManifestParser.MaximumManifestBytes)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.ManifestTooLarge,
                    $"A runtime provider plugin manifest must be no larger than "
                    + $"{RuntimeProviderPluginManifestParser.MaximumManifestBytes} bytes.");
            }

            byte[] sourceBytes = new byte[checked((int)source.Length)];
            await using FileStream stream = new(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != sourceBytes.Length)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.IoFailure,
                    "The plugin import source changed while it was being read.");
            }

            await stream.ReadExactlyAsync(sourceBytes, cancellationToken).ConfigureAwait(false);
            RuntimeProviderPluginManifest manifest =
                RuntimeProviderPluginManifestParser.Parse(sourceBytes);
            byte[] normalized = RuntimeProviderPluginManifestParser.SerializeNormalized(manifest);
            return new RuntimeProviderPluginImportPreview(
                manifest,
                fullSourcePath,
                normalized);
        }
        catch (RuntimeProviderPluginException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.IoFailure,
                "The plugin import source could not be read safely.",
                innerException: exception);
        }
    }

    public async Task<RuntimeProviderPluginDescriptor> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        RuntimeProviderPluginImportPreview preview = await PreviewImportAsync(
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        return await ImportAsync(preview, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeProviderPluginDescriptor> ImportAsync(
        RuntimeProviderPluginImportPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        byte[] normalizedBytes = preview.GetNormalizedManifest();
        RuntimeProviderPluginManifest manifest =
            RuntimeProviderPluginManifestParser.Parse(normalizedBytes);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using InterprocessLock operationLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureOperationalDirectories();
            RuntimeProviderPluginListResult existing = await ListCoreAsync(
                cancellationToken).ConfigureAwait(false);
            RuntimeProviderPluginError? globalError = existing.Errors.FirstOrDefault(error =>
                error.PluginId is null);
            if (globalError is not null)
            {
                throw PluginError(globalError.Code, globalError.Message);
            }

            if (existing.Plugins.Any(plugin => plugin.Id.Equals(
                    manifest.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.DuplicatePlugin,
                    "A runtime provider plugin with this ID is already imported.",
                    "id");
            }

            if (existing.Plugins.Count >= MaximumInstalledPlugins)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.InvalidState,
                    $"At most {MaximumInstalledPlugins} runtime provider plugins may be imported.");
            }

            PluginState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.EnabledPluginIds.Contains(manifest.Id))
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.InvalidState,
                    "The plugin activation state refers to a missing plugin.");
            }

            string destination = GetManifestPath(manifest.Id);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.DuplicatePlugin,
                    "A runtime provider plugin with this ID is already imported.",
                    "id");
            }

            string temporaryPath = Path.Combine(
                PluginRoot,
                $".{manifest.Id}.{Guid.NewGuid():N}.tmp");
            EnsureDirectPluginChild(temporaryPath, "temporary plugin manifest");
            try
            {
                await WriteNewFileAsync(
                    temporaryPath,
                    normalizedBytes,
                    cancellationToken).ConfigureAwait(false);
                EnsureSafePluginRoot();
                File.Move(temporaryPath, destination, overwrite: false);
                EnsureRegularFile(destination, "imported plugin manifest");
            }
            catch (RuntimeProviderPluginException)
            {
                throw;
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.IoFailure,
                    "The runtime provider plugin could not be imported atomically.",
                    innerException: exception);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }

            return new RuntimeProviderPluginDescriptor(manifest, destination, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeProviderPluginListResult> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using InterprocessLock operationLock = await AcquireInterprocessLockAsync(
                    cancellationToken).ConfigureAwait(false);
                EnsureOperationalDirectories();
                return await ListCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RuntimeProviderPluginException exception)
            {
                return new RuntimeProviderPluginListResult(
                    [],
                    [ToSafeError(exception)]);
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                return new RuntimeProviderPluginListResult(
                    [],
                    [
                        new RuntimeProviderPluginError(
                            RuntimeProviderPluginErrorCode.IoFailure,
                            "The runtime provider plugin store could not be read."),
                    ]);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<RuntimeProviderPluginDescriptor> EnableAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(pluginId, enabled: true, cancellationToken);

    public Task<RuntimeProviderPluginDescriptor> DisableAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(pluginId, enabled: false, cancellationToken);

    public async Task<RuntimeProviderPluginDeleteResult> DeleteAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = NormalizeRequestedId(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using InterprocessLock operationLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureOperationalDirectories();
            PluginState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            bool wasEnabled = state.EnabledPluginIds.Contains(normalizedId);
            string manifestPath = GetManifestPath(normalizedId);
            if (!File.Exists(manifestPath))
            {
                if (Directory.Exists(manifestPath))
                {
                    throw PluginError(
                        RuntimeProviderPluginErrorCode.UnsafePath,
                        "The requested plugin manifest path is not an ordinary file.");
                }

                if (!wasEnabled)
                {
                    throw PluginError(
                        RuntimeProviderPluginErrorCode.PluginNotFound,
                        "The requested runtime provider plugin is not installed.",
                        "id");
                }

                HashSet<string> repairedEnabled = new(
                    state.EnabledPluginIds,
                    StringComparer.OrdinalIgnoreCase);
                repairedEnabled.Remove(normalizedId);
                await SaveStateAsync(
                    new PluginState(repairedEnabled),
                    cancellationToken).ConfigureAwait(false);
                return new RuntimeProviderPluginDeleteResult(
                    normalizedId,
                    RuntimeProviderPluginDeleteOutcome.Deleted,
                    WasEnabled: true,
                    QuarantinePath: null);
            }

            string quarantineDirectory = Path.Combine(
                PluginRoot,
                ".quarantine",
                Guid.NewGuid().ToString("N"));
            string quarantinedPath = Path.Combine(
                quarantineDirectory,
                Path.GetFileName(manifestPath));
            try
            {
                EnsureChildPath(PluginRoot, quarantineDirectory, "plugin deletion quarantine");
                EnsureNoReparsePointInPath(Path.GetDirectoryName(quarantineDirectory)!);
                Directory.CreateDirectory(quarantineDirectory);
                EnsureNoReparsePointInPath(quarantineDirectory);
                EnsureChildPath(
                    quarantineDirectory,
                    quarantinedPath,
                    "quarantined plugin manifest");
                EnsureRegularFile(manifestPath, "plugin manifest");
                File.Move(manifestPath, quarantinedPath, overwrite: false);
            }
            catch (RuntimeProviderPluginException)
            {
                TryDeleteEmptyDirectory(quarantineDirectory);
                throw;
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                TryDeleteEmptyDirectory(quarantineDirectory);
                throw PluginError(
                    RuntimeProviderPluginErrorCode.IoFailure,
                    "The plugin manifest could not be moved into deletion quarantine.",
                    innerException: exception);
            }

            try
            {
                if (wasEnabled)
                {
                    HashSet<string> enabled = new(
                        state.EnabledPluginIds,
                        StringComparer.OrdinalIgnoreCase);
                    enabled.Remove(normalizedId);
                    await SaveStateAsync(
                        new PluginState(enabled),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception stateException)
            {
                try
                {
                    EnsureSafePluginRoot();
                    File.Move(quarantinedPath, manifestPath, overwrite: false);
                    TryDeleteEmptyDirectory(quarantineDirectory);
                }
                catch (Exception rollbackException) when (IsFileAccessException(rollbackException))
                {
                    throw PluginError(
                        RuntimeProviderPluginErrorCode.DeleteRollbackFailed,
                        "Plugin deletion failed and its quarantined manifest could not be restored.",
                        innerException: new AggregateException(stateException, rollbackException));
                }

                throw;
            }

            try
            {
                File.Delete(quarantinedPath);
                TryDeleteEmptyDirectory(quarantineDirectory);
                return new RuntimeProviderPluginDeleteResult(
                    normalizedId,
                    RuntimeProviderPluginDeleteOutcome.Deleted,
                    wasEnabled,
                    null);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new RuntimeProviderPluginDeleteResult(
                    normalizedId,
                    RuntimeProviderPluginDeleteOutcome.DeletedWithQuarantinedCopy,
                    wasEnabled,
                    quarantinedPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RuntimeProviderPluginDescriptor> SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        string normalizedId = NormalizeRequestedId(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using InterprocessLock operationLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureOperationalDirectories();
            RuntimeProviderPluginListResult current = await ListCoreAsync(
                cancellationToken).ConfigureAwait(false);
            ThrowIfTargetMutationUnsafe(current, normalizedId);
            RuntimeProviderPluginDescriptor descriptor = current.Plugins.FirstOrDefault(plugin =>
                    plugin.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
                ?? throw PluginError(
                    RuntimeProviderPluginErrorCode.PluginNotFound,
                    "The requested runtime provider plugin is not installed.",
                    "id");
            PluginState state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> enabledIds = new(
                state.EnabledPluginIds,
                StringComparer.OrdinalIgnoreCase);
            bool changed = enabled
                ? enabledIds.Add(normalizedId)
                : enabledIds.Remove(normalizedId);
            if (changed)
            {
                await SaveStateAsync(
                    new PluginState(enabledIds),
                    cancellationToken).ConfigureAwait(false);
            }

            return descriptor with { IsEnabled = enabled };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RuntimeProviderPluginListResult> ListCoreAsync(
        CancellationToken cancellationToken)
    {
        List<RuntimeProviderPluginError> errors = [];
        PluginState state;
        try
        {
            state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeProviderPluginException exception)
        {
            state = PluginState.Empty;
            errors.Add(ToSafeError(
                exception,
                fileName: Path.GetFileName(StatePath)));
        }

        List<RuntimeProviderPluginDescriptor> plugins = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        string[] manifestPaths = Directory.EnumerateFiles(
                PluginRoot,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumInstalledPlugins + 1)
            .ToArray();
        if (manifestPaths.Length > MaximumInstalledPlugins)
        {
            errors.Add(new RuntimeProviderPluginError(
                RuntimeProviderPluginErrorCode.InvalidState,
                $"The plugin store exceeds its {MaximumInstalledPlugins}-plugin limit."));
            manifestPaths = manifestPaths.Take(MaximumInstalledPlugins).ToArray();
        }

        foreach (string path in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            string? candidateId = Path.GetFileNameWithoutExtension(fileName);
            if (!RuntimeProviderPluginManifestParser.IsValidPluginId(candidateId))
            {
                candidateId = null;
            }

            try
            {
                EnsureDirectPluginChild(path, "plugin manifest");
                byte[] bytes = await ReadLimitedFileAsync(
                    path,
                    RuntimeProviderPluginManifestParser.MaximumManifestBytes,
                    cancellationToken).ConfigureAwait(false);
                RuntimeProviderPluginManifest manifest =
                    RuntimeProviderPluginManifestParser.Parse(bytes);
                string expectedFileName = manifest.Id + ".json";
                if (!fileName.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw PluginError(
                        RuntimeProviderPluginErrorCode.InvalidManifest,
                        "An imported plugin manifest file name does not match its plugin ID.",
                        "id");
                }

                if (!ids.Add(manifest.Id))
                {
                    throw PluginError(
                        RuntimeProviderPluginErrorCode.DuplicatePlugin,
                        "The plugin store contains a duplicate plugin ID.",
                        "id");
                }

                plugins.Add(new RuntimeProviderPluginDescriptor(
                    manifest,
                    path,
                    state.EnabledPluginIds.Contains(manifest.Id)));
            }
            catch (RuntimeProviderPluginException exception)
            {
                errors.Add(ToSafeError(
                    exception,
                    candidateId,
                    fileName,
                    candidateId is not null
                        && state.EnabledPluginIds.Contains(candidateId)));
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                errors.Add(new RuntimeProviderPluginError(
                    RuntimeProviderPluginErrorCode.IoFailure,
                    "An imported plugin manifest could not be read safely.",
                    candidateId,
                    fileName,
                    candidateId is not null
                        && state.EnabledPluginIds.Contains(candidateId)));
            }
        }

        foreach (string enabledId in state.EnabledPluginIds)
        {
            if (!plugins.Any(plugin => plugin.Id.Equals(
                    enabledId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new RuntimeProviderPluginError(
                    RuntimeProviderPluginErrorCode.InvalidState,
                    "The plugin activation state refers to a missing or invalid plugin.",
                    enabledId,
                    enabledId + ".json",
                    IsEnabled: true));
            }
        }

        return new RuntimeProviderPluginListResult(
            plugins
                .OrderBy(plugin => plugin.Manifest.LanguageToolId, StringComparer.Ordinal)
                .ThenBy(plugin => plugin.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plugin => plugin.Id, StringComparer.Ordinal)
                .ToArray(),
            errors);
    }

    private async Task<PluginState> ReadStateAsync(CancellationToken cancellationToken)
    {
        EnsureSafeStateLocation();
        if (!File.Exists(StatePath))
        {
            return PluginState.Empty;
        }

        byte[] bytes = await ReadLimitedFileAsync(
            StatePath,
            MaximumStateBytes,
            cancellationToken,
            RuntimeProviderPluginErrorCode.InvalidState).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
        }
        catch (JsonException exception)
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.InvalidState,
                "The runtime provider plugin activation state is not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            EnsureStateObject(root);
            if (!root.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion)
                || schemaVersion != CurrentStateSchemaVersion)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.InvalidState,
                    "The runtime provider plugin activation state schema is not supported.");
            }

            JsonElement enabledElement = root.GetProperty("enabledPluginIds");
            if (enabledElement.ValueKind != JsonValueKind.Array
                || enabledElement.GetArrayLength() > MaximumInstalledPlugins)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.InvalidState,
                    "The runtime provider plugin activation state is invalid.");
            }

            HashSet<string> enabled = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement item in enabledElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || !RuntimeProviderPluginIds.TryGetPluginId(
                        item.GetString()!,
                        out string id)
                    || !enabled.Add(id))
                {
                    throw PluginError(
                        RuntimeProviderPluginErrorCode.InvalidState,
                        "The runtime provider plugin activation state contains an invalid or duplicate ID.");
                }
            }

            return new PluginState(enabled);
        }
    }

    private async Task SaveStateAsync(
        PluginState state,
        CancellationToken cancellationToken)
    {
        EnsureSafeStateLocation();
        byte[] bytes;
        using (MemoryStream memory = new())
        {
            using (Utf8JsonWriter writer = new(
                memory,
                new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", CurrentStateSchemaVersion);
                writer.WriteStartArray("enabledPluginIds");
                foreach (string id in state.EnabledPluginIds.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            memory.WriteByte((byte)'\n');
            bytes = memory.ToArray();
        }

        string directory = Path.GetDirectoryName(StatePath)!;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(StatePath)}.{Guid.NewGuid():N}.tmp");
        EnsureChildPath(_managedRoot, temporaryPath, "temporary plugin state");
        try
        {
            await WriteNewFileAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            EnsureSafeStateLocation();
            File.Move(temporaryPath, StatePath, overwrite: true);
            EnsureRegularFile(StatePath, "plugin activation state");
        }
        catch (RuntimeProviderPluginException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.IoFailure,
                "The runtime provider plugin activation state could not be saved atomically.",
                innerException: exception);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<InterprocessLock> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken)
    {
        EnsureSafeStateLocation();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            LockTimeoutMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeStateLocation();
            if (TryGetAttributes(_lockPath) is FileAttributes attributes
                && (attributes & (FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.UnsafePath,
                    "The runtime provider plugin lock is not an ordinary file.");
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
                    FileOptions.WriteThrough);
                if (stream.Length == 0)
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }

                return new InterprocessLock(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                stream?.Dispose();
                await Task.Delay(LockRetryMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                stream?.Dispose();
                throw PluginError(
                    RuntimeProviderPluginErrorCode.IoFailure,
                    "The runtime provider plugin store lock could not be acquired.",
                    innerException: exception);
            }
        }
    }

    private void EnsureOperationalDirectories()
    {
        EnsureNoReparsePointInPath(_managedRoot);
        Directory.CreateDirectory(_managedRoot);
        EnsureNoReparsePointInPath(_managedRoot);
        Directory.CreateDirectory(PluginRoot);
        EnsureSafePluginRoot();
        EnsureSafeStateLocation();
    }

    private void EnsureSafePluginRoot()
    {
        EnsureNoReparsePointInPath(PluginRoot);
        if (Directory.Exists(PluginRoot))
        {
            EnsureRegularDirectory(PluginRoot, "runtime provider plugin root");
        }
    }

    private void EnsureSafeStateLocation()
    {
        string directory = Path.GetDirectoryName(StatePath)!;
        EnsureNoReparsePointInPath(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePointInPath(directory);
        EnsureRegularDirectory(directory, "runtime provider plugin state directory");
        if (File.Exists(StatePath))
        {
            EnsureRegularFile(StatePath, "plugin activation state");
        }
    }

    private string GetManifestPath(string pluginId)
    {
        string path = Path.Combine(PluginRoot, pluginId + ".json");
        EnsureDirectPluginChild(path, "plugin manifest");
        return path;
    }

    private void EnsureDirectPluginChild(string candidate, string description)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(_pluginRootPrefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(fullCandidate)!.Equals(
                PluginRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.UnsafePath,
                $"The {description} escaped the managed plugin directory.");
        }
    }

    private static async Task<byte[]> ReadLimitedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken,
        RuntimeProviderPluginErrorCode sizeErrorCode =
            RuntimeProviderPluginErrorCode.ManifestTooLarge)
    {
        EnsureNoReparsePointInPath(path);
        EnsureRegularFile(path, "plugin store file");
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw PluginError(
                sizeErrorCode,
                "A plugin store file is empty or exceeds its size limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureStateObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.InvalidState,
                "The runtime provider plugin activation state must be an object.");
        }

        HashSet<string> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!properties.Add(property.Name)
                || property.Name is not "schemaVersion" and not "enabledPluginIds")
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.InvalidState,
                    "The runtime provider plugin activation state contains unsupported fields.");
            }
        }

        if (!properties.SetEquals(["schemaVersion", "enabledPluginIds"])
            || root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number)
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.InvalidState,
                "The runtime provider plugin activation state is missing required fields.");
        }
    }

    private static void ThrowIfTargetMutationUnsafe(
        RuntimeProviderPluginListResult result,
        string pluginId)
    {
        RuntimeProviderPluginError? error = result.Errors.FirstOrDefault(item =>
            item.PluginId is null
            || item.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (error is null)
        {
            return;
        }

        throw PluginError(error.Code, error.Message);
    }

    private static string NormalizeRequestedId(string pluginId)
    {
        if (!RuntimeProviderPluginIds.TryGetPluginId(pluginId, out string normalizedId))
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.InvalidManifest,
                "The requested runtime provider plugin ID is invalid.",
                "id");
        }

        return normalizedId;
    }

    private static RuntimeProviderPluginError ToSafeError(
        RuntimeProviderPluginException exception,
        string? pluginId = null,
        string? fileName = null,
        bool isEnabled = false) => new(
            exception.Code,
            exception.Message,
            pluginId,
            fileName,
            isEnabled);

    private static RuntimeProviderPluginException PluginError(
        RuntimeProviderPluginErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null) => new(code, message, field, innerException);

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {description} must remain inside the managed root.");
        }
    }

    private static void EnsureRegularDirectory(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.UnsafePath,
                $"The {description} must be an ordinary directory.");
        }
    }

    private static void EnsureRegularFile(string path, string description)
    {
        EnsureNoReparsePointInPath(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw PluginError(
                RuntimeProviderPluginErrorCode.UnsafePath,
                $"The {description} must be an ordinary file.");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (TryGetAttributes(current.FullName) is FileAttributes attributes
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw PluginError(
                    RuntimeProviderPluginErrorCode.UnsafePath,
                    "A runtime provider plugin path crosses a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PluginState(IReadOnlySet<string> EnabledPluginIds)
    {
        public static PluginState Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class InterprocessLock : IDisposable
    {
        private readonly FileStream _stream;

        public InterprocessLock(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose() => _stream.Dispose();
    }
}
