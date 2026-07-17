using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Languages;

public enum ToolProviderAdapterKind
{
    MetadataOnly,
    ManagedArchive,
    WinGet,
}

public sealed record LanguageToolProviderIdentity(
    string LanguageToolId,
    string ProviderId)
{
    public string ScopedId => $"{LanguageToolId}/{ProviderId}";

    public override string ToString() => ScopedId;
}

public sealed record LanguageToolProviderCapabilities(
    bool Discover,
    bool ManagedInstall,
    bool VersionSwitch,
    bool ProjectPin,
    bool SessionActivation,
    bool PackageManagement,
    bool VirtualEnvironment,
    bool CacheManagement,
    bool SourceConfiguration,
    bool Diagnostics)
{
    public bool HasManagedAdapter => ManagedInstall;
}

public sealed record LanguageToolProviderProfile(
    LanguageToolProviderIdentity Identity,
    string ToolDisplayName,
    string ProviderDisplayName,
    ToolProviderAdapterKind AdapterKind,
    string? ExecutionProviderId,
    LanguageToolProviderCapabilities Capabilities,
    IReadOnlyList<ProviderMirrorSlotDefinition> SourceSlots)
{
    public bool IsMetadataOnly => AdapterKind == ToolProviderAdapterKind.MetadataOnly;

    public static LanguageToolProviderProfile Create(
        LanguageToolDefinition tool,
        LanguageToolProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(provider);
        LanguageToolRuntimeBridgeDefinition? bridge = LanguageToolRuntimeBridge.Definitions
            .FirstOrDefault(candidate => candidate.ToolId.Equals(
                tool.Id,
                StringComparison.OrdinalIgnoreCase));
        bool hasManagedAdapter = provider.ManagedInstallSupported && bridge is not null;
        ToolProviderAdapterKind adapterKind = hasManagedAdapter
            ? provider.DistributionKind switch
            {
                LanguageToolProviderDistributionKind.ManagedArchive =>
                    ToolProviderAdapterKind.ManagedArchive,
                LanguageToolProviderDistributionKind.WinGet => ToolProviderAdapterKind.WinGet,
                _ => ToolProviderAdapterKind.MetadataOnly,
            }
            : ToolProviderAdapterKind.MetadataOnly;
        string? executionProviderId = adapterKind switch
        {
            ToolProviderAdapterKind.ManagedArchive => bridge!.BuiltInProviderIds
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(),
            ToolProviderAdapterKind.WinGet => bridge!.WinGetProviderId,
            _ => null,
        };
        bool supportsSelection = hasManagedAdapter;
        LanguageToolProviderCapabilities capabilities = new(
            Discover: tool.Capabilities.Discover && tool.DiscoveryCommands.Count > 0,
            ManagedInstall: hasManagedAdapter,
            VersionSwitch: supportsSelection && tool.Capabilities.VersionSwitch,
            ProjectPin: supportsSelection && tool.Capabilities.ProjectPin,
            SessionActivation: supportsSelection && tool.Capabilities.SessionActivation,
            PackageManagement: tool.Capabilities.PackageManagement,
            VirtualEnvironment: tool.Capabilities.VirtualEnvironment,
            CacheManagement: tool.Capabilities.CacheManagement,
            SourceConfiguration: provider.MirrorSlots.Count > 0,
            Diagnostics: tool.Capabilities.Discover
                || provider.ManagedInstallSupported
                || provider.MirrorSlots.Count > 0);
        return new LanguageToolProviderProfile(
            new LanguageToolProviderIdentity(tool.Id, provider.Id),
            tool.DisplayName,
            provider.DisplayName,
            adapterKind,
            executionProviderId,
            capabilities,
            provider.MirrorSlots);
    }
}
