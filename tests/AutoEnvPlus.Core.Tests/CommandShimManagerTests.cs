using AutoEnvPlus.Core.Shell;

namespace AutoEnvPlus.Core.Tests;

public sealed class CommandShimManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Shim-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallAsync_CreatesStableWrappersThatForwardArgumentsAndExitCode()
    {
        Directory.CreateDirectory(_root);
        string executable = Path.Combine(_root, "autoenvplus.exe");
        File.WriteAllText(executable, string.Empty);

        CommandShimInstallResult result = await new CommandShimManager().InstallAsync(
            _root,
            executable);

        Assert.Equal(10, result.ShimFiles.Count);
        Assert.Equal(CommandShimImplementation.CmdFallback, result.Implementation);
        string python = File.ReadAllText(Path.Combine(result.ShimDirectory, "python.cmd"));
        Assert.Contains($"\"{executable}\" exec python -- %*", python);
        Assert.Contains("exit /b %errorlevel%", python, StringComparison.OrdinalIgnoreCase);
        string node = File.ReadAllText(Path.Combine(result.ShimDirectory, "node.cmd"));
        Assert.Contains("exec node -- %*", node);
        string npm = File.ReadAllText(Path.Combine(result.ShimDirectory, "npm.cmd"));
        Assert.Contains("tool npm -- %*", npm);
    }

    [Fact]
    public async Task InstallAsync_PrefersNativeExecutableAndCreatesTenExeAliases()
    {
        Directory.CreateDirectory(_root);
        string executable = Path.Combine(_root, "autoenvplus.exe");
        string native = Path.Combine(_root, "autoenvplus-shim.exe");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(native, "native-shim-content");

        CommandShimInstallResult result = await new CommandShimManager().InstallAsync(
            _root,
            executable,
            [],
            native);

        Assert.Equal(CommandShimImplementation.NativeWin32, result.Implementation);
        Assert.Equal(10, result.ShimFiles.Count);
        Assert.All(result.ShimFiles, path =>
        {
            Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("native-shim-content", File.ReadAllText(path));
        });
        Assert.Contains(
            Path.Combine(result.ShimDirectory, "python.exe"),
            result.ShimFiles,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine(result.ShimDirectory, "npm.exe"),
            result.ShimFiles,
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(result.ShimDirectory, "*.cmd"));
    }

    [Fact]
    public async Task InstallAsync_SwitchingImplementationRemovesOnlyKnownStaleAliases()
    {
        Directory.CreateDirectory(_root);
        string executable = Path.Combine(_root, "autoenvplus.exe");
        string native = Path.Combine(_root, "autoenvplus-shim.exe");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(native, "native");
        CommandShimManager manager = new();
        CommandShimInstallResult nativeResult = await manager.InstallAsync(
            _root,
            executable,
            [],
            native);
        string unrelated = Path.Combine(nativeResult.ShimDirectory, "user-tool.exe");
        File.WriteAllText(unrelated, "keep");

        CommandShimInstallResult fallback = await manager.InstallAsync(_root, executable);

        Assert.Equal(CommandShimImplementation.CmdFallback, fallback.Implementation);
        Assert.False(File.Exists(Path.Combine(fallback.ShimDirectory, "python.exe")));
        Assert.False(File.Exists(Path.Combine(fallback.ShimDirectory, "npm.exe")));
        Assert.True(File.Exists(Path.Combine(fallback.ShimDirectory, "python.cmd")));
        Assert.True(File.Exists(Path.Combine(fallback.ShimDirectory, "npm.cmd")));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task InstallAsync_IncludesQuotedPrefixArgumentsForFrameworkDependentCli()
    {
        Directory.CreateDirectory(_root);
        string executable = Path.Combine(_root, "dotnet files", "dotnet.exe");
        string assembly = Path.Combine(_root, "AutoEnvPlus CLI's files", "autoenvplus.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);

        CommandShimInstallResult result = await new CommandShimManager().InstallAsync(
            _root,
            executable,
            [assembly]);

        string python = File.ReadAllText(Path.Combine(result.ShimDirectory, "python.cmd"));
        Assert.Contains($"\"{executable}\" \"{assembly}\" exec python -- %*", python);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
