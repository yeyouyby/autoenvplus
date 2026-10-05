using System.Diagnostics.CodeAnalysis;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Cli;

public sealed record CliManagedInstallSourceSelection(
    RuntimeKind RuntimeKind,
    ProviderSourceOwner CatalogOwner,
    ResolvedProviderSource Source,
    Uri Endpoint)
{
    public string SourceId => string.Join(
        '/',
        Source.Owner.LanguageToolId,
        Source.Owner.ProviderId,
        Source.Owner.SlotId);

    public ProviderSourceOrigin Origin => Source.Origin;

    public bool IsCatalogDefault => Source.Origin == ProviderSourceOrigin.CatalogDefault;
}

public sealed record CliManagedInstallSourceResolution(
    CliManagedInstallSourceSelection? Selection,
    string? Error);

public static class CliManagedInstallSourceResolver
{
    private const string ManagedProviderId = "official-archive";

    public static async Task<CliManagedInstallSourceResolution> ResolveAsync(
        string managedRoot,
        RuntimeKind runtimeKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        if (!TryGetCatalogOwner(runtimeKind, out ProviderSourceOwner? catalogOwner))
        {
            return new(
                null,
                $"{runtimeKind} does not have a built-in managed install source.");
        }

        LanguageCatalog catalog = await new LanguagePackStore(managedRoot)
            .GetAvailableCatalogAsync(cancellationToken)
            .ConfigureAwait(false);
        ProviderSourceListResolutionResult resolved =
            await new ProviderSourcePreferenceStore(managedRoot).ResolveProviderAsync(
                catalog,
                catalogOwner.LanguageToolId,
                catalogOwner.ProviderId,
                cancellationToken).ConfigureAwait(false);
        if (!resolved.Success)
        {
            return new(
                null,
                "The managed install source preferences could not be resolved: "
                    + string.Join(' ', resolved.Errors.Select(error =>
                        $"[{error.Code}] {error.Path}: {error.Message}")));
        }

        ResolvedProviderSource? catalogSource = resolved.Sources.SingleOrDefault(source =>
            OwnersEqual(source.Owner, catalogOwner));
        if (catalogSource?.EffectiveEndpoint is null
            || catalogSource.EndpointKind != ProviderMirrorEndpointKind.GenericDownload)
        {
            return new(
                null,
                $"The required managed install source {FormatOwner(catalogOwner)} is missing, disabled, or not a GenericDownload endpoint.");
        }

        ResolvedProviderSource[] enabledCustomSources = resolved.Sources
            .Where(source => source.Origin == ProviderSourceOrigin.Custom
                && source.IsEnabled
                && source.EndpointKind == ProviderMirrorEndpointKind.GenericDownload
                && source.EffectiveEndpoint is not null)
            .ToArray();
        if (enabledCustomSources.Length > 1)
        {
            return new(
                null,
                $"More than one custom GenericDownload source is enabled for {catalogOwner.LanguageToolId}/{catalogOwner.ProviderId}; enable exactly one before using the CLI catalog or installer.");
        }

        ResolvedProviderSource selected = enabledCustomSources.SingleOrDefault() ?? catalogSource;
        return new(
            new CliManagedInstallSourceSelection(
                runtimeKind,
                catalogOwner,
                selected,
                selected.EffectiveEndpoint!),
            null);
    }

    public static bool TryGetCatalogOwner(
        RuntimeKind runtimeKind,
        [NotNullWhen(true)] out ProviderSourceOwner? owner)
    {
        owner = runtimeKind switch
        {
            RuntimeKind.Python => Owner("cpython", "python-downloads"),
            RuntimeKind.NodeJs => Owner("nodejs", "nodejs-downloads"),
            RuntimeKind.Java => Owner("eclipse-temurin", "adoptium-downloads"),
            RuntimeKind.DotNet => Owner("dotnet-sdk", "dotnet-downloads"),
            _ => null,
        };
        return owner is not null;
    }

    private static ProviderSourceOwner Owner(string languageToolId, string slotId) =>
        new(languageToolId, ManagedProviderId, slotId);

    private static bool OwnersEqual(ProviderSourceOwner left, ProviderSourceOwner right) =>
        left.LanguageToolId.Equals(right.LanguageToolId, StringComparison.OrdinalIgnoreCase)
        && left.ProviderId.Equals(right.ProviderId, StringComparison.OrdinalIgnoreCase)
        && left.SlotId.Equals(right.SlotId, StringComparison.OrdinalIgnoreCase);

    private static string FormatOwner(ProviderSourceOwner owner) => string.Join(
        '/',
        owner.LanguageToolId,
        owner.ProviderId,
        owner.SlotId);
}

public static class CliManagedInstallSourcePolicy
{
    public const string AcceptanceOption = "--accept-non-default-source";

    public static string? GetAcceptanceError(
        CliManagedInstallSourceSelection source,
        IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(arguments);
        if (source.IsCatalogDefault
            || arguments.Contains(AcceptanceOption, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return "The selected managed install source is not the catalog default and requires "
            + $"{AcceptanceOption}.{Environment.NewLine}"
            + $"  Source ID: {source.SourceId}{Environment.NewLine}"
            + $"  Source URL: {source.Endpoint.AbsoluteUri}{Environment.NewLine}"
            + $"  Source type: {source.Origin}";
    }
}

public static class CliInstallPlanPresentation
{
    public static string GetVerificationEvidenceLabel(
        RuntimeKind runtimeKind,
        bool isThirdParty) =>
        isThirdParty
            ? "Declared reference"
            : runtimeKind == RuntimeKind.DotNet
                ? "Source-declared hash"
                : "Verified by";
}
