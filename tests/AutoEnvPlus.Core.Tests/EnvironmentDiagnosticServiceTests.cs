using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
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
        string bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        string python = Path.Combine(bin, "python.exe");
        File.WriteAllText(python, string.Empty);
        PathInspectionReport path = new PathInspector().Inspect(bin, bin, string.Empty, ["python"]);
        DiscoveredRuntime runtime = new(
            RuntimeKind.Python,
            "python",
            python,
            RuntimeVersion.Parse("3.13.5"),
            "Python 3.13.5",
            null);
        string installRoot = Directory.CreateDirectory(
            Path.Combine(_root, "managed", "python-3.13.5")).FullName;
        File.WriteAllText(Path.Combine(installRoot, "python.exe"), string.Empty);
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
