using AutoEnvPlus.Core.Projects;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProjectVirtualEnvironmentDiscoveryServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-VirtualEnvironment-{Guid.NewGuid():N}")).FullName;
    private readonly List<string> _reparsePoints = [];

    [Fact]
    public void Discover_FindsProjectLocalEnvironmentsWithoutExecutingTools()
    {
        string project = CreateProject("complete");
        CreateFile(project, "poetry.lock", string.Empty);
        CreateFile(project, "pyproject.toml", "[tool.poetry]\nname = \"sample\"\n");
        CreateFile(
            project,
            Path.Combine(".venv", "pyvenv.cfg"),
            "home = D:\\Python\nversion = 3.13.5\ninclude-system-site-packages = false\n");
        CreateFile(project, Path.Combine(".venv", "Scripts", "python.exe"), string.Empty);
        CreateFile(
            project,
            Path.Combine("env", "conda-meta", "history"),
            "+defaults/win-64::python-3.12.8-h14ffc60_0\n");
        CreateFile(project, Path.Combine("env", "python.exe"), string.Empty);

        Directory.CreateDirectory(Path.Combine(project, "node_modules", ".bin"));
        CreateFile(project, "pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        CreateFile(project, "package.json", "{\"packageManager\":\"pnpm@10.13.1\"}");
        CreateFile(
            project,
            Path.Combine(".config", "dotnet-tools.json"),
            "{\"version\":1,\"isRoot\":true,\"tools\":{\"dotnet-ef\":{\"version\":\"9.0.7\",\"commands\":[\"dotnet-ef\"]}}}");

        CreateFile(project, "mvnw.cmd", "@echo off\n");
        CreateFile(
            project,
            Path.Combine(".mvn", "wrapper", "maven-wrapper.properties"),
            "distributionUrl=https://repo.example/apache-maven-3.9.10-bin.zip\n");
        CreateFile(project, "gradlew.bat", "@echo off\n");
        CreateFile(
            project,
            Path.Combine("gradle", "wrapper", "gradle-wrapper.properties"),
            "distributionUrl=https://services.gradle.org/distributions/gradle-8.14.3-bin.zip\n");

        CreateFile(project, "rust-toolchain.toml", "[toolchain]\nchannel = \"1.88.0\"\n");
        Directory.CreateDirectory(Path.Combine(project, "target"));
        CreateFile(project, "go.work", "go 1.24.0\ntoolchain go1.24.5\nuse ./module\n");

        ProjectVirtualEnvironmentDiscoveryResult result = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(project);

        ProjectVirtualEnvironment python = Assert.Single(
            result.Environments,
            item => item.LanguageId == "python" && item.Manager == "poetry");
        Assert.Equal("3.13.5", python.Version);
        Assert.Equal(ProjectVirtualEnvironmentHealth.Healthy, python.Health);
        Assert.EndsWith(Path.Combine(".venv", "Scripts", "python.exe"), python.Executable);
        Assert.Contains(python.Evidence, item => item == "include-system-site-packages=false");

        ProjectVirtualEnvironment conda = Assert.Single(
            result.Environments,
            item => item.LanguageId == "python" && item.Manager == "conda");
        Assert.Equal("3.12.8", conda.Version);
        Assert.EndsWith(Path.Combine("env", "python.exe"), conda.Executable);

        ProjectVirtualEnvironment node = Assert.Single(
            result.Environments,
            item => item.LanguageId == "nodejs");
        Assert.Equal("pnpm", node.Manager);
        Assert.Equal("10.13.1", node.Version);
        Assert.Equal(ProjectVirtualEnvironmentHealth.Healthy, node.Health);

        ProjectVirtualEnvironment dotnet = Assert.Single(
            result.Environments,
            item => item.LanguageId == "dotnet");
        Assert.Equal("9.0.7", dotnet.Version);
        Assert.Contains(dotnet.Evidence, item => item == "tool=dotnet-ef@9.0.7");

        ProjectVirtualEnvironment maven = Assert.Single(
            result.Environments,
            item => item.Manager == "maven-wrapper");
        Assert.Equal("3.9.10", maven.Version);
        ProjectVirtualEnvironment gradle = Assert.Single(
            result.Environments,
            item => item.Manager == "gradle-wrapper");
        Assert.Equal("8.14.3", gradle.Version);

        ProjectVirtualEnvironment rust = Assert.Single(
            result.Environments,
            item => item.LanguageId == "rust");
        Assert.Equal("1.88.0", rust.Version);
        Assert.Contains(rust.Evidence, item => item.EndsWith("target", StringComparison.OrdinalIgnoreCase));
        ProjectVirtualEnvironment go = Assert.Single(
            result.Environments,
            item => item.LanguageId == "go");
        Assert.Equal("1.24.5", go.Version);

        Assert.All(result.Environments, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.LanguageId));
            Assert.False(string.IsNullOrWhiteSpace(item.Kind));
            Assert.False(string.IsNullOrWhiteSpace(item.Manager));
            Assert.True(Path.IsPathFullyQualified(item.Root));
            Assert.NotEmpty(item.Evidence);
        });
        Assert.False(result.ScanLimitReached);
    }

    [Fact]
    public void Discover_ReportsPoetryAndPipenvDefinitionsWithoutInventingAnEnvironment()
    {
        string project = CreateProject("manager-definitions");
        CreateFile(project, "poetry.lock", string.Empty);
        CreateFile(project, "Pipfile.lock", "{}");

        ProjectVirtualEnvironmentDiscoveryResult result = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(project);

        ProjectVirtualEnvironment[] definitions = result.Environments
            .Where(item => item.LanguageId == "python" && item.Kind == "environment-definition")
            .ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.Contains(definitions, item => item.Manager == "poetry");
        Assert.Contains(definitions, item => item.Manager == "pipenv");
        Assert.All(definitions, item =>
        {
            Assert.Null(item.Executable);
            Assert.Equal(ProjectVirtualEnvironmentHealth.NeedsAttention, item.Health);
            Assert.Contains(item.Warnings, warning => warning.Contains("not found", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Discover_ContainsCorruptAndOversizedConfigurationsAsInvalidResults()
    {
        string project = CreateProject("invalid-configurations");
        CreateFile(
            project,
            Path.Combine(".venv", "pyvenv.cfg"),
            new string('x', 80));
        CreateFile(project, Path.Combine(".venv", "Scripts", "python.exe"), string.Empty);
        CreateFile(project, Path.Combine(".config", "dotnet-tools.json"), "{\"version\":1,\"tools\":");

        ProjectVirtualEnvironmentDiscoveryResult result = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(
                project,
                new ProjectVirtualEnvironmentDiscoveryOptions { MaxConfigFileBytes = 32 });

        ProjectVirtualEnvironment python = Assert.Single(
            result.Environments,
            item => item.LanguageId == "python");
        Assert.Equal(ProjectVirtualEnvironmentHealth.Invalid, python.Health);
        Assert.Contains(python.Warnings, warning => warning.Contains("32-byte limit", StringComparison.Ordinal));

        ProjectVirtualEnvironment dotnet = Assert.Single(
            result.Environments,
            item => item.LanguageId == "dotnet");
        Assert.Equal(ProjectVirtualEnvironmentHealth.Invalid, dotnet.Health);
        Assert.Contains(dotnet.Warnings, warning => warning.Contains("invalid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_SkipsEnvironmentBehindReparsePointWhenSupported()
    {
        string project = CreateProject("reparse");
        string external = Directory.CreateDirectory(Path.Combine(_root, "external-environment")).FullName;
        CreateFile(
            external,
            "pyvenv.cfg",
            "home = D:\\Python\nversion = 3.12.1\ninclude-system-site-packages = false\n");
        CreateFile(external, Path.Combine("Scripts", "python.exe"), string.Empty);
        string link = Path.Combine(project, ".venv");
        try
        {
            Directory.CreateSymbolicLink(link, external);
            _reparsePoints.Add(link);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        ProjectVirtualEnvironmentDiscoveryResult result = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(project);

        Assert.DoesNotContain(result.Environments, item => item.LanguageId == "python");
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("reparse point", StringComparison.OrdinalIgnoreCase)
            && warning.Contains(".venv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "home = D:\\Python\nversion = 3.12.1\ninclude-system-site-packages = false\n",
            File.ReadAllText(Path.Combine(external, "pyvenv.cfg")));
    }

    [Fact]
    public void Discover_EnforcesPathAndResultLimits()
    {
        string pathLimitedProject = CreateProject("path-limit");
        ProjectVirtualEnvironmentDiscoveryResult pathLimited = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(
                pathLimitedProject,
                new ProjectVirtualEnvironmentDiscoveryOptions { MaxInspectedPaths = 1 });

        Assert.Equal(1, pathLimited.InspectedPathCount);
        Assert.True(pathLimited.ScanLimitReached);
        Assert.Contains(pathLimited.Warnings, warning => warning.Contains("candidate paths", StringComparison.Ordinal));

        string resultLimitedProject = CreateProject("result-limit");
        CreateFile(resultLimitedProject, "poetry.lock", string.Empty);
        CreateFile(resultLimitedProject, "Pipfile.lock", "{}");
        CreateFile(resultLimitedProject, "pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        ProjectVirtualEnvironmentDiscoveryResult resultLimited = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(
                resultLimitedProject,
                new ProjectVirtualEnvironmentDiscoveryOptions { MaxResults = 2 });

        Assert.Equal(2, resultLimited.Environments.Count);
        Assert.True(resultLimited.ScanLimitReached);
        Assert.Contains(resultLimited.Warnings, warning => warning.Contains("at most 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_ReturnsDeterministicLanguageKindRootManagerOrder()
    {
        string project = CreateProject("sorting");
        CreateFile(project, "go.work", "go 1.23.0\n");
        CreateFile(project, "rust-toolchain", "stable\n");
        CreateFile(project, "package-lock.json", "{}");
        CreateFile(
            project,
            Path.Combine(".config", "dotnet-tools.json"),
            "{\"version\":1,\"tools\":{}}");
        CreateFile(project, "mvnw.cmd", "@echo off\n");

        ProjectVirtualEnvironmentDiscoveryResult result = new ProjectVirtualEnvironmentDiscoveryService()
            .Discover(project);
        string[] actual = result.Environments
            .Select(item => $"{item.LanguageId}|{item.Kind}|{item.Root}|{item.Manager}")
            .ToArray();
        string[] sorted = actual
            .OrderBy(item => item.Split('|')[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Split('|')[1], StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Split('|')[2], StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Split('|')[3], StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(sorted, actual);
    }

    public void Dispose()
    {
        foreach (string reparsePoint in _reparsePoints)
        {
            try
            {
                Directory.Delete(reparsePoint);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateProject(string name) => Directory.CreateDirectory(
        Path.Combine(_root, name)).FullName;

    private static string CreateFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
