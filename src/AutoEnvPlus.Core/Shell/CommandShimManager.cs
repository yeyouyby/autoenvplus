using System.Text;

namespace AutoEnvPlus.Core.Shell;

public sealed record CommandShimInstallResult(
    string ShimDirectory,
    IReadOnlyList<string> ShimFiles,
    CommandShimImplementation Implementation);

public enum CommandShimImplementation
{
    NativeWin32,
    CmdFallback,
}

public sealed class CommandShimManager
{
    private static readonly IReadOnlyDictionary<string, string> Commands =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["python.cmd"] = "python",
            ["python3.cmd"] = "python",
            ["node.cmd"] = "node",
            ["java.cmd"] = "java",
        };

    private static readonly IReadOnlyDictionary<string, string> ToolCommands =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pip.cmd"] = "pip",
            ["pip3.cmd"] = "pip3",
            ["npm.cmd"] = "npm",
            ["npx.cmd"] = "npx",
            ["javac.cmd"] = "javac",
            ["jar.cmd"] = "jar",
        };

    public async Task<CommandShimInstallResult> InstallAsync(
        string managedRoot,
        string autoEnvPlusExecutable,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(
            managedRoot,
            autoEnvPlusExecutable,
            [],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandShimInstallResult> InstallAsync(
        string managedRoot,
        string autoEnvPlusExecutable,
        IReadOnlyList<string> autoEnvPlusPrefixArguments,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(
            managedRoot,
            autoEnvPlusExecutable,
            autoEnvPlusPrefixArguments,
            nativeShimExecutable: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandShimInstallResult> InstallAsync(
        string managedRoot,
        string autoEnvPlusExecutable,
        IReadOnlyList<string> autoEnvPlusPrefixArguments,
        string? nativeShimExecutable,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(autoEnvPlusExecutable);
        ArgumentNullException.ThrowIfNull(autoEnvPlusPrefixArguments);
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        string executable = Path.GetFullPath(autoEnvPlusExecutable);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The AutoEnvPlus CLI executable does not exist.", executable);
        }

        string shimDirectory = Path.Combine(fullManagedRoot, "shims");
        Directory.CreateDirectory(shimDirectory);
        if (!string.IsNullOrWhiteSpace(nativeShimExecutable))
        {
            string native = Path.GetFullPath(nativeShimExecutable);
            if (!File.Exists(native))
            {
                throw new FileNotFoundException(
                    "The native AutoEnvPlus Shim executable does not exist.",
                    native);
            }

            List<string> nativeFiles = [];
            foreach (string alias in Commands.Keys
                .Concat(ToolCommands.Keys)
                .Select(fileName => Path.GetFileNameWithoutExtension(fileName)
                    ?? throw new InvalidOperationException("A Shim alias does not have a file name.")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(shimDirectory, alias + ".exe");
                string temporary = destination + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(native, temporary, overwrite: false);
                    File.Move(temporary, destination, overwrite: true);
                    nativeFiles.Add(destination);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }

            foreach (string stale in Commands.Keys.Concat(ToolCommands.Keys))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stalePath = Path.Combine(shimDirectory, stale);
                if (File.Exists(stalePath))
                {
                    File.Delete(stalePath);
                }
            }

            return new CommandShimInstallResult(
                shimDirectory,
                nativeFiles,
                CommandShimImplementation.NativeWin32);
        }

        string invocation = string.Join(
            " ",
            new[] { executable }
                .Concat(autoEnvPlusPrefixArguments)
                .Select(QuoteCmdArgument));
        List<string> installed = [];
        foreach ((string fileName, string runtime) in Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(shimDirectory, fileName);
            string temporary = destination + $".{Guid.NewGuid():N}.tmp";
            string content = $"@echo off\r\n{invocation} exec {runtime} -- %*\r\nexit /b %errorlevel%\r\n";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                installed.Add(destination);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        foreach ((string fileName, string tool) in ToolCommands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(shimDirectory, fileName);
            string temporary = destination + $".{Guid.NewGuid():N}.tmp";
            string content = $"@echo off\r\n{invocation} tool {tool} -- %*\r\nexit /b %errorlevel%\r\n";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                installed.Add(destination);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        foreach (string alias in Commands.Keys
            .Concat(ToolCommands.Keys)
            .Select(fileName => Path.GetFileNameWithoutExtension(fileName)
                ?? throw new InvalidOperationException("A Shim alias does not have a file name.")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stalePath = Path.Combine(shimDirectory, alias + ".exe");
            if (File.Exists(stalePath))
            {
                File.Delete(stalePath);
            }
        }

        return new CommandShimInstallResult(
            shimDirectory,
            installed,
            CommandShimImplementation.CmdFallback);
    }

    private static string QuoteCmdArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        string escaped = argument
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
