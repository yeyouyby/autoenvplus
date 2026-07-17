using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Plugins;

public sealed class RuntimeProviderPluginRegistry
{
    private readonly RuntimeProviderPluginStore _store;
    private readonly IReadOnlySet<string> _builtInProviderIds;

    public RuntimeProviderPluginRegistry(
        RuntimeProviderPluginStore store,
        IEnumerable<string>? additionalBuiltInProviderIds = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        HashSet<string> builtInIds = new(
            RuntimeProviderPluginIds.BuiltInProviderIds,
            StringComparer.OrdinalIgnoreCase);
        if (additionalBuiltInProviderIds is not null)
        {
            foreach (string id in additionalBuiltInProviderIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new ArgumentException(
                        "Built-in runtime provider IDs cannot be empty.",
                        nameof(additionalBuiltInProviderIds));
                }

                builtInIds.Add(id.Trim());
            }
        }

        _builtInProviderIds = builtInIds;
    }

    public async Task<IReadOnlyList<DeclarativeRuntimeCatalogProvider>>
        GetEnabledProvidersAsync(CancellationToken cancellationToken = default)
    {
        RuntimeProviderPluginListResult result = await _store.ListAsync(
            cancellationToken).ConfigureAwait(false);
        RuntimeProviderPluginError? fatalError = result.Errors.FirstOrDefault(error =>
            error.IsEnabled || error.PluginId is null);
        if (fatalError is not null)
        {
            throw new RuntimeProviderPluginException(
                fatalError.Code,
                "An enabled runtime provider plugin or its activation state is invalid.");
        }

        List<DeclarativeRuntimeCatalogProvider> providers = [];
        foreach (RuntimeProviderPluginDescriptor descriptor in result.Plugins.Where(
                     plugin => plugin.IsEnabled))
        {
            if (_builtInProviderIds.Contains(descriptor.Id)
                || _builtInProviderIds.Contains(descriptor.ProviderId))
            {
                throw new RuntimeProviderPluginException(
                    RuntimeProviderPluginErrorCode.BuiltInProviderConflict,
                    "An enabled plugin conflicts with a built-in runtime provider ID.");
            }

            providers.Add(new DeclarativeRuntimeCatalogProvider(descriptor.Manifest));
        }

        return providers
            .OrderBy(provider => provider.LanguageToolId, StringComparer.Ordinal)
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<DeclarativeRuntimeCatalogProvider?> ResolveByIdAsync(
        string providerIdOrPluginId,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeProviderPluginIds.TryGetPluginId(
                providerIdOrPluginId,
                out string pluginId))
        {
            return null;
        }

        IReadOnlyList<DeclarativeRuntimeCatalogProvider> providers =
            await GetEnabledProvidersAsync(cancellationToken).ConfigureAwait(false);
        return providers.FirstOrDefault(provider => provider.PluginId.Equals(
            pluginId,
            StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<DeclarativeRuntimeCatalogProvider>> ResolveByKindAsync(
        RuntimeKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        IReadOnlyList<DeclarativeRuntimeCatalogProvider> providers =
            await GetEnabledProvidersAsync(cancellationToken).ConfigureAwait(false);
        return providers.Where(provider => provider.Kind == kind).ToArray();
    }

    public async Task<IReadOnlyList<DeclarativeRuntimeCatalogProvider>> ResolveByToolAsync(
        string languageToolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageToolId);
        IReadOnlyList<DeclarativeRuntimeCatalogProvider> providers =
            await GetEnabledProvidersAsync(cancellationToken).ConfigureAwait(false);
        return providers.Where(provider => provider.LanguageToolId.Equals(
            languageToolId,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task<RuntimeRelease?> ResolveReleaseAsync(
        string providerIdOrPluginId,
        RuntimeVersion version,
        RuntimeArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (architecture is not RuntimeArchitecture.X86
            and not RuntimeArchitecture.X64
            and not RuntimeArchitecture.Arm64)
        {
            throw new ArgumentOutOfRangeException(nameof(architecture));
        }

        DeclarativeRuntimeCatalogProvider? provider = await ResolveByIdAsync(
            providerIdOrPluginId,
            cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return null;
        }

        IReadOnlyList<RuntimeRelease> releases = await provider.GetReleasesAsync(
            cancellationToken).ConfigureAwait(false);
        return releases.FirstOrDefault(release =>
            release.Version == version && release.Architecture == architecture);
    }
}
