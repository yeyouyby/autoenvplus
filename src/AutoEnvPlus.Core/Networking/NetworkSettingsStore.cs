using System.Collections.Concurrent;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoEnvPlus.Core.Networking;

public sealed class NetworkSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSettingsBytes = 256 * 1024;

    private const int LockRetryMilliseconds = 25;
    private const int LockTimeoutMilliseconds = 5_000;

    private static readonly HashSet<string> RootProperties =
        new(["schemaVersion", "global", "tools"], StringComparer.Ordinal);

    private static readonly HashSet<string> GlobalProperties =
        new(["httpProxy", "httpsProxy", "noProxy", "mirror"], StringComparer.Ordinal);

    private static readonly HashSet<string> ToolProperties =
        new(["httpProxy", "httpsProxy", "mirror"], StringComparer.Ordinal);

    private static readonly HashSet<string> OverrideProperties =
        new(["mode", "value"], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _managedRoot;
    private readonly string _stateDirectory;
    private readonly string _settingsPath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate;

    public NetworkSettingsStore(string managedRoot, string? settingsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _stateDirectory = Path.Combine(_managedRoot, "state");
        _settingsPath = Path.GetFullPath(
            settingsPath ?? Path.Combine(_stateDirectory, "network-settings.json"));
        _lockPath = Path.Combine(_stateDirectory, "network-settings.lock");
        EnsureChildPath(_managedRoot, _settingsPath, "network settings file");
        EnsureChildPath(_managedRoot, _lockPath, "network settings lock");
        if (_settingsPath.Equals(_lockPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The network settings file cannot be the store lock file.",
                nameof(settingsPath));
        }

        _gate = Gates.GetOrAdd(_lockPath, static _ => new SemaphoreSlim(1, 1));
    }

    public string SettingsPath => _settingsPath;

    public async Task<NetworkSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using InterprocessLock operationLock = await AcquireInterprocessLockAsync(
                    cancellationToken).ConfigureAwait(false);
                return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UnsafeNetworkSettingsPathException)
            {
                return Failure(
                    NetworkSettingsErrorCode.UnsafePath,
                    "$",
                    "The network settings path crosses a reparse point or is not an ordinary file path.");
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                return Failure(
                    NetworkSettingsErrorCode.IoFailure,
                    "$",
                    "The network settings store could not be read safely.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NetworkSettingsSaveResult> SaveAsync(
        NetworkSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<NetworkSettingsError> errors = [];
        NetworkSettings? normalized = NetworkSettingsResolver.Normalize(settings, errors);
        if (errors.Count > 0 || normalized is null)
        {
            return new NetworkSettingsSaveResult(false, null, errors);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                using InterprocessLock operationLock = await AcquireInterprocessLockAsync(
                    cancellationToken).ConfigureAwait(false);
                return await SaveCoreAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
            catch (UnsafeNetworkSettingsPathException)
            {
                return SaveFailure(
                    NetworkSettingsErrorCode.UnsafePath,
                    "The network settings path crosses a reparse point or is not an ordinary file path.");
            }
            catch (Exception exception) when (IsFileAccessException(exception))
            {
                return SaveFailure(
                    NetworkSettingsErrorCode.IoFailure,
                    "The network settings store could not be saved safely.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NetworkSettingsLoadResult> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        EnsureSafeSettingsLocation(createDirectory: false);
        if (!File.Exists(_settingsPath))
        {
            return new NetworkSettingsLoadResult(true, NetworkSettings.Default, []);
        }

        byte[] bytes;
        try
        {
            await using FileStream stream = new(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            EnsureSafeSettingsTarget();
            if (stream.Length > MaximumSettingsBytes)
            {
                return Failure(
                    NetworkSettingsErrorCode.DocumentTooLarge,
                    "$",
                    $"The network settings file exceeds the {MaximumSettingsBytes}-byte limit.");
            }

            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            return Failure(
                NetworkSettingsErrorCode.IoFailure,
                "$",
                "The network settings file could not be read.");
        }

        JsonDocument strictDocument;
        try
        {
            strictDocument = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
        }
        catch (JsonException)
        {
            return Failure(
                NetworkSettingsErrorCode.MalformedJson,
                "$",
                "The network settings file is not valid JSON.");
        }

        try
        {
            using (strictDocument)
            {
                ValidateStrictDocument(strictDocument.RootElement);
            }
        }
        catch (StrictDocumentException exception)
        {
            return Failure(
                NetworkSettingsErrorCode.InvalidDocument,
                exception.Path,
                exception.Message);
        }

        SettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsDocument>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return Failure(
                NetworkSettingsErrorCode.InvalidDocument,
                "$",
                "The network settings document contains an invalid value.");
        }

        if (document is null)
        {
            return Failure(
                NetworkSettingsErrorCode.InvalidDocument,
                "$",
                "The network settings document is empty.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            return Failure(
                NetworkSettingsErrorCode.UnsupportedSchema,
                "schemaVersion",
                $"Network settings schema {document.SchemaVersion} is not supported.");
        }

        List<NetworkSettingsError> errors = [];
        NetworkSettings candidate = FromDocument(document, errors);
        NetworkSettings? normalized = NetworkSettingsResolver.Normalize(candidate, errors);
        return errors.Count == 0 && normalized is not null
            ? new NetworkSettingsLoadResult(true, normalized, [])
            : new NetworkSettingsLoadResult(false, null, errors);
    }

    private async Task<NetworkSettingsSaveResult> SaveCoreAsync(
        NetworkSettings settings,
        CancellationToken cancellationToken)
    {
        byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
            ToDocument(settings),
            JsonOptions);
        if (contents.Length > MaximumSettingsBytes)
        {
            return SaveFailure(
                NetworkSettingsErrorCode.DocumentTooLarge,
                $"The network settings document exceeds the {MaximumSettingsBytes}-byte limit.");
        }

        EnsureSafeSettingsLocation(createDirectory: true);
        string directory = Path.GetDirectoryName(_settingsPath)!;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        EnsureChildPath(_managedRoot, temporaryPath, "temporary network settings file");
        EnsureNoReparsePointInPath(temporaryPath);

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
                EnsureRegularFile(temporaryPath, "temporary network settings file");
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureSafeSettingsLocation(createDirectory: false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            EnsureSafeSettingsTarget();
            return new NetworkSettingsSaveResult(true, settings, []);
        }
        catch (UnsafeNetworkSettingsPathException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileAccessException(exception))
        {
            return SaveFailure(
                NetworkSettingsErrorCode.IoFailure,
                "The network settings file could not be saved.");
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<InterprocessLock> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken)
    {
        EnsureSafeStateDirectory();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            LockTimeoutMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeStateDirectory();
            EnsureSafeLockTarget();

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
                EnsureSafeLockTarget();
                if (stream.Length == 0)
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }

                return new InterprocessLock(stream);
            }
            catch (UnsafeNetworkSettingsPathException)
            {
                stream?.Dispose();
                throw;
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
                throw new IOException(
                    "The network settings store lock could not be acquired.",
                    exception);
            }
        }
    }

    private void EnsureSafeStateDirectory()
    {
        EnsureNoReparsePointInPath(_stateDirectory);
        Directory.CreateDirectory(_stateDirectory);
        EnsureNoReparsePointInPath(_stateDirectory);
        EnsureRegularDirectory(_stateDirectory, "network settings state directory");
        EnsureSafeSettingsLocation(createDirectory: false);
    }

    private void EnsureSafeSettingsLocation(bool createDirectory)
    {
        string directory = Path.GetDirectoryName(_settingsPath)!;
        EnsureNoReparsePointInPath(directory);
        EnsureNoReparsePointInPath(_settingsPath);
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
            EnsureNoReparsePointInPath(directory);
        }

        if (TryGetAttributes(directory) is FileAttributes directoryAttributes
            && ((directoryAttributes & FileAttributes.Directory) == 0
                || (directoryAttributes & (FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0))
        {
            throw UnsafePath("The network settings directory must be an ordinary directory.");
        }

        EnsureSafeSettingsTarget();
    }

    private void EnsureSafeSettingsTarget()
    {
        if (TryGetAttributes(_settingsPath) is FileAttributes attributes
            && (attributes & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw UnsafePath("The network settings target must be an ordinary file.");
        }
    }

    private void EnsureSafeLockTarget()
    {
        EnsureNoReparsePointInPath(_lockPath);
        if (TryGetAttributes(_lockPath) is FileAttributes attributes
            && (attributes & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw UnsafePath("The network settings lock must be an ordinary file.");
        }
    }

    private static void ValidateStrictDocument(JsonElement root)
    {
        ValidateKnownObject(root, RootProperties, "$", "root object");

        if (root.TryGetProperty("global", out JsonElement global)
            && global.ValueKind != JsonValueKind.Null)
        {
            ValidateKnownObject(global, GlobalProperties, "global", "global object");
        }

        if (!root.TryGetProperty("tools", out JsonElement tools)
            || tools.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (tools.ValueKind != JsonValueKind.Object)
        {
            throw StrictDocument("tools", "The tools value must be an object.");
        }

        HashSet<string> toolIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty toolProperty in tools.EnumerateObject())
        {
            if (!toolIds.Add(toolProperty.Name))
            {
                throw StrictDocument(
                    "tools",
                    "Tool identifiers must be unique without regard to letter casing.");
            }

            if (toolProperty.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            ValidateKnownObject(
                toolProperty.Value,
                ToolProperties,
                "tools",
                "tool override object");
            ValidateOverrideProperty(toolProperty.Value, "httpProxy");
            ValidateOverrideProperty(toolProperty.Value, "httpsProxy");
            ValidateOverrideProperty(toolProperty.Value, "mirror");
        }
    }

    private static void ValidateOverrideProperty(JsonElement tool, string propertyName)
    {
        if (tool.TryGetProperty(propertyName, out JsonElement endpointOverride)
            && endpointOverride.ValueKind != JsonValueKind.Null)
        {
            ValidateKnownObject(
                endpointOverride,
                OverrideProperties,
                "tools",
                "endpoint override object");
        }
    }

    private static void ValidateKnownObject(
        JsonElement value,
        IReadOnlySet<string> allowedProperties,
        string path,
        string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StrictDocument(path, $"The {description} must be an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw StrictDocument(path, $"The {description} contains a duplicate field.");
            }

            if (!allowedProperties.Contains(property.Name))
            {
                throw StrictDocument(path, $"The {description} contains an unsupported field.");
            }
        }
    }

    private static NetworkSettings FromDocument(
        SettingsDocument document,
        List<NetworkSettingsError> errors)
    {
        GlobalDocument global = document.Global ?? new GlobalDocument();
        Dictionary<string, ToolNetworkSettings> tools = new(StringComparer.Ordinal);
        foreach ((string toolId, ToolDocument? tool) in document.Tools ?? [])
        {
            if (tool is null)
            {
                errors.Add(new NetworkSettingsError(
                    NetworkSettingsErrorCode.InvalidDocument,
                    $"tools.{toolId}",
                    "A tool override must be an object."));
                continue;
            }

            tools[toolId] = new ToolNetworkSettings(
                FromOverrideDocument(tool.HttpProxy),
                FromOverrideDocument(tool.HttpsProxy),
                FromOverrideDocument(tool.Mirror));
        }

        return new NetworkSettings(
            new GlobalNetworkSettings(
                global.HttpProxy,
                global.HttpsProxy,
                global.NoProxy,
                global.Mirror),
            tools);
    }

    private static NetworkEndpointOverride? FromOverrideDocument(OverrideDocument? value) =>
        value is null ? null : new NetworkEndpointOverride(value.Mode, value.Value);

    private static SettingsDocument ToDocument(NetworkSettings settings)
    {
        GlobalNetworkSettings global = settings.Global!;
        return new SettingsDocument(
            CurrentSchemaVersion,
            new GlobalDocument(
                global.HttpProxy,
                global.HttpsProxy,
                global.NoProxy?.ToList(),
                global.Mirror),
            settings.Tools!.ToDictionary(
                pair => pair.Key,
                pair => (ToolDocument?)new ToolDocument(
                    ToOverrideDocument(pair.Value!.HttpProxy),
                    ToOverrideDocument(pair.Value.HttpsProxy),
                    ToOverrideDocument(pair.Value.Mirror)),
                StringComparer.OrdinalIgnoreCase));
    }

    private static OverrideDocument? ToOverrideDocument(NetworkEndpointOverride? value)
    {
        if (value is null || value.Mode == NetworkEndpointOverrideMode.Inherit)
        {
            return null;
        }

        return new OverrideDocument(value.Mode, value.Value);
    }

    private static NetworkSettingsLoadResult Failure(
        NetworkSettingsErrorCode code,
        string path,
        string message) => new(
            false,
            null,
            [new NetworkSettingsError(code, path, message)]);

    private static NetworkSettingsSaveResult SaveFailure(
        NetworkSettingsErrorCode code,
        string message) => new(
            false,
            null,
            [new NetworkSettingsError(code, "$", message)]);

    private static StrictDocumentException StrictDocument(string path, string message) =>
        new(path, message);

    private static UnsafeNetworkSettingsPathException UnsafePath(string message) =>
        new(message);

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string rootPrefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} must remain inside the managed root.");
        }
    }

    private static void EnsureRegularDirectory(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw UnsafePath($"The {description} must be an ordinary directory.");
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
            throw UnsafePath($"The {description} must be an ordinary file.");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (TryGetAttributes(current) is FileAttributes attributes
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw UnsafePath("A network settings path crosses a reparse point.");
            }

            string? parent = Path.GetDirectoryName(current);
            current = parent is not null && !parent.Equals(current, StringComparison.OrdinalIgnoreCase)
                ? parent
                : null;
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        GlobalDocument? Global,
        Dictionary<string, ToolDocument?>? Tools);

    private sealed record GlobalDocument(
        string? HttpProxy = null,
        string? HttpsProxy = null,
        List<string>? NoProxy = null,
        string? Mirror = null);

    private sealed record ToolDocument(
        OverrideDocument? HttpProxy = null,
        OverrideDocument? HttpsProxy = null,
        OverrideDocument? Mirror = null);

    private sealed record OverrideDocument(
        NetworkEndpointOverrideMode Mode,
        string? Value = null);

    private sealed class StrictDocumentException : Exception
    {
        public StrictDocumentException(string path, string message)
            : base(message)
        {
            Path = path;
        }

        public string Path { get; }
    }

    private sealed class UnsafeNetworkSettingsPathException : Exception
    {
        public UnsafeNetworkSettingsPathException(string message)
            : base(message)
        {
        }
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
