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

        Assert.Equal("0.0.2", identity.ProductVersion);
        Assert.Equal("preview", identity.ReleaseStage);
        Assert.Equal("v0.0.2 预览版", identity.DisplayVersion);
        Assert.Equal("AutoEnvPlus v0.0.2 预览版", identity.WindowTitle);
        Assert.Equal(identity.WindowTitle, identity.AutomationName);
    }

    [Fact]
    public void StableRelease_HasNoPreviewLabel()
    {
        ProductIdentityPresentation identity =
            ProductIdentityPresentationPolicy.Create("1.2.3", "stable");

        Assert.Equal("v1.2.3", identity.DisplayVersion);
        Assert.Equal("AutoEnvPlus v1.2.3", identity.WindowTitle);
    }

    [Fact]
    public void UnknownReleaseStage_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductIdentityPresentationPolicy.Create("1.2.3", "nightly"));
    }
}
