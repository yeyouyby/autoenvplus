using System.Collections.Frozen;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Toolchains;

public static class ToolchainRuntimeProviderPolicy
{
    private static readonly FrozenSet<RuntimeKind> DeclarativeKinds = new[]
    {
        RuntimeKind.Msvc,
        RuntimeKind.Llvm,
        RuntimeKind.Mingw,
        RuntimeKind.CMake,
        RuntimeKind.Ninja,
    }.ToFrozenSet();

    public static IReadOnlySet<RuntimeKind> DeclarativeRuntimeKinds => DeclarativeKinds;

    public static bool RequiresExplicitPlugin(RuntimeKind kind) =>
        DeclarativeKinds.Contains(kind);

    public static RuntimeKind GetRuntimeKind(ToolchainComponent component) => component switch
    {
        ToolchainComponent.MsvcBuildTools => RuntimeKind.Msvc,
        ToolchainComponent.Llvm => RuntimeKind.Llvm,
        ToolchainComponent.MinGw => RuntimeKind.Mingw,
        ToolchainComponent.CMake => RuntimeKind.CMake,
        ToolchainComponent.Ninja => RuntimeKind.Ninja,
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    public static string GetActivationNotice(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Msvc =>
            "A declarative MSVC ZIP is registered only as a managed executable tool. "
            + "It does not install or activate a Visual Studio C++ workload or reproduce "
            + "the vcvars developer environment.",
        RuntimeKind.Llvm or RuntimeKind.Mingw or RuntimeKind.CMake or RuntimeKind.Ninja =>
            "A declarative toolchain archive is registered as a managed executable tool; "
            + "it does not run vendor installers or activation scripts.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
