using System.Collections.Frozen;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Plugins;

public sealed record RuntimeProviderPluginManifest(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Vendor,
    Uri Homepage,
    string License,
    string LanguageToolId,
    RuntimeKind Kind,
    IReadOnlyList<RuntimeProviderPluginRelease> Releases)
{
    public string ProviderId => RuntimeProviderPluginIds.ToProviderId(Id);

    public override string ToString() =>
        $"{DisplayName} ({LanguageToolId}/{ProviderId}, {Kind})";
}

public sealed record RuntimeProviderPluginRelease(
    RuntimeVersion Version,
    IReadOnlyList<string> Channels,
    DateOnly? ReleaseDate,
    IReadOnlyList<RuntimeProviderPluginAsset> Assets)
{
    public override string ToString() => $"{Version} ({Assets.Count} assets)";
}

public sealed record RuntimeProviderPluginAsset(
    RuntimeArchitecture Architecture,
    string FileName,
    Uri DownloadUri,
    Uri ChecksumSourceUri,
    PackageHashAlgorithm HashAlgorithm,
    string PackageHash,
    string? ArchiveRoot,
    string ExpectedExecutableRelativePath)
{
    public bool ChecksumSourceMatchesDownload => ChecksumSourceUri.Equals(DownloadUri);

    public override string ToString() =>
        $"{FileName} ({Architecture}, {HashAlgorithm.DisplayName()})";
}

public sealed record RuntimeProviderPluginDescriptor(
    RuntimeProviderPluginManifest Manifest,
    string ManifestPath,
    bool IsEnabled)
{
    public string Id => Manifest.Id;

    public string ProviderId => Manifest.ProviderId;

    public string LanguageToolId => Manifest.LanguageToolId;

    public override string ToString() =>
        $"{Manifest.DisplayName} ({ProviderId}, {(IsEnabled ? "enabled" : "disabled")})";
}

public sealed class RuntimeProviderPluginImportPreview
{
    private readonly byte[] _normalizedManifest;

    internal RuntimeProviderPluginImportPreview(
        RuntimeProviderPluginManifest manifest,
        string sourcePath,
        byte[] normalizedManifest)
    {
        Manifest = manifest;
        SourcePath = sourcePath;
        _normalizedManifest = (byte[])normalizedManifest.Clone();
        ReleaseCount = manifest.Releases.Count;
        AssetCount = manifest.Releases.Sum(release => release.Assets.Count);
        string[] downloadHosts = manifest.Releases
            .SelectMany(release => release.Assets)
            .Select(asset => asset.DownloadUri.IdnHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PackageHashAlgorithm[] hashAlgorithms = manifest.Releases
            .SelectMany(release => release.Assets)
            .Select(asset => asset.HashAlgorithm)
            .Distinct()
            .Order()
            .ToArray();
        DownloadHosts = Array.AsReadOnly(downloadHosts);
        HashAlgorithms = Array.AsReadOnly(hashAlgorithms);
    }

    public RuntimeProviderPluginManifest Manifest { get; }

    public string SourcePath { get; }

    public int ReleaseCount { get; }

    public int AssetCount { get; }

    public IReadOnlyList<string> DownloadHosts { get; }

    public IReadOnlyList<PackageHashAlgorithm> HashAlgorithms { get; }

    internal byte[] GetNormalizedManifest() => (byte[])_normalizedManifest.Clone();

    public override string ToString() =>
        $"{Manifest.DisplayName}: {ReleaseCount} releases, {AssetCount} assets";
}

public enum RuntimeProviderPluginDeleteOutcome
{
    Deleted,
    DeletedWithQuarantinedCopy,
}

public sealed record RuntimeProviderPluginDeleteResult(
    string PluginId,
    RuntimeProviderPluginDeleteOutcome Outcome,
    bool WasEnabled,
    string? QuarantinePath)
{
    public bool Success => true;

    public bool CleanupPending => Outcome ==
        RuntimeProviderPluginDeleteOutcome.DeletedWithQuarantinedCopy;

    public override string ToString() => $"{PluginId}: {Outcome}";
}

public enum RuntimeProviderPluginErrorCode
{
    MalformedJson,
    UnsupportedSchema,
    ManifestTooLarge,
    InvalidManifest,
    UnsafePath,
    DuplicatePlugin,
    PluginNotFound,
    BuiltInProviderConflict,
    InvalidState,
    IoFailure,
    DeleteRollbackFailed,
}

public sealed record RuntimeProviderPluginError(
    RuntimeProviderPluginErrorCode Code,
    string Message,
    string? PluginId = null,
    string? FileName = null,
    bool IsEnabled = false)
{
    public override string ToString() => PluginId is null
        ? $"{Code}: {Message}"
        : $"{Code} ({PluginId}): {Message}";
}

public sealed record RuntimeProviderPluginListResult(
    IReadOnlyList<RuntimeProviderPluginDescriptor> Plugins,
    IReadOnlyList<RuntimeProviderPluginError> Errors)
{
    public bool Success => Errors.Count == 0;

    public int EnabledCount => Plugins.Count(plugin => plugin.IsEnabled);

    public override string ToString() =>
        $"{Plugins.Count} plugins ({EnabledCount} enabled), {Errors.Count} errors";
}

public sealed class RuntimeProviderPluginException : Exception
{
    public RuntimeProviderPluginException(
        RuntimeProviderPluginErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Field = field;
    }

    public RuntimeProviderPluginErrorCode Code { get; }

    public string? Field { get; }
}

public static class RuntimeProviderPluginIds
{
    public const string Prefix = "plugin:";

    private static readonly FrozenSet<string> BuiltInIds = new[]
    {
        "python-org",
        "nodejs-official",
        "adoptium-temurin",
        "microsoft-dotnet-sdk",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> BuiltInProviderIds => BuiltInIds;

    public static string ToProviderId(string pluginId)
    {
        if (!IsCanonicalPluginId(pluginId))
        {
            throw new ArgumentException(
                "A plugin provider ID requires a canonical plugin ID.",
                nameof(pluginId));
        }

        return Prefix + pluginId;
    }

    public static bool TryGetPluginId(string providerIdOrPluginId, out string pluginId)
    {
        pluginId = string.Empty;
        if (string.IsNullOrWhiteSpace(providerIdOrPluginId))
        {
            return false;
        }

        string candidate = providerIdOrPluginId.Trim();
        if (candidate.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[Prefix.Length..];
        }

        if (!RuntimeProviderPluginManifestParser.IsValidPluginId(candidate))
        {
            return false;
        }

        pluginId = candidate.ToLowerInvariant();
        return true;
    }

    private static bool IsCanonicalPluginId(string? pluginId) =>
        RuntimeProviderPluginManifestParser.IsValidPluginId(pluginId)
        && pluginId!.Equals(pluginId.ToLowerInvariant(), StringComparison.Ordinal);
}
