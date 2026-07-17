namespace AutoEnvPlus.Core.Networking;

public static class ToolNetworkEnvironment
{
    private static readonly string[] ProxyVariables =
    [
        "HTTP_PROXY",
        "http_proxy",
        "HTTPS_PROXY",
        "https_proxy",
        "NO_PROXY",
        "no_proxy",
        "ALL_PROXY",
        "all_proxy",
    ];

    public static void Apply(
        IDictionary<string, string?> environment,
        string toolAlias,
        EffectiveNetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolAlias);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (string name in ProxyVariables)
        {
            environment.Remove(name);
        }

        SetPair(environment, "HTTP_PROXY", "http_proxy", settings.HttpProxy?.AbsoluteUri);
        SetPair(environment, "HTTPS_PROXY", "https_proxy", settings.HttpsProxy?.AbsoluteUri);
        if (settings.NoProxy.Count > 0)
        {
            string noProxy = string.Join(',', settings.NoProxy);
            SetPair(environment, "NO_PROXY", "no_proxy", noProxy);
        }

        string alias = toolAlias.Trim().ToLowerInvariant();
        switch (alias)
        {
            case "pip":
            case "pip3":
                SetOrRemove(environment, "PIP_INDEX_URL", settings.Mirror);
                break;

            case "npm":
            case "npx":
            case "pnpm":
                SetOrRemove(environment, "NPM_CONFIG_REGISTRY", settings.Mirror);
                break;

            case "yarn":
                SetOrRemove(environment, "YARN_NPM_REGISTRY_SERVER", settings.Mirror);
                SetOrRemove(environment, "NPM_CONFIG_REGISTRY", settings.Mirror);
                break;
        }
    }

    private static void SetPair(
        IDictionary<string, string?> environment,
        string upperName,
        string lowerName,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        environment[upperName] = value;
        environment[lowerName] = value;
    }

    private static void SetOrRemove(
        IDictionary<string, string?> environment,
        string name,
        Uri? value)
    {
        if (value is null)
        {
            environment.Remove(name);
            return;
        }

        environment[name] = value.AbsoluteUri;
    }
}
