namespace AutoEnvPlus.Core.Networking;

public static class NetworkSettingsResolver
{
    public const string ProxyCredentialGuidance =
        "Credentials in proxy URIs are not accepted; Windows integrated credentials may be used, but username/password proxy storage is not supported.";

    public static NetworkSettingsResolutionResult Resolve(
        NetworkSettings settings,
        string toolId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<NetworkSettingsError> errors = [];
        string normalizedToolId = toolId?.Trim() ?? string.Empty;
        if (!NetworkToolIds.IsSupported(normalizedToolId))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.UnsupportedTool,
                "toolId",
                "The tool identifier is not supported."));
        }

        NetworkSettings? normalized = Normalize(settings, errors);
        if (errors.Count > 0 || normalized is null)
        {
            return new NetworkSettingsResolutionResult(false, null, errors);
        }

        GlobalNetworkSettings global = normalized.Global!;
        normalized.Tools!.TryGetValue(normalizedToolId, out ToolNetworkSettings? tool);
        Uri? httpProxy = ResolveEndpoint(global.HttpProxy, tool?.HttpProxy);
        Uri? httpsProxy = ResolveEndpoint(global.HttpsProxy, tool?.HttpsProxy);
        Uri? mirror = ResolveEndpoint(global.Mirror, tool?.Mirror);
        return new NetworkSettingsResolutionResult(
            true,
            new EffectiveNetworkSettings(
                normalizedToolId.ToLowerInvariant(),
                httpProxy,
                httpsProxy,
                global.NoProxy!,
                mirror),
            []);
    }

    internal static NetworkSettings? Normalize(
        NetworkSettings settings,
        List<NetworkSettingsError> errors)
    {
        GlobalNetworkSettings global = settings.Global ?? new GlobalNetworkSettings();
        string? httpProxy = NormalizeProxy(global.HttpProxy, "global.httpProxy", errors);
        string? httpsProxy = NormalizeProxy(global.HttpsProxy, "global.httpsProxy", errors);
        string? mirror = NormalizeMirror(global.Mirror, "global.mirror", errors);
        IReadOnlyList<string> noProxy = NormalizeNoProxy(global.NoProxy, errors);

        Dictionary<string, ToolNetworkSettings> normalizedTools =
            new(StringComparer.OrdinalIgnoreCase);
        if (settings.Tools is not null)
        {
            foreach ((string toolId, ToolNetworkSettings tool) in settings.Tools)
            {
                string normalizedToolId = toolId?.Trim() ?? string.Empty;
                string toolPath = string.IsNullOrEmpty(normalizedToolId)
                    ? "tools"
                    : $"tools.{normalizedToolId}";
                if (!NetworkToolIds.IsSupported(normalizedToolId))
                {
                    errors.Add(new NetworkSettingsError(
                        NetworkSettingsErrorCode.UnsupportedTool,
                        toolPath,
                        "The tool identifier is not supported."));
                    continue;
                }

                if (tool is null)
                {
                    errors.Add(new NetworkSettingsError(
                        NetworkSettingsErrorCode.InvalidDocument,
                        toolPath,
                        "A tool override must be an object."));
                    continue;
                }

                if (normalizedTools.ContainsKey(normalizedToolId))
                {
                    errors.Add(new NetworkSettingsError(
                        NetworkSettingsErrorCode.InvalidDocument,
                        toolPath,
                        "Tool identifiers must be unique without regard to letter casing."));
                    continue;
                }

                NetworkEndpointOverride http = NormalizeOverride(
                    tool.HttpProxy,
                    $"{toolPath}.httpProxy",
                    mirrorEndpoint: false,
                    errors);
                NetworkEndpointOverride https = NormalizeOverride(
                    tool.HttpsProxy,
                    $"{toolPath}.httpsProxy",
                    mirrorEndpoint: false,
                    errors);
                NetworkEndpointOverride toolMirror = NormalizeOverride(
                    tool.Mirror,
                    $"{toolPath}.mirror",
                    mirrorEndpoint: true,
                    errors);
                normalizedTools[normalizedToolId.ToLowerInvariant()] = new ToolNetworkSettings(
                    http,
                    https,
                    toolMirror);
            }
        }

        if (errors.Count > 0)
        {
            return null;
        }

        return new NetworkSettings(
            new GlobalNetworkSettings(httpProxy, httpsProxy, noProxy, mirror),
            normalizedTools);
    }

    private static NetworkEndpointOverride NormalizeOverride(
        NetworkEndpointOverride? value,
        string path,
        bool mirrorEndpoint,
        List<NetworkSettingsError> errors)
    {
        NetworkEndpointOverride candidate = value ?? NetworkEndpointOverride.Inherit;
        switch (candidate.Mode)
        {
            case NetworkEndpointOverrideMode.Inherit:
            case NetworkEndpointOverrideMode.Disabled:
                if (!string.IsNullOrWhiteSpace(candidate.Value))
                {
                    errors.Add(new NetworkSettingsError(
                        NetworkSettingsErrorCode.InvalidOverride,
                        path,
                        $"A {candidate.Mode.ToString().ToLowerInvariant()} override cannot contain a value."));
                }

                return new NetworkEndpointOverride(candidate.Mode);

            case NetworkEndpointOverrideMode.Custom:
                string? endpoint = mirrorEndpoint
                    ? NormalizeMirror(candidate.Value, path, errors)
                    : NormalizeProxy(candidate.Value, path, errors);
                if (endpoint is null && string.IsNullOrWhiteSpace(candidate.Value))
                {
                    errors.Add(new NetworkSettingsError(
                        NetworkSettingsErrorCode.InvalidOverride,
                        path,
                        "A custom override must contain a value."));
                }

                return endpoint is null
                    ? new NetworkEndpointOverride(NetworkEndpointOverrideMode.Custom)
                    : NetworkEndpointOverride.Custom(endpoint);

            default:
                errors.Add(new NetworkSettingsError(
                    NetworkSettingsErrorCode.InvalidOverride,
                    path,
                    "The override mode is not supported."));
                return NetworkEndpointOverride.Inherit;
        }
    }

    private static string? NormalizeProxy(
        string? value,
        string path,
        List<NetworkSettingsError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryCreateAbsoluteEndpoint(value, out Uri? uri)
            || uri is null
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.InvalidProxyUri,
                path,
                "A proxy must be an absolute HTTP or HTTPS URI."));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.InvalidProxyUri,
                path,
                ProxyCredentialGuidance));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.InvalidProxyUri,
                path,
                "A proxy URI cannot contain a fragment."));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.InvalidProxyUri,
                path,
                "A proxy URI cannot contain a query. Windows integrated credentials may be used, but username/password proxy storage is not supported."));
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static string? NormalizeMirror(
        string? value,
        string path,
        List<NetworkSettingsError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TryCreateAbsoluteEndpoint(value, out Uri? uri)
            || uri is null
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.InvalidMirrorUri,
                path,
                "A mirror must be an absolute HTTPS URI."));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add(new NetworkSettingsError(
                NetworkSettingsErrorCode.InvalidMirrorUri,
                path,
                "A mirror URI cannot contain credentials, a query, or a fragment."));
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static IReadOnlyList<string> NormalizeNoProxy(
        IReadOnlyList<string>? values,
        List<NetworkSettingsError> errors)
    {
        if (values is null)
        {
            return [];
        }

        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < values.Count; index++)
        {
            string? value = values[index];
            string path = $"global.noProxy[{index}]";
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new NetworkSettingsError(
                    NetworkSettingsErrorCode.InvalidNoProxyEntry,
                    path,
                    "A no-proxy entry cannot be empty."));
                continue;
            }

            string candidate = value.Trim();
            if (candidate.Length > 1_024
                || candidate.Any(char.IsControl)
                || candidate.Any(char.IsWhiteSpace)
                || candidate.Contains(',', StringComparison.Ordinal)
                || candidate.Contains(';', StringComparison.Ordinal))
            {
                errors.Add(new NetworkSettingsError(
                    NetworkSettingsErrorCode.InvalidNoProxyEntry,
                    path,
                    "A no-proxy entry must be a single host, address, CIDR range, or wildcard pattern."));
                continue;
            }

            if (seen.Add(candidate))
            {
                normalized.Add(candidate);
            }
        }

        return normalized;
    }

    private static bool TryCreateAbsoluteEndpoint(string value, out Uri? uri)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
            && uri is not null
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static Uri? ResolveEndpoint(
        string? global,
        NetworkEndpointOverride? toolOverride)
    {
        NetworkEndpointOverride effectiveOverride =
            toolOverride ?? NetworkEndpointOverride.Inherit;
        string? value = effectiveOverride.Mode switch
        {
            NetworkEndpointOverrideMode.Inherit => global,
            NetworkEndpointOverrideMode.Disabled => null,
            NetworkEndpointOverrideMode.Custom => effectiveOverride.Value,
            _ => null,
        };
        return value is null ? null : new Uri(value, UriKind.Absolute);
    }
}
