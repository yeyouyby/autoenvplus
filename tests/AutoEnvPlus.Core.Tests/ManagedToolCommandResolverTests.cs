using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Shell;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedToolCommandResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Tools-{Guid.NewGuid():N}");

    [Fact]
    public async Task ResolveAsync_PipUsesSelectedPythonModuleInvocation()
    {
        ManagedRuntimeEntry python = await Register(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            []);
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));

        ManagedToolCommandResult result = await new ManagedToolCommandResolver(_root).ResolveAsync(
            "pip",
            _root);

        Assert.True(result.Success);
        Assert.Equal(python.ExecutablePath, result.Command!.ExecutablePath);
        Assert.Equal(["-m", "pip"], result.Command.PrefixArguments);
    }

    [Fact]
    public async Task ResolveAsync_NpmUsesCliScriptFromSelectedNode()
    {
        ManagedRuntimeEntry node = await Register(
            RuntimeKind.NodeJs,
            "24.18.0",
            "node.exe",
            [@"node_modules\npm\bin\npm-cli.js"]);
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.NodeJs,
            VersionSelector.Parse("24"));

        ManagedToolCommandResult result = await new ManagedToolCommandResolver(_root).ResolveAsync(
            "npm",
            _root);

        Assert.True(result.Success);
        Assert.Equal(node.ExecutablePath, result.Command!.ExecutablePath);
        Assert.EndsWith(
            @"node_modules\npm\bin\npm-cli.js",
            Assert.Single(result.Command.PrefixArguments),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_JavacUsesJdkBinaryAndReportsMissingTools()
    {
        ManagedRuntimeEntry java = await Register(
            RuntimeKind.Java,
            "21.0.11",
            @"bin\java.exe",
            [@"bin\javac.exe"]);
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Java,
            VersionSelector.Parse("21"));

        ManagedToolCommandResult javac = await new ManagedToolCommandResolver(_root).ResolveAsync(
            "javac",
            _root);
        File.Delete(Path.Combine(java.InstallRoot, "bin", "jar.exe"));
        ManagedToolCommandResult jar = await new ManagedToolCommandResolver(_root).ResolveAsync(
            "jar",
            _root);

        Assert.True(javac.Success);
        Assert.EndsWith(@"bin\javac.exe", javac.Command!.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(jar.Success);
        Assert.Contains("jar.exe", Assert.Single(jar.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RuntimeKind.Llvm, "clang++", "bin\\clang.exe", "bin\\clang++.exe")]
    [InlineData(RuntimeKind.Mingw, "g++", "bin\\gcc.exe", "bin\\g++.exe")]
    public async Task ResolveAsync_CppDriverAliasUsesSelectedToolchainBinary(
        RuntimeKind kind,
        string alias,
        string primaryExecutable,
        string driverExecutable)
    {
        await Register(
            kind,
            "20.1.0",
            primaryExecutable,
            [driverExecutable]);
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            kind,
            VersionSelector.Parse("20.1.0"));

        ManagedToolCommandResult result = await new ManagedToolCommandResolver(_root).ResolveAsync(
            alias,
            _root,
            architecture: RuntimeArchitecture.X64);

        Assert.True(result.Success);
        Assert.EndsWith(
            driverExecutable,
            result.Command!.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsDistinctProvidersAtTheSelectedVersion()
    {
        await Register(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            [],
            "python-org",
            "python-3.13.5-x64");
        await Register(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            [],
            "plugin:community-python",
            "plugin-community-python-3.13.5-x64");
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.13"));

        ManagedToolCommandResult result = await new ManagedToolCommandResolver(_root).ResolveAsync(
            "pip",
            _root,
            architecture: RuntimeArchitecture.X64);

        Assert.False(result.Success);
        Assert.Contains("Multiple Providers", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    private async Task<ManagedRuntimeEntry> Register(
        RuntimeKind kind,
        string version,
        string executable,
        IReadOnlyList<string> extraFiles,
        string providerId = "test-provider",
        string? runtimeId = null)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        runtimeId ??= $"{kind}-{parsed}-x64";
        string installRoot = Path.Combine(_root, "runtimes", kind.ToString(), runtimeId);
        string executablePath = Path.Combine(installRoot, executable);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, string.Empty);
        foreach (string extra in extraFiles)
        {
            string path = Path.Combine(installRoot, extra);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        ManagedRuntimeEntry entry = new(
            runtimeId,
            providerId,
            kind,
            parsed,
            RuntimeArchitecture.X64,
            installRoot,
            executable,
            new string('a', 64),
            DateTimeOffset.UtcNow,
            ["stable"]);
        await new ManagedRuntimeRegistry(_root).UpsertAsync(entry);
        return entry;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
