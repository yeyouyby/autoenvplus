using System.Runtime.InteropServices;

namespace AutoEnvPlus.Core.Storage;

public sealed record CacheMigrationPlan(
    CacheDirectoryLocation Source,
    string DestinationPath,
    CacheConfigurationKind ConfigurationKind,
    string ConfigurationTarget,
    bool ConfigurationBeforeKnown,
    bool ConfigurationTargetExisted,
    string? ConfigurationBefore,
    string ConfigurationAfter)
{
    public string ConfigurationDescription => ConfigurationKind switch
    {
        CacheConfigurationKind.MavenSettingsXml => $"Maven settings.xml: {ConfigurationTarget}",
        CacheConfigurationKind.PnpmRc => $"pnpm 全局配置 store-dir: {ConfigurationTarget}",
        _ => $"用户环境变量 {ConfigurationTarget}",
    };
}

public sealed record CacheMigrationProgress(
    string Stage,
    string? RelativePath = null,
    long CompletedBytes = 0,
    long? TotalBytes = null);

public sealed record CacheMigrationResult(
    bool Success,
    string SourcePath,
    string? DestinationPath,
    bool SourceRetained,
    string? Error,
    string? SnapshotPath = null);

public sealed record CacheMigrationSnapshot(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string CacheId,
    CacheConfigurationKind ConfigurationKind,
    string ConfigurationTarget,
    bool ConfigurationTargetExisted,
    string? ConfigurationBefore,
    string ConfigurationAfter,
    string SourcePath,
    string DestinationPath);

public interface IUserEnvironmentVariableStore
{
    string? Get(string name);

    Task SetAsync(
        string name,
        string? value,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsUserEnvironmentVariableStore : IUserEnvironmentVariableStore
{
    public string? Get(string name) => System.Environment.GetEnvironmentVariable(
        name,
        EnvironmentVariableTarget.User);

    public Task SetAsync(
        string name,
        string? value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        System.Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        string? processValue = value ?? System.Environment.GetEnvironmentVariable(
            name,
            EnvironmentVariableTarget.Machine);
        if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
        {
            string machinePath = System.Environment.GetEnvironmentVariable(
                "PATH",
                EnvironmentVariableTarget.Machine) ?? string.Empty;
            processValue = string.IsNullOrWhiteSpace(value)
                ? machinePath
                : string.Join(';', machinePath.TrimEnd(';'), value.TrimStart(';'));
        }

        System.Environment.SetEnvironmentVariable(name, processValue, EnvironmentVariableTarget.Process);
        if (OperatingSystem.IsWindows())
        {
            _ = SendMessageTimeout(
                new IntPtr(0xffff),
                0x001A,
                UIntPtr.Zero,
                "Environment",
                0x0002,
                5_000,
                out _);
        }

        return Task.CompletedTask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr messageParameter,
        string messageData,
        uint flags,
        uint timeout,
        out UIntPtr result);
}
