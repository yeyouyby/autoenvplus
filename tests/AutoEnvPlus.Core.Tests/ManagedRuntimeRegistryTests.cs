using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedRuntimeRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Registry-{Guid.NewGuid():N}");

    [Fact]
    public async Task UpsertAsync_PersistsAndReplacesEntryAtomically()
    {
        ManagedRuntimeRegistry registry = new(_root);
        ManagedRuntimeEntry original = CreateEntry("3.13.1");
        ManagedRuntimeEntry replacement = CreateEntry("3.13.2") with { Id = original.Id };

        await registry.UpsertAsync(original);
        await registry.UpsertAsync(replacement);
        RegistryLoadResult loaded = await new ManagedRuntimeRegistry(_root).LoadAsync();

        ManagedRuntimeEntry entry = Assert.Single(loaded.Entries);
        Assert.Equal(RuntimeVersion.Parse("3.13.2"), entry.Version);
        Assert.Empty(loaded.Errors);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(registry.RegistryPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task UpsertAsync_RejectsInstallRootOutsideManagedRoot()
    {
        ManagedRuntimeRegistry registry = new(_root);
        ManagedRuntimeEntry entry = CreateEntry("3.13.2") with
        {
            InstallRoot = Path.Combine(Path.GetDirectoryName(_root)!, "outside-runtime"),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(entry));
        Assert.False(File.Exists(registry.RegistryPath));
    }

    [Fact]
    public async Task LoadAsync_ReturnsDiagnosticForMalformedJson()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        File.WriteAllText(registry.RegistryPath, "{ malformed");

        RegistryLoadResult loaded = await registry.LoadAsync();

        Assert.Empty(loaded.Entries);
        Assert.Single(loaded.Errors);
        Assert.Contains("Invalid registry JSON", loaded.Errors[0]);
    }

    private ManagedRuntimeEntry CreateEntry(string version)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        return new ManagedRuntimeEntry(
            "python-x64",
            "python-org",
            RuntimeKind.Python,
            parsed,
            RuntimeArchitecture.X64,
            Path.Combine(_root, "runtimes", "python", parsed.ToString(), "x64"),
            "python.exe",
            new string('a', 64),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
