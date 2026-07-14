using System.Text.Json;
using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Environment;

public sealed record UserPathMutationPlan(
    string Before,
    string After,
    string Directory,
    bool Changed);

public sealed record UserPathSnapshot(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Before,
    string After,
    string AddedDirectory);

public sealed record UserPathMutationResult(
    bool Success,
    bool Changed,
    string? SnapshotPath,
    string? Error);

public sealed class UserPathManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _managedRoot;
    private readonly IUserEnvironmentVariableStore _environmentStore;

    public UserPathManager(
        string managedRoot,
        IUserEnvironmentVariableStore environmentStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _environmentStore = environmentStore ?? throw new ArgumentNullException(nameof(environmentStore));
    }

    public UserPathMutationPlan PlanEnsureFirst(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string before = _environmentStore.Get("PATH") ?? string.Empty;
        string fullDirectory = Path.GetFullPath(directory);
        List<string> entries = before
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !PathsEqual(entry, fullDirectory))
            .ToList();
        entries.Insert(0, fullDirectory);
        string after = string.Join(';', entries);
        if (after.Length > 32_767)
        {
            throw new InvalidOperationException("The resulting user PATH exceeds the Windows environment block limit.");
        }

        return new UserPathMutationPlan(
            before,
            after,
            fullDirectory,
            !before.Equals(after, StringComparison.Ordinal));
    }

    public async Task<UserPathMutationResult> ApplyAsync(
        UserPathMutationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string current = _environmentStore.Get("PATH") ?? string.Empty;
        if (!current.Equals(plan.Before, StringComparison.Ordinal))
        {
            return new UserPathMutationResult(
                false,
                false,
                null,
                "The user PATH changed after the plan was created; refresh and review the new plan.");
        }

        if (!plan.Changed)
        {
            return new UserPathMutationResult(true, false, null, null);
        }

        string snapshotDirectory = Path.Combine(_managedRoot, "state", "path-snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        UserPathSnapshot snapshot = new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            plan.Before,
            plan.After,
            plan.Directory);
        string snapshotPath = Path.Combine(snapshotDirectory, snapshot.Id + ".json");
        string temporaryPath = snapshotPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, snapshotPath, overwrite: false);
            await _environmentStore.SetAsync("PATH", plan.After, cancellationToken).ConfigureAwait(false);
            return new UserPathMutationResult(true, true, snapshotPath, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return new UserPathMutationResult(false, false, snapshotPath, exception.Message);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<UserPathMutationResult> RollbackAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        string fullSnapshot = Path.GetFullPath(snapshotPath);
        EnsureChildPath(Path.Combine(_managedRoot, "state", "path-snapshots"), fullSnapshot);
        UserPathSnapshot? snapshot = JsonSerializer.Deserialize<UserPathSnapshot>(
            await File.ReadAllTextAsync(fullSnapshot, cancellationToken).ConfigureAwait(false),
            JsonOptions);
        if (snapshot is null)
        {
            return new UserPathMutationResult(false, false, fullSnapshot, "The PATH snapshot is empty.");
        }

        string current = _environmentStore.Get("PATH") ?? string.Empty;
        if (!current.Equals(snapshot.After, StringComparison.Ordinal))
        {
            return new UserPathMutationResult(
                false,
                false,
                fullSnapshot,
                "The user PATH changed after this snapshot; automatic rollback would overwrite newer changes.");
        }

        await _environmentStore.SetAsync("PATH", snapshot.Before, cancellationToken).ConfigureAwait(false);
        return new UserPathMutationResult(true, true, fullSnapshot, null);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            string normalizedLeft = Path.GetFullPath(
                System.Environment.ExpandEnvironmentVariables(left.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The PATH snapshot must remain inside the AutoEnvPlus state directory.");
        }
    }
}
