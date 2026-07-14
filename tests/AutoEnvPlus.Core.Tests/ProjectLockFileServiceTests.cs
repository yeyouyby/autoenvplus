using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class ProjectLockFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Lock-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsync_ResolvesChannelsAndWritesReproduciblePackageIdentity()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\nnode = \"lts\"\n");
        ManagedRuntimeEntry python = CreateEntry(RuntimeKind.Python, "3.13.5", "python.exe", ["stable"]);
        ManagedRuntimeEntry node = CreateEntry(RuntimeKind.NodeJs, "24.18.0", "node.exe", ["lts", "krypton"]);
        ProjectLockFileService service = new();

        ProjectLockResult result = await service.CreateAsync(manifest, [python, node]);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.LockPath));
        Assert.Equal(2, result.Document!.Runtimes.Count);
        ProjectLockEntry lockedNode = result.Document.Runtimes.Single(entry => entry.Kind == RuntimeKind.NodeJs);
        Assert.Equal("lts", lockedNode.RequestedSelector);
        Assert.Equal(RuntimeVersion.Parse("24.18.0"), lockedNode.ResolvedVersion);
        Assert.Equal(node.PackageSha256, lockedNode.PackageSha256);

        ProjectLockResult loaded = await service.LoadAsync(result.LockPath!);
        Assert.True(loaded.Success);
        Assert.True(await service.IsCurrentAsync(loaded.Document!, manifest));
        File.AppendAllText(manifest, "# changed\n");
        Assert.False(await service.IsCurrentAsync(loaded.Document!, manifest));
    }

    [Fact]
    public async Task CreateAsync_MissingRuntimeDoesNotWritePartialLock()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\njava = \"21\"\n");

        ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
            manifest,
            []);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.False(File.Exists(Path.Combine(_root, ProjectLockFileService.LockFileName)));
    }

    private ManagedRuntimeEntry CreateEntry(
        RuntimeKind kind,
        string version,
        string executable,
        IReadOnlyCollection<string> channels)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        string installRoot = Path.Combine(_root, "managed", kind.ToString(), parsed.ToString(), "x64");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, executable), string.Empty);
        return new ManagedRuntimeEntry(
            $"{kind}-{parsed}-x64",
            "test-provider",
            kind,
            parsed,
            RuntimeArchitecture.X64,
            installRoot,
            executable,
            new string(kind == RuntimeKind.Python ? 'a' : 'b', 64),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            channels);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
