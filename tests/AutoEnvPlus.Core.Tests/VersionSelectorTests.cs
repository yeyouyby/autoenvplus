using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class VersionSelectorTests
{
    [Theory]
    [InlineData("3", VersionSelectorKind.Major, "3")]
    [InlineData("3.12", VersionSelectorKind.MajorMinor, "3.12")]
    [InlineData("3.12.7", VersionSelectorKind.Exact, "3.12.7")]
    [InlineData("lts", VersionSelectorKind.Channel, "lts")]
    [InlineData("22-lts", VersionSelectorKind.Channel, "22-lts")]
    public void Parse_SupportedSelectors(string value, VersionSelectorKind kind, string normalized)
    {
        VersionSelector selector = VersionSelector.Parse(value);

        Assert.Equal(kind, selector.Kind);
        Assert.Equal(normalized, selector.ToString());
    }

    [Fact]
    public void MajorMinor_MatchesPatchRelease()
    {
        VersionSelector selector = VersionSelector.Parse("3.12");
        RuntimeInstallation installation = RuntimeInstallation.Create("python", RuntimeKind.Python, "3.12.8");

        Assert.True(selector.Matches(installation));
    }

    [Fact]
    public void VersionedChannel_RequiresBothMajorAndChannel()
    {
        VersionSelector selector = VersionSelector.Parse("22-lts");
        RuntimeInstallation version22 = RuntimeInstallation.Create("node22", RuntimeKind.NodeJs, "22.17.0", channels: "lts");
        RuntimeInstallation version20 = RuntimeInstallation.Create("node20", RuntimeKind.NodeJs, "20.19.0", channels: "lts");

        Assert.True(selector.Matches(version22));
        Assert.False(selector.Matches(version20));
    }
}
