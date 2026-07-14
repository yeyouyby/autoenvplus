namespace AutoEnvPlus.Core.Runtimes;

public sealed record RuntimeInstallation(
    string Id,
    RuntimeKind Kind,
    RuntimeVersion Version,
    RuntimeArchitecture Architecture,
    string InstallRoot,
    RuntimeOwnership Ownership,
    IReadOnlyCollection<string> Channels)
{
    public static RuntimeInstallation Create(
        string id,
        RuntimeKind kind,
        string version,
        RuntimeArchitecture architecture = RuntimeArchitecture.X64,
        string? installRoot = null,
        RuntimeOwnership ownership = RuntimeOwnership.Managed,
        params string[] channels) =>
        new(
            id,
            kind,
            RuntimeVersion.Parse(version),
            architecture,
            installRoot ?? string.Empty,
            ownership,
            channels);
}
