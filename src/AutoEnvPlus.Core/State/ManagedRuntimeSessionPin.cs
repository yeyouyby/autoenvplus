using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.State;

public static class ManagedRuntimeSessionPin
{
    public static string GetVersionVariableName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.NodeJs => "AUTOENVPLUS_NODE_VERSION",
        _ => $"AUTOENVPLUS_{kind.ToString().ToUpperInvariant()}_VERSION",
    };

    public static string GetRuntimeIdVariableName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.NodeJs => "AUTOENVPLUS_NODE_RUNTIME_ID",
        _ => $"AUTOENVPLUS_{kind.ToString().ToUpperInvariant()}_RUNTIME_ID",
    };

    public static string GetProviderIdVariableName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.NodeJs => "AUTOENVPLUS_NODE_RUNTIME_PROVIDER_ID",
        _ => $"AUTOENVPLUS_{kind.ToString().ToUpperInvariant()}_RUNTIME_PROVIDER_ID",
    };
}
