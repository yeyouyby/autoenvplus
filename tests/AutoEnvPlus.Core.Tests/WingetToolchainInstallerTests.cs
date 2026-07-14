using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Tests;

public sealed class WingetToolchainInstallerTests
{
    [Theory]
    [InlineData(ToolchainComponent.Llvm, "LLVM.LLVM")]
    [InlineData(ToolchainComponent.MinGw, "BrechtSanders.WinLibs.POSIX.UCRT")]
    [InlineData(ToolchainComponent.CMake, "Kitware.CMake")]
    [InlineData(ToolchainComponent.Ninja, "Ninja-build.Ninja")]
    public void CreatePlan_UsesExactAllowlistedPackage(
        ToolchainComponent component,
        string packageId)
    {
        ExternalToolInstallPlan plan = new WingetToolchainInstaller().CreatePlan(
            component,
            @"C:\WindowsApps\winget.exe");

        Assert.Equal(packageId, plan.PackageId);
        Assert.Contains("--exact", plan.Arguments);
        Assert.Contains("--disable-interactivity", plan.Arguments);
        int idIndex = plan.Arguments.ToList().IndexOf("--id");
        Assert.Equal(packageId, plan.Arguments[idIndex + 1]);
    }

    [Fact]
    public void CreatePlan_MsvcIncludesOnlyVctoolsWorkloadOverride()
    {
        ExternalToolInstallPlan plan = new WingetToolchainInstaller().CreatePlan(
            ToolchainComponent.MsvcBuildTools,
            @"C:\WindowsApps\winget.exe");

        Assert.Equal("Microsoft.VisualStudio.2022.BuildTools", plan.PackageId);
        string overrideValue = plan.Arguments[plan.Arguments.ToList().IndexOf("--override") + 1];
        Assert.Contains("Microsoft.VisualStudio.Workload.VCTools", overrideValue);
        Assert.Contains("--includeRecommended", overrideValue);
    }
}
