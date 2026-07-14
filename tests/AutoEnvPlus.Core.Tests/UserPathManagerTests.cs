using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class UserPathManagerTests : IDisposable
{
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
    }
}
