using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProjectEnvironmentImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Import-{Guid.NewGuid():N}");

    [Fact]
    public void Discover_ImportsNearestProjectMarkersAndExplainsConflicts()
    {
        string project = Directory.CreateDirectory(Path.Combine(_root, "project", "src", "feature")).Parent!.Parent!.FullName;
        string nested = Path.Combine(project, "src", "feature");
        File.WriteAllText(Path.Combine(project, ".python-version"), "3.12.7\n");
        File.WriteAllText(Path.Combine(project, ".nvmrc"), "v22.17.0\n");
        File.WriteAllText(Path.Combine(project, ".node-version"), "20.19.0\n");
        File.WriteAllText(Path.Combine(project, ".java-version"), "21\n");
        File.WriteAllText(
            Path.Combine(project, "package.json"),
            "{\"engines\":{\"node\":\"^20.19.0 || >=22.12.0\"}}");
        File.WriteAllText(
            Path.Combine(project, "global.json"),
            "{\"sdk\":{\"version\":\"10.0.200\"}}");

        ProjectEnvironmentImportResult result = new ProjectEnvironmentImportService().Discover(nested);

        Assert.True(result.Found);
        Assert.Equal(project, result.ProjectRoot);
        Assert.Equal(VersionSelector.Parse("3.12.7"), result.Selections[RuntimeKind.Python]);
        Assert.Equal(VersionSelector.Parse("22.17.0"), result.Selections[RuntimeKind.NodeJs]);
        Assert.Equal(VersionSelector.Parse("21"), result.Selections[RuntimeKind.Java]);
        Assert.Equal(VersionSelector.Parse("10.0.200"), result.Selections[RuntimeKind.DotNet]);
        Assert.Contains(result.Warnings, warning => warning.Contains("higher-priority", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("compound range", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("lts/*", "lts")]
    [InlineData("node", "latest")]
    [InlineData("22.x", "22")]
    public void ImportDirectory_NormalizesLosslessNodeSelectors(
        string nodeValue,
        string expected)
    {
        string project = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        if (nodeValue is "lts/*" or "node")
        {
            File.WriteAllText(Path.Combine(project, ".nvmrc"), nodeValue);
        }
        else
        {
            File.WriteAllText(
                Path.Combine(project, "package.json"),
                $"{{\"engines\":{{\"node\":\"{nodeValue}\"}}}}");
        }

        ProjectEnvironmentImportResult result = new ProjectEnvironmentImportService().ImportDirectory(project);

        Assert.Equal(VersionSelector.Parse(expected), result.Selections[RuntimeKind.NodeJs]);
    }

    [Fact]
    public async Task WriteManifestAsync_CreatesLoadableManifestAndProtectsExistingFile()
    {
        string project = Directory.CreateDirectory(Path.Combine(_root, "write-project")).FullName;
        File.WriteAllText(Path.Combine(project, ".python-version"), "3.13");
        File.WriteAllText(Path.Combine(project, ".nvmrc"), "24");
        ProjectEnvironmentImportService service = new();
        ProjectEnvironmentImportResult import = service.ImportDirectory(project);

        string manifestPath = await service.WriteManifestAsync(import);
        ProjectManifestLoadResult loaded = new ProjectManifestService().Load(manifestPath);

        Assert.True(loaded.Success);
        Assert.Equal(VersionSelector.Parse("3.13"), loaded.Manifest.Tools[RuntimeKind.Python]);
        Assert.Equal(VersionSelector.Parse("24"), loaded.Manifest.Tools[RuntimeKind.NodeJs]);
        await Assert.ThrowsAsync<IOException>(() => service.WriteManifestAsync(import));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
