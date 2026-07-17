using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Languages;

public sealed class ProviderSourcePreferenceStore
{
    public const int CurrentSchemaVersion = 1;
    public const long MaximumDocumentBytes = 256 * 1024;

    private static readonly HashSet<string> RootProperties = new(
        ["schemaVersion", "overrides", "customSources"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> OverrideProperties = new(
        ["languageToolId", "providerId", "slotId", "endpoint"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> CustomSourceProperties = new(
        [
            "languageToolId", "providerId", "slotId", "displayName", "endpoint",
            "endpointKind", "purpose", "enabled",
        ],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    private readonly string _managedRoot;
    private readonly string _preferencesPath;
    private readonly ManagedStateLock _stateLock;

    public ProviderSourcePreferenceStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _preferencesPath = Path.Combine(
            _managedRoot,
            "state",
            "provider-source-preferences.json");
        _stateLock = new ManagedStateLock(
            _managedRoot,
            _preferencesPath,
            "provider-source-preferences.lock");
    }

    public string PreferencesPath => _preferencesPath;

    public async Task<ProviderSourcePreferences> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSourcePreferences> LoadValidatedAsync(
        LanguageCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ProviderSourcePreferences preferences = await LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        ThrowIfInvalid(ProviderSourcePreferenceValidator.Validate(catalog, preferences));
        return preferences;
    }

    public Task<ProviderSourcePreferences> SetBuiltInOverrideAsync(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ProviderSourceOwner normalizedOwner =
            ProviderSourcePreferenceValidator.NormalizeAndValidateOwner(owner);
        _ = ResolveBuiltInSlot(catalog, normalizedOwner, requireOverridable: true);
        Uri normalizedEndpoint = ProviderSourcePreferenceValidator.ParseHttpsEndpoint(endpoint);
        return UpdateAsync(
            catalog,
            preferences =>
            {
                ProviderMirrorSlotOverride replacement = new(
                    normalizedOwner,
                    normalizedEndpoint);
                return preferences with
                {
                    Overrides = preferences.Overrides
                        .Where(item => !ProviderSourcePreferenceValidator.OwnersEqual(
                            item.Owner,
                            normalizedOwner))
                        .Append(replacement)
                        .ToArray(),
                };
            },
            cancellationToken);
    }

    public Task<ProviderSourcePreferences> RestoreBuiltInDefaultAsync(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ProviderSourceOwner normalizedOwner =
            ProviderSourcePreferenceValidator.NormalizeAndValidateOwner(owner);
        _ = ResolveBuiltInSlot(catalog, normalizedOwner, requireOverridable: false);
        return UpdateAsync(
            catalog,
            preferences => preferences with
            {
                Overrides = preferences.Overrides
                    .Where(item => !ProviderSourcePreferenceValidator.OwnersEqual(
                        item.Owner,
                        normalizedOwner))
                    .ToArray(),
            },
            cancellationToken);
    }

    public Task<ProviderSourcePreferences> AddCustomSourceAsync(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        string displayName,
        string endpoint,
        ProviderMirrorEndpointKind endpointKind,
        string purpose,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ProviderSourceOwner normalizedOwner =
            ProviderSourcePreferenceValidator.NormalizeAndValidateOwner(owner);
        LanguageToolProviderDefinition provider = ResolveProvider(catalog, normalizedOwner);
        if (provider.MirrorSlots.Any(slot => slot.Id.Equals(
                normalizedOwner.SlotId,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw Error(
                ProviderSourcePreferenceErrorCode.SlotConflict,
                "owner.slotId",
                "A custom source slot ID cannot replace a catalog-owned mirror slot.");
        }

        string normalizedDisplayName = ProviderSourcePreferenceValidator.NormalizeText(
            displayName,
            96,
            "displayName");
        Uri normalizedEndpoint = ProviderSourcePreferenceValidator.ParseHttpsEndpoint(endpoint);
        string normalizedPurpose = ProviderSourcePreferenceValidator.NormalizeText(
            purpose,
            256,
            "purpose");
        if (!Enum.IsDefined(endpointKind))
        {
            throw Error(
                ProviderSourcePreferenceErrorCode.InvalidEndpoint,
                "endpointKind",
                "The custom source endpoint kind is not supported.");
        }

        return UpdateAsync(
            catalog,
            preferences =>
            {
                if (preferences.CustomSources.Any(item =>
                        ProviderSourcePreferenceValidator.OwnersEqual(
                            item.Owner,
                            normalizedOwner)))
                {
                    throw Error(
                        ProviderSourcePreferenceErrorCode.DuplicateEntry,
                        "owner.slotId",
                        "A custom source already uses this owned slot ID.");
                }

                CustomProviderSource source = new(
                    normalizedOwner,
                    normalizedDisplayName,
                    normalizedEndpoint,
                    endpointKind,
                    normalizedPurpose,
                    enabled);
                IEnumerable<CustomProviderSource> existing = preferences.CustomSources;
                if (enabled)
                {
                    existing = existing.Select(item => IsSameSourceGroup(
                            item,
                            normalizedOwner,
                            endpointKind)
                            ? item with { IsEnabled = false }
                            : item);
                }

                return preferences with
                {
                    CustomSources = existing.Append(source).ToArray(),
                };
            },
            cancellationToken);
    }

    public Task<ProviderSourcePreferences> SetCustomSourceEnabledAsync(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ProviderSourceOwner normalizedOwner =
            ProviderSourcePreferenceValidator.NormalizeAndValidateOwner(owner);
        _ = ResolveProvider(catalog, normalizedOwner);
        return UpdateAsync(
            catalog,
            preferences =>
            {
                int index = FindCustomSourceIndex(preferences, normalizedOwner);
                if (index < 0)
                {
                    throw Error(
                        ProviderSourcePreferenceErrorCode.EntryNotFound,
                        "owner.slotId",
                        "The custom Provider source does not exist.");
                }

                CustomProviderSource[] updated = preferences.CustomSources.ToArray();
                if (enabled)
                {
                    CustomProviderSource target = updated[index];
                    for (int itemIndex = 0; itemIndex < updated.Length; itemIndex++)
                    {
                        if (itemIndex != index && IsSameSourceGroup(
                                updated[itemIndex],
                                normalizedOwner,
                                target.EndpointKind))
                        {
                            updated[itemIndex] = updated[itemIndex] with { IsEnabled = false };
                        }
                    }
                }

                updated[index] = updated[index] with { IsEnabled = enabled };
                return preferences with { CustomSources = updated };
            },
            cancellationToken);
    }

    public Task<ProviderSourcePreferences> DeleteCustomSourceAsync(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ProviderSourceOwner normalizedOwner =
            ProviderSourcePreferenceValidator.NormalizeAndValidateOwner(owner);
        _ = ResolveProvider(catalog, normalizedOwner);
        return UpdateAsync(
            catalog,
            preferences =>
            {
                int index = FindCustomSourceIndex(preferences, normalizedOwner);
                if (index < 0)
                {
                    throw Error(
                        ProviderSourcePreferenceErrorCode.EntryNotFound,
                        "owner.slotId",
                        "The custom Provider source does not exist.");
                }

                return preferences with
                {
                    CustomSources = preferences.CustomSources
                        .Where((_, itemIndex) => itemIndex != index)
                        .ToArray(),
                };
            },
            cancellationToken);
    }

    public async Task<ProviderSourceResolutionResult> ResolveAsync(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        CancellationToken cancellationToken = default)
    {
        ProviderSourcePreferences preferences = await LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        return ProviderSourceResolver.Resolve(catalog, preferences, owner);
    }

    public async Task<ProviderSourceListResolutionResult> ResolveProviderAsync(
        LanguageCatalog catalog,
        string languageToolId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ProviderSourcePreferences preferences = await LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        return ProviderSourceResolver.ResolveProvider(
            catalog,
            preferences,
            languageToolId,
            providerId);
    }

    private async Task<ProviderSourcePreferences> UpdateAsync(
        LanguageCatalog catalog,
        Func<ProviderSourcePreferences, ProviderSourcePreferences> update,
        CancellationToken cancellationToken)
    {
        using ManagedStateLock.Lease operationLock = await _stateLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        ProviderSourcePreferences current = await LoadCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        ProviderSourcePreferences updated = update(current)
            ?? throw new InvalidOperationException("The Provider source update returned no preferences.");
        ThrowIfInvalid(ProviderSourcePreferenceValidator.Validate(catalog, updated));
        ProviderSourcePreferences normalized = ProviderSourcePreferenceValidator.Normalize(updated);
        await SaveCoreAsync(normalized, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    private async Task<ProviderSourcePreferences> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        _stateLock.EnsureStatePathSafe(createDirectory: false);
        if (!File.Exists(_preferencesPath))
        {
            return ProviderSourcePreferences.Empty;
        }

        byte[] bytes;
        await using (FileStream stream = new(
            _preferencesPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            _stateLock.EnsureStatePathSafe(createDirectory: false);
            if (stream.Length is <= 0 or > MaximumDocumentBytes)
            {
                throw new InvalidDataException(
                    $"Provider source preferences must be between 1 and {MaximumDocumentBytes} bytes.");
            }

            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        ValidateStrictDocument(bytes);
        PreferencesDocument document;
        try
        {
            document = JsonSerializer.Deserialize<PreferencesDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Provider source preferences are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Provider source preferences contain an invalid value.",
                exception);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Provider source preference schema {document.SchemaVersion} is not supported.");
        }

        if (document.Overrides is null || document.CustomSources is null)
        {
            throw new InvalidDataException(
                "Provider source preference collections must be JSON arrays.");
        }

        try
        {
            ProviderSourcePreferences preferences = new(
                document.Overrides.Select(item => new ProviderMirrorSlotOverride(
                    new ProviderSourceOwner(
                        Required(item.LanguageToolId, "languageToolId"),
                        Required(item.ProviderId, "providerId"),
                        Required(item.SlotId, "slotId")),
                    ProviderSourcePreferenceValidator.ParseHttpsEndpoint(
                        Required(item.Endpoint, "endpoint"))))
                    .ToArray(),
                document.CustomSources.Select(item => new CustomProviderSource(
                    new ProviderSourceOwner(
                        Required(item.LanguageToolId, "languageToolId"),
                        Required(item.ProviderId, "providerId"),
                        Required(item.SlotId, "slotId")),
                    Required(item.DisplayName, "displayName"),
                    ProviderSourcePreferenceValidator.ParseHttpsEndpoint(
                        Required(item.Endpoint, "endpoint")),
                    item.EndpointKind,
                    Required(item.Purpose, "purpose"),
                    item.Enabled))
                    .ToArray());
            ThrowIfInvalid(ProviderSourcePreferenceValidator.ValidateStructure(preferences));
            return ProviderSourcePreferenceValidator.Normalize(preferences);
        }
        catch (ProviderSourcePreferenceException exception)
        {
            throw new InvalidDataException(
                $"Provider source preferences are invalid at {exception.Error.Path}: "
                    + exception.Error.Message,
                exception);
        }
    }

    private async Task SaveCoreAsync(
        ProviderSourcePreferences preferences,
        CancellationToken cancellationToken)
    {
        PreferencesDocument document = new(
            CurrentSchemaVersion,
            preferences.Overrides.Select(item => new OverrideDocument(
                item.Owner.LanguageToolId,
                item.Owner.ProviderId,
                item.Owner.SlotId,
                item.Endpoint.AbsoluteUri)).ToList(),
            preferences.CustomSources.Select(item => new CustomSourceDocument(
                item.Owner.LanguageToolId,
                item.Owner.ProviderId,
                item.Owner.SlotId,
                item.DisplayName,
                item.Endpoint.AbsoluteUri,
                item.EndpointKind,
                item.Purpose,
                item.IsEnabled)).ToList());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Provider source preferences cannot exceed {MaximumDocumentBytes} bytes.");
        }

        _stateLock.EnsureStatePathSafe(createDirectory: true);
        string directory = Path.GetDirectoryName(_preferencesPath)!;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_preferencesPath)}.{Guid.NewGuid():N}.tmp");
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
            File.Move(temporaryPath, _preferencesPath, overwrite: true);
            _stateLock.EnsureStatePathSafe(createDirectory: false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
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
            throw new InvalidDataException(
                "Provider source preferences are not valid JSON.",
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            EnsureObject(root, RootProperties, RootProperties, "$", "root object");
            JsonElement schema = root.GetProperty("schemaVersion");
            if (schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int schemaVersion)
                || schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Provider source preferences use an unsupported schema.");
            }

            ValidateArray(
                root.GetProperty("overrides"),
                ProviderSourcePreferenceValidator.MaximumOverrides,
                OverrideProperties,
                "overrides");
            ValidateArray(
                root.GetProperty("customSources"),
                ProviderSourcePreferenceValidator.MaximumCustomSources,
                CustomSourceProperties,
                "customSources");
        }
    }

    private static void ValidateArray(
        JsonElement value,
        int maximumItems,
        IReadOnlySet<string> properties,
        string path)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumItems)
        {
            throw new InvalidDataException(
                $"Provider source preference {path} must be an array with at most {maximumItems} items.");
        }

        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            EnsureObject(item, properties, properties, $"{path}[{index}]", "entry");
            index++;
        }
    }

    private static void EnsureObject(
        JsonElement value,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string> required,
        string path,
        string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Provider source preference {path} must be a JSON {description}.");
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Provider source preference {path} contains a duplicate field.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Provider source preference {path} contains an unsupported field.");
            }
        }

        foreach (string property in required)
        {
            if (!value.TryGetProperty(property, out _))
            {
                throw new InvalidDataException(
                    $"Provider source preference {path} is missing the {property} field.");
            }
        }
    }

    private static LanguageToolProviderDefinition ResolveProvider(
        LanguageCatalog catalog,
        ProviderSourceOwner owner)
    {
        List<ProviderSourcePreferenceError> errors = [];
        if (!ProviderSourcePreferenceValidator.TryFindProvider(
                catalog,
                owner,
                "owner",
                errors,
                out LanguageToolProviderDefinition? provider))
        {
            throw new ProviderSourcePreferenceException(errors[0]);
        }

        return provider!;
    }

    private static ProviderMirrorSlotDefinition ResolveBuiltInSlot(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        bool requireOverridable)
    {
        LanguageToolProviderDefinition provider = ResolveProvider(catalog, owner);
        ProviderMirrorSlotDefinition? slot = provider.MirrorSlots.FirstOrDefault(candidate =>
            candidate.Id.Equals(owner.SlotId, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            throw Error(
                ProviderSourcePreferenceErrorCode.CatalogSlotNotFound,
                "owner.slotId",
                "The built-in mirror slot is not present in the effective language catalog.");
        }

        if (requireOverridable && !slot.UserOverridable)
        {
            throw Error(
                ProviderSourcePreferenceErrorCode.SlotNotOverridable,
                "owner.slotId",
                "The effective language catalog does not allow this mirror slot to be overridden.");
        }

        return slot;
    }

    private static int FindCustomSourceIndex(
        ProviderSourcePreferences preferences,
        ProviderSourceOwner owner)
    {
        for (int index = 0; index < preferences.CustomSources.Count; index++)
        {
            if (ProviderSourcePreferenceValidator.OwnersEqual(
                    preferences.CustomSources[index].Owner,
                    owner))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSameSourceGroup(
        CustomProviderSource source,
        ProviderSourceOwner owner,
        ProviderMirrorEndpointKind endpointKind) =>
        source.Owner.LanguageToolId.Equals(
            owner.LanguageToolId,
            StringComparison.OrdinalIgnoreCase)
        && source.Owner.ProviderId.Equals(
            owner.ProviderId,
            StringComparison.OrdinalIgnoreCase)
        && source.EndpointKind == endpointKind;

    private static void ThrowIfInvalid(ProviderSourcePreferenceValidationResult validation)
    {
        if (!validation.Success)
        {
            throw new ProviderSourcePreferenceException(validation.Errors[0]);
        }
    }

    private static ProviderSourcePreferenceException Error(
        ProviderSourcePreferenceErrorCode code,
        string path,
        string message) => new(
            ProviderSourcePreferenceValidator.Error(code, path, message));

    private static string Required(string? value, string field)
    {
        if (value is null)
        {
            throw new InvalidDataException(
                $"Provider source preference {field} must be a string.");
        }

        return value;
    }

    private sealed record PreferencesDocument(
        int SchemaVersion,
        List<OverrideDocument>? Overrides,
        List<CustomSourceDocument>? CustomSources);

    private sealed record OverrideDocument(
        string? LanguageToolId,
        string? ProviderId,
        string? SlotId,
        string? Endpoint);

    private sealed record CustomSourceDocument(
        string? LanguageToolId,
        string? ProviderId,
        string? SlotId,
        string? DisplayName,
        string? Endpoint,
        ProviderMirrorEndpointKind EndpointKind,
        string? Purpose,
        bool Enabled);
}
