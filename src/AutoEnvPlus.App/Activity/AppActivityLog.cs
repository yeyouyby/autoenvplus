using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;

namespace AutoEnvPlus.App.Activity;

internal static class AppActivityLog
{
    private static readonly Lazy<ActivityLogStore?> Store = new(
        CreateStore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task TryWriteAsync(
        ActivityOperationType operationType,
        ActivityStatus status,
        string summary,
        IEnumerable<string>? affectedPaths = null,
        string? snapshotPath = null,
        string? rollbackPath = null)
    {
        ActivityLogStore? store = Store.Value;
        if (store is null)
        {
            return;
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await store.AppendAsync(
                operationType,
                status,
                summary,
                affectedPaths,
                snapshotPath,
                rollbackPath,
                cancellationToken: timeout.Token);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or OperationCanceledException
            or System.Security.SecurityException)
        {
        }
    }

    private static ActivityLogStore? CreateStore()
    {
        if (!ManagedRootResolver.TryResolve(null, out string? managedRoot, out _)
            || managedRoot is null)
        {
            return null;
        }

        try
        {
            return new ActivityLogStore(managedRoot);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null;
        }
    }
}
