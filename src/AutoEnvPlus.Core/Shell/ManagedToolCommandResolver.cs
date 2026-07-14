using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Shell;

public sealed record ManagedToolCommand(
    string Alias,
    RuntimeKind RuntimeKind,
    ManagedRuntimeEntry Runtime,
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments);

public sealed record ManagedToolCommandResult(
    ManagedToolCommand? Command,
    IReadOnlyList<string> Errors)
{
    public bool Success => Command is not null && Errors.Count == 0;
}

public sealed class ManagedToolCommandResolver
{
    private static readonly IReadOnlyDictionary<string, ToolDefinition> Definitions =
        new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["pip"] = new(RuntimeKind.Python, ToolLaunchMode.RuntimeExecutable, null, ["-m", "pip"]),
            ["pip3"] = new(RuntimeKind.Python, ToolLaunchMode.RuntimeExecutable, null, ["-m", "pip"]),
            ["npm"] = new(RuntimeKind.NodeJs, ToolLaunchMode.RuntimeExecutable, @"node_modules\npm\bin\npm-cli.js", []),
            ["npx"] = new(RuntimeKind.NodeJs, ToolLaunchMode.RuntimeExecutable, @"node_modules\npm\bin\npx-cli.js", []),
            ["javac"] = new(RuntimeKind.Java, ToolLaunchMode.RelativeExecutable, @"bin\javac.exe", []),
            ["jar"] = new(RuntimeKind.Java, ToolLaunchMode.RelativeExecutable, @"bin\jar.exe", []),
        };

    private readonly string _managedRoot;

    public ManagedToolCommandResolver(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
    }

    public static IReadOnlyCollection<string> SupportedAliases => Definitions.Keys.ToArray();

    public static bool TryGetRuntimeKind(string alias, out RuntimeKind kind)
    {
        if (Definitions.TryGetValue(alias, out ToolDefinition? definition))
        {
            kind = definition.RuntimeKind;
            return true;
        }

        kind = default;
        return false;
    }

    public async Task<ManagedToolCommandResult> ResolveAsync(
        string alias,
        string startPath,
        RuntimeProfile? sessionProfile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if (!Definitions.TryGetValue(alias, out ToolDefinition? definition))
        {
            return new ManagedToolCommandResult(null, [$"Unsupported managed tool alias: {alias}"]);
        }

        ManagedRuntimeResolutionResult resolved = await new ManagedRuntimeResolutionService(_managedRoot).ResolveAsync(
            definition.RuntimeKind,
            startPath,
            sessionProfile,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!resolved.Success)
        {
            return new ManagedToolCommandResult(null, resolved.Errors);
        }

        ManagedRuntimeEntry runtime = resolved.Entry!;
        string executable;
        List<string> prefix = [.. definition.PrefixArguments];
        if (definition.Mode == ToolLaunchMode.RuntimeExecutable)
        {
            executable = runtime.ExecutablePath;
            if (definition.RelativeToolPath is string relativeScript)
            {
                string scriptPath = ResolveInside(runtime.InstallRoot, relativeScript);
                if (!File.Exists(scriptPath))
                {
                    return new ManagedToolCommandResult(
                        null,
                        [$"The selected {runtime.Kind} installation does not contain {relativeScript}."]);
                }

                prefix.Insert(0, scriptPath);
            }
        }
        else
        {
            executable = ResolveInside(runtime.InstallRoot, definition.RelativeToolPath!);
            if (!File.Exists(executable))
            {
                return new ManagedToolCommandResult(
                    null,
                    [$"The selected {runtime.Kind} installation does not contain {definition.RelativeToolPath}."]);
            }
        }

        return new ManagedToolCommandResult(
            new ManagedToolCommand(alias, definition.RuntimeKind, runtime, executable, prefix),
            []);
    }

    private static string ResolveInside(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string prefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Managed tool path escaped its runtime root.");
        }

        return path;
    }

    private enum ToolLaunchMode
    {
        RuntimeExecutable,
        RelativeExecutable,
    }

    private sealed record ToolDefinition(
        RuntimeKind RuntimeKind,
        ToolLaunchMode Mode,
        string? RelativeToolPath,
        IReadOnlyList<string> PrefixArguments);
}
