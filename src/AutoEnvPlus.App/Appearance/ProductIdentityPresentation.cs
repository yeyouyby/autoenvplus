using System.Reflection;

namespace AutoEnvPlus.App.Appearance;

/// <summary>
/// Structured product identity. This file is compiled into the core test
/// project, so it must stay free of resource lookups; the display strings
/// are formatted by <see cref="ProductIdentityText"/> in the app assembly.
/// </summary>
internal sealed record ProductIdentityPresentation(
    string ProductVersion,
    string ReleaseStage)
{
    public bool IsPreview => ReleaseStage == "preview";
}

internal static class ProductIdentityPresentationPolicy
{
    internal const string ReleaseStageMetadataKey = "AutoEnvPlusReleaseStage";

    public static ProductIdentityPresentation FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? throw new InvalidOperationException(
                "The AutoEnvPlus assembly has no informational version.");
        string productVersion = informationalVersion.Split('+', 2)[0];
        string[] releaseStages = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.Equals(
                ReleaseStageMetadataKey,
                StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (releaseStages.Length != 1)
        {
            throw new InvalidOperationException(
                "The AutoEnvPlus assembly must declare exactly one release stage.");
        }

        return Create(productVersion, releaseStages[0]);
    }

    internal static ProductIdentityPresentation Create(
        string productVersion,
        string releaseStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseStage);
        if (releaseStage is not ("preview" or "stable"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(releaseStage),
                releaseStage,
                "The release stage must be preview or stable.");
        }

        return new ProductIdentityPresentation(productVersion, releaseStage);
    }
}
