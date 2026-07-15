using System.Diagnostics;
using System.Text.Json;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class NativeShimContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-NativeShim-{Guid.NewGuid():N}");

    [Fact]
    public async Task NativeShim_UsesSessionProjectGlobalPriorityAndForwardsExitCode()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();

        ProcessResult global = await RunAsync(
            environment.Shim,
            environment.Project,
            ["12", "global value"]);
        Directory.CreateDirectory(Path.Combine(environment.Project, "nested"));
        await File.WriteAllTextAsync(
            Path.Combine(environment.Project, "autoenvplus.toml"),
            "[tools]\npython = \"3.13\"\n");
        ProcessResult project = await RunAsync(
            environment.Shim,
            Path.Combine(environment.Project, "nested"),
            ["13", "project value"]);
        ProcessResult session = await RunAsync(
            environment.Shim,
            environment.Project,
            ["12", "session value"],
            new Dictionary<string, string?>
            {
                ["AUTOENVPLUS_PYTHON_VERSION"] = "3.12.8",
                ["AUTOENVPLUS_SHIM_TRACE"] = "1",
            });

        Assert.Equal(12, global.ExitCode);
        Assert.Contains("VERSION=12", global.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARG0=global value", global.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(13, project.ExitCode);
        Assert.Contains("VERSION=13", project.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARG0=project value", project.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(12, session.ExitCode);
        Assert.Contains("VERSION=12", session.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("(Session)", session.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeShim_RejectsRecursionAndMalformedRegistry()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();
        ProcessResult recursion = await RunAsync(
            environment.Shim,
            environment.Project,
            [],
            new Dictionary<string, string?>
            {
                ["AUTOENVPLUS_SHIM_DEPTH"] = "4",
            });
        await File.WriteAllTextAsync(
            Path.Combine(environment.ManagedRoot, "state", "installations.json"),
            "{ \"schemaVersion\": 1, \"installations\": [null] }");
        ProcessResult malformed = await RunAsync(
            environment.Shim,
            environment.Project,
            []);

        Assert.Equal(70, recursion.ExitCode);
        Assert.Contains("recursion limit", recursion.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(70, malformed.ExitCode);
        Assert.Contains("non-object entry", malformed.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeShim_ResolvesDerivedPipThroughSelectedPython()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();
        string pip = Path.Combine(environment.ManagedRoot, "shims", "pip.exe");
        File.Copy(environment.Shim, pip);

        ProcessResult result = await RunAsync(
            pip,
            environment.Project,
            ["12", "install", "package name"],
            new Dictionary<string, string?>
            {
                ["AUTOENVPLUS_TEST_EXIT"] = "12",
            });

        Assert.Equal(12, result.ExitCode);
        Assert.Contains("VERSION=-m", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARG0=pip", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARG1=12", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARG3=package name", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeShim_SelectsRuntimeMatchingItsProcessArchitecture()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();
        string pythonX86 = Directory.CreateDirectory(Path.Combine(
            environment.ManagedRoot,
            "runtimes",
            "python",
            "3.12.8",
            "x86")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(pythonX86, "python.cmd"),
            "@echo off\r\necho ARCH=x86\r\nexit /b 86\r\n");
        await new ManagedRuntimeRegistry(environment.ManagedRoot).UpsertAsync(new ManagedRuntimeEntry(
            "python-3.12.8-x86",
            "test-provider",
            RuntimeKind.Python,
            RuntimeVersion.Parse("3.12.8"),
            RuntimeArchitecture.X86,
            pythonX86,
            "python.cmd",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            ["latest"]));

        ProcessResult result = await RunAsync(
            environment.Shim,
            environment.Project,
            ["12", "architecture"]);

        Assert.Equal(12, result.ExitCode);
        Assert.Contains("VERSION=12", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ARCH=x86", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeShim_AcceptsLegacySchemaOneRegistryWithInferredSha256()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();
        string pythonRoot = Path.Combine(
            environment.ManagedRoot,
            "runtimes",
            "python",
            "3.12.8",
            "x64");
        string registry = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            installations = new[]
            {
                new
                {
                    id = "python-3.12.8-x64",
                    providerId = "legacy-provider",
                    kind = "Python",
                    version = "3.12.8",
                    architecture = "X64",
                    installRoot = pythonRoot,
                    executableRelativePath = "python.cmd",
                    packageSha256 = new string('a', 64),
                    channels = new[] { "latest" },
                },
            },
        });
        await File.WriteAllTextAsync(
            Path.Combine(environment.ManagedRoot, "state", "installations.json"),
            registry);

        ProcessResult result = await RunAsync(
            environment.Shim,
            environment.Project,
            ["12", "legacy schema"]);

        Assert.Equal(12, result.ExitCode);
        Assert.Contains("VERSION=12", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ARG0=legacy schema", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeShim_LaunchesDotNetFromSchemaTwoSha512AndSetsIsolationEnvironment()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();
        string dotnetRoot = Directory.CreateDirectory(Path.Combine(
            environment.ManagedRoot,
            "runtimes",
            "dotnet",
            "sdk",
            "10.0.302",
            "x64")).FullName;
        const string fakeDotNet = """
            @echo off
            echo DOTNET_ROOT=%DOTNET_ROOT%
            echo DOTNET_MULTILEVEL_LOOKUP=%DOTNET_MULTILEVEL_LOOKUP%
            echo PATH=%PATH%
            exit /b 0
            """;
        await File.WriteAllTextAsync(Path.Combine(dotnetRoot, "dotnet.cmd"), fakeDotNet);
        await new ManagedRuntimeRegistry(environment.ManagedRoot).UpsertAsync(new ManagedRuntimeEntry(
            "dotnet-10.0.302-x64",
            "microsoft-dotnet-sdk",
            RuntimeKind.DotNet,
            RuntimeVersion.Parse("10.0.302"),
            RuntimeArchitecture.X64,
            dotnetRoot,
            "dotnet.cmd",
            new string('b', 128),
            DateTimeOffset.UtcNow,
            ["stable"],
            PackageHashAlgorithm.Sha512));
        string dotnetShim = Path.Combine(environment.ManagedRoot, "shims", "dotnet.exe");
        File.Copy(environment.Shim, dotnetShim);

        ProcessResult result = await RunAsync(
            dotnetShim,
            environment.Project,
            [],
            new Dictionary<string, string?>
            {
                ["DOTNET_ROOT"] = @"C:\system-dotnet",
                ["DOTNET_MULTILEVEL_LOOKUP"] = "1",
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"DOTNET_ROOT={dotnetRoot}",
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DOTNET_MULTILEVEL_LOOKUP=0",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            $"PATH={dotnetRoot};",
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeShim_RejectsSchemaTwoHashThatDoesNotMatchItsAlgorithm()
    {
        TestEnvironment environment = await CreateEnvironmentAsync();
        string pythonRoot = Path.Combine(
            environment.ManagedRoot,
            "runtimes",
            "python",
            "3.12.8",
            "x64");
        string registry = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            installations = new[]
            {
                new
                {
                    id = "python-3.12.8-x64",
                    providerId = "test-provider",
                    kind = "Python",
                    version = "3.12.8",
                    architecture = "X64",
                    installRoot = pythonRoot,
                    executableRelativePath = "python.cmd",
                    packageHash = new string('a', 64),
                    packageHashAlgorithm = "Sha512",
                    channels = new[] { "latest" },
                },
            },
        });
        await File.WriteAllTextAsync(
            Path.Combine(environment.ManagedRoot, "state", "installations.json"),
            registry);

        ProcessResult result = await RunAsync(
            environment.Shim,
            environment.Project,
            []);

        Assert.Equal(70, result.ExitCode);
        Assert.Contains("registry entry", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (
                attempt < 19 &&
                (exception is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(50);
            }
        }
    }

    private async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        string native = Path.Combine(AppContext.BaseDirectory, "autoenvplus-shim.exe");
        Assert.True(File.Exists(native), $"Native Shim was not copied to test output: {native}");
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        string state = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        string project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        string python312 = Directory.CreateDirectory(
            Path.Combine(_root, "runtimes", "python", "3.12.8", "x64")).FullName;
        string python313 = Directory.CreateDirectory(
            Path.Combine(_root, "runtimes", "python", "3.13.5", "x64")).FullName;
        string fakeRuntime = """
            @echo off
            set "exitCode=%AUTOENVPLUS_TEST_EXIT%"
            if not defined exitCode set "exitCode=%~1"
            echo VERSION=%~1
            set index=0
            :loop
            if "%~2"=="" goto done
            echo ARG%index%=%~2
            set /a index+=1
            shift
            goto loop
            :done
            exit /b %exitCode%
            """;
        await File.WriteAllTextAsync(Path.Combine(python312, "python.cmd"), fakeRuntime);
        await File.WriteAllTextAsync(Path.Combine(python313, "python.cmd"), fakeRuntime);
        ManagedRuntimeRegistry registry = new(_root);
        await registry.UpsertAsync(CreateEntry("3.12.8", python312));
        await registry.UpsertAsync(CreateEntry("3.13.5", python313));
        await new GlobalRuntimeProfileStore(_root).SetAsync(
            RuntimeKind.Python,
            VersionSelector.Parse("3.12"));
        string shim = Path.Combine(shims, "python.exe");
        File.Copy(native, shim);
        return new TestEnvironment(_root, project, shim);
    }

    private static ManagedRuntimeEntry CreateEntry(string version, string root) => new(
        $"python-{version}-x64",
        "test-provider",
        RuntimeKind.Python,
        RuntimeVersion.Parse(version),
        RuntimeArchitecture.X64,
        root,
        "python.cmd",
        new string('a', 64),
        DateTimeOffset.UtcNow,
        ["latest"]);

    private static async Task<ProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start native Shim test process.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private sealed record TestEnvironment(
        string ManagedRoot,
        string Project,
        string Shim);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
