using System.Net;

namespace AutoEnvPlus.Core.Networking;

public static class NetworkHttpClientFactory
{
    public static HttpClient Create(
        EffectiveNetworkSettings settings,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        NetworkProxyPolicy proxy = new(settings);
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            // Runtime archives and segmented Range transfers must observe the exact wire
            // representation described by Content-Length/Content-Range. Callers can still
            // request compressed catalog content explicitly, but the shared client never
            // silently inflates bytes underneath integrity and range accounting.
            AutomaticDecompression = DecompressionMethods.None,
            Proxy = proxy,
            UseCookies = false,
            UseProxy = proxy.HasConfiguredProxy,
        };
        HttpClient client = new(handler, disposeHandler: true)
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoEnvPlus/0.1");
        return client;
    }
}

public sealed class NetworkProxyPolicy : IWebProxy
{
    private readonly Uri? _httpProxy;
    private readonly Uri? _httpsProxy;
    private readonly string[] _noProxy;

    public NetworkProxyPolicy(EffectiveNetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _httpProxy = settings.HttpProxy;
        _httpsProxy = settings.HttpsProxy;
        _noProxy = settings.NoProxy
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
    }

    public bool HasConfiguredProxy => _httpProxy is not null || _httpsProxy is not null;

    public ICredentials? Credentials
    {
        get => CredentialCache.DefaultCredentials;
        set
        {
            if (value is not null
                && !ReferenceEquals(value, CredentialCache.DefaultCredentials))
            {
                throw new NotSupportedException(
                    NetworkSettingsResolver.ProxyCredentialGuidance);
            }
        }
    }

    public Uri GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Uri? configured = GetConfiguredProxy(destination);
        return configured is null || _noProxy.Any(entry => MatchesNoProxy(destination, entry))
            ? destination
            : configured;
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return GetConfiguredProxy(host) is null
            || _noProxy.Any(entry => MatchesNoProxy(host, entry));
    }

    private Uri? GetConfiguredProxy(Uri destination) => destination.Scheme.ToLowerInvariant() switch
    {
        "http" => _httpProxy,
        "https" => _httpsProxy,
        _ => null,
    };

    private static bool MatchesNoProxy(Uri destination, string entry)
    {
        if (entry == "*")
        {
            return true;
        }

        if (TryMatchCidr(destination, entry))
        {
            return true;
        }

        (string pattern, int? port) = SplitHostAndPort(entry);
        if (port is int expectedPort && destination.Port != expectedPort)
        {
            return false;
        }

        string host = destination.IdnHost.TrimEnd('.');
        pattern = pattern.Trim().TrimEnd('.');
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            pattern = pattern[2..];
            return host.EndsWith('.' + pattern, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith(".", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        if (pattern.Contains('*', StringComparison.Ordinal)
            || pattern.Contains('?', StringComparison.Ordinal))
        {
            return WildcardMatch(host, pattern);
        }

        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith('.' + pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMatchCidr(Uri destination, string entry)
    {
        int slash = entry.LastIndexOf('/');
        if (slash <= 0
            || !int.TryParse(entry[(slash + 1)..], out int prefixLength)
            || !IPAddress.TryParse(entry[..slash].Trim('[', ']'), out IPAddress? network)
            || !IPAddress.TryParse(destination.IdnHost, out IPAddress? address))
        {
            return false;
        }

        byte[] networkBytes = network.GetAddressBytes();
        byte[] addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length
            || prefixLength < 0
            || prefixLength > networkBytes.Length * 8)
        {
            return false;
        }

        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        for (int index = 0; index < wholeBytes; index++)
        {
            if (networkBytes[index] != addressBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        int mask = 0xff << (8 - remainingBits);
        return (networkBytes[wholeBytes] & mask) == (addressBytes[wholeBytes] & mask);
    }

    private static (string Host, int? Port) SplitHostAndPort(string entry)
    {
        if (entry.StartsWith("[", StringComparison.Ordinal))
        {
            int closingBracket = entry.IndexOf(']');
            if (closingBracket > 0)
            {
                string host = entry[1..closingBracket];
                string remainder = entry[(closingBracket + 1)..];
                return remainder.StartsWith(":", StringComparison.Ordinal)
                    && int.TryParse(remainder[1..], out int port)
                    ? (host, port)
                    : (host, null);
            }
        }

        int firstColon = entry.IndexOf(':');
        int lastColon = entry.LastIndexOf(':');
        if (firstColon > 0
            && firstColon == lastColon
            && int.TryParse(entry[(lastColon + 1)..], out int parsedPort))
        {
            return (entry[..lastColon], parsedPort);
        }

        return (entry, null);
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        int valueIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int retryValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || char.ToUpperInvariant(pattern[patternIndex])
                        == char.ToUpperInvariant(value[valueIndex])))
            {
                valueIndex++;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
