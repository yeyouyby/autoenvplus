using System.Text;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProjectToolSelectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-ProjectSelection-{Guid.NewGuid():N}");
    private readonly string _managedRoot;
    private readonly string _projectRoot;

    public ProjectToolSelectionServiceTests()
    {
        _managedRoot = Directory.CreateDirectory(Path.Combine(_root, "managed")).FullName;
        _projectRoot = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
    }

    [Fact]
    public async Task CreatePlanAndApplyAsync_PreserveUnknownContentAndUpdateExactIdentity()
    {
        ManagedRuntimeEntry selected = await RegisterPythonAsync();
        string manifestPath = Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName);
        string before = """
            # Keep this project metadata.
            [metadata]
            name = "sample"

            [tools]
            node = "22" # keep Node
            python = "3.12" # switch only this value

            [tool-identities]
            node.runtime-id = "node-22-x64"
            node.provider-id = "node-org"
            python.runtime-id = "old-python"
            python.provider-id = "old-provider"

            [custom.settings]
            untouched = "yes"
            """.Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(manifestPath, before, new UTF8Encoding(false));
        ProjectToolSelectionService service = new(_managedRoot, _projectRoot);

        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(selected);

        Assert.True(plan.Changed);
        Assert.Equal(before, plan.Before);
        Assert.Contains("name = \"sample\"", plan.After, StringComparison.Ordinal);
        Assert.Contains("node = \"22\" # keep Node", plan.After, StringComparison.Ordinal);
        Assert.Contains("untouched = \"yes\"", plan.After, StringComparison.Ordinal);
        Assert.Contains(
            "python = \"3.13.5\" # switch only this value",
            plan.After,
            StringComparison.Ordinal);
        Assert.Contains(
            $"python.runtime-id = \"{selected.Id}\"",
            plan.After,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\n", plan.After.Replace("\r\n", string.Empty, StringComparison.Ordinal));

        ProjectToolSelectionResult applied = await service.ApplyAsync(plan);

        Assert.True(applied.Success);
        Assert.True(applied.Changed);
        ProjectManifestLoadResult loaded = new ProjectManifestService().Load(manifestPath);
        Assert.True(loaded.Success);
        Assert.Equal(
            new RuntimeSelectionIdentity(selected.Id, selected.ProviderId),
            loaded.Manifest.ExactSelections[RuntimeKind.Python]);
        Assert.Equal(
            VersionSelector.Parse("3.13.5"),
            loaded.Manifest.Tools[RuntimeKind.Python]);
    }

    [Fact]
    public async Task ApplyAsync_CreatesMissingManifestAtomically()
    {
        ManagedRuntimeEntry selected = await RegisterPythonAsync();
        ProjectToolSelectionService service = new(_managedRoot, _projectRoot);

        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(selected);
        ProjectToolSelectionResult applied = await service.ApplyAsync(plan);

        Assert.False(plan.ManifestExisted);
        Assert.True(applied.Success);
        Assert.True(applied.Changed);
        string manifestPath = Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName);
        ProjectManifestLoadResult loaded = new ProjectManifestService().Load(manifestPath);
        Assert.True(loaded.Success);
        Assert.Equal(selected.Id, loaded.Manifest.ExactSelections[RuntimeKind.Python].RuntimeId);
        Assert.Empty(Directory.EnumerateFiles(_projectRoot, ".autoenvplus.toml.*.tmp"));
    }

    [Fact]
    public async Task ApplyAsync_RejectsManifestChangedAfterPreview()
    {
        ManagedRuntimeEntry selected = await RegisterPythonAsync();
        string manifestPath = Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "[tools]\npython = \"3.12\"\n");
        ProjectToolSelectionService service = new(_managedRoot, _projectRoot);
        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(selected);
        await File.AppendAllTextAsync(manifestPath, "# edited by the user\n");

        ProjectToolSelectionResult applied = await service.ApplyAsync(plan);

        Assert.False(applied.Success);
        Assert.Contains("changed after the preview", applied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("edited by the user", await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task ApplyAsync_RejectsRuntimeChangedAfterPreview()
    {
        ManagedRuntimeEntry selected = await RegisterPythonAsync();
        ProjectToolSelectionService service = new(_managedRoot, _projectRoot);
        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(selected);
        await new ManagedRuntimeRegistry(_managedRoot).UpsertAsync(selected with
        {
            PackageHash = new string('b', 64),
        });

        ProjectToolSelectionResult applied = await service.ApplyAsync(plan);

        Assert.False(applied.Success);
        Assert.Contains("changed or was removed", applied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName)));
    }

    [Fact]
    public async Task ApplyAsync_RejectsTamperedPreviewContent()
    {
        ManagedRuntimeEntry selected = await RegisterPythonAsync();
        ProjectToolSelectionService service = new(_managedRoot, _projectRoot);
        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(selected);
        ProjectToolSelectionPlan tampered = plan with
        {
            After = plan.After + "\n[unexpected]\nvalue = \"tampered\"\n",
        };

        ProjectToolSelectionResult applied = await service.ApplyAsync(tampered);

        Assert.False(applied.Success);
        Assert.Contains("content changed", applied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName)));
    }

    [Fact]
    public async Task CreatePlanAndApplyAsync_PreserveUtf8Bom()
    {
        ManagedRuntimeEntry selected = await RegisterPythonAsync();
        string manifestPath = Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            "[tools]\r\npython = \"3.12\"\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        ProjectToolSelectionService service = new(_managedRoot, _projectRoot);

        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(selected);
        ProjectToolSelectionResult applied = await service.ApplyAsync(plan);
        byte[] bytes = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(plan.Utf8Bom);
        Assert.True(applied.Success);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    private async Task<ManagedRuntimeEntry> RegisterPythonAsync()
    {
        string installRoot = Directory.CreateDirectory(
            Path.Combine(_managedRoot, "runtimes", "python", "3.13.5", "x64")).FullName;
        await File.WriteAllTextAsync(Path.Combine(installRoot, "python.exe"), "runtime");
        ManagedRuntimeEntry entry = new(
            "python-3.13.5-x64",
            "python-org",
            RuntimeKind.Python,
            RuntimeVersion.Parse("3.13.5"),
            RuntimeArchitecture.X64,
            installRoot,
            "python.exe",
            new string('a', 64),
            new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
            ["stable"]);
        await new ManagedRuntimeRegistry(_managedRoot).UpsertAsync(entry);
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
