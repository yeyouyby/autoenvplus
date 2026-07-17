using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProjectManifestServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Project-{Guid.NewGuid():N}");

    public ProjectManifestServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void FindAndLoad_SearchesParentDirectories()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_root, "src", "feature")).FullName;
        File.WriteAllText(
            Path.Combine(_root, ProjectManifestService.ManifestFileName),
            "[tools]\npython = \"3.12\"\nnode = \"22-lts\"\njava = 21\n");

        ProjectManifestLoadResult? result = new ProjectManifestService().FindAndLoad(nested);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(VersionSelector.Parse("3.12"), result.Manifest.Tools[RuntimeKind.Python]);
        Assert.Equal(VersionSelector.Parse("22-lts"), result.Manifest.Tools[RuntimeKind.NodeJs]);
        Assert.Equal(VersionSelector.Parse("21"), result.Manifest.Tools[RuntimeKind.Java]);
    }

    [Fact]
    public void Load_ReportsLineNumbersForInvalidAndDuplicateTools()
    {
        string manifest = Path.Combine(_root, ProjectManifestService.ManifestFileName);
        File.WriteAllText(
            manifest,
            "[tools]\npython = \"3.12\"\npython = \"3.13\"\nruby = \"3.4\"\nnode = \"not a version\"\n");

        ProjectManifestLoadResult result = new ProjectManifestService().Load(manifest);

        Assert.False(result.Success);
        Assert.Collection(
            result.Errors,
            error => Assert.Equal(3, error.LineNumber),
            error => Assert.Equal(4, error.LineNumber),
            error => Assert.Equal(5, error.LineNumber));
    }

    [Fact]
    public void Load_IgnoresCommentsAndOtherSections()
    {
        string manifest = Path.Combine(_root, ProjectManifestService.ManifestFileName);
        File.WriteAllText(
            manifest,
            "# project environment\n[tools]\npython = \"3.13\" # keep current\n\n[environment]\nMODE = \"development\"\n");

        ProjectManifestLoadResult result = new ProjectManifestService().Load(manifest);

        Assert.True(result.Success);
        Assert.Single(result.Manifest.Tools);
    }

    [Fact]
    public void Load_ParsesExactToolIdentityAlongsideSelector()
    {
        string manifest = Path.Combine(_root, ProjectManifestService.ManifestFileName);
        File.WriteAllText(
            manifest,
            "[tools]\npython = \"3.13.5\"\n\n[tool-identities]\npython.runtime-id = \"python-vendor-x64\"\npython.provider-id = \"vendor-python\"\n");

        ProjectManifestLoadResult result = new ProjectManifestService().Load(manifest);

        Assert.True(result.Success);
        RuntimeSelectionIdentity identity = result.Manifest.ExactSelections[RuntimeKind.Python];
        Assert.Equal("python-vendor-x64", identity.RuntimeId);
        Assert.Equal("vendor-python", identity.ProviderId);
        RuntimeProfile profile = result.Manifest.ToRuntimeProfile();
        Assert.Equal(identity, profile.ExactSelections[RuntimeKind.Python]);
    }

    [Theory]
    [InlineData("python.runtime-id = \"python-vendor-x64\"")]
    [InlineData("python.provider-id = \"vendor-python\"")]
    public void Load_RejectsUnpairedToolIdentity(string identityLine)
    {
        string manifest = Path.Combine(_root, ProjectManifestService.ManifestFileName);
        File.WriteAllText(
            manifest,
            $"[tools]\npython = \"3.13.5\"\n\n[tool-identities]\n{identityLine}\n");

        ProjectManifestLoadResult result = new ProjectManifestService().Load(manifest);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Message.Contains("both runtime-id and provider-id", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RejectsToolIdentityWithoutMatchingSelector()
    {
        string manifest = Path.Combine(_root, ProjectManifestService.ManifestFileName);
        File.WriteAllText(
            manifest,
            "[tool-identities]\npython.runtime-id = \"python-vendor-x64\"\npython.provider-id = \"vendor-python\"\n");

        ProjectManifestLoadResult result = new ProjectManifestService().Load(manifest);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Message.Contains("matching [tools] selector", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
