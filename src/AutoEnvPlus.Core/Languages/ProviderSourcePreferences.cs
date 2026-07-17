using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Languages;

public sealed record ProviderSourceOwner(
    string LanguageToolId,
    string ProviderId,
    string SlotId);

public sealed record ProviderMirrorSlotOverride(
    ProviderSourceOwner Owner,
    Uri Endpoint);

public sealed record CustomProviderSource(
    ProviderSourceOwner Owner,
    string DisplayName,
    Uri Endpoint,
    ProviderMirrorEndpointKind EndpointKind,
    string Purpose,
    bool IsEnabled);

public sealed record ProviderSourcePreferences(
    IReadOnlyList<ProviderMirrorSlotOverride> Overrides,
    IReadOnlyList<CustomProviderSource> CustomSources)
{
    public static ProviderSourcePreferences Empty { get; } = new([], []);
}

public enum ProviderSourcePreferenceErrorCode
{
    InvalidIdentifier,
    InvalidText,
    InvalidEndpoint,
    TooManyEntries,
    DuplicateEntry,
    CatalogToolNotFound,
    CatalogProviderNotFound,
    CatalogSlotNotFound,
    SlotNotOverridable,
    SlotConflict,
    EntryNotFound,
}

public sealed record ProviderSourcePreferenceError(
    ProviderSourcePreferenceErrorCode Code,
    string Path,
    string Message);

public sealed record ProviderSourcePreferenceValidationResult(
    IReadOnlyList<ProviderSourcePreferenceError> Errors)
{
    public bool Success => Errors.Count == 0;
}

public sealed class ProviderSourcePreferenceException : Exception
{
    public ProviderSourcePreferenceException(ProviderSourcePreferenceError error)
        : base(error.Message)
    {
        Error = error;
    }

    public ProviderSourcePreferenceError Error { get; }
}

public enum ProviderSourceOrigin
{
    CatalogDefault,
    UserOverride,
    Custom,
}

public sealed record ResolvedProviderSource(
    ProviderSourceOwner Owner,
    string DisplayName,
    Uri ConfiguredEndpoint,
    ProviderMirrorEndpointKind EndpointKind,
    string Purpose,
    ProviderSourceOrigin Origin,
    bool IsEnabled)
{
    public Uri? EffectiveEndpoint => IsEnabled ? ConfiguredEndpoint : null;
}

public sealed record ProviderSourceResolutionResult(
    ResolvedProviderSource? Source,
    IReadOnlyList<ProviderSourcePreferenceError> Errors)
{
    public bool Success => Source is not null && Errors.Count == 0;
}

public sealed record ProviderSourceListResolutionResult(
    IReadOnlyList<ResolvedProviderSource> Sources,
    IReadOnlyList<ProviderSourcePreferenceError> Errors)
{
    public bool Success => Errors.Count == 0;
}

public static partial class ProviderSourcePreferenceValidator
{
    public const int MaximumOverrides = 1_024;
    public const int MaximumCustomSources = 1_024;
    public const int MaximumCustomSourcesPerProvider = 64;
    public const int MaximumEndpointLength = 2_048;

    public static ProviderSourcePreferenceValidationResult Validate(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(preferences);
        List<ProviderSourcePreferenceError> errors = ValidateStructure(preferences).Errors.ToList();
        if (errors.Count > 0)
        {
            return new ProviderSourcePreferenceValidationResult(errors);
        }

        for (int index = 0; index < preferences.Overrides.Count; index++)
        {
            ProviderMirrorSlotOverride item = preferences.Overrides[index];
            string path = $"overrides[{index}]";
            if (!TryFindProvider(catalog, item.Owner, path, errors, out LanguageToolProviderDefinition? provider))
            {
                continue;
            }

            ProviderMirrorSlotDefinition? slot = provider!.MirrorSlots.FirstOrDefault(candidate =>
                candidate.Id.Equals(item.Owner.SlotId, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.CatalogSlotNotFound,
                    $"{path}.slotId",
                    "The mirror override references a slot that is not present in the effective language catalog."));
            }
            else if (!slot.UserOverridable)
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.SlotNotOverridable,
                    $"{path}.slotId",
                    "The effective language catalog does not allow this mirror slot to be overridden."));
            }
        }

        for (int index = 0; index < preferences.CustomSources.Count; index++)
        {
            CustomProviderSource item = preferences.CustomSources[index];
            string path = $"customSources[{index}]";
            if (!TryFindProvider(catalog, item.Owner, path, errors, out LanguageToolProviderDefinition? provider))
            {
                continue;
            }

            if (provider!.MirrorSlots.Any(slot => slot.Id.Equals(
                    item.Owner.SlotId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.SlotConflict,
                    $"{path}.slotId",
                    "A custom source slot ID cannot replace a catalog-owned mirror slot."));
            }
        }

        return new ProviderSourcePreferenceValidationResult(errors);
    }

    public static ProviderSourcePreferenceValidationResult ValidateStructure(
        ProviderSourcePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        List<ProviderSourcePreferenceError> errors = [];
        if (preferences.Overrides is null)
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.TooManyEntries,
                "overrides",
                "The mirror override collection is required."));
            return new ProviderSourcePreferenceValidationResult(errors);
        }

        if (preferences.CustomSources is null)
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.TooManyEntries,
                "customSources",
                "The custom source collection is required."));
            return new ProviderSourcePreferenceValidationResult(errors);
        }

        if (preferences.Overrides.Count > MaximumOverrides)
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.TooManyEntries,
                "overrides",
                $"At most {MaximumOverrides} mirror overrides may be stored."));
        }

        if (preferences.CustomSources.Count > MaximumCustomSources)
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.TooManyEntries,
                "customSources",
                $"At most {MaximumCustomSources} custom sources may be stored."));
        }

        HashSet<string> overrideOwners = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < preferences.Overrides.Count; index++)
        {
            ProviderMirrorSlotOverride? item = preferences.Overrides[index];
            string path = $"overrides[{index}]";
            if (item is null)
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.DuplicateEntry,
                    path,
                    "A mirror override cannot be null."));
                continue;
            }

            ValidateOwner(item.Owner, path, errors);
            ValidateEndpoint(item.Endpoint, $"{path}.endpoint", errors);
            if (!overrideOwners.Add(OwnerIdentity(item.Owner)))
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.DuplicateEntry,
                    path,
                    "Only one mirror override may be stored for an owned slot."));
            }
        }

        HashSet<string> customOwners = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> customCounts = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < preferences.CustomSources.Count; index++)
        {
            CustomProviderSource? item = preferences.CustomSources[index];
            string path = $"customSources[{index}]";
            if (item is null)
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.DuplicateEntry,
                    path,
                    "A custom source cannot be null."));
                continue;
            }

            ValidateOwner(item.Owner, path, errors);
            ValidateText(item.DisplayName, 96, $"{path}.displayName", errors);
            ValidateEndpoint(item.Endpoint, $"{path}.endpoint", errors);
            ValidateText(item.Purpose, 256, $"{path}.purpose", errors);
            if (!Enum.IsDefined(item.EndpointKind))
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.InvalidEndpoint,
                    $"{path}.endpointKind",
                    "The custom source endpoint kind is not supported."));
            }

            string identity = OwnerIdentity(item.Owner);
            if (!customOwners.Add(identity))
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.DuplicateEntry,
                    path,
                    "A custom source slot must be unique within its language tool and provider."));
            }

            if (overrideOwners.Contains(identity))
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.SlotConflict,
                    path,
                    "A slot cannot be both a mirror override and a custom source."));
            }

            string providerIdentity = ProviderIdentity(item.Owner);
            customCounts.TryGetValue(providerIdentity, out int count);
            customCounts[providerIdentity] = count + 1;
        }

        foreach ((string providerIdentity, int count) in customCounts)
        {
            if (count > MaximumCustomSourcesPerProvider)
            {
                errors.Add(Error(
                    ProviderSourcePreferenceErrorCode.TooManyEntries,
                    "customSources",
                    $"Provider {providerIdentity} has more than {MaximumCustomSourcesPerProvider} custom sources."));
            }
        }

        return new ProviderSourcePreferenceValidationResult(errors);
    }

    public static ProviderSourceOwner NormalizeOwner(ProviderSourceOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ProviderSourceOwner(
            owner.LanguageToolId?.Trim().ToLowerInvariant() ?? string.Empty,
            owner.ProviderId?.Trim().ToLowerInvariant() ?? string.Empty,
            owner.SlotId?.Trim().ToLowerInvariant() ?? string.Empty);
    }

    internal static ProviderSourceOwner NormalizeAndValidateOwner(
        ProviderSourceOwner owner,
        string path = "owner")
    {
        ProviderSourceOwner normalized = NormalizeOwner(owner);
        List<ProviderSourcePreferenceError> errors = [];
        ValidateOwner(normalized, path, errors);
        if (errors.Count > 0)
        {
            throw new ProviderSourcePreferenceException(errors[0]);
        }

        return normalized;
    }

    public static Uri ParseHttpsEndpoint(string endpoint, string path = "endpoint")
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || endpoint.Length > MaximumEndpointLength
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ProviderSourcePreferenceException(Error(
                ProviderSourcePreferenceErrorCode.InvalidEndpoint,
                path,
                "A provider source must be an absolute HTTPS URI without credentials, query, or fragment."));
        }

        return uri;
    }

    public static string NormalizeText(string value, int maximumLength, string path)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ProviderSourcePreferenceException(Error(
                ProviderSourcePreferenceErrorCode.InvalidText,
                path,
                $"Text must contain 1 to {maximumLength} safe characters."));
        }

        return normalized;
    }

    internal static ProviderSourcePreferences Normalize(ProviderSourcePreferences preferences) => new(
        preferences.Overrides
            .Select(item => item with
            {
                Owner = NormalizeOwner(item.Owner),
                Endpoint = ParseHttpsEndpoint(item.Endpoint.OriginalString),
            })
            .OrderBy(item => item.Owner.LanguageToolId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Owner.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Owner.SlotId, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        preferences.CustomSources
            .Select(item => item with
            {
                Owner = NormalizeOwner(item.Owner),
                DisplayName = NormalizeText(item.DisplayName, 96, "displayName"),
                Endpoint = ParseHttpsEndpoint(item.Endpoint.OriginalString),
                Purpose = NormalizeText(item.Purpose, 256, "purpose"),
            })
            .OrderBy(item => item.Owner.LanguageToolId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Owner.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Owner.SlotId, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    internal static bool OwnersEqual(ProviderSourceOwner left, ProviderSourceOwner right) =>
        left.LanguageToolId.Equals(right.LanguageToolId, StringComparison.OrdinalIgnoreCase)
        && left.ProviderId.Equals(right.ProviderId, StringComparison.OrdinalIgnoreCase)
        && left.SlotId.Equals(right.SlotId, StringComparison.OrdinalIgnoreCase);

    internal static bool TryFindProvider(
        LanguageCatalog catalog,
        ProviderSourceOwner owner,
        string path,
        ICollection<ProviderSourcePreferenceError> errors,
        out LanguageToolProviderDefinition? provider)
    {
        provider = null;
        if (!catalog.TryGetTool(owner.LanguageToolId, out LanguageToolDefinition? tool))
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.CatalogToolNotFound,
                $"{path}.languageToolId",
                "The source references a language tool that is not present in the effective language catalog."));
            return false;
        }

        provider = tool!.Providers.FirstOrDefault(candidate => candidate.Id.Equals(
            owner.ProviderId,
            StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.CatalogProviderNotFound,
                $"{path}.providerId",
                "The source references a provider that is not present on the language tool."));
            return false;
        }

        return true;
    }

    internal static ProviderSourcePreferenceError Error(
        ProviderSourcePreferenceErrorCode code,
        string path,
        string message) => new(code, path, message);

    private static void ValidateOwner(
        ProviderSourceOwner? owner,
        string path,
        ICollection<ProviderSourcePreferenceError> errors)
    {
        if (owner is null)
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.InvalidIdentifier,
                path,
                "The provider source owner is required."));
            return;
        }

        ValidateIdentifier(owner.LanguageToolId, $"{path}.languageToolId", errors);
        ValidateIdentifier(owner.ProviderId, $"{path}.providerId", errors);
        ValidateIdentifier(owner.SlotId, $"{path}.slotId", errors);
    }

    private static void ValidateIdentifier(
        string? value,
        string path,
        ICollection<ProviderSourcePreferenceError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierPattern().IsMatch(value))
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.InvalidIdentifier,
                path,
                "An identifier must be a lowercase catalog-compatible ID of at most 64 characters."));
        }
    }

    private static void ValidateText(
        string? value,
        int maximumLength,
        string path,
        ICollection<ProviderSourcePreferenceError> errors)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            errors.Add(Error(
                ProviderSourcePreferenceErrorCode.InvalidText,
                path,
                $"Text must contain 1 to {maximumLength} safe characters."));
        }
    }

    private static void ValidateEndpoint(
        Uri? endpoint,
        string path,
        ICollection<ProviderSourcePreferenceError> errors)
    {
        try
        {
            _ = ParseHttpsEndpoint(endpoint?.OriginalString ?? string.Empty, path);
        }
        catch (ProviderSourcePreferenceException exception)
        {
            errors.Add(exception.Error);
        }
    }

    private static string OwnerIdentity(ProviderSourceOwner owner) =>
        $"{owner.LanguageToolId}\n{owner.ProviderId}\n{owner.SlotId}";

    private static string ProviderIdentity(ProviderSourceOwner owner) =>
        $"{owner.LanguageToolId}/{owner.ProviderId}";

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

public static class ProviderSourceResolver
{
    public static ProviderSourceResolutionResult Resolve(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences,
        ProviderSourceOwner owner)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(owner);
        ProviderSourcePreferenceValidationResult validation =
            ProviderSourcePreferenceValidator.Validate(catalog, preferences);
        if (!validation.Success)
        {
            return new ProviderSourceResolutionResult(null, validation.Errors);
        }

        ProviderSourceOwner normalizedOwner = ProviderSourcePreferenceValidator.NormalizeOwner(owner);
        List<ProviderSourcePreferenceError> errors = [];
        if (!ProviderSourcePreferenceValidator.TryFindProvider(
                catalog,
                normalizedOwner,
                "owner",
                errors,
                out LanguageToolProviderDefinition? provider))
        {
            return new ProviderSourceResolutionResult(null, errors);
        }

        ProviderMirrorSlotDefinition? slot = provider!.MirrorSlots.FirstOrDefault(candidate =>
            candidate.Id.Equals(normalizedOwner.SlotId, StringComparison.OrdinalIgnoreCase));
        if (slot is not null)
        {
            return new ProviderSourceResolutionResult(
                ResolveCatalogSlot(preferences, normalizedOwner, slot),
                []);
        }

        CustomProviderSource? custom = preferences.CustomSources.FirstOrDefault(candidate =>
            ProviderSourcePreferenceValidator.OwnersEqual(candidate.Owner, normalizedOwner));
        if (custom is not null)
        {
            return new ProviderSourceResolutionResult(ResolveCustom(custom), []);
        }

        return new ProviderSourceResolutionResult(
            null,
            [ProviderSourcePreferenceValidator.Error(
                ProviderSourcePreferenceErrorCode.CatalogSlotNotFound,
                "owner.slotId",
                "The source slot is not present in the effective language catalog or custom preferences.")]);
    }

    public static ProviderSourceListResolutionResult ResolveProvider(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences,
        string languageToolId,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(preferences);
        ProviderSourcePreferenceValidationResult validation =
            ProviderSourcePreferenceValidator.Validate(catalog, preferences);
        if (!validation.Success)
        {
            return new ProviderSourceListResolutionResult([], validation.Errors);
        }

        ProviderSourceOwner providerOwner = ProviderSourcePreferenceValidator.NormalizeOwner(
            new ProviderSourceOwner(languageToolId, providerId, "source"));
        List<ProviderSourcePreferenceError> errors = [];
        if (!ProviderSourcePreferenceValidator.TryFindProvider(
                catalog,
                providerOwner,
                "provider",
                errors,
                out LanguageToolProviderDefinition? provider))
        {
            return new ProviderSourceListResolutionResult([], errors);
        }

        List<ResolvedProviderSource> sources = provider!.MirrorSlots
            .Select(slot => ResolveCatalogSlot(
                preferences,
                providerOwner with { SlotId = slot.Id },
                slot))
            .ToList();
        sources.AddRange(preferences.CustomSources
            .Where(source => source.Owner.LanguageToolId.Equals(
                    providerOwner.LanguageToolId,
                    StringComparison.OrdinalIgnoreCase)
                && source.Owner.ProviderId.Equals(
                    providerOwner.ProviderId,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source.Owner.SlotId, StringComparer.OrdinalIgnoreCase)
            .Select(ResolveCustom));
        return new ProviderSourceListResolutionResult(sources, []);
    }

    private static ResolvedProviderSource ResolveCatalogSlot(
        ProviderSourcePreferences preferences,
        ProviderSourceOwner owner,
        ProviderMirrorSlotDefinition slot)
    {
        ProviderMirrorSlotOverride? item = preferences.Overrides.FirstOrDefault(candidate =>
            ProviderSourcePreferenceValidator.OwnersEqual(candidate.Owner, owner));
        return new ResolvedProviderSource(
            owner,
            slot.DisplayName,
            item?.Endpoint ?? slot.DefaultEndpoint,
            slot.EndpointKind,
            slot.Purpose,
            item is null
                ? ProviderSourceOrigin.CatalogDefault
                : ProviderSourceOrigin.UserOverride,
            true);
    }

    private static ResolvedProviderSource ResolveCustom(CustomProviderSource source) => new(
        source.Owner,
        source.DisplayName,
        source.Endpoint,
        source.EndpointKind,
        source.Purpose,
        ProviderSourceOrigin.Custom,
        source.IsEnabled);
}
