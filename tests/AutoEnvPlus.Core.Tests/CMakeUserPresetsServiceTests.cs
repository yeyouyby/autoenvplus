using System.Text.Json;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Tests;

public sealed class CMakeUserPresetsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-CMakePresets-{Guid.NewGuid():N}");

    [Fact]
    public void CreatePlan_PreservesUserPresetsAndAddsManagedConfigureAndBuildPresets()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        File.WriteAllText(
            presets,
            """
            {
              "version": 4,
              "vendor": { "company.example/data": { "keep": true } },
              "configurePresets": [
                { "name": "user-ninja", "generator": "Ninja", "binaryDir": "${sourceDir}/out/user" }
              ]
            }
            """);
        VisualCppInstallation installation = CreateInstallation();
        CppArchitecturePair pair = new(
            RuntimeArchitecture.X64,
            RuntimeArchitecture.X86,
            "x64_x86");

        CMakeUserPresetsPlan plan = new CMakeUserPresetsService(_root, project)
            .CreatePlan(installation, pair);

        using JsonDocument document = JsonDocument.Parse(plan.After);
        JsonElement root = document.RootElement;
        Assert.Equal(4, root.GetProperty("version").GetInt32());
        Assert.True(root.GetProperty("vendor")
            .GetProperty("company.example/data")
            .GetProperty("keep")
            .GetBoolean());
        JsonElement[] configure = root.GetProperty("configurePresets").EnumerateArray().ToArray();
        Assert.Contains(configure, item => item.GetProperty("name").GetString() == "user-ninja");
        JsonElement managed = configure.Single(item => item.GetProperty("name").GetString()
            == "autoenvplus-msvc-win32-host-x64");
        Assert.Equal("Visual Studio 17 2022", managed.GetProperty("generator").GetString());
        Assert.Equal("Win32", managed.GetProperty("architecture").GetString());
        Assert.Equal("host=x64", managed.GetProperty("toolset").GetString());
        Assert.Equal(
            installation.InstallationPath,
            managed.GetProperty("cacheVariables").GetProperty("CMAKE_GENERATOR_INSTANCE").GetString());
        Assert.Contains(
            root.GetProperty("buildPresets").EnumerateArray(),
            item => item.GetProperty("configurePreset").GetString()
                == "autoenvplus-msvc-win32-host-x64");
    }

    [Fact]
    public async Task ApplyAsync_IsIdempotentAndRollbackRestoresExactFile()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        const string original = "{\r\n  \"version\": 3,\r\n  \"configurePresets\": []\r\n}\r\n";
        File.WriteAllText(presets, original);
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan plan = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));

        CMakeUserPresetsResult applied = await service.ApplyAsync(plan);
        CMakeUserPresetsPlan secondPlan = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        CMakeUserPresetsResult second = await service.ApplyAsync(secondPlan);
        CMakeUserPresetsResult rollback = await service.RollbackAsync(applied.SnapshotPath!);

        Assert.True(applied.Success);
        Assert.NotNull(applied.SnapshotPath);
        Assert.False(second.Changed);
        Assert.True(rollback.Success);
        Assert.Equal(original, File.ReadAllText(presets));
    }

    [Fact]
    public async Task ApplyAndRollback_RefuseNewerChangesAndOutsideSnapshots()
    {
        string project = CreateProject();
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan plan = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        File.WriteAllText(plan.PresetsPath, "{ \"version\": 3 }");

        CMakeUserPresetsResult concurrent = await service.ApplyAsync(plan);
        Assert.False(concurrent.Success);
        Assert.Contains("changed after", concurrent.Error, StringComparison.OrdinalIgnoreCase);

        File.Delete(plan.PresetsPath);
        CMakeUserPresetsResult applied = await service.ApplyAsync(
            service.CreatePlan(
                CreateInstallation(),
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X64,
                    "x64")));
        File.AppendAllText(applied.PresetsPath, "\n");
        CMakeUserPresetsResult newer = await service.RollbackAsync(applied.SnapshotPath!);
        CMakeUserPresetsResult outside = await service.RollbackAsync(
            Path.Combine(_root, "outside.json"));

        Assert.False(newer.Success);
        Assert.Contains("newer", newer.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(outside.Success);
        Assert.Contains("escaped", outside.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreatePlan_RejectsMalformedPresetArraysAndUnsupportedVisualStudio()
    {
        string project = CreateProject();
        File.WriteAllText(
            Path.Combine(project, CMakeUserPresetsService.PresetsFileName),
            "{ \"version\": 3, \"configurePresets\": {} }");
        CMakeUserPresetsService service = new(_root, project);
        CppArchitecturePair pair = new(
            RuntimeArchitecture.X64,
            RuntimeArchitecture.X64,
            "x64");

        Assert.Throws<InvalidDataException>(() => service.CreatePlan(
            CreateInstallation(),
            pair));
        File.Delete(Path.Combine(project, CMakeUserPresetsService.PresetsFileName));
        Assert.Throws<NotSupportedException>(() => service.CreatePlan(
            CreateInstallation() with { VisualStudioVersion = "18.0" },
            pair));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateProject()
    {
        string project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.25)");
        return project;
    }

    private VisualCppInstallation CreateInstallation()
    {
        string installationPath = Directory.CreateDirectory(
            Path.Combine(_root, "Visual Studio 2022")).FullName;
        return new VisualCppInstallation(
            "vs-test",
            "Visual Studio 2022",
            installationPath,
            "17.14.0",
            "14.44",
            Path.Combine(installationPath, "vcvarsall.bat"),
            true,
            true,
            [
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X64,
                    "x64"),
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X86,
                    "x64_x86"),
            ]);
    }
}
