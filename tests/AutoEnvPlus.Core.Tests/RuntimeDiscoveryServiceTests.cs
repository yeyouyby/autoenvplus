using System.Collections.Concurrent;
using System.Diagnostics;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class RuntimeDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ProbesOnlyWinnersWithBoundedConcurrency()
    {
        RuntimeProbeDefinition[] definitions = Enumerable.Range(0, 6)
            .Select(index => new RuntimeProbeDefinition(
                RuntimeKind.Python,
                $"runtime-{index}",
                []))
            .ToArray();
        PathInspectionReport report = new(
            [],
            definitions.Select((definition, index) => new CommandResolution(
                definition.Command,
                [
                    new CommandCandidate(definition.Command, $"winner-{index}.exe", index),
                    new CommandCandidate(definition.Command, $"shadowed-{index}.exe", index + 10),
                ])).ToArray());
        ConcurrentBag<string> probedPaths = [];
        object concurrencyLock = new();
        int active = 0;
        int maximumActive = 0;

        RuntimeDiscoveryService service = new(
            definitions,
            maximumConcurrentProbes: 2,
            maximumCapturedBytesPerStream: 1024,
            probeTimeout: TimeSpan.FromSeconds(5),
            probeRunner: async (definition, candidate, cancellationToken) =>
            {
                lock (concurrencyLock)
                {
                    active++;
                    maximumActive = Math.Max(maximumActive, active);
                }

                try
                {
                    probedPaths.Add(candidate.ExecutablePath);
                    await Task.Delay(75, cancellationToken);
                    return new DiscoveredRuntime(
                        definition.Kind,
                        definition.Command,
                        candidate.ExecutablePath,
                        RuntimeVersion.Parse("3.13.5"),
                        "Python 3.13.5",
                        null);
                }
                finally
                {
                    lock (concurrencyLock)
                    {
                        active--;
                    }
                }
            });

        IReadOnlyList<DiscoveredRuntime> results = await service.DiscoverAsync(report);

        Assert.Equal(definitions.Length, results.Count);
        Assert.InRange(maximumActive, 1, 2);
        Assert.Equal(definitions.Length, probedPaths.Count);
        Assert.All(probedPaths, path => Assert.StartsWith("winner-", path, StringComparison.Ordinal));
        Assert.DoesNotContain(probedPaths, path => path.StartsWith("shadowed-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_CapsCapturedOutputWhileContinuingToDrainTheProcess()
    {
        string powershell = GetWindowsPowerShell();
        RuntimeProbeDefinition definition = new(
            RuntimeKind.Python,
            "probe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Out.Write('Python 3.13.5 ' + ('x' * 200000))",
            ]);
        RuntimeDiscoveryService service = new(
            [definition],
            maximumConcurrentProbes: 1,
            maximumCapturedBytesPerStream: 1024,
            probeTimeout: TimeSpan.FromSeconds(10),
            probeRunner: null);

        DiscoveredRuntime result = Assert.Single(await service.DiscoverAsync(
            CreateReport("probe", powershell)));

        Assert.Equal(RuntimeVersion.Parse("3.13.5"), result.Version);
        Assert.Contains("standard output truncated", result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(result.RawOutput.Length, 1, 1200);
    }

    [Fact]
    public async Task DiscoverAsync_KillsAndWaitsForProbeOnExternalCancellation()
    {
        string powershell = GetWindowsPowerShell();
        RuntimeProbeDefinition definition = new(
            RuntimeKind.Python,
            "probe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
        RuntimeDiscoveryService service = new(
            [definition],
            maximumConcurrentProbes: 1,
            maximumCapturedBytesPerStream: 1024,
            probeTimeout: TimeSpan.FromMinutes(1),
            probeRunner: null);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
        Stopwatch elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DiscoverAsync(CreateReport("probe", powershell), cancellation.Token));

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"Canceled discovery took {elapsed.Elapsed} to terminate its probe.");
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsTimedOutFailureAfterTerminatingProbe()
    {
        string powershell = GetWindowsPowerShell();
        RuntimeProbeDefinition definition = new(
            RuntimeKind.Python,
            "probe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
        RuntimeDiscoveryService service = new(
            [definition],
            maximumConcurrentProbes: 1,
            maximumCapturedBytesPerStream: 1024,
            probeTimeout: TimeSpan.FromMilliseconds(500),
            probeRunner: null);
        Stopwatch elapsed = Stopwatch.StartNew();

        DiscoveredRuntime result = Assert.Single(await service.DiscoverAsync(
            CreateReport("probe", powershell)));

        Assert.Null(result.Version);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"Timed-out discovery took {elapsed.Elapsed} to terminate its probe.");
    }

    private static PathInspectionReport CreateReport(string command, string executable) => new(
        [],
        [new CommandResolution(command, [new CommandCandidate(command, executable, 0)])]);

    private static string GetWindowsPowerShell()
    {
        string path = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(path), $"Windows PowerShell was not found: {path}");
        return path;
    }
}
