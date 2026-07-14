using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class RuntimeVersionTests
{
    [Theory]
    [InlineData("3.13.5", 3, 13, 5)]
    [InlineData("v22.17.0", 22, 17, 0)]
    [InlineData("21.0.8+9", 21, 0, 8)]
    public void Parse_CommonRuntimeVersions(string value, int major, int minor, int patch)
    {
        RuntimeVersion version = RuntimeVersion.Parse(value);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Fact]
    public void StableVersion_SortsAfterPrerelease()
    {
        RuntimeVersion preview = RuntimeVersion.Parse("3.14.0-rc.1");
        RuntimeVersion stable = RuntimeVersion.Parse("3.14.0");

        Assert.True(stable.CompareTo(preview) > 0);
    }
}
