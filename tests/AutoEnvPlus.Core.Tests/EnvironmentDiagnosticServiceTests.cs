using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class EnvironmentDiagnosticServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Diagnostics-{Guid.NewGuid():N}");

    [Fact]
    public void Analyze_ReportsPathConflictProbeFailureMissingManagedFileAndGlobalFailure()
    {
        string first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        string missing = Path.Combine(_root, "missing");
        File.WriteAllText(Path.Combine(first, "python.exe"), string.Empty);
        File.WriteAllText(Path.Combine(second, "python.exe"), string.Empty);
        PathInspectionReport path = new PathInspector().Inspect(
            $"{first};{second};{missing};{first}",
            first,
            string.Empty,
            ["python", "node"]);
        DiscoveredRuntime unhealthy = new(
            RuntimeKind.Python,
            "python",
            Path.Combine(first, "python.exe"),
            null,
            "unexpected output",
            "Version output could not be parsed.");
        ManagedRuntimeEntry entry = CreateEntry(
            RuntimeKind.NodeJs,
            "nodejs-20-x64",
            "20.0.0",
            Path.Combine(_root, "managed", "nodejs-20"),
            "node.exe");
        RuntimeProfile global = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.NodeJs] = VersionSelector.Parse("22"),
        });

        EnvironmentDiagnosticReport report = new EnvironmentDiagnosticService(_root).Analyze(
            path,
            [unhealthy],
            new RegistryLoadResult([entry], ["registry damaged"]),
            global,
            RuntimeArchitecture.X64,
            new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero));

        Assert.False(report.IsHealthy);
        Assert.True(report.ErrorCount >= 4);
        Assert.True(report.WarningCount >= 3);
        Assert.Contains(report.Issues, issue => issue.Id.StartsWith("path-missing", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Id.StartsWith("path-duplicate", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Id == "command-conflict-python");
        Assert.Contains(report.Issues, issue => issue.Id.StartsWith("runtime-unhealthy", StringComparison.Ordinal));
        Assert.Contains(report.Issues, issue => issue.Id == "managed-missing-nodejs-20-x64");
        Assert.Contains(report.Issues, issue => issue.Id == "global-unresolved-NodeJs");
        DiagnosticCommandStatus command = report.Commands.Single(item => item.Command == "python");
        Assert.Equal(Path.Combine(first, "python.exe"), command.WinnerPath);
        Assert.Equal(2, command.CandidateCount);
    }

    [Fact]
    public void Analyze_HealthyManagedSelectionProducesNoIssues()
    {
        string installRoot = Directory.CreateDirectory(
            Path.Combine(_root, "managed", "python-3.13.5")).FullName;
        string python = Path.Combine(installRoot, "python.exe");
        File.WriteAllText(python, string.Empty);
        PathInspectionReport path = new PathInspector().Inspect(
            installRoot,
            installRoot,
            string.Empty,
            ["python"]);
        DiscoveredRuntime runtime = new(
            RuntimeKind.Python,
            "python",
            python,
            RuntimeVersion.Parse("3.13.5"),
            "Python 3.13.5",
            null);
        ManagedRuntimeEntry entry = CreateEntry(
            RuntimeKind.Python,
            "python-3.13.5-x64",
            "3.13.5",
            installRoot,
            "python.exe");
        RuntimeProfile global = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.Python] = VersionSelector.Parse("3.13.5"),
        });

        EnvironmentDiagnosticReport report = new EnvironmentDiagnosticService(_root).Analyze(
            path,
            [runtime],
            new RegistryLoadResult([entry], []),
            global,
            RuntimeArchitecture.X64);

        Assert.True(report.IsHealthy);
        Assert.Empty(report.Issues);
        DiagnosticGlobalSelection selection = Assert.Single(report.GlobalSelections);
        Assert.True(selection.Success);
        Assert.Equal(entry.Id, selection.RuntimeId);
    }

    [Fact]
    public void Analyze_ReportsDeletedExactGlobalRuntimeIdentity()
    {
        string installRoot = Directory.CreateDirectory(
            Path.Combine(_root, "managed", "python-other")).FullName;
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), string.Empty);
        ManagedRuntimeEntry available = CreateEntry(
            RuntimeKind.Python,
            "python-other-x64",
            "3.13.5",
            installRoot,
            "python.exe");
        RuntimeProfile global = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.Python] = VersionSelector.Parse("3.13.5"),
        })
        {
            ExactSelections = new Dictionary<RuntimeKind, RuntimeSelectionIdentity>
            {
                [RuntimeKind.Python] = new("python-deleted-x64", "deleted-provider"),
            },
        };

        EnvironmentDiagnosticReport report = new EnvironmentDiagnosticService(_root).Analyze(
            new PathInspectionReport([], []),
            [],
            new RegistryLoadResult([available], []),
            global,
            RuntimeArchitecture.X64,
            scopes: DiagnosticScanScope.ManagedTools);

        Assert.Contains(report.Issues, issue =>
            issue.Id == "global-exact-unresolved-Python"
            && issue.Severity == DiagnosticSeverity.Error);
        Assert.False(Assert.Single(report.GlobalSelections).Success);
    }

    [Fact]
    public void Analyze_OnlyRunsSelectedScopes()
    {
        string missing = Path.Combine(_root, "missing");
        PathInspectionReport path = new PathInspector().Inspect(
            missing,
            string.Empty,
            string.Empty,
            ["python"]);
        ManagedRuntimeEntry entry = CreateEntry(
            RuntimeKind.Python,
            "python-3.13-x64",
            "3.13.0",
            Path.Combine(_root, "managed", "python-3.13"),
            "python.exe");

        EnvironmentDiagnosticReport report = new EnvironmentDiagnosticService(_root).Analyze(
            path,
            [],
            new RegistryLoadResult([entry], ["state failed"]),
            RuntimeProfile.Empty,
            scopes: DiagnosticScanScope.ProviderConfiguration);

        Assert.Empty(report.Issues);
        Assert.Empty(report.Commands);
        Assert.Empty(report.Runtimes);
        Assert.Empty(report.GlobalSelections);
        Assert.Equal(DiagnosticScanScope.ProviderConfiguration, report.CompletedScopes);
    }

    [Fact]
    public void Analyze_ReportsDuplicateLanguageToolVersionsAndGlobalPathBypass()
    {
        string first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        string firstPython = Path.Combine(first, "python.exe");
        string secondPython = Path.Combine(second, "python.exe");
        File.WriteAllText(firstPython, string.Empty);
        File.WriteAllText(secondPython, string.Empty);
        string installRoot = Directory.CreateDirectory(
            Path.Combine(_root, "managed", "python-3.13")).FullName;
        string managedPython = Path.Combine(installRoot, "python.exe");
        File.WriteAllText(managedPython, string.Empty);
        PathInspectionReport path = new PathInspector().Inspect(
            $"{first};{second}",
            string.Empty,
            string.Empty,
            ["python"]);
        DiscoveredRuntime older = new(
            RuntimeKind.Python,
            "python",
            firstPython,
            RuntimeVersion.Parse("3.12.9"),
            "Python 3.12.9",
            null);
        DiscoveredRuntime current = new(
            RuntimeKind.Python,
            "python",
            secondPython,
            RuntimeVersion.Parse("3.13.5"),
            "Python 3.13.5",
            null);
        ManagedRuntimeEntry entry = CreateEntry(
            RuntimeKind.Python,
            "python-3.13.5-x64",
            "3.13.5",
            installRoot,
            "python.exe");
        RuntimeProfile global = new(new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.Python] = VersionSelector.Parse("3.13.5"),
        });

        EnvironmentDiagnosticReport report = new EnvironmentDiagnosticService(_root).Analyze(
            path,
            [older, current],
            new RegistryLoadResult([entry], []),
            global,
            RuntimeArchitecture.X64);

        Assert.Contains(report.Issues, issue => issue.Id == "runtime-version-drift-Python");
        Assert.Contains(report.Issues, issue => issue.Id == "global-bypassed-Python");
        Assert.All(
            report.Issues.Where(issue => issue.Id.StartsWith(
                "global-",
                StringComparison.Ordinal)),
            issue => Assert.Equal(DiagnosticScanScope.ManagedTools, issue.Scope));
    }

    [Fact]
    public void Analyze_ReportsIncompleteManagedShim()
    {
        string shimDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "shims")).FullName;
        File.WriteAllText(Path.Combine(shimDirectory, "python.cmd"), string.Empty);

        EnvironmentDiagnosticReport report = new EnvironmentDiagnosticService(_root).Analyze(
            new PathInspectionReport([], []),
            [],
            new RegistryLoadResult([], []),
            RuntimeProfile.Empty,
            scopes: DiagnosticScanScope.PathAndCommands);

        Assert.Contains(report.Issues, issue => issue.Id == "shim-aliases-missing");
        Assert.Contains(report.Issues, issue => issue.Id == "shim-aliases-empty");
        Assert.Contains(report.Issues, issue => issue.Id == "shim-not-in-user-path");
    }

    [Fact]
    public async Task InspectCurrentAsync_ProjectScopeReportsLockAndVirtualEnvironmentProblems()
    {
        string project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, ProjectManifestService.ManifestFileName),
            "[tools]\npython = \"9.9\"\n");
        string virtualEnvironment = Directory.CreateDirectory(
            Path.Combine(project, ".venv")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(virtualEnvironment, "pyvenv.cfg"),
            "version = 3.13.5\n");

        EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
            _root).InspectCurrentAsync(new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ProjectEnvironment,
                ProjectRoot = project,
            });

        Assert.Equal(DiagnosticScanScope.ProjectEnvironment, report.CompletedScopes);
        Assert.Contains(report.Issues, issue => issue.Id == "project-lock-unresolved-Python");
        Assert.Contains(
            report.Issues,
            issue => issue.Id.StartsWith(
                "project-environment-",
                StringComparison.Ordinal)
                && issue.Severity == DiagnosticSeverity.Error);
        Assert.Empty(report.Commands);
    }

    [Fact]
    public async Task InspectCurrentAsync_ProjectScopeRequiresExplicitDirectory()
    {
        EnvironmentDiagnosticService service = new(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => service.InspectCurrentAsync(
            new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ProjectEnvironment,
            }));
    }

    [Fact]
    public async Task InspectCurrentAsync_ProjectScopeReportsDeletedExactIdentity()
    {
        string project = Directory.CreateDirectory(Path.Combine(_root, "exact-project")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(project, ProjectManifestService.ManifestFileName),
            "[tools]\npython = \"3.13.5\"\n\n[tool-identities]\npython.runtime-id = \"python-deleted-x64\"\npython.provider-id = \"deleted-provider\"\n");

        EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
            _root).InspectCurrentAsync(new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ProjectEnvironment,
                ProjectRoot = project,
            });

        Assert.Contains(report.Issues, issue =>
            issue.Id == "project-exact-unresolved-Python"
            && issue.Detail.Contains("python-deleted-x64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectCurrentAsync_ManagedScopeReportsInventoryDriftAndStaleness()
    {
        LanguageToolInventoryStore store = new(_root);
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        await store.SaveAsync(new LanguageToolInventorySnapshot(
            captured,
            new string('0', 64),
            [
                new LanguageToolInventoryEntry(
                    "cpython",
                    captured.Subtract(TimeSpan.FromDays(45)),
                    ["python"]),
            ]));

        EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
            _root).InspectCurrentAsync(new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ManagedTools,
            });

        Assert.Contains(report.Issues, issue => issue.Id == "inventory-catalog-drift");
        Assert.Contains(report.Issues, issue => issue.Id == "inventory-stale");
    }

    [Fact]
    public async Task InspectCurrentAsync_ManagedScopeReportsCorruptInventoryWithoutScanning()
    {
        LanguageToolInventoryStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SnapshotPath)!);
        await File.WriteAllTextAsync(store.SnapshotPath, "{ invalid");

        EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
            _root).InspectCurrentAsync(new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ManagedTools,
            });

        Assert.Contains(report.Issues, issue => issue.Id == "inventory-invalid");
    }

    [Fact]
    public async Task InspectCurrentAsync_ProviderScopeReadsAuthoritativeSourcePreferences()
    {
        ProviderSourcePreferenceStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.PreferencesPath)!);
        await File.WriteAllTextAsync(
            store.PreferencesPath,
            """
            {
              "schemaVersion": 1,
              "overrides": [],
              "customSources": [],
              "legacyMirror": "https://invalid.example"
            }
            """);

        EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
            _root).InspectCurrentAsync(new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ProviderConfiguration,
            });

        Assert.Contains(
            report.Issues,
            issue => issue.Id == "provider-source-state-io"
                && issue.Scope == DiagnosticScanScope.ProviderConfiguration);
    }

    [Fact]
    public async Task InspectCurrentAsync_ConnectivityScopeReportsWhenConfigurationPreventsScan()
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(store.SettingsPath, "{ invalid");

        EnvironmentDiagnosticReport report = await new EnvironmentDiagnosticService(
            _root).InspectCurrentAsync(new EnvironmentDiagnosticOptions
            {
                Scopes = DiagnosticScanScope.ProviderConnectivity,
            });

        Assert.Contains(
            report.Issues,
            issue => issue.Id == "provider-connectivity-skipped"
                && issue.Scope == DiagnosticScanScope.ProviderConnectivity);
        Assert.DoesNotContain(
            report.Issues,
            issue => issue.Id.StartsWith("provider-http-", StringComparison.Ordinal)
                || issue.Id.StartsWith("provider-transport-", StringComparison.Ordinal));
    }

    [Fact]
    public void InspectManagedStoragePlacement_DoesNotCreateMissingManagedRoot()
    {
        string missingRoot = Path.Combine(_root, "missing-managed-root");
        EnvironmentDiagnosticService service = new(missingRoot);
        List<DiagnosticIssue> issues = [];

        bool safe = service.InspectManagedStoragePlacement(issues);

        Assert.True(safe);
        Assert.False(Directory.Exists(missingRoot));
        Assert.DoesNotContain(issues, issue => issue.Id == "managed-root-unsafe");
    }

    [Fact]
    public void InspectPendingDirectory_ReportsOrdinaryFileAsUnsafe()
    {
        string root = Directory.CreateDirectory(Path.Combine(_root, "staging-file")).FullName;
        string staging = Path.Combine(root, ".staging");
        File.WriteAllText(staging, "not a directory");
        List<DiagnosticIssue> issues = [];

        EnvironmentDiagnosticService.InspectPendingDirectory(
            staging,
            "runtime-staging-pending",
            "pending",
            issues);

        DiagnosticIssue issue = Assert.Single(issues);
        Assert.Equal("runtime-staging-pending-unsafe", issue.Id);
        Assert.Equal(DiagnosticSeverity.Error, issue.Severity);
        Assert.Equal("not a directory", File.ReadAllText(staging));
    }

    [Fact]
    public void InspectManagedStoragePlacement_RejectsReparsePointRootWithoutReadingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        string external = Directory.CreateDirectory(Path.Combine(_root, "external-managed")).FullName;
        string sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "external");
        string link = Path.Combine(_root, "managed-link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            List<DiagnosticIssue> issues = [];
            bool safe = new EnvironmentDiagnosticService(link)
                .InspectManagedStoragePlacement(issues);

            Assert.False(safe);
            Assert.Contains(issues, issue =>
                issue.Id == "managed-root-unsafe"
                && issue.Severity == DiagnosticSeverity.Error);
            Assert.Equal("external", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void CountPartialFilesBounded_RejectsNestedReparsePointWithoutReadingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        File.WriteAllText(Path.Combine(downloads, "valid.partial"), "partial");
        string external = Directory.CreateDirectory(Path.Combine(_root, "external-downloads")).FullName;
        string sentinel = Path.Combine(external, "secret.partial");
        File.WriteAllText(sentinel, "external");
        string link = Path.Combine(downloads, "linked");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                EnvironmentDiagnosticService.CountPartialFilesBounded(
                    downloads,
                    maximumDepth: 3,
                    maximumEntries: 4096));

            Assert.Contains("重解析点", exception.Message, StringComparison.Ordinal);
            Assert.Equal("external", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void IsOnDrive_NormalizesExtendedDrivePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(EnvironmentDiagnosticService.IsOnDrive(
            @"\\?\C:\AutoEnvPlus",
            @"C:\"));
        Assert.False(EnvironmentDiagnosticService.IsOnDrive(
            @"\\?\D:\AutoEnvPlus",
            @"C:\"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ManagedRuntimeEntry CreateEntry(
        RuntimeKind kind,
        string id,
        string version,
        string installRoot,
        string executable) => new(
            id,
            "test-provider",
            kind,
            RuntimeVersion.Parse(version),
            RuntimeArchitecture.X64,
            installRoot,
            executable,
            new string('a', 64),
            DateTimeOffset.UtcNow);
}
