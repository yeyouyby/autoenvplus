using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.Core.Tests;

public sealed class NetworkExecutionTests
{
    [Fact]
    public void ProxyPolicyRoutesEachSchemeAndHonorsNoProxy()
    {
        EffectiveNetworkSettings settings = new(
            NetworkToolIds.Pip,
            new Uri("http://127.0.0.1:8080"),
            new Uri("https://proxy.example:8443"),
            ["localhost", ".internal.example", "10.0.0.0/8", "cdn-*.example:443"],
            null);
        NetworkProxyPolicy policy = new(settings);

        Assert.Equal(
            "http://127.0.0.1:8080/",
            policy.GetProxy(new Uri("http://python.org/")).AbsoluteUri);
        Assert.Equal(
            "https://proxy.example:8443/",
            policy.GetProxy(new Uri("https://python.org/")).AbsoluteUri);
        Assert.True(policy.IsBypassed(new Uri("https://localhost/")));
        Assert.True(policy.IsBypassed(new Uri("https://packages.internal.example/")));
        Assert.Equal(
            "https://packages.internal.example/",
            policy.GetProxy(new Uri("https://packages.internal.example/")).AbsoluteUri);
        Assert.True(policy.IsBypassed(new Uri("https://10.25.3.4/")));
        Assert.True(policy.IsBypassed(new Uri("https://cdn-cn.example/")));
        Assert.False(policy.IsBypassed(new Uri("https://python.org/")));
    }

    [Fact]
    public void ProxyPolicyDoesNotReuseHttpProxyForHttps()
    {
        EffectiveNetworkSettings settings = new(
            NetworkToolIds.Pip,
            new Uri("http://127.0.0.1:8080"),
            null,
            [],
            null);
        NetworkProxyPolicy policy = new(settings);
        Uri destination = new("https://python.org/");

        Assert.True(policy.IsBypassed(destination));
        Assert.Equal(destination, policy.GetProxy(destination));
    }

    [Fact]
    public void ToolEnvironmentReplacesInheritedProxyAndAddsPipMirror()
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP_PROXY"] = "http://old.invalid:1",
            ["HTTPS_PROXY"] = "http://old.invalid:2",
            ["ALL_PROXY"] = "socks5://old.invalid:3",
        };
        EffectiveNetworkSettings settings = new(
            NetworkToolIds.Pip,
            new Uri("http://proxy.example:8080"),
            new Uri("http://proxy.example:8080"),
            ["localhost", ".corp.example"],
            new Uri("https://pypi.example/simple/"));

        ToolNetworkEnvironment.Apply(environment, "pip", settings);

        Assert.Equal("http://proxy.example:8080/", environment["HTTP_PROXY"]);
        Assert.Equal("http://proxy.example:8080/", environment["https_proxy"]);
        Assert.Equal("localhost,.corp.example", environment["NO_PROXY"]);
        Assert.Equal("https://pypi.example/simple/", environment["PIP_INDEX_URL"]);
        Assert.DoesNotContain("ALL_PROXY", environment.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolEnvironmentClearsInheritedProxyWhenEffectiveProxyIsDisabled()
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP_PROXY"] = "http://old.invalid:1",
            ["https_proxy"] = "http://old.invalid:2",
        };
        EffectiveNetworkSettings settings = new(
            NetworkToolIds.Npm,
            null,
            null,
            [],
            new Uri("https://registry.example/"));

        ToolNetworkEnvironment.Apply(environment, "npm", settings);

        Assert.DoesNotContain("HTTP_PROXY", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTPS_PROXY", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("https://registry.example/", environment["NPM_CONFIG_REGISTRY"]);
    }

    [Theory]
    [InlineData("pip", "PIP_INDEX_URL")]
    [InlineData("npm", "NPM_CONFIG_REGISTRY")]
    [InlineData("pnpm", "NPM_CONFIG_REGISTRY")]
    [InlineData("yarn", "YARN_NPM_REGISTRY_SERVER")]
    public void ToolEnvironmentClearsInheritedMirrorWhenEffectiveMirrorIsDisabled(
        string alias,
        string variable)
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            [variable] = "https://inherited.invalid/",
        };
        EffectiveNetworkSettings settings = new(
            alias,
            null,
            null,
            [],
            null);

        ToolNetworkEnvironment.Apply(environment, alias, settings);

        Assert.DoesNotContain(variable, environment.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
