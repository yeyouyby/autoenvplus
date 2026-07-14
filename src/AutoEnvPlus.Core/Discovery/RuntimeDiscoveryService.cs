using System.Diagnostics;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Discovery;

public sealed class RuntimeDiscoveryService
{
    private readonly IReadOnlyList<RuntimeProbeDefinition> _definitions;

    public RuntimeDiscoveryService(IReadOnlyList<RuntimeProbeDefinition>? definitions = null)
    {
        _definitions = definitions ?? RuntimeProbeDefinition.Defaults;
    }

    public async Task<IReadOnlyList<DiscoveredRuntime>> DiscoverCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        PathInspectionReport pathReport = new PathInspector().InspectCurrent(
            _definitions.Select(definition => definition.Command));
        return await DiscoverAsync(pathReport, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscoveredRuntime>> DiscoverAsync(
        PathInspectionReport pathReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathReport);
        List<Task<DiscoveredRuntime>> probes = [];

        foreach (RuntimeProbeDefinition definition in _definitions)
        {
            CommandResolution? resolution = pathReport.CommandResolutions.FirstOrDefault(
                item => item.Command.Equals(definition.Command, StringComparison.OrdinalIgnoreCase));

            if (resolution is null)
            {
                continue;
            }

            foreach (CommandCandidate candidate in resolution.Candidates)
            {
                probes.Add(ProbeAsync(definition, candidate, cancellationToken));
            }
        }

        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private static async Task<DiscoveredRuntime> ProbeAsync(
        RuntimeProbeDefinition definition,
        CommandCandidate candidate,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = candidate.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in definition.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return Failure(definition, candidate, "The process could not be started.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return Failure(definition, candidate, "The version probe timed out after five seconds.");
            }

            string standardOutput = await outputTask.ConfigureAwait(false);
            string standardError = await errorTask.ConfigureAwait(false);
            string rawOutput = JoinOutput(standardOutput, standardError);

            if (!RuntimeOutputParser.TryParse(
                definition.Kind,
                standardOutput,
                standardError,
                out RuntimeVersion? version))
            {
                return new DiscoveredRuntime(
                    definition.Kind,
                    definition.Command,
                    candidate.ExecutablePath,
                    null,
                    rawOutput,
                    $"Version output could not be parsed (exit code {process.ExitCode}).");
            }

            return new DiscoveredRuntime(
                definition.Kind,
                definition.Command,
                candidate.ExecutablePath,
                version,
                rawOutput,
                process.ExitCode == 0 ? null : $"Version command exited with code {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failure(definition, candidate, exception.Message);
        }
    }

    private static DiscoveredRuntime Failure(
        RuntimeProbeDefinition definition,
        CommandCandidate candidate,
        string error) =>
        new(
            definition.Kind,
            definition.Command,
            candidate.ExecutablePath,
            null,
            string.Empty,
            error);

    private static string JoinOutput(string standardOutput, string standardError) =>
        string.Join(
            System.Environment.NewLine,
            new[] { standardOutput, standardError }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
}
