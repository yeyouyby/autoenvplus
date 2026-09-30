using System.Diagnostics;
using System.Text;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Discovery;

public sealed class RuntimeDiscoveryService
{
    private const int DefaultMaximumConcurrentProbes = 4;
    private const int DefaultMaximumCapturedBytesPerStream = 64 * 1024;
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<RuntimeProbeDefinition> _definitions;
    private readonly int _maximumConcurrentProbes;
    private readonly int _maximumCapturedBytesPerStream;
    private readonly TimeSpan _probeTimeout;
    private readonly Func<RuntimeProbeDefinition, CommandCandidate, CancellationToken, Task<DiscoveredRuntime>>
        _probeRunner;

    public RuntimeDiscoveryService(IReadOnlyList<RuntimeProbeDefinition>? definitions = null)
        : this(
            definitions ?? RuntimeProbeDefinition.Defaults,
            DefaultMaximumConcurrentProbes,
            DefaultMaximumCapturedBytesPerStream,
            DefaultProbeTimeout,
            probeRunner: null)
    {
    }

    internal RuntimeDiscoveryService(
        IReadOnlyList<RuntimeProbeDefinition> definitions,
        int maximumConcurrentProbes,
        int maximumCapturedBytesPerStream,
        TimeSpan probeTimeout,
        Func<RuntimeProbeDefinition, CommandCandidate, CancellationToken, Task<DiscoveredRuntime>>?
            probeRunner)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (maximumConcurrentProbes is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentProbes));
        }

        if (maximumCapturedBytesPerStream is <= 0 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCapturedBytesPerStream));
        }

        if (probeTimeout <= TimeSpan.Zero || probeTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        _definitions = definitions;
        _maximumConcurrentProbes = maximumConcurrentProbes;
        _maximumCapturedBytesPerStream = maximumCapturedBytesPerStream;
        _probeTimeout = probeTimeout;
        _probeRunner = probeRunner ?? ProbeAsync;
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
        List<(RuntimeProbeDefinition Definition, CommandCandidate Candidate)> work = [];

        foreach (RuntimeProbeDefinition definition in _definitions)
        {
            CommandResolution? resolution = pathReport.CommandResolutions.FirstOrDefault(
                item => item.Command.Equals(definition.Command, StringComparison.OrdinalIgnoreCase));

            if (resolution?.Winner is not { } winner)
            {
                continue;
            }

            work.Add((definition, winner));
        }

        DiscoveredRuntime?[] results = new DiscoveredRuntime?[work.Count];
        using SemaphoreSlim gate = new(_maximumConcurrentProbes, _maximumConcurrentProbes);
        Task[] probes = work.Select((item, index) => RunProbeAsync(item, index)).ToArray();
        await Task.WhenAll(probes).ConfigureAwait(false);
        return results.Select(result => result!).ToArray();

        async Task RunProbeAsync(
            (RuntimeProbeDefinition Definition, CommandCandidate Candidate) item,
            int index)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await _probeRunner(
                    item.Definition,
                    item.Candidate,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<DiscoveredRuntime> ProbeAsync(
        RuntimeProbeDefinition definition,
        CommandCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

            using CancellationTokenSource timeout = new(_probeTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            Task<BoundedOutput> outputTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                process.StandardOutput.CurrentEncoding,
                linked.Token);
            Task<BoundedOutput> errorTask = ReadBoundedAsync(
                process.StandardError.BaseStream,
                process.StandardError.CurrentEncoding,
                linked.Token);

            BoundedOutput standardOutput;
            BoundedOutput standardError;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                standardOutput = await outputTask.ConfigureAwait(false);
                standardError = await errorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TerminateProcessTreeAndWaitAsync(process).ConfigureAwait(false);
                await ObserveCaptureTasksAsync(outputTask, errorTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return Failure(
                    definition,
                    candidate,
                    $"The version probe timed out after {_probeTimeout.TotalSeconds:0.###} seconds.");
            }

            string rawOutput = JoinOutput(standardOutput, standardError);

            if (!RuntimeOutputParser.TryParse(
                definition.Kind,
                standardOutput.Text,
                standardError.Text,
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
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
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

    private async Task<BoundedOutput> ReadBoundedAsync(
        Stream stream,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        using MemoryStream captured = new(_maximumCapturedBytesPerStream);
        bool truncated = false;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int remaining = _maximumCapturedBytesPerStream - checked((int)captured.Length);
            int toCapture = Math.Min(read, Math.Max(remaining, 0));
            if (toCapture > 0)
            {
                captured.Write(buffer, 0, toCapture);
            }

            truncated |= toCapture < read;
        }

        return new BoundedOutput(
            encoding.GetString(captured.GetBuffer(), 0, checked((int)captured.Length)),
            truncated);
    }

    private static async Task TerminateProcessTreeAndWaitAsync(Process process)
    {
        bool waitWithoutTimeout = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            waitWithoutTimeout = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
        }

        try
        {
            if (waitWithoutTimeout)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or OperationCanceledException)
        {
        }
    }

    private static async Task ObserveCaptureTasksAsync(params Task<BoundedOutput>[] captures)
    {
        try
        {
            await Task.WhenAll(captures).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string JoinOutput(BoundedOutput standardOutput, BoundedOutput standardError) =>
        string.Join(
            System.Environment.NewLine,
            new[]
                {
                    FormatOutput(standardOutput, "standard output"),
                    FormatOutput(standardError, "standard error"),
                }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatOutput(BoundedOutput output, string description)
    {
        string text = output.Text.Trim();
        return output.Truncated
            ? string.IsNullOrEmpty(text)
                ? $"[{description} truncated]"
                : text + System.Environment.NewLine + $"[{description} truncated]"
            : text;
    }

    private sealed record BoundedOutput(string Text, bool Truncated);
}
