using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Packages;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class PipLocalPackageInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-PipInstall-{Guid.NewGuid():N}");
    private readonly string _outsideRoot;
    private readonly string _library;
    private readonly string _runtimeRoot;
    private readonly string _runtimeExecutable;

    public PipLocalPackageInstallTests()
    {
        _outsideRoot = _root + "-outside";
        _library = Path.Combine(_root, "downloads", "library");
        _runtimeRoot = Path.Combine(_root, "runtimes", "python", "3.13.5", "x64");
        _runtimeExecutable = Path.Combine(_runtimeRoot, "python.exe");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_runtimeRoot);
        File.WriteAllText(_runtimeExecutable, "managed-python-runtime");
    }

    [Fact]
    public async Task CreatePlanAsync_BuildsExactOfflineCommandsAndManagedEnvironment()
    {
        string wheel = CreateWheel("torch package;--target-out.whl");
        EffectiveNetworkSettings network = new(
            NetworkToolIds.Pip,
            new Uri("http://proxy.example:8080"),
            new Uri("https://secure-proxy.example:8443"),
            ["localhost", ".corp.example"],
            new Uri("https://mirror.example/simple/"));
        PipLocalPackageInstallService service = new(_root, new RecordingRunner());

        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "torch-3.13",
            PipPackageSourceMode.OfflineManagedLibrary,
            network));

        string environmentRoot = Path.Combine(
            _root,
            "environments",
            "python",
            "torch-3.13");
        Assert.True(plan.RequiresEnvironmentCreation);
        Assert.Equal(environmentRoot, plan.EnvironmentRoot);
        Assert.Equal(_runtimeExecutable, plan.CreateEnvironmentCommand.ExecutablePath);
        Assert.Equal(
            ["-m", "venv", environmentRoot],
            plan.CreateEnvironmentCommand.ArgumentList);
        Assert.Equal(
            ["-m", "pip", "install", "--no-index", "--find-links", _library, "--no-deps", wheel],
            plan.InstallPackageCommand.ArgumentList);
        Assert.Equal(
            Path.Combine(environmentRoot, "Scripts", "python.exe"),
            plan.InstallPackageCommand.ExecutablePath);
        Assert.Equal(Path.Combine(_root, "temporary", "pip"), plan.InstallPackageCommand.Environment["TEMP"]);
        Assert.Equal(Path.Combine(_root, "temporary", "pip"), plan.InstallPackageCommand.Environment["TMP"]);
        Assert.Equal(Path.Combine(_root, "caches", "pip"), plan.InstallPackageCommand.Environment["PIP_CACHE_DIR"]);
        Assert.Equal("https://mirror.example/simple/", plan.InstallPackageCommand.Environment["PIP_INDEX_URL"]);
        Assert.Equal("http://proxy.example:8080/", plan.InstallPackageCommand.Environment["HTTP_PROXY"]);
        Assert.Equal("localhost,.corp.example", plan.InstallPackageCommand.Environment["NO_PROXY"]);
        Assert.Equal("NUL", plan.InstallPackageCommand.Environment["PIP_CONFIG_FILE"]);
        Assert.Equal("1", plan.InstallPackageCommand.Environment["PIP_NO_INDEX"]);
        Assert.Equal(_library, plan.InstallPackageCommand.Environment["PIP_FIND_LINKS"]);
        Assert.Equal(64, plan.IntegritySha256.Length);
        Assert.Equal(64, plan.WheelSnapshot.Sha256.Length);
        Assert.Equal(64, plan.RuntimeExecutableSnapshot.Sha256.Length);
    }

    [Fact]
    public async Task CreatePlanAsync_ConfiguredNetworkKeepsWheelAsOneArgument()
    {
        string wheel = CreateWheel("package name;--prefix-escape.whl");
        PipLocalPackageInstallPlan plan = await new PipLocalPackageInstallService(_root)
            .CreatePlanAsync(new(
                CreateRuntime(),
                wheel,
                "networked",
                PipPackageSourceMode.ConfiguredNetwork,
                new EffectiveNetworkSettings(
                    NetworkToolIds.Pip,
                    null,
                    null,
                    [],
                    new Uri("https://pypi.example/simple/"))));

        Assert.Equal(["-m", "pip", "install", wheel], plan.InstallPackageCommand.ArgumentList);
        Assert.DoesNotContain("--no-index", plan.InstallPackageCommand.ArgumentList);
        Assert.DoesNotContain("--no-deps", plan.InstallPackageCommand.ArgumentList);
        Assert.DoesNotContain("--prefix", plan.InstallPackageCommand.ArgumentList);
        Assert.Equal(wheel, plan.InstallPackageCommand.ArgumentList[^1]);
        Assert.Equal("https://pypi.example/simple/", plan.InstallPackageCommand.Environment["PIP_INDEX_URL"]);
        Assert.Null(plan.InstallPackageCommand.Environment["HTTP_PROXY"]);
    }

    [Fact]
    public async Task CreatePlanAsync_WithoutNetworkSettingsExplicitlyRemovesAmbientProxyAndIndex()
    {
        string wheel = CreateWheel("ambient-safe.whl");

        PipLocalPackageInstallPlan plan = await new PipLocalPackageInstallService(_root)
            .CreatePlanAsync(new(
                CreateRuntime(),
                wheel,
                "ambient-safe"));

        foreach (string name in new[]
                 {
                     "HTTP_PROXY",
                     "http_proxy",
                     "HTTPS_PROXY",
                     "https_proxy",
                     "NO_PROXY",
                     "no_proxy",
                     "ALL_PROXY",
                     "all_proxy",
                     "PIP_INDEX_URL",
                     "PIP_EXTRA_INDEX_URL",
                     "PIP_PROXY",
                     "PIP_TRUSTED_HOST",
                 })
        {
            Assert.True(plan.InstallPackageCommand.Environment.ContainsKey(name));
            Assert.Null(plan.InstallPackageCommand.Environment[name]);
        }

        Assert.Equal("NUL", plan.InstallPackageCommand.Environment["PIP_CONFIG_FILE"]);
        Assert.Equal("1", plan.InstallPackageCommand.Environment["PIP_NO_INDEX"]);
        Assert.Equal(_library, plan.InstallPackageCommand.Environment["PIP_FIND_LINKS"]);
    }

    [Theory]
    [InlineData("https://mirror.example/simple/?token=secret", true)]
    [InlineData("https://mirror.example/simple/#fragment", true)]
    [InlineData("http://proxy.example:8080/?token=secret", false)]
    [InlineData("http://proxy.example:8080/#fragment", false)]
    public async Task CreatePlanAsync_RejectsUnreviewableNetworkEndpointParts(
        string endpoint,
        bool mirror)
    {
        string wheel = CreateWheel($"endpoint-{Guid.NewGuid():N}.whl");
        Uri? proxy = mirror ? null : new Uri(endpoint);
        Uri? index = mirror ? new Uri(endpoint) : null;
        EffectiveNetworkSettings settings = new(
            NetworkToolIds.Pip,
            proxy,
            proxy,
            [],
            index);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PipLocalPackageInstallService(_root).CreatePlanAsync(new(
                CreateRuntime(),
                wheel,
                "endpoint-safe",
                PipPackageSourceMode.ConfiguredNetwork,
                settings)));
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsNonPythonEscapedAndMissingRuntimes()
    {
        string wheel = CreateWheel("package.whl");
        PipLocalPackageInstallService service = new(_root);
        ManagedRuntimeEntry node = CreateRuntime() with { Kind = RuntimeKind.NodeJs };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new(
            node,
            wheel,
            "env")));

        Directory.CreateDirectory(_outsideRoot);
        string outsideExecutable = Path.Combine(_outsideRoot, "python.exe");
        File.WriteAllText(outsideExecutable, "outside");
        ManagedRuntimeEntry outside = CreateRuntime() with
        {
            InstallRoot = _outsideRoot,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new(
            outside,
            wheel,
            "env")));

        ManagedRuntimeEntry escapedRelative = CreateRuntime() with
        {
            ExecutableRelativePath = Path.Combine("..", "x64", "python.exe"),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new(
            escapedRelative,
            wheel,
            "env")));

        File.Delete(_runtimeExecutable);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "env")));
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsEscapedNestedNonWheelAndMissingPackages()
    {
        PipLocalPackageInstallService service = new(_root);
        Directory.CreateDirectory(_outsideRoot);
        string outside = Path.Combine(_outsideRoot, "outside.whl");
        File.WriteAllText(outside, "wheel");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new(
            CreateRuntime(),
            outside,
            "env")));

        string nestedDirectory = Directory.CreateDirectory(Path.Combine(_library, "nested")).FullName;
        string nested = Path.Combine(nestedDirectory, "nested.whl");
        File.WriteAllText(nested, "wheel");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new(
            CreateRuntime(),
            nested,
            "env")));

        string archive = Path.Combine(_library, "package.zip");
        File.WriteAllText(archive, "archive");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new(
            CreateRuntime(),
            archive,
            "env")));

        string missing = Path.Combine(_library, "missing.whl");
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CreatePlanAsync(new(
            CreateRuntime(),
            missing,
            "env")));
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsWheelThatChangedAfterLibraryVerification()
    {
        Directory.CreateDirectory(_outsideRoot);
        string source = Path.Combine(_outsideRoot, "verified.whl");
        byte[] original = [1, 2, 3];
        await File.WriteAllBytesAsync(source, original);
        string expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant();
        LocalPackageImportResult imported = await new LocalPackageImportService(_library)
            .ImportAsync(new(
                source,
                "verified.whl",
                100,
                new PackageHashExpectation(PackageHashAlgorithm.Sha256, expected)));
        await File.WriteAllBytesAsync(imported.FilePath, new byte[] { 4, 5, 6 });

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PipLocalPackageInstallService(_root).CreatePlanAsync(new(
                CreateRuntime(),
                imported.FilePath,
                "changed-wheel")));

        Assert.Contains("no longer match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("with space")]
    [InlineData("env&whoami")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("COM1.tools")]
    [InlineData("a..b")]
    public async Task CreatePlanAsync_RejectsUnsafeEnvironmentNames(string environmentName)
    {
        string wheel = CreateWheel("package.whl");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PipLocalPackageInstallService(_root).CreatePlanAsync(new(
                CreateRuntime(),
                wheel,
                environmentName)));
    }

    [Fact]
    public async Task ExecuteAsync_CreatesEnvironmentThenInstallsWithReviewedCommands()
    {
        string wheel = CreateWheel("package.whl");
        RecordingRunner runner = new(async (command, invocation, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation == 1)
            {
                string environmentRoot = command.ArgumentList[2];
                string scripts = Directory.CreateDirectory(Path.Combine(environmentRoot, "Scripts")).FullName;
                File.WriteAllText(Path.Combine(scripts, "python.exe"), "venv-python");
            }

            return new PipLocalPackageProcessResult(0, $"invocation-{invocation}", string.Empty);
        });
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "created-env"));

        PipLocalPackageInstallResult result = await service.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.True(result.EnvironmentCreated);
        Assert.True(result.PackageInstalled);
        Assert.Equal(PipLocalPackageRollbackBehavior.NotTransactional, result.RollbackBehavior);
        Assert.Equal(2, runner.Commands.Count);
        AssertCommandEqual(plan.CreateEnvironmentCommand, runner.Commands[0]);
        AssertCommandEqual(plan.InstallPackageCommand, runner.Commands[1]);
        Assert.Equal(PipLocalPackageInstallStage.Completed, result.FinalStage);
        Assert.Collection(
            result.Stages,
            stage => Assert.Equal(PipLocalPackageInstallStage.Validating, stage.Stage),
            stage => Assert.Equal(PipLocalPackageInstallStage.CreatingEnvironment, stage.Stage),
            stage => Assert.Equal(PipLocalPackageInstallStage.InstallingPackage, stage.Stage),
            stage => Assert.Equal(PipLocalPackageInstallStage.Completed, stage.Stage));
    }

    [Fact]
    public async Task ExecuteAsync_UsesExistingEnvironmentWithoutRunningVenv()
    {
        string wheel = CreateWheel("package.whl");
        string environmentExecutable = CreateEnvironment("existing");
        RecordingRunner runner = new();
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "existing"));

        PipLocalPackageInstallResult result = await service.ExecuteAsync(plan);

        Assert.True(result.Success);
        Assert.False(result.EnvironmentCreated);
        PipLocalPackageInstallCommand command = Assert.Single(runner.Commands);
        Assert.Equal(environmentExecutable, command.ExecutablePath);
        Assert.Equal(
            PipLocalPackageInstallStageStatus.Skipped,
            result.Stages.Single(stage => stage.Stage == PipLocalPackageInstallStage.CreatingEnvironment).Status);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureWithoutClaimingRollback()
    {
        string wheel = CreateWheel("package.whl");
        string environmentExecutable = CreateEnvironment("failing");
        RecordingRunner runner = new((_, _, _) => Task.FromResult(
            new PipLocalPackageProcessResult(9, string.Empty, "install failed")));
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "failing"));

        PipLocalPackageInstallResult result = await service.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.False(result.PackageInstalled);
        Assert.Equal(PipLocalPackageRollbackBehavior.NotTransactional, result.RollbackBehavior);
        Assert.Contains("No transactional rollback", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(environmentExecutable));
        Assert.Equal(9, result.Stages[^1].ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsWhichCapturedProcessStreamsWereTruncated()
    {
        string wheel = CreateWheel("truncated-output.whl");
        CreateEnvironment("truncated-output");
        RecordingRunner runner = new((_, _, _) => Task.FromResult(
            new PipLocalPackageProcessResult(
                9,
                "stdout tail",
                "stderr tail",
                StandardOutputTruncated: true,
                StandardErrorTruncated: true)));
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "truncated-output"));

        PipLocalPackageInstallResult result = await service.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        PipLocalPackageInstallStageResult failed = Assert.Single(
            result.Stages,
            stage => stage.Status == PipLocalPackageInstallStageStatus.Failed);
        Assert.Equal("stdout tail", failed.StandardOutput);
        Assert.Equal("stderr tail", failed.StandardError);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCancelledResultAndPassesCancellationToRunner()
    {
        string wheel = CreateWheel("package.whl");
        CreateEnvironment("cancelled");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunner runner = new(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PipLocalPackageProcessResult(0, string.Empty, string.Empty);
        });
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "cancelled"));
        using CancellationTokenSource cancellation = new();

        Task<PipLocalPackageInstallResult> execution = service.ExecuteAsync(
            plan,
            cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        PipLocalPackageInstallResult result = await execution;

        Assert.False(result.Success);
        Assert.True(result.WasCancelled);
        Assert.Equal(PipLocalPackageRollbackBehavior.NotTransactional, result.RollbackBehavior);
        Assert.Single(runner.Commands);
        Assert.Contains(
            result.Stages,
            stage => stage.Status == PipLocalPackageInstallStageStatus.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsTamperedCommandBeforeStartingProcess()
    {
        string wheel = CreateWheel("package.whl");
        RecordingRunner runner = new();
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "reviewed"));
        PipLocalPackageInstallPlan tampered = plan with
        {
            InstallPackageCommand = plan.InstallPackageCommand with
            {
                ArgumentList = ["-m", "pip", "install", wheel, "--target", _outsideRoot],
            },
        };

        PipLocalPackageInstallResult result = await service.ExecuteAsync(tampered);

        Assert.False(result.Success);
        Assert.Empty(runner.Commands);
        Assert.Equal(PipLocalPackageInstallStage.Validating, result.FinalStage);
        Assert.Contains("modified", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsWheelChangedAfterPlanning()
    {
        string wheel = CreateWheel("package.whl");
        RecordingRunner runner = new();
        PipLocalPackageInstallService service = new(_root, runner);
        PipLocalPackageInstallPlan plan = await service.CreatePlanAsync(new(
            CreateRuntime(),
            wheel,
            "stale"));
        File.AppendAllText(wheel, "changed");

        PipLocalPackageInstallResult result = await service.ExecuteAsync(plan);

        Assert.False(result.Success);
        Assert.Empty(runner.Commands);
        Assert.Equal(PipLocalPackageInstallStage.Validating, result.FinalStage);
        Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunner_DrainsButBoundsLargeChildOutput()
    {
        string commandInterpreter = System.Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
        PipLocalPackageInstallCommand command = new(
            commandInterpreter,
            [
                "/d",
                "/c",
                "for /L %i in (1,1,10000) do @echo 0123456789-output-%i",
            ],
            _root,
            new Dictionary<string, string?>());

        PipLocalPackageProcessResult result = await new PipLocalPackageProcessRunner()
            .RunAsync(command);

        Assert.True(result.Success);
        Assert.True(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
        Assert.InRange(
            result.StandardOutput.Length,
            1,
            PipLocalPackageProcessRunner.MaximumCapturedOutputCharacters);
        Assert.Empty(result.StandardError);
        Assert.Contains("10000", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunner_CancellationTerminatesChildAndDoesNotWaitForUnboundedOutput()
    {
        string ping = Path.Combine(System.Environment.SystemDirectory, "PING.EXE");
        PipLocalPackageInstallCommand command = new(
            ping,
            ["127.0.0.1", "-t"],
            _root,
            new Dictionary<string, string?>());
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PipLocalPackageProcessRunner().RunAsync(command, cancellation.Token));
    }

    private ManagedRuntimeEntry CreateRuntime() => new(
        "python-3.13.5-x64",
        "python-org",
        RuntimeKind.Python,
        RuntimeVersion.Parse("3.13.5"),
        RuntimeArchitecture.X64,
        _runtimeRoot,
        "python.exe",
        new string('a', 64),
        new DateTimeOffset(2026, 7, 15, 4, 0, 0, TimeSpan.Zero),
        ["stable"]);

    private string CreateWheel(string fileName)
    {
        string path = Path.Combine(_library, fileName);
        File.WriteAllText(path, "wheel-content");
        return path;
    }

    private string CreateEnvironment(string name)
    {
        string scripts = Directory.CreateDirectory(Path.Combine(
            _root,
            "environments",
            "python",
            name,
            "Scripts")).FullName;
        string executable = Path.Combine(scripts, "python.exe");
        File.WriteAllText(executable, "venv-python");
        return executable;
    }

    private static void AssertCommandEqual(
        PipLocalPackageInstallCommand expected,
        PipLocalPackageInstallCommand actual)
    {
        Assert.Equal(expected.ExecutablePath, actual.ExecutablePath);
        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.ArgumentList, actual.ArgumentList);
        Assert.Equal(
            expected.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            actual.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_outsideRoot))
        {
            Directory.Delete(_outsideRoot, recursive: true);
        }
    }

    private sealed class RecordingRunner(
        Func<PipLocalPackageInstallCommand, int, CancellationToken, Task<PipLocalPackageProcessResult>>? handler = null)
        : IPipLocalPackageProcessRunner
    {
        public List<PipLocalPackageInstallCommand> Commands { get; } = [];

        public Task<PipLocalPackageProcessResult> RunAsync(
            PipLocalPackageInstallCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return handler?.Invoke(command, Commands.Count, cancellationToken)
                ?? Task.FromResult(new PipLocalPackageProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
        }
    }
}
