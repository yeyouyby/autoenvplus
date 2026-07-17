using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Settings;

public sealed class AutoEnvPlusApplicationSettingsStore
{
    public const long MaximumSettingsBytes = 64 * 1024;

    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly HashSet<string> AllowedProperties = new(
        [
            "schemaVersion",
            "startupDestination",
            "overviewRefreshPolicy",
            "deepScanIntervalHours",
            "languageVisibilityPolicy",
            "defaultDownloadConnections",
            "defaultDownloadMaximumBytes",
            "theme",
            "backdrop",
            "density",
            "shellAutoActivation",
            "useManagedShims",
            "catalogUpdatePolicy",
            "logRetentionDays",
            "requireDestructiveActionConfirmation",
            "showExperimentalTools",
        ],
        StringComparer.Ordinal);

    private readonly string _managedRoot;
    private readonly string _settingsPath;
    private readonly ManagedStateLock _stateLock;

    public AutoEnvPlusApplicationSettingsStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _settingsPath = Path.Combine(_managedRoot, "state", "application-settings.json");
        _stateLock = new ManagedStateLock(
            _managedRoot,
            _settingsPath,
            "application-settings.lock");
    }

    public string SettingsPath => _settingsPath;

    public async Task<AutoEnvPlusApplicationSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AutoEnvPlusApplicationSettings> SaveAsync(
        AutoEnvPlusApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public async Task<AutoEnvPlusApplicationSettings> UpdateAsync(
        Func<AutoEnvPlusApplicationSettings, AutoEnvPlusApplicationSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        AutoEnvPlusApplicationSettings current = await LoadCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        AutoEnvPlusApplicationSettings updated = update(current)
            ?? throw new InvalidOperationException("The settings update returned no value.");
        updated.Validate();
        await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<AutoEnvPlusApplicationSettings> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (!File.Exists(_settingsPath))
        {
            return AutoEnvPlusApplicationSettings.Default;
        }

        byte[] bytes;
        await using (FileStream stream = new(
            _settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            _stateLock.EnsureStatePathSafe(createDirectory: false);
            if (stream.Length is <= 0 or > MaximumSettingsBytes)
            {
                throw new InvalidDataException(
                    $"Application settings must be between 1 and {MaximumSettingsBytes} bytes.");
            }

            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        ValidateStrictDocument(bytes);
        SettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsDocument>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Application settings contain an invalid value.", exception);
        }

        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Application settings use an unsupported schema.");
        }

        AutoEnvPlusApplicationSettings settings = document.ToSettings();
        settings.Validate();
        return settings;
    }

    private async Task SaveCoreAsync(
        AutoEnvPlusApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            SettingsDocument.FromSettings(settings),
            JsonOptions);
        if (bytes.Length > MaximumSettingsBytes)
        {
            throw new InvalidDataException(
                $"Application settings cannot exceed {MaximumSettingsBytes} bytes.");
        }

        string directory = Path.GetDirectoryName(_settingsPath)!;
        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
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
                _stateLock.EnsureTemporaryFilePathSafe(temporaryPath);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _stateLock.EnsureStatePathSafe(createDirectory: false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _stateLock.EnsureStatePathSafe(createDirectory: false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateStrictDocument(byte[] bytes)
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
                    MaxDepth = 8,
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Application settings are not valid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Application settings must be a JSON object.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Application settings contain a duplicate field.");
                }

                if (!AllowedProperties.Contains(property.Name))
                {
                    throw new InvalidDataException(
                        "Application settings contain an unsupported field.");
                }
            }
        }
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        StartupDestination StartupDestination,
        OverviewRefreshPolicy OverviewRefreshPolicy,
        int DeepScanIntervalHours,
        LanguageVisibilityPolicy LanguageVisibilityPolicy,
        int DefaultDownloadConnections,
        long DefaultDownloadMaximumBytes,
        ApplicationThemePreference Theme,
        BackdropPreference Backdrop,
        InterfaceDensity Density,
        bool ShellAutoActivation,
        bool UseManagedShims,
        CatalogUpdatePolicy CatalogUpdatePolicy,
        int LogRetentionDays,
        bool RequireDestructiveActionConfirmation,
        bool ShowExperimentalTools)
    {
        public AutoEnvPlusApplicationSettings ToSettings() => new(
            StartupDestination,
            OverviewRefreshPolicy,
            DeepScanIntervalHours,
            LanguageVisibilityPolicy,
            DefaultDownloadConnections,
            DefaultDownloadMaximumBytes,
            Theme,
            Backdrop,
            Density,
            ShellAutoActivation,
            UseManagedShims,
            CatalogUpdatePolicy,
            LogRetentionDays,
            RequireDestructiveActionConfirmation,
            ShowExperimentalTools);

        public static SettingsDocument FromSettings(
            AutoEnvPlusApplicationSettings settings) => new(
            CurrentSchemaVersion,
            settings.StartupDestination,
            settings.OverviewRefreshPolicy,
            settings.DeepScanIntervalHours,
            settings.LanguageVisibilityPolicy,
            settings.DefaultDownloadConnections,
            settings.DefaultDownloadMaximumBytes,
            settings.Theme,
            settings.Backdrop,
            settings.Density,
            settings.ShellAutoActivation,
            settings.UseManagedShims,
            settings.CatalogUpdatePolicy,
            settings.LogRetentionDays,
            settings.RequireDestructiveActionConfirmation,
            settings.ShowExperimentalTools);
    }
}
