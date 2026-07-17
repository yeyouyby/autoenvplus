using System.ComponentModel;
using System.Diagnostics;

namespace AutoEnvPlus.Core.Packages;

public sealed class PipLocalPackageProcessRunner : IPipLocalPackageProcessRunner
{
    public const int MaximumCapturedOutputCharacters = 65_536;

    public async Task<PipLocalPackageProcessResult> RunAsync(
        PipLocalPackageInstallCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in command.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in command.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new PipLocalPackageProcessResult(
                    -1,
                    string.Empty,
                    string.Empty,
                    "The managed Python process could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException)
        {
            return new PipLocalPackageProcessResult(
                -1,
                string.Empty,
                string.Empty,
                exception.Message);
        }

        Task<BoundedTextResult> outputTask = ReadBoundedAsync(process.StandardOutput);
        Task<BoundedTextResult> errorTask = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await WaitForTerminationAsync(process).ConfigureAwait(false);
            await ObserveOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        BoundedTextResult standardOutput = await outputTask.ConfigureAwait(false);
        BoundedTextResult standardError = await errorTask.ConfigureAwait(false);
        return new PipLocalPackageProcessResult(
            process.ExitCode,
            standardOutput.Text,
            standardError.Text,
            StandardOutputTruncated: standardOutput.Truncated,
            StandardErrorTruncated: standardError.Truncated);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or NotSupportedException)
        {
            // Cancellation remains authoritative if the process exits during the kill attempt.
        }
    }

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process exited between cancellation and the follow-up wait.
        }
    }

    private static async Task ObserveOutputAsync(
        Task<BoundedTextResult> outputTask,
        Task<BoundedTextResult> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            // The child streams may close abruptly when the process tree is terminated.
        }
    }

    private static async Task<BoundedTextResult> ReadBoundedAsync(StreamReader reader)
    {
        BoundedTextBuffer captured = new(MaximumCapturedOutputCharacters);
        char[] buffer = new char[4_096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            captured.Append(buffer.AsSpan(0, read));
        }

        return new BoundedTextResult(captured.GetText(), captured.Truncated);
    }

    private sealed class BoundedTextBuffer(int capacity)
    {
        private readonly char[] _buffer = new char[capacity];
        private int _start;
        private int _count;

        public bool Truncated { get; private set; }

        public void Append(ReadOnlySpan<char> value)
        {
            if (value.Length >= _buffer.Length)
            {
                value[^_buffer.Length..].CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                Truncated = true;
                return;
            }

            int overflow = Math.Max(0, _count + value.Length - _buffer.Length);
            if (overflow > 0)
            {
                _start = (_start + overflow) % _buffer.Length;
                _count -= overflow;
                Truncated = true;
            }

            int writeIndex = (_start + _count) % _buffer.Length;
            int firstLength = Math.Min(value.Length, _buffer.Length - writeIndex);
            value[..firstLength].CopyTo(_buffer.AsSpan(writeIndex));
            value[firstLength..].CopyTo(_buffer);
            _count += value.Length;
        }

        public string GetText()
        {
            if (_count == 0)
            {
                return string.Empty;
            }

            char[] result = new char[_count];
            int firstLength = Math.Min(_count, _buffer.Length - _start);
            _buffer.AsSpan(_start, firstLength).CopyTo(result);
            _buffer.AsSpan(0, _count - firstLength).CopyTo(result.AsSpan(firstLength));
            return new string(result);
        }
    }

    private sealed record BoundedTextResult(string Text, bool Truncated);
}
