using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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
    internal CacheMigrationSourceManifest SourceManifest { get; init; } =
        CacheMigrationSourceManifest.Empty;

    public string ConfigurationDescription => ConfigurationKind switch
    {
        CacheConfigurationKind.MavenSettingsXml => $"Maven settings.xml: {ConfigurationTarget}",
        CacheConfigurationKind.PnpmRc => $"pnpm 全局配置 store-dir: {ConfigurationTarget}",
        _ => $"用户环境变量 {ConfigurationTarget}",
    };
}

internal sealed record CacheMigrationFileIdentity(
    string RelativePath,
    long Length,
    string Sha256);

internal sealed record CacheMigrationSourceManifest(
    IReadOnlyList<string> Directories,
    IReadOnlyList<CacheMigrationFileIdentity> Files,
    long TotalBytes)
{
    public static CacheMigrationSourceManifest Empty { get; } = new([], [], 0);
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

    Task<bool> CompareExchangeAsync(
        string name,
        string? expectedValue,
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
        using Mutex mutationMutex = CreateMutationMutex(name);
        bool lockTaken = WaitForMutationMutex(mutationMutex, cancellationToken);
        try
        {
            SetCore(name, value);
        }
        finally
        {
            if (lockTaken)
            {
                mutationMutex.ReleaseMutex();
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> CompareExchangeAsync(
        string name,
        string? expectedValue,
        string? value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Mutex mutationMutex = CreateMutationMutex(name);
        bool lockTaken = WaitForMutationMutex(mutationMutex, cancellationToken);
        try
        {
            if (!string.Equals(Get(name), expectedValue, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            SetCore(name, value);
            string? observedValue = Get(name);
            bool exchanged = string.Equals(
                observedValue,
                value,
                StringComparison.Ordinal);
            if (!exchanged)
            {
                SetProcessValue(name, observedValue);
            }

            return Task.FromResult(exchanged);
        }
        finally
        {
            if (lockTaken)
            {
                mutationMutex.ReleaseMutex();
            }
        }
    }

    private static void SetCore(string name, string? value)
    {
        System.Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        SetProcessValue(name, value);
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
    }

    private static void SetProcessValue(string name, string? userValue)
    {
        string? processValue = userValue ?? System.Environment.GetEnvironmentVariable(
            name,
            EnvironmentVariableTarget.Machine);
        if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
        {
            string machinePath = System.Environment.GetEnvironmentVariable(
                "PATH",
                EnvironmentVariableTarget.Machine) ?? string.Empty;
            processValue = string.IsNullOrWhiteSpace(userValue)
                ? machinePath
                : string.Join(';', machinePath.TrimEnd(';'), userValue.TrimStart(';'));
        }

        System.Environment.SetEnvironmentVariable(name, processValue, EnvironmentVariableTarget.Process);
    }

    private static Mutex CreateMutationMutex(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalizedName = name.ToUpperInvariant();
        string identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedName)));
        return new Mutex(initiallyOwned: false, $"Local\\AutoEnvPlus.UserEnvironment.{identity}");
    }

    private static bool WaitForMutationMutex(
        Mutex mutationMutex,
        CancellationToken cancellationToken)
    {
        try
        {
            int signaled = WaitHandle.WaitAny([mutationMutex, cancellationToken.WaitHandle]);
            if (signaled != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
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
