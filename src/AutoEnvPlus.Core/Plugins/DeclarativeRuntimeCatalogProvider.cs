using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Plugins;

public sealed class DeclarativeRuntimeCatalogProvider : IArchiveRuntimeProvider
{
    public const string AuthenticityNotice =
        "Third-party declarative checksum evidence; the plugin manifest and its checksum source are not treated as an official signature.";

    private readonly RuntimeProviderPluginManifest _manifest;
    private readonly IReadOnlyList<RuntimeRelease> _releases;
    private readonly IReadOnlyDictionary<string, RuntimePackageAsset> _assets;

    public DeclarativeRuntimeCatalogProvider(RuntimeProviderPluginManifest manifest)
    {
        _manifest = RuntimeProviderPluginManifestParser.ValidateAndNormalize(manifest);
        Dictionary<string, RuntimePackageAsset> assets = new(StringComparer.Ordinal);
        List<RuntimeRelease> releases = [];
        foreach (RuntimeProviderPluginRelease pluginRelease in _manifest.Releases)
        {
            foreach (RuntimeProviderPluginAsset pluginAsset in pluginRelease.Assets)
            {
                RuntimeRelease release = new(
                    Id,
                    pluginRelease.Version.ToString(),
                    Kind,
                    pluginRelease.Version,
                    pluginAsset.Architecture,
                    _manifest.Vendor,
                    pluginRelease.ReleaseDate,
                    pluginRelease.Channels,
                    pluginRelease.Channels.Contains(
                        "security",
                        StringComparer.OrdinalIgnoreCase));
                RuntimePackageAsset asset = new(
                    release,
                    pluginAsset.DownloadUri,
                    pluginAsset.FileName,
                    pluginAsset.PackageHash,
                    RuntimePackageFormat.Zip,
                    pluginAsset.ArchiveRoot,
                    [
                        new PackageVerification(
                            PackageVerificationKind.ProviderChecksum,
                            pluginAsset.ChecksumSourceUri,
                            $"Plugin-declared checksum reference for {pluginAsset.FileName}",
                            pluginAsset.HashAlgorithm.DisplayName(),
                            pluginAsset.PackageHash),
                    ],
                    [],
                    PackageAuthenticityRequirement.ChecksumEvidence,
                    SignatureRequirement: null,
                    HashAlgorithm: pluginAsset.HashAlgorithm);
                string key = GetAssetKey(release);
                if (!assets.TryAdd(key, asset))
                {
                    throw new RuntimeProviderPluginException(
                        RuntimeProviderPluginErrorCode.InvalidManifest,
                        "The plugin declares an ambiguous version and architecture pair.");
                }

                releases.Add(release);
            }
        }

        _assets = assets;
        _releases = Array.AsReadOnly(releases
            .OrderByDescending(release => release.Version)
            .ThenBy(release => release.Architecture)
            .ToArray());
    }

    public string Id => _manifest.ProviderId;

    public string PluginId => _manifest.Id;

    public RuntimeKind Kind => _manifest.Kind;

    public string LanguageToolId => _manifest.LanguageToolId;

    public RuntimeProviderPluginManifest Manifest => _manifest;

    public Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_releases);
    }

    public Task<RuntimePackageAsset> GetAssetAsync(
        RuntimeRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRelease(release);
        return Task.FromResult(_assets[GetAssetKey(release)]);
    }

    public ArchiveInstallPlan CreateInstallPlan(
        RuntimePackageAsset asset,
        string managedRoot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ValidateRelease(asset.Release);
        if (!_assets.TryGetValue(GetAssetKey(asset.Release), out RuntimePackageAsset? expected)
            || !ReferenceEquals(expected, asset))
        {
            throw new ArgumentException(
                "The package asset does not belong to this declarative provider instance.",
                nameof(asset));
        }

        string root = Path.GetFullPath(managedRoot);
        EnsureNoReparsePointInPath(root);
        string destination = Path.Combine(
            root,
            "runtimes",
            Kind.ToString().ToLowerInvariant(),
            "plugins",
            PluginId,
            asset.Release.Version.ToString(),
            asset.Release.Architecture.ToString().ToLowerInvariant());
        EnsureChildPath(root, destination, "plugin runtime destination");
        EnsureNoReparsePointInPath(destination);
        RuntimeProviderPluginAsset declaredAsset = FindDeclaredAsset(asset.Release);
        return new ArchiveInstallPlan(
            asset,
            root,
            destination,
            declaredAsset.ExpectedExecutableRelativePath,
            MaximumDownloadBytes: 2_147_483_648,
            MaximumArchiveEntries: 100_000,
            MaximumUncompressedBytes: 8_589_934_592);
    }

    public string CreateManagedRuntimeId(RuntimeRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateRelease(release);
        return $"plugin-{Kind.ToString().ToLowerInvariant()}-{PluginId}-{release.Version}-"
            + release.Architecture.ToString().ToLowerInvariant();
    }

    private RuntimeProviderPluginAsset FindDeclaredAsset(RuntimeRelease release)
    {
        RuntimeProviderPluginRelease declaredRelease = _manifest.Releases.Single(item =>
            item.Version == release.Version);
        return declaredRelease.Assets.Single(item =>
            item.Architecture == release.Architecture);
    }

    private void ValidateRelease(RuntimeRelease release)
    {
        if (!release.ProviderId.Equals(Id, StringComparison.Ordinal)
            || release.Kind != Kind
            || !_assets.ContainsKey(GetAssetKey(release)))
        {
            throw new ArgumentException(
                "The release does not belong to this declarative runtime provider.",
                nameof(release));
        }
    }

    private static string GetAssetKey(RuntimeRelease release) =>
        $"{release.Version}\0{release.Architecture}";

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {description} must remain inside the managed root.");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            if (TryGetAttributes(current.FullName) is FileAttributes attributes
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "The managed root cannot traverse a reparse point.",
                    nameof(path));
            }

            current = current.Parent;
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
