namespace AutoEnvPlus.Core.Runtimes;

public enum ResolutionScope
{
    Automatic,
    Global,
    Project,
    Session,
}

public sealed record RuntimeSelectionIdentity(
    string RuntimeId,
    string ProviderId);

public sealed record RuntimeProfile(IReadOnlyDictionary<RuntimeKind, VersionSelector> Selections)
{
    public IReadOnlyDictionary<RuntimeKind, RuntimeSelectionIdentity> ExactSelections
    {
        get;
        init;
    } = new Dictionary<RuntimeKind, RuntimeSelectionIdentity>();

    public static RuntimeProfile Empty { get; } = new(
        new Dictionary<RuntimeKind, VersionSelector>());
}

public sealed record RuntimeResolutionContext(
    RuntimeProfile? Session = null,
    RuntimeProfile? Project = null,
    RuntimeProfile? Global = null);

public sealed record RuntimeResolutionResult(
    RuntimeKind Kind,
    VersionSelector Selector,
    ResolutionScope Scope,
    RuntimeInstallation? Installation,
    string? Error)
{
    public bool Success => Installation is not null;
}

public sealed class RuntimeResolver
{
    public RuntimeResolutionResult Resolve(
        RuntimeKind kind,
        RuntimeResolutionContext context,
        IEnumerable<RuntimeInstallation> installations,
        RuntimeArchitecture architecture = RuntimeArchitecture.Any)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(installations);

        (VersionSelector selector, ResolutionScope scope) = SelectConstraint(kind, context);

        RuntimeInstallation? selected = installations
            .Where(item => item.Kind == kind)
            .Where(item => architecture == RuntimeArchitecture.Any
                || item.Architecture == RuntimeArchitecture.Any
                || item.Architecture == architecture)
            .Where(selector.Matches)
            .OrderByDescending(item => item.Version)
            .ThenBy(item => OwnershipRank(item.Ownership))
            .FirstOrDefault();

        string? error = selected is null
            ? $"No installed {kind} runtime matches '{selector}' from the {scope} scope."
            : null;

        return new RuntimeResolutionResult(kind, selector, scope, selected, error);
    }

    private static (VersionSelector Selector, ResolutionScope Scope) SelectConstraint(
        RuntimeKind kind,
        RuntimeResolutionContext context)
    {
        if (TryGet(context.Session, kind, out VersionSelector? selector))
        {
            return (selector, ResolutionScope.Session);
        }

        if (TryGet(context.Project, kind, out selector))
        {
            return (selector, ResolutionScope.Project);
        }

        if (TryGet(context.Global, kind, out selector))
        {
            return (selector, ResolutionScope.Global);
        }

        return (VersionSelector.Auto, ResolutionScope.Automatic);
    }

    private static bool TryGet(
        RuntimeProfile? profile,
        RuntimeKind kind,
        out VersionSelector selector)
    {
        if (profile is not null && profile.Selections.TryGetValue(kind, out VersionSelector? value))
        {
            selector = value;
            return true;
        }

        selector = VersionSelector.Auto;
        return false;
    }

    private static int OwnershipRank(RuntimeOwnership ownership) => ownership switch
    {
        RuntimeOwnership.Managed => 0,
        RuntimeOwnership.External => 1,
        RuntimeOwnership.System => 2,
        _ => 3,
    };
}
