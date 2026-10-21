using AutoEnvPlus.App.Appearance;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProductIdentityPresentationPolicyTests
{
    [Fact]
    public void CurrentAssemblyIdentity_ComesFromAuthoritativeBuildProperties()
    {
        ProductIdentityPresentation identity =
            ProductIdentityPresentationPolicy.FromAssembly(
                typeof(ProductIdentityPresentationPolicyTests).Assembly);

        Assert.Equal("0.0.4", identity.ProductVersion);
        Assert.Equal("preview", identity.ReleaseStage);
        Assert.True(identity.IsPreview);
    }

    [Fact]
    public void StableRelease_IsNotPreview()
    {
        ProductIdentityPresentation identity =
            ProductIdentityPresentationPolicy.Create("1.2.3", "stable");

        Assert.Equal("1.2.3", identity.ProductVersion);
        Assert.Equal("stable", identity.ReleaseStage);
        Assert.False(identity.IsPreview);
    }

    [Fact]
    public void UnknownReleaseStage_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductIdentityPresentationPolicy.Create("1.2.3", "nightly"));
    }
}
