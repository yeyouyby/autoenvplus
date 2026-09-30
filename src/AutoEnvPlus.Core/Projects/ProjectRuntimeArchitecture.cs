using System.Runtime.InteropServices;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Projects;

internal static class ProjectRuntimeArchitecture
{
    public static RuntimeArchitecture Current => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => RuntimeArchitecture.X86,
        Architecture.Arm64 => RuntimeArchitecture.Arm64,
        _ => RuntimeArchitecture.X64,
    };
}
