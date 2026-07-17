using System.Collections.Frozen;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Networking;

public static class NetworkToolIds
{
    public const string RuntimePython = "runtime-python";
    public const string RuntimeNode = "runtime-node";
    public const string RuntimeJava = "runtime-java";
    public const string RuntimeDotNet = "runtime-dotnet";
    public const string RuntimeCpp = "runtime-cpp";
    public const string Downloads = "downloads";
    public const string Pip = "pip";
    public const string Npm = "npm";
    public const string Pnpm = "pnpm";
    public const string Yarn = "yarn";
    public const string NuGet = "nuget";
    public const string Maven = "maven";
    public const string Gradle = "gradle";
    public const string Vcpkg = "vcpkg";
    public const string Conan = "conan";

    private static readonly FrozenSet<string> Supported = new[]
    {
        RuntimePython,
        RuntimeNode,
        RuntimeJava,
        RuntimeDotNet,
        RuntimeCpp,
        Downloads,
        Pip,
        Npm,
        Pnpm,
        Yarn,
        NuGet,
        Maven,
        Gradle,
        Vcpkg,
        Conan,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> All { get; } = Supported;

    public static bool IsSupported(string? toolId) =>
        toolId is not null && Supported.Contains(toolId);

    public static bool TryGetRuntimeScope(RuntimeKind kind, out string toolId)
    {
        toolId = kind switch
        {
            RuntimeKind.Python => RuntimePython,
            RuntimeKind.NodeJs => RuntimeNode,
            RuntimeKind.Java => RuntimeJava,
            RuntimeKind.DotNet => RuntimeDotNet,
            RuntimeKind.Msvc or RuntimeKind.Llvm or RuntimeKind.Mingw
                or RuntimeKind.CMake or RuntimeKind.Ninja => RuntimeCpp,
            _ => string.Empty,
        };
        return toolId.Length > 0;
    }
}

public enum NetworkEndpointOverrideMode
{
    Inherit,
    Disabled,
    Custom,
}

public sealed record NetworkEndpointOverride(
    NetworkEndpointOverrideMode Mode,
    string? Value = null)
{
    public static NetworkEndpointOverride Inherit { get; } = new(
        NetworkEndpointOverrideMode.Inherit);

    public static NetworkEndpointOverride Disabled { get; } = new(
        NetworkEndpointOverrideMode.Disabled);

    public static NetworkEndpointOverride Custom(string value) => new(
        NetworkEndpointOverrideMode.Custom,
        value);

    public override string ToString() => $"NetworkEndpointOverride {{ Mode = {Mode} }}";
}

public sealed record GlobalNetworkSettings(
    string? HttpProxy = null,
    string? HttpsProxy = null,
    IReadOnlyList<string>? NoProxy = null,
    string? Mirror = null)
{
    public override string ToString() =>
        $"GlobalNetworkSettings {{ HttpProxy = {Describe(HttpProxy)}, "
        + $"HttpsProxy = {Describe(HttpsProxy)}, NoProxyCount = {NoProxy?.Count ?? 0}, "
        + $"Mirror = {Describe(Mirror)} }}";

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "not configured" : "configured";
}

public sealed record ToolNetworkSettings(
    NetworkEndpointOverride? HttpProxy = null,
    NetworkEndpointOverride? HttpsProxy = null,
    NetworkEndpointOverride? Mirror = null)
{
    public override string ToString() =>
        $"ToolNetworkSettings {{ HttpProxy = {Describe(HttpProxy)}, "
        + $"HttpsProxy = {Describe(HttpsProxy)}, Mirror = {Describe(Mirror)} }}";

    private static NetworkEndpointOverrideMode Describe(NetworkEndpointOverride? value) =>
        value?.Mode ?? NetworkEndpointOverrideMode.Inherit;
}

public sealed record NetworkSettings(
    GlobalNetworkSettings? Global = null,
    IReadOnlyDictionary<string, ToolNetworkSettings>? Tools = null)
{
    public static NetworkSettings Default { get; } = new(
        new GlobalNetworkSettings(NoProxy: []),
        new Dictionary<string, ToolNetworkSettings>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    public override string ToString() =>
        $"NetworkSettings {{ Global = {Global ?? new GlobalNetworkSettings()}, "
        + $"ToolOverrideCount = {Tools?.Count ?? 0} }}";
}

public sealed record EffectiveNetworkSettings(
    string ToolId,
    Uri? HttpProxy,
    Uri? HttpsProxy,
    IReadOnlyList<string> NoProxy,
    Uri? Mirror)
{
    public override string ToString() =>
        $"EffectiveNetworkSettings {{ ToolId = {ToolId}, "
        + $"HttpProxy = {Describe(HttpProxy)}, HttpsProxy = {Describe(HttpsProxy)}, "
        + $"NoProxyCount = {NoProxy.Count}, Mirror = {Describe(Mirror)} }}";

    private static string Describe(Uri? value) => value is null ? "disabled" : "configured";
}

public enum NetworkSettingsErrorCode
{
    MalformedJson,
    DocumentTooLarge,
    UnsupportedSchema,
    InvalidDocument,
    UnsafePath,
    UnsupportedTool,
    InvalidOverride,
    InvalidProxyUri,
    InvalidMirrorUri,
    InvalidNoProxyEntry,
    IoFailure,
}

public sealed record NetworkSettingsError(
    NetworkSettingsErrorCode Code,
    string Path,
    string Message);

public sealed record NetworkSettingsLoadResult(
    bool Success,
    NetworkSettings? Settings,
    IReadOnlyList<NetworkSettingsError> Errors);

public sealed record NetworkSettingsSaveResult(
    bool Success,
    NetworkSettings? Settings,
    IReadOnlyList<NetworkSettingsError> Errors);

public sealed record NetworkSettingsResolutionResult(
    bool Success,
    EffectiveNetworkSettings? EffectiveSettings,
    IReadOnlyList<NetworkSettingsError> Errors);
