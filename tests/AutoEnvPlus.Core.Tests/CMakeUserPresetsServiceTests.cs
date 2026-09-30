using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task ApplyAndRollback_PreserveUserContentAndAddManagedPresets()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        const string original = """
            {
              "version": 4,
              "vendor": { "company.example/data": { "keep": true } },
              "configurePresets": [
                { "name": "user-ninja", "generator": "Ninja", "binaryDir": "${sourceDir}/out/user" }
              ]
            }
            """;
        File.WriteAllText(presets, original);
        VisualCppInstallation installation = CreateInstallation();
        CppArchitecturePair pair = new(
            RuntimeArchitecture.X64,
            RuntimeArchitecture.X86,
            "x64_x86");
        CMakeUserPresetsService service = new(_root, project);

        CMakeUserPresetsPlan plan = service.CreatePlan(installation, pair);

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
        JsonElement managedBuild = root.GetProperty("buildPresets")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString()
                == "autoenvplus-msvc-win32-host-x64-build");
        Assert.Equal(
            "autoenvplus-msvc-win32-host-x64",
            managedBuild.GetProperty("vendor")
                .GetProperty(CMakeUserPresetsService.VendorMarker)
                .GetProperty("configurePreset")
                .GetString());

        CMakeUserPresetsResult applied = await service.ApplyAsync(plan);
        CMakeUserPresetsResult rollback = await service.RollbackAsync(applied.SnapshotPath!);

        Assert.True(applied.Success, applied.Error);
        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal(original, File.ReadAllText(presets));
    }

    [Theory]
    [InlineData("configurePresets", "autoenvplus-msvc-x64-host-x64")]
    [InlineData("buildPresets", "autoenvplus-msvc-x64-host-x64-build")]
    public void CreatePlan_RejectsUnownedGeneratedNameCollision(
        string collectionName,
        string presetName)
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        string original = $$"""
            {
              "version": 3,
              "{{collectionName}}": [
                { "name": "{{presetName}}", "displayName": "User owned" }
              ]
            }
            """;
        File.WriteAllText(presets, original);
        CMakeUserPresetsService service = new(_root, project);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            service.CreatePlan(
                CreateInstallation(),
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X64,
                    "x64")));

        Assert.Contains("not owned by AutoEnvPlus", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(presets));
    }

    [Fact]
    public async Task ApplyAndRollback_UpgradeLegacyManagedBuildPresetWithoutVendorMarker()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        VisualCppInstallation installation = CreateInstallation();
        string legacy = CreateLegacyManagedPresets(installation);
        File.WriteAllText(presets, legacy);
        CMakeUserPresetsService service = new(_root, project);

        CMakeUserPresetsPlan plan = service.CreatePlan(
            installation,
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        CMakeUserPresetsResult result = await service.ApplyAsync(plan);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(presets));
        JsonElement buildPreset = document.RootElement
            .GetProperty("buildPresets")
            .EnumerateArray()
            .Single();
        Assert.Equal(
            "autoenvplus-msvc-x64-host-x64",
            buildPreset.GetProperty("vendor")
                .GetProperty(CMakeUserPresetsService.VendorMarker)
                .GetProperty("configurePreset")
                .GetString());

        CMakeUserPresetsResult rollback = await service.RollbackAsync(result.SnapshotPath!);

        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal(legacy, File.ReadAllText(presets));
    }

    [Theory]
    [InlineData("missing-linked-configure")]
    [InlineData("duplicate-linked-configure")]
    [InlineData("duplicate-build")]
    [InlineData("duplicate-build-near-miss")]
    [InlineData("duplicate-build-owned-last")]
    [InlineData("extra-field")]
    [InlineData("wrong-display-name")]
    [InlineData("wrong-configure-preset")]
    [InlineData("wrong-configuration")]
    public void CreatePlan_RejectsMarkerlessBuildThatIsNotExactLegacyOutput(string mutation)
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        VisualCppInstallation installation = CreateInstallation();
        JsonObject root = JsonNode.Parse(CreateLegacyManagedPresets(installation))!.AsObject();
        JsonArray configurePresets = root["configurePresets"]!.AsArray();
        JsonArray buildPresets = root["buildPresets"]!.AsArray();
        JsonObject buildPreset = buildPresets[0]!.AsObject();
        switch (mutation)
        {
            case "missing-linked-configure":
                configurePresets.Clear();
                break;
            case "duplicate-linked-configure":
                configurePresets.Add(configurePresets[0]!.DeepClone());
                break;
            case "duplicate-build":
                buildPresets.Add(buildPreset.DeepClone());
                break;
            case "duplicate-build-near-miss":
                JsonObject nearMiss = buildPreset.DeepClone().AsObject();
                nearMiss["displayName"] = "User-owned build";
                buildPresets.Add(nearMiss);
                break;
            case "duplicate-build-owned-last":
                JsonObject ownedDuplicate = buildPreset.DeepClone().AsObject();
                ownedDuplicate["vendor"] = new JsonObject
                {
                    [CMakeUserPresetsService.VendorMarker] = new JsonObject(),
                };
                buildPresets.Add(ownedDuplicate);
                break;
            case "extra-field":
                buildPreset["jobs"] = 1;
                break;
            case "wrong-display-name":
                buildPreset["displayName"] = "User-owned build";
                break;
            case "wrong-configure-preset":
                buildPreset["configurePreset"] = "user-configure";
                break;
            case "wrong-configuration":
                buildPreset["configuration"] = "Release";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        string original = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(presets, original);
        CMakeUserPresetsService service = new(_root, project);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            service.CreatePlan(
                installation,
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X64,
                    "x64")));

        Assert.Contains("not owned by AutoEnvPlus", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(presets));
    }

    [Fact]
    public async Task ApplyAsync_RevalidatesPresetOwnershipAtCommitBoundary()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        const string unowned = """
            {
              "version": 3,
              "configurePresets": [
                { "name": "autoenvplus-msvc-x64-host-x64", "generator": "Ninja" }
              ]
            }
            """;
        File.WriteAllText(presets, unowned);
        CMakeUserPresetsPlan forgedPreview = generated with
        {
            PresetsExisted = true,
            Before = unowned,
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forgedPreview);

        Assert.False(result.Success);
        Assert.Contains("not owned by AutoEnvPlus", result.Error, StringComparison.Ordinal);
        Assert.Equal(unowned, File.ReadAllText(presets));

        const string otherUnowned = """
            {
              "version": 3,
              "configurePresets": [
                { "name": "autoenvplus-msvc-win32-host-x64", "generator": "Ninja" }
              ]
            }
            """;
        File.WriteAllText(presets, otherUnowned);
        CMakeUserPresetsPlan broaderPlan = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        JsonObject forgedRoot = JsonNode.Parse(broaderPlan.After)!.AsObject();
        JsonArray configurePresets = forgedRoot["configurePresets"]!.AsArray();
        JsonNode unownedNode = configurePresets.Single(node =>
            node?["name"]?.GetValue<string>() == "autoenvplus-msvc-win32-host-x64")!;
        configurePresets.Remove(unownedNode);
        CMakeUserPresetsPlan broaderForgery = broaderPlan with
        {
            After = forgedRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
        };

        CMakeUserPresetsResult broaderResult = await service.ApplyAsync(broaderForgery);

        Assert.False(broaderResult.Success);
        Assert.Contains("not owned by AutoEnvPlus", broaderResult.Error, StringComparison.Ordinal);
        Assert.Equal(otherUnowned, File.ReadAllText(presets));
    }

    [Fact]
    public async Task ApplyAsync_RejectsForgedPlanThatAddsLegacyMarkerlessBuild()
    {
        string project = CreateProject();
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        JsonObject forgedRoot = JsonNode.Parse(generated.After)!.AsObject();
        forgedRoot["configurePresets"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "autoenvplus-msvc-win32-host-x64",
            ["vendor"] = new JsonObject
            {
                [CMakeUserPresetsService.VendorMarker] = new JsonObject(),
            },
        });
        forgedRoot["buildPresets"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "autoenvplus-msvc-win32-host-x64-build",
            ["displayName"] = "Build Win32 with AutoEnvPlus MSVC",
            ["configurePreset"] = "autoenvplus-msvc-win32-host-x64",
            ["configuration"] = "Debug",
        });
        CMakeUserPresetsPlan forged = generated with
        {
            After = forgedRoot.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }) + "\n",
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forged);

        Assert.False(result.Success);
        Assert.Contains("not owned by AutoEnvPlus", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(generated.PresetsPath));
    }

    [Fact]
    public async Task ApplyAsync_RejectsForgedLegacyBeforeWithoutLinkedConfigurePreset()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        VisualCppInstallation installation = CreateInstallation();
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            installation,
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        JsonObject legacyRoot = JsonNode.Parse(
            CreateLegacyManagedPresets(installation))!.AsObject();
        legacyRoot["configurePresets"] = new JsonArray();
        string forgedBefore = legacyRoot.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(presets, forgedBefore);
        CMakeUserPresetsPlan forged = generated with
        {
            PresetsExisted = true,
            Before = forgedBefore,
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forged);

        Assert.False(result.Success);
        Assert.Contains("not owned by AutoEnvPlus", result.Error, StringComparison.Ordinal);
        Assert.Equal(forgedBefore, File.ReadAllText(presets));
    }

    [Theory]
    [InlineData("root-property")]
    [InlineData("user-configure-preset")]
    [InlineData("user-build-preset")]
    public async Task ApplyAsync_RejectsForgedPlanThatChangesUserContent(string mutation)
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        const string original = """
            {
              "version": 4,
              "vendor": { "company.example/data": { "keep": true } },
              "configurePresets": [
                { "name": "user-ninja", "generator": "Ninja" }
              ],
              "buildPresets": [
                { "name": "user-build", "configurePreset": "user-ninja", "configuration": "Release" }
              ]
            }
            """;
        File.WriteAllText(presets, original);
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        JsonObject forgedRoot = JsonNode.Parse(generated.After)!.AsObject();
        switch (mutation)
        {
            case "root-property":
                forgedRoot["vendor"]!["company.example/data"]!["keep"] = false;
                break;
            case "user-configure-preset":
                forgedRoot["configurePresets"]!.AsArray().RemoveAt(0);
                break;
            case "user-build-preset":
                forgedRoot["buildPresets"]!.AsArray()[0]!["configuration"] = "Debug";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        CMakeUserPresetsPlan forged = generated with
        {
            After = forgedRoot.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }) + "\n",
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forged);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(original, File.ReadAllText(presets));
    }

    [Theory]
    [InlineData("configure-instance")]
    [InlineData("configure-extra-field")]
    [InlineData("configure-noncanonical-path")]
    [InlineData("build-link")]
    [InlineData("build-extra-field")]
    public async Task ApplyAsync_RejectsForgedGeneratedPresetTemplate(string mutation)
    {
        string project = CreateProject();
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        JsonObject root = JsonNode.Parse(generated.After)!.AsObject();
        JsonObject configurePreset = root["configurePresets"]!.AsArray()[0]!.AsObject();
        JsonObject buildPreset = root["buildPresets"]!.AsArray()[0]!.AsObject();
        switch (mutation)
        {
            case "configure-instance":
                configurePreset["vendor"]![CMakeUserPresetsService.VendorMarker]!
                    ["instanceId"] = "other-instance";
                break;
            case "configure-extra-field":
                configurePreset["hidden"] = true;
                break;
            case "configure-noncanonical-path":
                string instancePath = configurePreset["cacheVariables"]!
                    ["CMAKE_GENERATOR_INSTANCE"]!.GetValue<string>();
                configurePreset["cacheVariables"]!["CMAKE_GENERATOR_INSTANCE"] =
                    Path.Combine(instancePath, "..", Path.GetFileName(instancePath));
                break;
            case "build-link":
                buildPreset["configurePreset"] = "user-configure";
                break;
            case "build-extra-field":
                buildPreset["jobs"] = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        CMakeUserPresetsPlan forged = generated with
        {
            After = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forged);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(generated.PresetsPath));
    }

    [Theory]
    [InlineData("nonempty-before-for-missing-file")]
    [InlineData("null-instance-id")]
    [InlineData("null-after")]
    [InlineData("null-architecture-pair")]
    [InlineData("reformatted-after")]
    public async Task ApplyAsync_RejectsInconsistentOrNonWriterPlan(string mutation)
    {
        string project = CreateProject();
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        CMakeUserPresetsPlan forged = mutation switch
        {
            "nonempty-before-for-missing-file" => generated with
            {
                Before = "{ \"version\": 3 }",
            },
            "null-instance-id" => generated with
            {
                VisualStudioInstanceId = null!,
            },
            "null-after" => generated with
            {
                After = null!,
            },
            "null-architecture-pair" => generated with
            {
                ArchitecturePair = null!,
            },
            "reformatted-after" => generated with
            {
                After = generated.After.Replace("\n", " \n", StringComparison.Ordinal),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forged);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(generated.PresetsPath));
    }

    [Fact]
    public async Task ApplyAsync_LegacyExceptionCannotRemoveAnotherArchitectureBuild()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        VisualCppInstallation installation = CreateInstallation();
        JsonObject legacyRoot = JsonNode.Parse(
            CreateLegacyManagedPresets(installation))!.AsObject();
        JsonObject configurePreset = legacyRoot["configurePresets"]!.AsArray()[0]!.AsObject();
        configurePreset["name"] = "autoenvplus-msvc-win32-host-x64";
        configurePreset["displayName"] = "AutoEnvPlus MSVC Win32 (x64 host)";
        configurePreset["architecture"] = "Win32";
        configurePreset["binaryDir"] =
            "${sourceDir}/out/build/autoenvplus-msvc-win32-host-x64";
        JsonObject configureMarker = configurePreset["vendor"]!
            [CMakeUserPresetsService.VendorMarker]!.AsObject();
        configureMarker["targetArchitecture"] = "X86";
        configureMarker["vcVarsArgument"] = "x64_x86";
        JsonObject legacyBuild = legacyRoot["buildPresets"]!.AsArray()[0]!.AsObject();
        legacyBuild["name"] = "autoenvplus-msvc-win32-host-x64-build";
        legacyBuild["displayName"] = "Build Win32 with AutoEnvPlus MSVC";
        legacyBuild["configurePreset"] = "autoenvplus-msvc-win32-host-x64";
        string before = legacyRoot.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(presets, before);
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            installation,
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        JsonObject forgedRoot = JsonNode.Parse(generated.After)!.AsObject();
        JsonArray buildPresets = forgedRoot["buildPresets"]!.AsArray();
        JsonNode otherArchitectureBuild = buildPresets.Single(node =>
            node?["name"]?.GetValue<string>()
                == "autoenvplus-msvc-win32-host-x64-build")!;
        buildPresets.Remove(otherArchitectureBuild);
        CMakeUserPresetsPlan forged = generated with
        {
            After = forgedRoot.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }) + "\n",
        };

        CMakeUserPresetsResult result = await service.ApplyAsync(forged);

        Assert.False(result.Success);
        Assert.Contains("not owned by AutoEnvPlus", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(presets));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RollbackAsync_AcceptsLegacySnapshotWithTrailingProjectRoot(
        bool presetsExisted)
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        string before = presetsExisted
            ? "{\r\n  \"version\": 3,\r\n  \"configurePresets\": []\r\n}\r\n"
            : string.Empty;
        string legacyAfter = CreateLegacyManagedPresets(CreateInstallation());
        File.WriteAllText(presets, legacyAfter);
        CMakeUserPresetsService service = new(_root, project);
        string snapshotPath = WriteSnapshot(
            project + Path.DirectorySeparatorChar,
            presets,
            before,
            legacyAfter,
            presetsExisted);

        CMakeUserPresetsResult result = await service.RollbackAsync(snapshotPath);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        if (presetsExisted)
        {
            Assert.Equal(before, File.ReadAllText(presets));
        }
        else
        {
            Assert.False(File.Exists(presets));
        }
    }

    [Fact]
    public async Task RollbackAsync_LegacySnapshotRestoresSameNameUserCollision()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        const string before = """
            {
              "version": 3,
              "configurePresets": [
                { "name": "autoenvplus-msvc-x64-host-x64", "generator": "Ninja" }
              ],
              "buildPresets": [
                { "name": "autoenvplus-msvc-x64-host-x64-build", "displayName": "User build" }
              ]
            }
            """;
        string legacyAfter = CreateLegacyManagedPresets(CreateInstallation());
        File.WriteAllText(presets, legacyAfter);
        CMakeUserPresetsService service = new(_root, project);
        string snapshotPath = WriteSnapshot(
            project,
            presets,
            before,
            legacyAfter);

        CMakeUserPresetsResult result = await service.RollbackAsync(snapshotPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal(before, File.ReadAllText(presets));
    }

    [Fact]
    public async Task RollbackAsync_RejectsMarkerlessSameNameUserBuildInSnapshot()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        JsonObject root = JsonNode.Parse(
            CreateLegacyManagedPresets(CreateInstallation()))!.AsObject();
        JsonObject buildPreset = root["buildPresets"]!.AsArray()[0]!.AsObject();
        buildPreset["displayName"] = "User-owned build";
        string forgedAfter = root.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(presets, forgedAfter);
        CMakeUserPresetsService service = new(_root, project);
        string snapshotPath = WriteSnapshot(
            project,
            presets,
            "{ \"version\": 3 }",
            forgedAfter);

        CMakeUserPresetsResult result = await service.RollbackAsync(snapshotPath);

        Assert.False(result.Success);
        Assert.Contains("snapshot", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(forgedAfter, File.ReadAllText(presets));
    }

    [Theory]
    [InlineData("inconsistent-existence")]
    [InlineData("changed-root-property")]
    [InlineData("removed-user-preset")]
    public async Task RollbackAsync_RejectsSnapshotThatIsNotACompleteWriterTransformation(
        string mutation)
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        string after = CreateLegacyManagedPresets(CreateInstallation());
        bool presetsExisted = !mutation.Equals(
            "inconsistent-existence",
            StringComparison.Ordinal);
        string before = mutation switch
        {
            "inconsistent-existence" => "{ \"version\": 3 }",
            "changed-root-property" =>
                "{ \"version\": 3, \"vendor\": { \"forged\": true } }",
            "removed-user-preset" =>
                "{ \"version\": 3, \"configurePresets\": [ { \"name\": \"user-ninja\" } ] }",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        File.WriteAllText(presets, after);
        CMakeUserPresetsService service = new(_root, project);
        string snapshotPath = WriteSnapshot(
            project,
            presets,
            before,
            after,
            presetsExisted);

        CMakeUserPresetsResult result = await service.RollbackAsync(snapshotPath);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(after, File.ReadAllText(presets));
    }

    [Fact]
    public async Task RollbackAsync_CurrentSnapshotRejectsSameNameUserCollision()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsPlan generated = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        const string before = """
            {
              "version": 3,
              "configurePresets": [
                { "name": "autoenvplus-msvc-x64-host-x64", "generator": "Ninja" }
              ],
              "buildPresets": [
                { "name": "autoenvplus-msvc-x64-host-x64-build", "displayName": "User build" }
              ]
            }
            """;
        File.WriteAllText(presets, generated.After);
        string snapshotPath = WriteSnapshot(
            project,
            presets,
            before,
            generated.After);

        CMakeUserPresetsResult result = await service.RollbackAsync(snapshotPath);

        Assert.False(result.Success);
        Assert.Contains("not owned by AutoEnvPlus", result.Error, StringComparison.Ordinal);
        Assert.Equal(generated.After, File.ReadAllText(presets));
    }

    [Fact]
    public async Task RollbackAsync_RejectsUnchangedOrDuplicatePropertySnapshot()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        string after = CreateLegacyManagedPresets(CreateInstallation());
        File.WriteAllText(presets, after);
        CMakeUserPresetsService service = new(_root, project);
        string unchangedSnapshot = WriteSnapshot(
            project,
            presets,
            after,
            after);

        CMakeUserPresetsResult unchanged = await service.RollbackAsync(unchangedSnapshot);

        Assert.False(unchanged.Success);
        Assert.Equal(after, File.ReadAllText(presets));

        string duplicateSnapshot = WriteSnapshot(
            project,
            presets,
            "{ \"version\": 3 }",
            after);
        string duplicateEnvelope = File.ReadAllText(duplicateSnapshot).Replace(
            "\"before\":",
            "\"before\": \"duplicate\", \"before\":",
            StringComparison.Ordinal);
        File.WriteAllText(duplicateSnapshot, duplicateEnvelope);

        CMakeUserPresetsResult duplicate = await service.RollbackAsync(duplicateSnapshot);

        Assert.False(duplicate.Success);
        Assert.Contains("duplicate property", duplicate.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(after, File.ReadAllText(presets));
    }

    [Fact]
    public async Task ApplyAndRollback_RecheckExpectedContentImmediatelyBeforeCommitOrDelete()
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        const string externalContent = "{ \"version\": 3, \"vendor\": { \"external\": true } }";
        CMakeUserPresetsService applyService = new(
            _root,
            project,
            _ =>
            {
                File.WriteAllText(presets, externalContent);
                return Task.CompletedTask;
            });
        CMakeUserPresetsPlan plan = applyService.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));

        CMakeUserPresetsResult apply = await applyService.ApplyAsync(plan);

        Assert.False(apply.Success);
        Assert.Contains("changed while", apply.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(externalContent, File.ReadAllText(presets));

        File.Delete(presets);
        CMakeUserPresetsService normalService = new(_root, project);
        CMakeUserPresetsResult applied = await normalService.ApplyAsync(
            normalService.CreatePlan(
                CreateInstallation(),
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X64,
                    "x64")));
        Assert.True(applied.Success);
        string changedAfterInitialRollbackCheck = File.ReadAllText(presets) + "\n";
        CMakeUserPresetsService rollbackService = new(
            _root,
            project,
            _ =>
            {
                File.WriteAllText(presets, changedAfterInitialRollbackCheck);
                return Task.CompletedTask;
            });

        CMakeUserPresetsResult rollback = await rollbackService.RollbackAsync(
            applied.SnapshotPath!);

        Assert.False(rollback.Success);
        Assert.Contains("changed while rollback", rollback.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(changedAfterInitialRollbackCheck, File.ReadAllText(presets));
    }

    [Fact]
    public async Task ApplyAndRollback_HonorProjectSpecificCrossProcessLock()
    {
        string project = CreateProject();
        CMakeUserPresetsService service = new(_root, project);
        CMakeUserPresetsService sameProjectWithTrailingSeparator = new(
            _root,
            project + Path.DirectorySeparatorChar);
        CMakeUserPresetsPlan plan = service.CreatePlan(
            CreateInstallation(),
            new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64"));
        Directory.CreateDirectory(Path.GetDirectoryName(service.TransactionLockPath)!);
        Assert.Equal(
            service.TransactionLockPath,
            sameProjectWithTrailingSeparator.TransactionLockPath);

        using (FileStream heldLock = new(
            service.TransactionLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ApplyAsync(plan, cancellation.Token));
        }

        Assert.False(File.Exists(plan.PresetsPath));
        CMakeUserPresetsResult applied = await service.ApplyAsync(plan);
        Assert.True(applied.Success);
        string appliedContent = File.ReadAllText(plan.PresetsPath);

        using (FileStream heldLock = new(
            service.TransactionLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.RollbackAsync(applied.SnapshotPath!, cancellation.Token));
        }

        Assert.Equal(appliedContent, File.ReadAllText(plan.PresetsPath));
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

    [Theory]
    [InlineData("{ \"version\": 3, \"ver\\u0073ion\": 4 }")]
    [InlineData("{ \"version\": 3, \"vendor\": { \"key\": 1, \"k\\u0065y\": 2 } }")]
    public void CreatePlan_RejectsDuplicateJsonProperties(string content)
    {
        string project = CreateProject();
        string presets = Path.Combine(project, CMakeUserPresetsService.PresetsFileName);
        File.WriteAllText(presets, content);
        CMakeUserPresetsService service = new(_root, project);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            service.CreatePlan(
                CreateInstallation(),
                new CppArchitecturePair(
                    RuntimeArchitecture.X64,
                    RuntimeArchitecture.X64,
                    "x64")));

        Assert.Contains("duplicate property", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, File.ReadAllText(presets));
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

    private static string CreateLegacyManagedPresets(VisualCppInstallation installation)
    {
        JsonObject root = new()
        {
            ["version"] = 3,
            ["configurePresets"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "autoenvplus-msvc-x64-host-x64",
                    ["displayName"] = "AutoEnvPlus MSVC x64 (x64 host)",
                    ["description"] = $"Managed by AutoEnvPlus for {installation.DisplayName}",
                    ["generator"] = "Visual Studio 17 2022",
                    ["architecture"] = "x64",
                    ["toolset"] = "host=x64",
                    ["binaryDir"] = "${sourceDir}/out/build/autoenvplus-msvc-x64-host-x64",
                    ["cacheVariables"] = new JsonObject
                    {
                        ["CMAKE_GENERATOR_INSTANCE"] = installation.InstallationPath,
                    },
                    ["vendor"] = new JsonObject
                    {
                        [CMakeUserPresetsService.VendorMarker] = new JsonObject
                        {
                            ["instanceId"] = installation.InstanceId,
                            ["hostArchitecture"] = "X64",
                            ["targetArchitecture"] = "X64",
                            ["vcVarsArgument"] = "x64",
                        },
                    },
                },
            },
            ["buildPresets"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "autoenvplus-msvc-x64-host-x64-build",
                    ["displayName"] = "Build x64 with AutoEnvPlus MSVC",
                    ["configurePreset"] = "autoenvplus-msvc-x64-host-x64",
                    ["configuration"] = "Debug",
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private string WriteSnapshot(
        string project,
        string presets,
        string before,
        string after,
        bool presetsExisted = true)
    {
        string id = Guid.NewGuid().ToString("N");
        string directory = Directory.CreateDirectory(Path.Combine(
            _root,
            "state",
            "cmake-preset-snapshots")).FullName;
        string snapshotPath = Path.Combine(directory, id + ".json");
        CMakeUserPresetsSnapshot snapshot = new(
            id,
            DateTimeOffset.UtcNow,
            project,
            presets,
            presetsExisted,
            before,
            after);
        File.WriteAllText(
            snapshotPath,
            JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
        return snapshotPath;
    }
}
