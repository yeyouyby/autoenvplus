using System.Text.Json;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class UserPathManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Path-{Guid.NewGuid():N}");

    [Fact]
    public async Task ApplyAndRollback_PreservesSnapshotAndRestoresExactPath()
    {
        string existing = Directory.CreateDirectory(Path.Combine(_root, "existing")).FullName;
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = $"{existing};{shims}{Path.DirectorySeparatorChar};{existing}";
        UserPathManager manager = new(_root, environment);

        UserPathMutationPlan plan = manager.PlanEnsureFirst(shims);
        UserPathMutationResult applied = await manager.ApplyAsync(plan);

        Assert.True(applied.Success);
        Assert.True(applied.Changed);
        Assert.StartsWith(shims + ";", environment.Values["PATH"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, environment.Values["PATH"]!.Split(';').Count(
            item => Path.GetFullPath(item).Equals(shims, StringComparison.OrdinalIgnoreCase)));
        Assert.True(File.Exists(applied.SnapshotPath));

        UserPathMutationResult rolledBack = await manager.RollbackAsync(applied.SnapshotPath!);
        Assert.True(rolledBack.Success);
        Assert.Equal(plan.Before, environment.Values["PATH"]);
    }

    [Fact]
    public async Task ApplyAsync_RefusesToOverwriteConcurrentPathChange()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = "C:\\Original";
        UserPathManager manager = new(_root, environment);
        UserPathMutationPlan plan = manager.PlanEnsureFirst(shims);
        environment.Values["PATH"] = "C:\\Changed";

        UserPathMutationResult result = await manager.ApplyAsync(plan);

        Assert.False(result.Success);
        Assert.Equal("C:\\Changed", environment.Values["PATH"]);
    }

    [Fact]
    public async Task GetSnapshotsAsync_ReportsWhetherRollbackIsStillSafe()
    {
        string existing = Directory.CreateDirectory(Path.Combine(_root, "existing")).FullName;
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = existing;
        UserPathManager manager = new(_root, environment);
        UserPathMutationPlan plan = manager.PlanEnsureFirst(shims);
        UserPathMutationResult applied = await manager.ApplyAsync(plan);

        UserPathSnapshotInfo available = Assert.Single(await manager.GetSnapshotsAsync());
        Assert.Equal(applied.SnapshotPath, available.SnapshotPath);
        Assert.Equal(shims, available.AddedDirectory);
        Assert.Equal(UserPathSnapshotState.RollbackAvailable, available.State);
        Assert.True(available.CanRollback);

        environment.Values["PATH"] = "C:\\Newer";
        UserPathSnapshotInfo changed = Assert.Single(await manager.GetSnapshotsAsync());
        Assert.Equal(UserPathSnapshotState.PathChanged, changed.State);
        Assert.False(changed.CanRollback);

        environment.Values["PATH"] = plan.Before;
        UserPathSnapshotInfo rolledBack = Assert.Single(await manager.GetSnapshotsAsync());
        Assert.Equal(UserPathSnapshotState.AlreadyRolledBack, rolledBack.State);
        Assert.False(rolledBack.CanRollback);
    }

    [Fact]
    public async Task GetSnapshotsAsync_SortsValidSnapshotsAndSkipsUntrustedFiles()
    {
        string firstDirectory = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        string secondDirectory = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = firstDirectory;
        UserPathManager manager = new(_root, environment);

        UserPathMutationResult first = await manager.ApplyAsync(manager.PlanEnsureFirst(shims));
        environment.Values["PATH"] = secondDirectory;
        UserPathMutationResult second = await manager.ApplyAsync(manager.PlanEnsureFirst(shims));
        UserPathSnapshot firstSnapshot = await ReadSnapshotAsync(first.SnapshotPath!);
        UserPathSnapshot secondSnapshot = await ReadSnapshotAsync(second.SnapshotPath!);
        await WriteSnapshotAsync(
            first.SnapshotPath!,
            firstSnapshot with { CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        await WriteSnapshotAsync(
            second.SnapshotPath!,
            secondSnapshot with { CreatedAtUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) });

        string snapshotDirectory = Path.GetDirectoryName(first.SnapshotPath!)!;
        await File.WriteAllTextAsync(
            Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + ".json"),
            "{");
        string mismatchedId = Guid.NewGuid().ToString("N");
        await WriteSnapshotAsync(
            Path.Combine(snapshotDirectory, mismatchedId + ".json"),
            secondSnapshot with { Id = Guid.NewGuid().ToString("N") });
        string tamperedId = Guid.NewGuid().ToString("N");
        await WriteSnapshotAsync(
            Path.Combine(snapshotDirectory, tamperedId + ".json"),
            secondSnapshot with { Id = tamperedId, Before = "C:\\Injected" });

        IReadOnlyList<UserPathSnapshotInfo> snapshots = await manager.GetSnapshotsAsync();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(second.SnapshotPath, snapshots[0].SnapshotPath);
        Assert.Equal(UserPathSnapshotState.RollbackAvailable, snapshots[0].State);
        Assert.Equal(first.SnapshotPath, snapshots[1].SnapshotPath);
        Assert.Equal(UserPathSnapshotState.PathChanged, snapshots[1].State);
    }

    [Fact]
    public async Task RollbackAsync_RevalidatesSnapshotIdentityAndTransition()
    {
        string existing = Directory.CreateDirectory(Path.Combine(_root, "existing")).FullName;
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = existing;
        UserPathManager manager = new(_root, environment);
        UserPathMutationResult applied = await manager.ApplyAsync(manager.PlanEnsureFirst(shims));
        string appliedPath = environment.Values["PATH"]!;
        UserPathSnapshot snapshot = await ReadSnapshotAsync(applied.SnapshotPath!);
        await WriteSnapshotAsync(
            applied.SnapshotPath!,
            snapshot with { Id = Guid.NewGuid().ToString("N") });

        UserPathMutationResult identityResult = await manager.RollbackAsync(applied.SnapshotPath!);

        Assert.False(identityResult.Success);
        Assert.Contains("identity", identityResult.Error, StringComparison.Ordinal);
        Assert.Equal(appliedPath, environment.Values["PATH"]);

        await WriteSnapshotAsync(
            applied.SnapshotPath!,
            snapshot with { Before = "C:\\Injected" });

        UserPathMutationResult result = await manager.RollbackAsync(applied.SnapshotPath!);

        Assert.False(result.Success);
        Assert.Contains("valid AutoEnvPlus PATH change", result.Error, StringComparison.Ordinal);
        Assert.Equal(appliedPath, environment.Values["PATH"]);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.RollbackAsync(
            Path.Combine(_root, "outside.json")));
    }

    [Fact]
    public async Task PlanRemove_DropsEveryOccurrenceAndKeepsOthersInOrder()
    {
        string keep = Directory.CreateDirectory(Path.Combine(_root, "keep")).FullName;
        string remove = Directory.CreateDirectory(Path.Combine(_root, "remove")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = $"{keep};{remove};{keep};{remove}\\";
        UserPathManager manager = new(_root, environment);

        UserPathMutationPlan plan = manager.PlanRemove(remove);

        Assert.True(plan.Changed);
        Assert.Equal(UserPathSnapshotKind.RemoveDirectory, plan.Kind);
        Assert.Equal($"{keep};{keep}", plan.After);
        UserPathMutationResult applied = await manager.ApplyAsync(plan);
        Assert.True(applied.Success);
        Assert.Equal($"{keep};{keep}", environment.Values["PATH"]);

        UserPathMutationResult rolledBack = await manager.RollbackAsync(applied.SnapshotPath!);
        Assert.True(rolledBack.Success);
        Assert.Equal(plan.Before, environment.Values["PATH"]);
    }

    [Fact]
    public void PlanRemove_OfAbsentDirectoryReportsNoChange()
    {
        string keep = Directory.CreateDirectory(Path.Combine(_root, "keep")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = keep;
        UserPathManager manager = new(_root, environment);

        UserPathMutationPlan plan = manager.PlanRemove(
            Path.Combine(_root, "absent"));

        Assert.False(plan.Changed);
        Assert.Equal(keep, plan.After);
    }

    [Fact]
    public async Task PlanRepair_RemovesDeadEntriesAndLaterDuplicates()
    {
        string live = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        string dead = Path.Combine(_root, "dead");
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = $"{live};{dead};{live}\\;{live}";
        UserPathManager manager = new(_root, environment);

        UserPathRepairPlan plan = manager.PlanRepair();

        Assert.True(plan.Changed);
        Assert.Equal(UserPathSnapshotKind.Repair, plan.Mutation.Kind);
        Assert.Equal(new[] { dead }, plan.RemovedMissing);
        Assert.Equal(new[] { $"{live}\\", live }, plan.RemovedDuplicates);
        Assert.Equal(live, plan.Mutation.After);
        UserPathMutationResult applied = await manager.ApplyAsync(plan.Mutation);
        Assert.True(applied.Success);
        Assert.Equal(live, environment.Values["PATH"]);

        UserPathMutationResult rolledBack = await manager.RollbackAsync(applied.SnapshotPath!);
        Assert.True(rolledBack.Success);
        Assert.Equal(plan.Mutation.Before, environment.Values["PATH"]);
    }

    [Fact]
    public void PlanRepair_KeepsLiveDuplicateWhenEarlierOccurrenceIsDead()
    {
        string live = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        string dead = Path.Combine(_root, "dead");
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = $"{dead};{live}";
        UserPathManager manager = new(_root, environment);

        UserPathRepairPlan plan = manager.PlanRepair();

        Assert.Equal(new[] { dead }, plan.RemovedMissing);
        Assert.Empty(plan.RemovedDuplicates);
        Assert.Equal(live, plan.Mutation.After);
    }

    [Fact]
    public void PlanRepair_OnHealthyPathReportsNoChange()
    {
        string live = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = $"{live};{other}";
        UserPathManager manager = new(_root, environment);

        UserPathRepairPlan plan = manager.PlanRepair();

        Assert.False(plan.Changed);
        Assert.Empty(plan.RemovedMissing);
        Assert.Empty(plan.RemovedDuplicates);
    }

    [Fact]
    public async Task GetSnapshotsAsync_ExposesSnapshotKindForDisplay()
    {
        string live = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        string dead = Path.Combine(_root, "dead");
        FakeEnvironmentStore environment = new();
        environment.Values["PATH"] = $"{live};{dead}";
        UserPathManager manager = new(_root, environment);

        UserPathMutationResult repair = await manager.ApplyAsync(manager.PlanRepair().Mutation);
        UserPathMutationResult removal = await manager.ApplyAsync(
            manager.PlanRemove(live));

        IReadOnlyList<UserPathSnapshotInfo> snapshots = await manager.GetSnapshotsAsync();

        Assert.Equal(UserPathSnapshotKind.RemoveDirectory, snapshots[0].Kind);
        Assert.Equal(live, snapshots[0].AddedDirectory);
        Assert.True(snapshots[0].CanRollback);
        Assert.Equal(UserPathSnapshotKind.Repair, snapshots[1].Kind);
        Assert.Equal(string.Empty, snapshots[1].AddedDirectory);
        // The repair snapshot's After no longer matches the current PATH
        // (the removal changed it again), so only the newest snapshot is
        // rollback-safe.
        Assert.Equal(UserPathSnapshotState.PathChanged, snapshots[1].State);
        Assert.True(File.Exists(repair.SnapshotPath));
        Assert.True(File.Exists(removal.SnapshotPath));
    }

    private static async Task<UserPathSnapshot> ReadSnapshotAsync(string snapshotPath)
    {
        string json = await File.ReadAllTextAsync(snapshotPath);
        return JsonSerializer.Deserialize<UserPathSnapshot>(json, SnapshotJsonOptions)!;
    }

    private static Task WriteSnapshotAsync(string snapshotPath, UserPathSnapshot snapshot) =>
        File.WriteAllTextAsync(
            snapshotPath,
            JsonSerializer.Serialize(snapshot, SnapshotJsonOptions));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeEnvironmentStore : IUserEnvironmentVariableStore
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string name) => Values.TryGetValue(name, out string? value) ? value : null;

        public Task SetAsync(
            string name,
            string? value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[name] = value;
            return Task.CompletedTask;
        }

        public Task<bool> CompareExchangeAsync(
            string name,
            string? expectedValue,
            string? value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? current = Get(name);
            if (!string.Equals(current, expectedValue, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Values[name] = value;
            return Task.FromResult(true);
        }
    }
}
