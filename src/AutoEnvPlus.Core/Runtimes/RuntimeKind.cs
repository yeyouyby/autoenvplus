namespace AutoEnvPlus.Core.Runtimes;

public enum RuntimeKind
{
    Python,
    NodeJs,
    Java,
    DotNet,
    Msvc,
    Llvm,
    Mingw,
    CMake,
    Ninja,
}

public enum RuntimeArchitecture
{
    Any,
    X86,
    X64,
    Arm64,
}

public enum RuntimeOwnership
{
    Managed,
    External,
    System,
}
