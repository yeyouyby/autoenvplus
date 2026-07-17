using System.Collections.Frozen;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Providers.DotNet;
using AutoEnvPlus.Core.Providers.Java;
using AutoEnvPlus.Core.Providers.NodeJs;
using AutoEnvPlus.Core.Providers.Python;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Languages;

public enum LanguageToolProviderBridgeKind
{
    Official,
    WinGet,
    DeclarativePlugin,
}

public sealed record LanguageToolRuntimeBridgeDefinition(
    RuntimeKind RuntimeKind,
    string ToolId,
    IReadOnlyList<string> LanguageIds,
    IReadOnlySet<string> BuiltInProviderIds,
    string WinGetProviderId);

public sealed record LanguageToolRuntimeBinding(
    RuntimeKind RuntimeKind,
    string ToolId,
    IReadOnlyList<string> LanguageIds,
    string ProviderId,
    LanguageToolProviderBridgeKind ProviderKind);

public static class LanguageToolRuntimeBridge
{
    public const string WindowsSdkToolId = "windows-sdk";

    private static readonly FrozenDictionary<RuntimeKind, LanguageToolRuntimeBridgeDefinition>
        ByRuntimeKind = CreateDefinitions().ToFrozenDictionary(definition => definition.RuntimeKind);

    public static IReadOnlyCollection<LanguageToolRuntimeBridgeDefinition> Definitions =>
        ByRuntimeKind.Values;

    public static LanguageToolRuntimeBridgeDefinition Get(RuntimeKind kind) =>
        ByRuntimeKind.TryGetValue(kind, out LanguageToolRuntimeBridgeDefinition? definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(kind));

    public static LanguageToolRuntimeBinding Resolve(RuntimeKind kind, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        LanguageToolRuntimeBridgeDefinition definition = Get(kind);
        LanguageToolProviderBridgeKind providerKind;
        if (providerId.StartsWith(
                RuntimeProviderPluginIds.Prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!RuntimeProviderPluginIds.TryGetPluginId(providerId, out _))
            {
                throw new ArgumentException("The declarative plugin provider ID is invalid.", nameof(providerId));
            }

            providerKind = LanguageToolProviderBridgeKind.DeclarativePlugin;
        }
        else if (definition.BuiltInProviderIds.Contains(providerId))
        {
            providerKind = LanguageToolProviderBridgeKind.Official;
        }
        else if (definition.WinGetProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        {
            providerKind = LanguageToolProviderBridgeKind.WinGet;
        }
        else
        {
            throw new ArgumentException(
                "The provider ID is not registered for this runtime kind.",
                nameof(providerId));
        }

        return new LanguageToolRuntimeBinding(
            kind,
            definition.ToolId,
            definition.LanguageIds,
            providerId,
            providerKind);
    }

    public static LanguageToolRuntimeBridgeDefinition Get(ToolchainComponent component) =>
        Get(ToolchainRuntimeProviderPolicy.GetRuntimeKind(component));

    public static string GetToolId(WindowsSdkInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return WindowsSdkToolId;
    }

    private static IEnumerable<LanguageToolRuntimeBridgeDefinition> CreateDefinitions()
    {
        yield return Definition(
            RuntimeKind.Python,
            "cpython",
            ["python"],
            [PythonOrgCatalogProvider.ProviderName],
            "winget:python.python.3");
        yield return Definition(
            RuntimeKind.NodeJs,
            "nodejs",
            ["javascript", "typescript"],
            [NodeJsCatalogProvider.ProviderName],
            "winget:openjs.nodejs");
        yield return Definition(
            RuntimeKind.Java,
            "eclipse-temurin",
            ["java"],
            [AdoptiumCatalogProvider.ProviderName],
            "winget:eclipseadoptium.temurin");
        yield return Definition(
            RuntimeKind.DotNet,
            "dotnet-sdk",
            ["csharp", "fsharp", "visual-basic"],
            [DotNetSdkCatalogProvider.ProviderName],
            "winget:microsoft.dotnet.sdk");
        yield return Definition(
            RuntimeKind.Msvc,
            "msvc-build-tools",
            ["c", "cpp"],
            [],
            "winget:microsoft.visualstudio.2022.buildtools");
        yield return Definition(
            RuntimeKind.Llvm,
            "clang",
            ["c", "cpp", "objective-c"],
            [],
            "winget:llvm.llvm");
        yield return Definition(
            RuntimeKind.Mingw,
            "gcc",
            ["c", "cpp", "fortran", "ada"],
            [],
            "winget:brechtsanders.winlibs.posix.ucrt");
        yield return Definition(
            RuntimeKind.CMake,
            "cmake",
            ["c", "cpp"],
            [],
            "winget:kitware.cmake");
        yield return Definition(
            RuntimeKind.Ninja,
            "ninja",
            ["c", "cpp"],
            [],
            "winget:ninja-build.ninja");
    }

    private static LanguageToolRuntimeBridgeDefinition Definition(
        RuntimeKind kind,
        string toolId,
        IReadOnlyList<string> languageIds,
        IEnumerable<string> providerIds,
        string winGetProviderId) =>
        new(
            kind,
            toolId,
            languageIds,
            providerIds.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            winGetProviderId);
}
