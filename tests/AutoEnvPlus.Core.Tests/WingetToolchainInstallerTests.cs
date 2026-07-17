using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Tests;

public sealed class WingetToolchainInstallerTests
{
    [Theory]
    [InlineData(ToolchainComponent.MsvcBuildTools, RuntimeKind.Msvc)]
    [InlineData(ToolchainComponent.Llvm, RuntimeKind.Llvm)]
    [InlineData(ToolchainComponent.MinGw, RuntimeKind.Mingw)]
    [InlineData(ToolchainComponent.CMake, RuntimeKind.CMake)]
    [InlineData(ToolchainComponent.Ninja, RuntimeKind.Ninja)]
    public void RuntimeProviderPolicy_MapsEveryWinGetComponentToDeclarativeKind(
        ToolchainComponent component,
        RuntimeKind expectedKind)
    {
        RuntimeKind kind = ToolchainRuntimeProviderPolicy.GetRuntimeKind(component);

        Assert.Equal(expectedKind, kind);
        Assert.True(ToolchainRuntimeProviderPolicy.RequiresExplicitPlugin(kind));
        Assert.True(NetworkToolIds.TryGetRuntimeScope(kind, out string networkScope));
        Assert.Equal(NetworkToolIds.RuntimeCpp, networkScope);
    }

    [Fact]
    public void RuntimeProviderPolicy_DoesNotTreatLanguageProvidersAsToolchainPlugins()
    {
        Assert.False(ToolchainRuntimeProviderPolicy.RequiresExplicitPlugin(RuntimeKind.Python));
        Assert.DoesNotContain(
            RuntimeKind.DotNet,
            ToolchainRuntimeProviderPolicy.DeclarativeRuntimeKinds);
    }

    [Fact]
    public void RuntimeProviderPolicy_ExplainsMsvcActivationBoundary()
    {
        string notice = ToolchainRuntimeProviderPolicy.GetActivationNotice(RuntimeKind.Msvc);

        Assert.Contains("managed executable", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Visual Studio C++ workload", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vcvars", notice, StringComparison.OrdinalIgnoreCase);
    }

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
