using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Installation;

namespace AutoEnvPlus.Core.Providers;

public interface IRuntimeCatalogProvider
{
    string Id { get; }

    RuntimeKind Kind { get; }

    Task<IReadOnlyList<RuntimeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default);

    Task<RuntimePackageAsset> GetAssetAsync(
        RuntimeRelease release,
        CancellationToken cancellationToken = default);
}

public interface IArchiveRuntimeProvider : IRuntimeCatalogProvider
{
    ArchiveInstallPlan CreateInstallPlan(
        RuntimePackageAsset asset,
        string managedRoot);
}
