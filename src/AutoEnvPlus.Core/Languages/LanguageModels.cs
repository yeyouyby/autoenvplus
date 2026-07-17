using System.Collections.Frozen;

namespace AutoEnvPlus.Core.Languages;

public enum LanguageToolRole
{
    Compiler,
    Interpreter,
    Runtime,
    Sdk,
    PackageManager,
    Build,
    Debugger,
    Formatter,
    Linter,
    VersionManager,
    VirtualEnvironment,
    Repl,
    LanguageServer,
    TestRunner,
}

public enum LanguageToolWindowsSupport
{
    Native,
    Wsl,
    Conditional,
    Unsupported,
}

public enum LanguageToolProviderDistributionKind
{
    ManagedArchive,
    Manual,
    System,
    WinGet,
    External,
}

public enum ProviderMirrorEndpointKind
{
    PyPi,
    Npm,
    NuGet,
    Maven,
    Gradle,
    GoProxy,
    Crates,
    RubyGems,
    Composer,
    GenericDownload,
}

public sealed record LanguageDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> FileExtensions,
    bool DefaultEnabled,
    Uri Homepage);

public sealed record LanguageToolCapabilities(
    bool Discover,
    bool Install,
    bool VersionSwitch,
    bool ProjectPin,
    bool SessionActivation,
    bool PackageManagement,
    bool VirtualEnvironment,
    bool CacheManagement,
    bool MirrorConfiguration,
    bool Debug,
    bool Format,
    bool Lint);

public sealed record ProviderMirrorSlotDefinition(
    string Id,
    string DisplayName,
    Uri DefaultEndpoint,
    ProviderMirrorEndpointKind EndpointKind,
    string Purpose,
    bool UserOverridable);

public sealed record LanguageToolProviderDefinition(
    string Id,
    string DisplayName,
    LanguageToolProviderDistributionKind DistributionKind,
    bool ManagedInstallSupported,
    IReadOnlyList<ProviderMirrorSlotDefinition> MirrorSlots);

public sealed record LanguageToolDefinition(
    string Id,
    string DisplayName,
    string Vendor,
    Uri Homepage,
    string License,
    IReadOnlyList<string> LanguageIds,
    IReadOnlySet<LanguageToolRole> Roles,
    IReadOnlyList<string> DiscoveryCommands,
    LanguageToolWindowsSupport WindowsSupport,
    LanguageToolCapabilities Capabilities,
    IReadOnlyList<LanguageToolProviderDefinition> Providers);

public sealed class LanguageCatalog
{
    private readonly FrozenDictionary<string, LanguageDefinition> _languagesById;
    private readonly FrozenDictionary<string, LanguageToolDefinition> _toolsById;
    private readonly FrozenDictionary<string, LanguageToolProviderProfile> _providerProfilesById;

    public LanguageCatalog(
        IEnumerable<LanguageDefinition> languages,
        IEnumerable<LanguageToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(tools);
        LanguageDefinition[] languageItems = languages.ToArray();
        LanguageToolDefinition[] toolItems = tools.ToArray();
        _languagesById = languageItems.ToFrozenDictionary(
            language => language.Id,
            StringComparer.OrdinalIgnoreCase);
        _toolsById = toolItems.ToFrozenDictionary(
            tool => tool.Id,
            StringComparer.OrdinalIgnoreCase);
        LanguageToolProviderProfile[] providerProfiles = toolItems
            .SelectMany(tool => tool.Providers.Select(provider =>
                LanguageToolProviderProfile.Create(tool, provider)))
            .ToArray();
        _providerProfilesById = providerProfiles.ToFrozenDictionary(
            profile => profile.Identity.ScopedId,
            StringComparer.OrdinalIgnoreCase);
        Languages = Array.AsReadOnly(languageItems);
        Tools = Array.AsReadOnly(toolItems);
        ProviderProfiles = Array.AsReadOnly(providerProfiles);
    }

    public IReadOnlyList<LanguageDefinition> Languages { get; }

    public IReadOnlyList<LanguageToolDefinition> Tools { get; }

    public IReadOnlyList<LanguageToolProviderProfile> ProviderProfiles { get; }

    public IReadOnlyList<LanguageDefinition> DefaultLanguages => Languages
        .Where(language => language.DefaultEnabled)
        .ToArray();

    public bool TryGetLanguage(string id, out LanguageDefinition? language) =>
        _languagesById.TryGetValue(id, out language);

    public bool TryGetTool(string id, out LanguageToolDefinition? tool) =>
        _toolsById.TryGetValue(id, out tool);

    public bool TryGetProviderProfile(
        string languageToolId,
        string providerId,
        out LanguageToolProviderProfile? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providerProfilesById.TryGetValue(
            new LanguageToolProviderIdentity(languageToolId, providerId).ScopedId,
            out profile);
    }

    public IReadOnlyList<LanguageToolProviderProfile> GetProviderProfilesForTool(
        string languageToolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageToolId);
        return ProviderProfiles.Where(profile => profile.Identity.LanguageToolId.Equals(
            languageToolId,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public IReadOnlyList<LanguageToolDefinition> GetToolsForLanguage(string languageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        return Tools.Where(tool => tool.LanguageIds.Contains(
            languageId,
            StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
