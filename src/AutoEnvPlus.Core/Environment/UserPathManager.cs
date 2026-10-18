using System.Text.Json;
using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Environment;

public sealed record UserPathMutationPlan(
    string Before,
    string After,
    string Directory,
    bool Changed,
    UserPathSnapshotKind Kind = UserPathSnapshotKind.EnsureFirst);

/// <summary>The kind of user PATH change a snapshot describes.</summary>
public enum UserPathSnapshotKind
{
    /// <summary>The snapshot directory was moved to the front of PATH.</summary>
    EnsureFirst = 0,

    /// <summary>The snapshot directory was removed from PATH.</summary>
    RemoveDirectory = 1,

    /// <summary>Smart repair: dead entries and duplicates were removed.</summary>
    Repair = 2,
}

/// <summary>
/// A computed smart-repair plan: which raw PATH entries would be removed
/// because their directories no longer exist, and which are later
/// duplicates of an earlier entry.
/// </summary>
public sealed record UserPathRepairPlan(
    UserPathMutationPlan Mutation,
    IReadOnlyList<string> RemovedMissing,
    IReadOnlyList<string> RemovedDuplicates)
{
    public bool Changed => Mutation.Changed;

    public int RemovedCount => RemovedMissing.Count + RemovedDuplicates.Count;
}

public sealed record UserPathSnapshot(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Before,
    string After,
    string AddedDirectory,
    UserPathSnapshotKind Kind = UserPathSnapshotKind.EnsureFirst);

public enum UserPathSnapshotState
{
    RollbackAvailable,
    AlreadyRolledBack,
    PathChanged,
}

public sealed record UserPathSnapshotInfo(
    string SnapshotPath,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string AddedDirectory,
    UserPathSnapshotState State,
    UserPathSnapshotKind Kind = UserPathSnapshotKind.EnsureFirst)
{
    public bool CanRollback => State == UserPathSnapshotState.RollbackAvailable;
}

public sealed record UserPathMutationResult(
    bool Success,
    bool Changed,
    string? SnapshotPath,
    string? Error);

public sealed class UserPathManager
{
    private const long MaximumSnapshotBytes = 512 * 1024;

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
        string after = EnsureDirectoryFirst(before, fullDirectory);
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

    /// <summary>
    /// Plans the removal of every occurrence of <paramref name="directory"/>
    /// from the persisted user PATH. The plan flows through the same
    /// snapshot-backed apply/rollback pipeline as shim installation.
    /// </summary>
    public UserPathMutationPlan PlanRemove(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string before = _environmentStore.Get("PATH") ?? string.Empty;
        string fullDirectory = Path.GetFullPath(directory);
        string after = RemoveDirectory(before, fullDirectory);
        if (after.Length > 32_767)
        {
            throw new InvalidOperationException("The resulting user PATH exceeds the Windows environment block limit.");
        }

        return new UserPathMutationPlan(
            before,
            after,
            fullDirectory,
            !before.Equals(after, StringComparison.Ordinal),
            UserPathSnapshotKind.RemoveDirectory);
    }

    /// <summary>
    /// Plans a smart repair of the persisted user PATH: entries whose
    /// directories no longer exist are dropped, and later duplicates of an
    /// earlier entry are removed (the first occurrence wins, and a live
    /// duplicate is kept when the earlier occurrence is dead). The result
    /// flows through the same snapshot-backed apply/rollback pipeline.
    /// </summary>
    public UserPathRepairPlan PlanRepair(
        bool removeMissing = true,
        bool deduplicate = true)
    {
        string before = _environmentStore.Get("PATH") ?? string.Empty;
        List<string> kept = [];
        List<string> removedMissing = [];
        List<string> removedDuplicates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in SplitPath(before))
        {
            string expanded = Expand(raw);
            if (removeMissing && !Directory.Exists(expanded))
            {
                removedMissing.Add(raw);
                continue;
            }

            if (deduplicate && !seen.Add(Normalize(expanded)))
            {
                removedDuplicates.Add(raw);
                continue;
            }

            kept.Add(raw);
        }

        string after = string.Join(';', kept);
        if (after.Length > 32_767)
        {
            throw new InvalidOperationException("The resulting user PATH exceeds the Windows environment block limit.");
        }

        return new UserPathRepairPlan(
            new UserPathMutationPlan(
                before,
                after,
                string.Empty,
                !before.Equals(after, StringComparison.Ordinal),
                UserPathSnapshotKind.Repair),
            removedMissing,
            removedDuplicates);
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

        string snapshotDirectory = SnapshotDirectory;
        Directory.CreateDirectory(snapshotDirectory);
        UserPathSnapshot snapshot = new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            plan.Before,
            plan.After,
            plan.Directory,
            plan.Kind);
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
        EnsureSnapshotPath(fullSnapshot);
        SnapshotReadResult readResult = await ReadSnapshotAsync(
            fullSnapshot,
            cancellationToken).ConfigureAwait(false);
        if (readResult.Snapshot is not { } snapshot)
        {
            return new UserPathMutationResult(false, false, fullSnapshot, readResult.Error);
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

    public async Task<IReadOnlyList<UserPathSnapshotInfo>> GetSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(SnapshotDirectory))
        {
            return [];
        }

        string current = _environmentStore.Get("PATH") ?? string.Empty;
        List<UserPathSnapshotInfo> snapshots = [];
        foreach (string snapshotPath in Directory.EnumerateFiles(
                     SnapshotDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullSnapshot = Path.GetFullPath(snapshotPath);
            SnapshotReadResult readResult = await ReadSnapshotAsync(
                fullSnapshot,
                cancellationToken).ConfigureAwait(false);
            if (readResult.Snapshot is not { } snapshot)
            {
                continue;
            }

            UserPathSnapshotState state = current.Equals(snapshot.After, StringComparison.Ordinal)
                ? UserPathSnapshotState.RollbackAvailable
                : current.Equals(snapshot.Before, StringComparison.Ordinal)
                    ? UserPathSnapshotState.AlreadyRolledBack
                    : UserPathSnapshotState.PathChanged;
            snapshots.Add(new UserPathSnapshotInfo(
                fullSnapshot,
                snapshot.Id,
                snapshot.CreatedAtUtc,
                snapshot.AddedDirectory,
                state,
                snapshot.Kind));
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
            .ThenByDescending(snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private string SnapshotDirectory => Path.Combine(_managedRoot, "state", "path-snapshots");

    private async Task<SnapshotReadResult> ReadSnapshotAsync(
        string fullSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureSnapshotPath(fullSnapshot);
            FileInfo file = new(fullSnapshot);
            if (!file.Exists)
            {
                return new SnapshotReadResult(null, "The PATH snapshot does not exist.");
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new SnapshotReadResult(null, "Linked PATH snapshots are not accepted.");
            }

            if (file.Length is <= 0 or > MaximumSnapshotBytes)
            {
                return new SnapshotReadResult(null, "The PATH snapshot has an invalid size.");
            }

            UserPathSnapshot? snapshot = JsonSerializer.Deserialize<UserPathSnapshot>(
                await File.ReadAllTextAsync(fullSnapshot, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            string? validationError = ValidateSnapshot(fullSnapshot, snapshot);
            return validationError is null
                ? new SnapshotReadResult(snapshot, null)
                : new SnapshotReadResult(null, validationError);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return new SnapshotReadResult(
                null,
                $"The PATH snapshot could not be read: {exception.Message}");
        }
    }

    private static string? ValidateSnapshot(string fullSnapshot, UserPathSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "The PATH snapshot is empty.";
        }

        if (!Guid.TryParseExact(snapshot.Id, "N", out _)
            || !Path.GetFileName(fullSnapshot).Equals(
                snapshot.Id + ".json",
                StringComparison.Ordinal))
        {
            return "The PATH snapshot identity does not match its file name.";
        }

        if (snapshot.CreatedAtUtc == default
            || snapshot.Before is null
            || snapshot.After is null
            || snapshot.AddedDirectory is null)
        {
            return "The PATH snapshot is missing required values.";
        }

        // Repair snapshots cannot be recomputed at validation time: whether
        // an entry was "missing" depended on the file system when the plan
        // was created, so only structural invariants are checked.
        if (snapshot.Kind == UserPathSnapshotKind.Repair)
        {
            if (snapshot.AddedDirectory.Length > 0
                || snapshot.After.Length > 32_767
                || snapshot.Before.Equals(snapshot.After, StringComparison.Ordinal))
            {
                return "The PATH snapshot does not describe a valid AutoEnvPlus PATH change.";
            }

            return null;
        }

        string addedDirectory;
        try
        {
            if (!Path.IsPathFullyQualified(snapshot.AddedDirectory))
            {
                return "The PATH snapshot contains a relative Shim directory.";
            }

            addedDirectory = Path.GetFullPath(snapshot.AddedDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return $"The PATH snapshot contains an invalid Shim directory: {exception.Message}";
        }

        if (!addedDirectory.Equals(snapshot.AddedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return "The PATH snapshot contains a non-canonical Shim directory.";
        }

        if (snapshot.Kind == UserPathSnapshotKind.RemoveDirectory)
        {
            string expectedAfter = RemoveDirectory(snapshot.Before, addedDirectory);
            if (expectedAfter.Length > 32_767
                || snapshot.Before.Equals(snapshot.After, StringComparison.Ordinal)
                || !expectedAfter.Equals(snapshot.After, StringComparison.Ordinal))
            {
                return "The PATH snapshot does not describe a valid AutoEnvPlus PATH change.";
            }

            return null;
        }

        string expectedEnsureAfter = EnsureDirectoryFirst(snapshot.Before, addedDirectory);
        if (expectedEnsureAfter.Length > 32_767
            || snapshot.Before.Equals(snapshot.After, StringComparison.Ordinal)
            || !expectedEnsureAfter.Equals(snapshot.After, StringComparison.Ordinal))
        {
            return "The PATH snapshot does not describe a valid AutoEnvPlus PATH change.";
        }

        return null;
    }

    private static string RemoveDirectory(string before, string fullDirectory) => string.Join(
        ';',
        SplitPath(before).Where(entry => !PathsEqual(entry, fullDirectory)));

    private static IEnumerable<string> SplitPath(string? value) =>
        (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0);

    private static string Expand(string value) =>
        System.Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));

    private static string Normalize(string value)
    {
        try
        {
            string fullPath = Path.GetFullPath(value);
            string? root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string EnsureDirectoryFirst(string before, string fullDirectory)
    {
        List<string> entries = before
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !PathsEqual(entry, fullDirectory))
            .ToList();
        entries.Insert(0, fullDirectory);
        return string.Join(';', entries);
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

    private void EnsureSnapshotPath(string candidate)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string? parent = Path.GetDirectoryName(fullCandidate);
        if (parent is null
            || !Path.GetFullPath(parent).Equals(
                Path.GetFullPath(SnapshotDirectory),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(fullCandidate).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The PATH snapshot must be a JSON file directly inside the AutoEnvPlus state directory.");
        }
    }

    private sealed record SnapshotReadResult(UserPathSnapshot? Snapshot, string? Error);
}
