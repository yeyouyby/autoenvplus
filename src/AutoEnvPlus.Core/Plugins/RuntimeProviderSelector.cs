using System.Text.RegularExpressions;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Plugins;

public sealed record RuntimeProviderSelectionRequest(
    string ProviderId,
    RuntimeKind Kind,
    VersionSelector Selector,
    RuntimeArchitecture Architecture = RuntimeArchitecture.Any)
{
    public override string ToString() =>
        $"Provider-constrained {Kind} runtime selection ({Architecture})";
}

public sealed record RuntimeProviderSelectionResult(
    RuntimeProviderSelectionRequest Request,
    ManagedRuntimeEntry? Entry,
    string? Error)
{
    public bool Success => Entry is not null;
}

public static partial class RuntimeProviderSelector
{
    public static RuntimeProviderSelectionResult ResolveInstalled(
        RuntimeProviderSelectionRequest request,
        IEnumerable<ManagedRuntimeEntry> installations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installations);
        ValidateRequest(request);

        ManagedRuntimeEntry? selected = installations
            .Where(entry => entry is not null)
            .Where(entry => entry.ProviderId.Equals(
                request.ProviderId,
                StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Kind == request.Kind)
            .Where(entry => request.Architecture == RuntimeArchitecture.Any
                || entry.Architecture == request.Architecture)
            .Where(entry => request.Selector.Matches(entry.ToRuntimeInstallation()))
            .OrderByDescending(entry => entry.Version)
            .ThenBy(entry => entry.Architecture)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected is null
            ? new RuntimeProviderSelectionResult(
                request,
                null,
                "No installed runtime matches the requested provider and version selector.")
            : new RuntimeProviderSelectionResult(request, selected, null);
    }

    private static void ValidateRequest(RuntimeProviderSelectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId)
            || !ProviderIdPattern().IsMatch(request.ProviderId))
        {
            throw new ArgumentException(
                "The runtime provider ID is invalid.",
                nameof(request));
        }

        if (!Enum.IsDefined(request.Kind)
            || !Enum.IsDefined(request.Architecture))
        {
            throw new ArgumentException(
                "The runtime kind or architecture is invalid.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Selector);
        bool selectorIsValid = request.Selector.Kind switch
        {
            VersionSelectorKind.Auto => request.Selector.Version is null
                && request.Selector.Channel is null,
            VersionSelectorKind.Major or VersionSelectorKind.MajorMinor
                or VersionSelectorKind.Exact => request.Selector.Version is not null
                    && request.Selector.Channel is null,
            VersionSelectorKind.Channel => request.Selector.Channel is { Length: > 0 and <= 48 }
                && ChannelPattern().IsMatch(request.Selector.Channel),
            _ => false,
        };
        if (!selectorIsValid)
        {
            throw new ArgumentException(
                "The runtime version selector is invalid.",
                nameof(request));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
}
