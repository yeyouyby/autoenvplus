using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Tests;

public sealed class CppToolchainDiscoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Cpp-{Guid.NewGuid():N}");

    [Fact]
    public void ParseVisualStudioInstances_ReadsMsvcVersionAndActivationScript()
    {
        string installation = Path.Combine(_root, "VisualStudio");
        string buildDirectory = Directory.CreateDirectory(
            Path.Combine(installation, "VC", "Auxiliary", "Build")).FullName;
        File.WriteAllText(
            Path.Combine(buildDirectory, "Microsoft.VCToolsVersion.default.txt"),
            "14.44.35207\n");
        File.WriteAllText(Path.Combine(buildDirectory, "vcvarsall.bat"), "@echo off");
        string escapedPath = installation.Replace("\\", "\\\\", StringComparison.Ordinal);
        string json = $$"""
            [
              {
                "instanceId": "vs-buildtools",
                "installationPath": "{{escapedPath}}",
                "installationVersion": "17.14.0",
                "displayName": "Visual Studio Build Tools",
                "isComplete": true,
                "isLaunchable": true,
                "catalog": { "productDisplayVersion": "2022" }
              }
            ]
            """;

        VisualCppInstallation result = Assert.Single(
            new CppToolchainDiscoveryService().ParseVisualStudioInstances(json));

        Assert.Equal("14.44.35207", result.MsvcToolsVersion);
        Assert.Equal("Visual Studio Build Tools 2022", result.DisplayName);
        Assert.NotNull(result.ActivationScript);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void DiscoverWindowsSdks_RequiresHeadersAndLibrariesAndFindsArchitectures()
    {
        string version = "10.0.26100.0";
        string kitsRoot = Path.Combine(_root, "WindowsKits", "10");
        string include = Directory.CreateDirectory(
            Path.Combine(kitsRoot, "Include", version, "um")).FullName;
        string x64 = Directory.CreateDirectory(
            Path.Combine(kitsRoot, "Lib", version, "um", "x64")).FullName;
        string arm64 = Directory.CreateDirectory(
            Path.Combine(kitsRoot, "Lib", version, "um", "arm64")).FullName;
        File.WriteAllText(Path.Combine(include, "Windows.h"), string.Empty);
        File.WriteAllText(Path.Combine(x64, "kernel32.lib"), string.Empty);
        File.WriteAllText(Path.Combine(arm64, "kernel32.lib"), string.Empty);

        WindowsSdkInstallation sdk = Assert.Single(
            new CppToolchainDiscoveryService().DiscoverWindowsSdks(kitsRoot));

        Assert.Equal(RuntimeVersion.Parse(version), sdk.Version);
        Assert.Contains(RuntimeArchitecture.X64, sdk.Architectures);
        Assert.Contains(RuntimeArchitecture.Arm64, sdk.Architectures);
        Assert.DoesNotContain(RuntimeArchitecture.X86, sdk.Architectures);
    }

    [Fact]
    public void CreateActivationPlan_UsesVcVarsArchitectureMapping()
    {
        Directory.CreateDirectory(_root);
        string script = Path.Combine(_root, "vcvarsall.bat");
        File.WriteAllText(script, string.Empty);
        VisualCppInstallation installation = new(
            "id",
            "Build Tools 2022",
            _root,
            "17.14",
            "14.44",
            script,
            true,
            true);

        CppActivationPlan plan = new CppToolchainDiscoveryService().CreateActivationPlan(
            installation,
            RuntimeArchitecture.Arm64);

        Assert.Contains("x64_arm64", plan.Arguments[2]);
        Assert.Equal(RuntimeArchitecture.Arm64, plan.TargetArchitecture);
        Assert.Equal(RuntimeArchitecture.X64, plan.HostArchitecture);
        Assert.Equal("/d", plan.Arguments[0]);
        Assert.Equal("/k", plan.Arguments[1]);
        Assert.StartsWith("call \"", plan.Arguments[2], StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverArchitecturePairs_RequiresMatchingCompilerBinary()
    {
        string version = "14.44.35207";
        string tools = Path.Combine(
            _root,
            "VC",
            "Tools",
            "MSVC",
            version,
            "bin");
        CreateCompiler(tools, "Hostx64", "x64");
        CreateCompiler(tools, "Hostx64", "arm64");
        CreateCompiler(tools, "Hostx86", "x86");

        IReadOnlyList<CppArchitecturePair> pairs = new CppToolchainDiscoveryService()
            .DiscoverArchitecturePairs(_root, version);

        Assert.Contains(pairs, pair => pair == new CppArchitecturePair(
            RuntimeArchitecture.X64,
            RuntimeArchitecture.X64,
            "x64"));
        Assert.Contains(pairs, pair => pair == new CppArchitecturePair(
            RuntimeArchitecture.X64,
            RuntimeArchitecture.Arm64,
            "x64_arm64"));
        Assert.Contains(pairs, pair => pair == new CppArchitecturePair(
            RuntimeArchitecture.X86,
            RuntimeArchitecture.X86,
            "x86"));
        Assert.DoesNotContain(pairs, pair => pair.TargetArchitecture == RuntimeArchitecture.X86
            && pair.HostArchitecture == RuntimeArchitecture.X64);
    }

    [Fact]
    public void CreateActivationPlan_RejectsUnavailablePairWhenDiscoveryIsPresent()
    {
        Directory.CreateDirectory(_root);
        string script = Path.Combine(_root, "vcvarsall.bat");
        File.WriteAllText(script, string.Empty);
        VisualCppInstallation installation = new(
            "id",
            "Build Tools 2022",
            _root,
            "17.14",
            "14.44",
            script,
            true,
            true,
            [new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64")]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new CppToolchainDiscoveryService().CreateActivationPlan(
                installation,
                RuntimeArchitecture.Arm64));

        Assert.Contains("does not contain", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateCompiler(string tools, string host, string target)
    {
        string directory = Directory.CreateDirectory(
            Path.Combine(tools, host, target)).FullName;
        File.WriteAllText(Path.Combine(directory, "cl.exe"), string.Empty);
    }
}
