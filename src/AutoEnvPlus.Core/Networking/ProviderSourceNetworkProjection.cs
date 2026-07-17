using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.Core.Networking;

public sealed record ProviderSourceNetworkMapping(
    string NetworkToolId,
    ProviderSourceOwner CatalogOwner,
    ProviderMirrorEndpointKind EndpointKind);

public enum ProviderSourceNetworkProjectionErrorCode
{
    InvalidNetworkSettings,
    InvalidSourcePreferences,
    CatalogMappingUnavailable,
    EndpointKindMismatch,
    MultipleEnabledCustomSources,
}

public sealed record ProviderSourceNetworkProjectionError(
    ProviderSourceNetworkProjectionErrorCode Code,
    string NetworkToolId,
    string Message);

public sealed record ProviderSourceNetworkSelection(
    string NetworkToolId,
    ProviderSourceOwner SourceOwner,
    Uri Endpoint,
    ProviderSourceOrigin Origin);

public sealed record ProviderSourceNetworkProjectionResult(
    NetworkSettings? Settings,
    IReadOnlyList<ProviderSourceNetworkSelection> Selections,
    IReadOnlyList<ProviderSourceNetworkProjectionError> Errors)
{
    public bool Success => Settings is not null && Errors.Count == 0;
}

/// <summary>
/// Projects Provider-owned package sources onto existing tool network settings.
/// Only the Mirror member is replaced; proxy and bypass settings remain owned by NetworkSettings.
/// </summary>
public static class ProviderSourceNetworkProjection
{
    private static readonly ProviderSourceNetworkMapping[] KnownMappings =
    [
        Mapping(
            NetworkToolIds.Pip,
            "pip",
            "bundled",
            "python-package-index",
            ProviderMirrorEndpointKind.PyPi),
        Mapping(
            NetworkToolIds.Npm,
            "npm",
            "bundled",
            "npm-registry",
            ProviderMirrorEndpointKind.Npm),
        Mapping(
            NetworkToolIds.Pnpm,
            "pnpm",
            "official-distribution",
            "npm-registry",
            ProviderMirrorEndpointKind.Npm),
        Mapping(
            NetworkToolIds.Yarn,
            "yarn",
            "official-distribution",
            "npm-registry",
            ProviderMirrorEndpointKind.Npm),
        Mapping(
            NetworkToolIds.NuGet,
            "nuget-cli",
            "official-distribution",
            "nuget-v3",
            ProviderMirrorEndpointKind.NuGet),
        Mapping(
            NetworkToolIds.Maven,
            "apache-maven",
            "official-distribution",
            "maven-central",
            ProviderMirrorEndpointKind.Maven),
        Mapping(
            NetworkToolIds.Gradle,
            "gradle",
            "official-distribution",
            "gradle-distributions",
            ProviderMirrorEndpointKind.Gradle),
    ];

    public static IReadOnlyList<ProviderSourceNetworkMapping> Mappings { get; } =
        Array.AsReadOnly(KnownMappings);

    public static ProviderSourceNetworkProjectionResult Project(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences,
        NetworkSettings networkSettings)
    {
        return ProjectCore(
            catalog,
            preferences,
            networkSettings,
            KnownMappings);
    }

    public static ProviderSourceNetworkProjectionResult ProjectForTools(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences,
        NetworkSettings networkSettings,
        IEnumerable<string> networkToolIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(networkSettings);
        ArgumentNullException.ThrowIfNull(networkToolIds);
        HashSet<string> requestedTools = new(StringComparer.OrdinalIgnoreCase);
        foreach (string networkToolId in networkToolIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(networkToolId);
            requestedTools.Add(networkToolId.Trim());
        }

        ProviderSourcePreferenceValidationResult structureValidation =
            ProviderSourcePreferenceValidator.ValidateStructure(preferences);
        if (!structureValidation.Success)
        {
            return new ProviderSourceNetworkProjectionResult(
                null,
                [],
                structureValidation.Errors.Select(error => new ProviderSourceNetworkProjectionError(
                    ProviderSourceNetworkProjectionErrorCode.InvalidSourcePreferences,
                    string.Empty,
                    $"Provider source preference {error.Path}: {error.Message}"))
                    .ToArray());
        }

        ProviderSourceNetworkMapping[] mappings = KnownMappings
            .Where(mapping => requestedTools.Contains(mapping.NetworkToolId))
            .ToArray();
        ProviderSourcePreferences scopedPreferences = ScopePreferences(preferences, mappings);
        return ProjectCore(catalog, scopedPreferences, networkSettings, mappings);
    }

    private static ProviderSourceNetworkProjectionResult ProjectCore(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences,
        NetworkSettings networkSettings,
        IReadOnlyList<ProviderSourceNetworkMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(networkSettings);
        ArgumentNullException.ThrowIfNull(mappings);
        ProviderSourcePreferenceValidationResult preferenceValidation =
            ProviderSourcePreferenceValidator.Validate(catalog, preferences);
        if (!preferenceValidation.Success)
        {
            return new ProviderSourceNetworkProjectionResult(
                null,
                [],
                preferenceValidation.Errors.Select(error => new ProviderSourceNetworkProjectionError(
                    ProviderSourceNetworkProjectionErrorCode.InvalidSourcePreferences,
                    string.Empty,
                    $"Provider source preference {error.Path}: {error.Message}"))
                    .ToArray());
        }

        List<NetworkSettingsError> networkErrors = [];
        NetworkSettings? normalized = NetworkSettingsResolver.Normalize(
            networkSettings,
            networkErrors);
        if (normalized is null || networkErrors.Count > 0)
        {
            return new ProviderSourceNetworkProjectionResult(
                null,
                [],
                networkErrors.Select(error => new ProviderSourceNetworkProjectionError(
                    ProviderSourceNetworkProjectionErrorCode.InvalidNetworkSettings,
                    string.Empty,
                    $"Network settings {error.Path}: {error.Message}"))
                    .ToArray());
        }

        Dictionary<string, ToolNetworkSettings> tools = normalized.Tools!
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        List<ProviderSourceNetworkSelection> selections = [];
        List<ProviderSourceNetworkProjectionError> errors = [];
        foreach (ProviderSourceNetworkMapping mapping in mappings)
        {
            ProviderSourceNetworkSelection? selection = SelectSource(
                catalog,
                preferences,
                mapping,
                errors);
            if (selection is null)
            {
                continue;
            }

            tools.TryGetValue(mapping.NetworkToolId, out ToolNetworkSettings? existing);
            tools[mapping.NetworkToolId] = new ToolNetworkSettings(
                existing?.HttpProxy ?? NetworkEndpointOverride.Inherit,
                existing?.HttpsProxy ?? NetworkEndpointOverride.Inherit,
                NetworkEndpointOverride.Custom(selection.Endpoint.AbsoluteUri));
            selections.Add(selection);
        }

        if (errors.Count > 0)
        {
            return new ProviderSourceNetworkProjectionResult(null, [], errors);
        }

        return new ProviderSourceNetworkProjectionResult(
            new NetworkSettings(normalized.Global, tools),
            selections,
            []);
    }

    private static ProviderSourcePreferences ScopePreferences(
        ProviderSourcePreferences preferences,
        IReadOnlyList<ProviderSourceNetworkMapping> mappings) => new(
        preferences.Overrides.Where(item => mappings.Any(mapping =>
            ProviderSourcePreferenceValidator.OwnersEqual(
                item.Owner,
                mapping.CatalogOwner))).ToArray(),
        preferences.CustomSources.Where(item => mappings.Any(mapping =>
            item.EndpointKind == mapping.EndpointKind
            && item.Owner.LanguageToolId.Equals(
                mapping.CatalogOwner.LanguageToolId,
                StringComparison.OrdinalIgnoreCase)
            && item.Owner.ProviderId.Equals(
                mapping.CatalogOwner.ProviderId,
                StringComparison.OrdinalIgnoreCase))).ToArray());

    private static ProviderSourceNetworkSelection? SelectSource(
        LanguageCatalog catalog,
        ProviderSourcePreferences preferences,
        ProviderSourceNetworkMapping mapping,
        ICollection<ProviderSourceNetworkProjectionError> errors)
    {
        CustomProviderSource[] enabledCustomSources = preferences.CustomSources
            .Where(source => source.IsEnabled
                && source.EndpointKind == mapping.EndpointKind
                && source.Owner.LanguageToolId.Equals(
                    mapping.CatalogOwner.LanguageToolId,
                    StringComparison.OrdinalIgnoreCase)
                && source.Owner.ProviderId.Equals(
                    mapping.CatalogOwner.ProviderId,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (enabledCustomSources.Length > 1)
        {
            errors.Add(new ProviderSourceNetworkProjectionError(
                ProviderSourceNetworkProjectionErrorCode.MultipleEnabledCustomSources,
                mapping.NetworkToolId,
                $"More than one custom {mapping.EndpointKind} source is enabled for "
                    + $"{mapping.CatalogOwner.LanguageToolId}/{mapping.CatalogOwner.ProviderId}. "
                    + "Enable only one source in the language tool page."));
            return null;
        }

        if (enabledCustomSources.Length == 1)
        {
            CustomProviderSource custom = enabledCustomSources[0];
            return new ProviderSourceNetworkSelection(
                mapping.NetworkToolId,
                custom.Owner,
                custom.Endpoint,
                ProviderSourceOrigin.Custom);
        }

        ProviderSourceResolutionResult resolved = ProviderSourceResolver.Resolve(
            catalog,
            preferences,
            mapping.CatalogOwner);
        if (!resolved.Success || resolved.Source?.EffectiveEndpoint is null)
        {
            string detail = resolved.Errors.Count == 0
                ? "The catalog source is disabled or unavailable."
                : string.Join(" ", resolved.Errors.Select(error => error.Message));
            errors.Add(new ProviderSourceNetworkProjectionError(
                ProviderSourceNetworkProjectionErrorCode.CatalogMappingUnavailable,
                mapping.NetworkToolId,
                detail));
            return null;
        }

        if (resolved.Source.EndpointKind != mapping.EndpointKind)
        {
            errors.Add(new ProviderSourceNetworkProjectionError(
                ProviderSourceNetworkProjectionErrorCode.EndpointKindMismatch,
                mapping.NetworkToolId,
                "The catalog source endpoint kind does not match the execution mapping."));
            return null;
        }

        return new ProviderSourceNetworkSelection(
            mapping.NetworkToolId,
            resolved.Source.Owner,
            resolved.Source.EffectiveEndpoint,
            resolved.Source.Origin);
    }

    private static ProviderSourceNetworkMapping Mapping(
        string networkToolId,
        string languageToolId,
        string providerId,
        string slotId,
        ProviderMirrorEndpointKind endpointKind) => new(
            networkToolId,
            new ProviderSourceOwner(languageToolId, providerId, slotId),
            endpointKind);
}
