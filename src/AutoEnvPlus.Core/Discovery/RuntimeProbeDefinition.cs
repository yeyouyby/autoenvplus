using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Discovery;

public sealed record RuntimeProbeDefinition(
    RuntimeKind Kind,
    string Command,
    IReadOnlyList<string> Arguments)
{
    public static IReadOnlyList<RuntimeProbeDefinition> Defaults { get; } =
    [
        new(RuntimeKind.Python, "python", ["--version"]),
        new(RuntimeKind.NodeJs, "node", ["--version"]),
        new(RuntimeKind.Java, "java", ["-version"]),
        new(RuntimeKind.DotNet, "dotnet", ["--version"]),
        new(RuntimeKind.CMake, "cmake", ["--version"]),
        new(RuntimeKind.Ninja, "ninja", ["--version"]),
        new(RuntimeKind.Llvm, "clang", ["--version"]),
        new(RuntimeKind.Mingw, "gcc", ["--version"]),
    ];
}

public sealed record DiscoveredRuntime(
    RuntimeKind Kind,
    string Command,
    string ExecutablePath,
    RuntimeVersion? Version,
    string RawOutput,
    string? Error)
{
    public bool IsHealthy => Version is not null && Error is null;
}
