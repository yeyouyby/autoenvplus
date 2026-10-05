using System.Text;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Providers;
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
        Assert.Equal(node.PackageHashAlgorithm, lockedNode.PackageHashAlgorithm);
        Assert.Equal(node.PackageHash, lockedNode.PackageHash);

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

    [Fact]
    public async Task CreateAsync_DefaultsToCurrentProcessArchitecture()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n");
        RuntimeArchitecture currentArchitecture = ProjectRuntimeArchitecture.Current;
        RuntimeArchitecture otherArchitecture = currentArchitecture == RuntimeArchitecture.X86
            ? RuntimeArchitecture.X64
            : RuntimeArchitecture.X86;
        ManagedRuntimeEntry other = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            runtimeId: $"python-other-{otherArchitecture}",
            architecture: otherArchitecture);
        ManagedRuntimeEntry current = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            runtimeId: $"python-current-{currentArchitecture}",
            architecture: currentArchitecture);

        ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
            manifest,
            [other, current]);

        Assert.True(result.Success);
        Assert.Equal(currentArchitecture, Assert.Single(result.Document!.Runtimes).Architecture);
    }

    [Fact]
    public async Task CreateAsync_UsesExplicitDefaultArchitectureForOrdinarySelector()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n");
        ManagedRuntimeEntry x64 = CreateEntry(
            RuntimeKind.Python,
            "3.13.9",
            "python.exe",
            ["stable"],
            runtimeId: "python-newer-x64",
            architecture: RuntimeArchitecture.X64);
        ManagedRuntimeEntry x86 = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            runtimeId: "python-default-x86",
            architecture: RuntimeArchitecture.X86);

        ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
            manifest,
            [x64, x86],
            RuntimeArchitecture.X86);

        Assert.True(result.Success);
        ProjectLockEntry locked = Assert.Single(result.Document!.Runtimes);
        Assert.Equal(RuntimeArchitecture.X86, locked.Architecture);
        Assert.Equal(x86.Version, locked.ResolvedVersion);
    }

    [Fact]
    public async Task CreateAsync_ExactRuntimePinUsesInstalledEntryArchitecture()
    {
        Directory.CreateDirectory(_root);
        ManagedRuntimeEntry pinned = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            providerId: "python-x86-provider",
            runtimeId: "python-pinned-x86",
            architecture: RuntimeArchitecture.X86);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(
            manifest,
            $"[tools]\npython = \"3.13\"\n\n[tool-identities]\npython.runtime-id = \"{pinned.Id}\"\npython.provider-id = \"{pinned.ProviderId}\"\n");

        ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
            manifest,
            [pinned],
            RuntimeArchitecture.X64);

        Assert.True(result.Success);
        ProjectLockEntry locked = Assert.Single(result.Document!.Runtimes);
        Assert.Equal(RuntimeArchitecture.X86, locked.Architecture);
        Assert.Equal(pinned.ProviderId, locked.ProviderId);
    }

    [Fact]
    public async Task CreateAsync_AllowsMixedArchitecturesForExactRuntimePins()
    {
        Directory.CreateDirectory(_root);
        ManagedRuntimeEntry python = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            providerId: "python-x86-provider",
            runtimeId: "python-pinned-x86",
            architecture: RuntimeArchitecture.X86);
        ManagedRuntimeEntry node = CreateEntry(
            RuntimeKind.NodeJs,
            "24.18.0",
            "node.exe",
            ["lts"],
            providerId: "node-x64-provider",
            runtimeId: "node-pinned-x64",
            architecture: RuntimeArchitecture.X64);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(
            manifest,
            $"[tools]\npython = \"3.13\"\nnode = \"lts\"\n\n[tool-identities]\npython.runtime-id = \"{python.Id}\"\npython.provider-id = \"{python.ProviderId}\"\nnode.runtime-id = \"{node.Id}\"\nnode.provider-id = \"{node.ProviderId}\"\n");

        ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
            manifest,
            [python, node],
            RuntimeArchitecture.X64);

        Assert.True(result.Success);
        Assert.Equal(2, result.Document!.Runtimes.Count);
        Assert.Equal(
            RuntimeArchitecture.X86,
            result.Document.Runtimes.Single(entry => entry.Kind == RuntimeKind.Python).Architecture);
        Assert.Equal(
            RuntimeArchitecture.X64,
            result.Document.Runtimes.Single(entry => entry.Kind == RuntimeKind.NodeJs).Architecture);
    }

    [Fact]
    public async Task CreateAsync_RejectsArchitectureAny()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\n");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new ProjectLockFileService().CreateAsync(
                manifest,
                [],
                RuntimeArchitecture.Any));
    }

    [Fact]
    public async Task CreateAsync_UsesOneManifestSnapshotForSelectionAndHash()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n");
        ManagedRuntimeEntry python = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"]);
        ProjectLockFileService service = new((_, _) =>
        {
            File.WriteAllText(manifest, "[tools]\npython = \"3.14\"\n");
            return Task.CompletedTask;
        });

        ProjectLockResult result = await service.CreateAsync(manifest, [python]);

        Assert.True(result.Success);
        Assert.Equal("3.13", Assert.Single(result.Document!.Runtimes).RequestedSelector);
        Assert.False(await service.IsCurrentAsync(result.Document!, manifest));
    }

    [Fact]
    public async Task CreateAsync_RejectsOversizedOrNonUtf8Manifest()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, new string('#', ProjectLockFileService.MaximumManifestBytes + 1));
        ProjectLockFileService service = new();

        InvalidDataException oversized = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CreateAsync(manifest, []));
        Assert.Contains("byte limit", oversized.Message, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n", Encoding.Unicode);
        InvalidDataException encoding = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CreateAsync(manifest, []));
        Assert.Contains("UTF-8", encoding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedFileAndExcessiveRuntimeEntries()
    {
        Directory.CreateDirectory(_root);
        string lockPath = Path.Combine(_root, ProjectLockFileService.LockFileName);
        File.WriteAllBytes(lockPath, new byte[ProjectLockFileService.MaximumLockFileBytes + 1]);

        ProjectLockResult oversized = await new ProjectLockFileService().LoadAsync(lockPath);

        Assert.False(oversized.Success);
        Assert.Contains(oversized.Errors, error =>
            error.Contains("byte limit", StringComparison.OrdinalIgnoreCase));

        lockPath = await CreateMutableLockAsync();
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        JsonNode template = root["runtimes"]!.AsArray()[0]!.DeepClone();
        JsonArray runtimes = [];
        for (int index = 0; index <= ProjectLockFileService.MaximumRuntimeEntries; index++)
        {
            runtimes.Add(template.DeepClone());
        }

        root["runtimes"] = runtimes;
        await File.WriteAllTextAsync(lockPath, root.ToJsonString());
        ProjectLockResult excessive = await new ProjectLockFileService().LoadAsync(lockPath);

        Assert.False(excessive.Success);
        Assert.Contains(excessive.Errors, error =>
            error.Contains("at most", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAsync_UsesExactProjectProviderIdentity()
    {
        Directory.CreateDirectory(_root);
        ManagedRuntimeEntry official = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            providerId: "python-org",
            runtimeId: "python-official-x64");
        ManagedRuntimeEntry community = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"],
            providerId: "plugin:community-python",
            runtimeId: "python-community-x64");
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(
            manifest,
            $"[tools]\npython = \"3.13.5\"\n\n[tool-identities]\npython.runtime-id = \"{community.Id}\"\npython.provider-id = \"{community.ProviderId}\"\n");

        ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
            manifest,
            [official, community],
            RuntimeArchitecture.X64);

        Assert.True(result.Success);
        ProjectLockEntry locked = Assert.Single(result.Document!.Runtimes);
        Assert.Equal(community.ProviderId, locked.ProviderId);
        Assert.Equal(community.PackageHash, locked.PackageHash);
    }

    [Fact]
    public async Task CreateAsync_PersistsSha512IdentityInSchemaTwo()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\ndotnet = \"10.0.302\"\n");
        ManagedRuntimeEntry dotnet = CreateEntry(
            RuntimeKind.DotNet,
            "10.0.302",
            "dotnet.exe",
            ["sdk", "lts"],
            PackageHashAlgorithm.Sha512);
        ProjectLockFileService service = new();

        ProjectLockResult result = await service.CreateAsync(manifest, [dotnet]);
        string json = await File.ReadAllTextAsync(result.LockPath!);

        Assert.True(result.Success);
        Assert.Equal(ProjectLockFileService.CurrentSchemaVersion, result.Document!.SchemaVersion);
        ProjectLockEntry locked = Assert.Single(result.Document.Runtimes);
        Assert.Equal(PackageHashAlgorithm.Sha512, locked.PackageHashAlgorithm);
        Assert.Equal(dotnet.PackageHash, locked.PackageHash);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"packageHashAlgorithm\": \"Sha512\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MigratesSchemaOnePackageSha256InMemory()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n");
        ManagedRuntimeEntry python = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"]);
        ProjectLockFileService service = new();
        ProjectLockResult created = await service.CreateAsync(manifest, [python]);
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(created.LockPath!))!.AsObject();
        JsonObject runtime = root["runtimes"]!.AsArray()[0]!.AsObject();
        string packageHash = runtime["packageHash"]!.GetValue<string>();
        root["schemaVersion"] = 1;
        runtime["packageSha256"] = packageHash;
        runtime.Remove("packageHashAlgorithm");
        runtime.Remove("packageHash");
        await File.WriteAllTextAsync(created.LockPath!, root.ToJsonString());

        ProjectLockResult loaded = await service.LoadAsync(created.LockPath!);

        Assert.True(loaded.Success);
        Assert.Equal(ProjectLockFileService.CurrentSchemaVersion, loaded.Document!.SchemaVersion);
        ProjectLockEntry entry = Assert.Single(loaded.Document.Runtimes);
        Assert.Equal(PackageHashAlgorithm.Sha256, entry.PackageHashAlgorithm);
        Assert.Equal(packageHash, entry.PackageHash);
    }

    [Fact]
    public async Task LoadAsync_RejectsSchemaTwoWithoutPackageHash()
    {
        string lockPath = await CreateMutableLockAsync();
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        root["runtimes"]!.AsArray()[0]!.AsObject().Remove("packageHash");
        await File.WriteAllTextAsync(lockPath, root.ToJsonString());

        ProjectLockResult loaded = await new ProjectLockFileService().LoadAsync(lockPath);

        Assert.False(loaded.Success);
        Assert.Contains(loaded.Errors, error =>
            error.Contains("package hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_RejectsPackageHashThatDoesNotMatchAlgorithm()
    {
        string lockPath = await CreateMutableLockAsync();
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        root["runtimes"]!.AsArray()[0]!["packageHashAlgorithm"] = "Sha512";
        await File.WriteAllTextAsync(lockPath, root.ToJsonString());

        ProjectLockResult loaded = await new ProjectLockFileService().LoadAsync(lockPath);

        Assert.False(loaded.Success);
        Assert.Contains(loaded.Errors, error => error.Contains("SHA-512", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_RejectsInvalidManifestHash()
    {
        string lockPath = await CreateMutableLockAsync();
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        root["manifestSha256"] = "invalid";
        await File.WriteAllTextAsync(lockPath, root.ToJsonString());

        ProjectLockResult loaded = await new ProjectLockFileService().LoadAsync(lockPath);

        Assert.False(loaded.Success);
        Assert.Contains(loaded.Errors, error =>
            error.Contains("manifest SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_RejectsNullRuntimeEntryWithoutThrowing()
    {
        string lockPath = await CreateMutableLockAsync();
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        JsonArray runtimes = [];
        runtimes.Add(null);
        root["runtimes"] = runtimes;
        await File.WriteAllTextAsync(lockPath, root.ToJsonString());

        ProjectLockResult loaded = await new ProjectLockFileService().LoadAsync(lockPath);

        Assert.False(loaded.Success);
        Assert.Contains(loaded.Errors, error =>
            error.Contains("entry 1 is null", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> CreateMutableLockAsync()
    {
        Directory.CreateDirectory(_root);
        string manifest = Path.Combine(_root, "autoenvplus.toml");
        File.WriteAllText(manifest, "[tools]\npython = \"3.13\"\n");
        ManagedRuntimeEntry python = CreateEntry(
            RuntimeKind.Python,
            "3.13.5",
            "python.exe",
            ["stable"]);
        ProjectLockResult created = await new ProjectLockFileService().CreateAsync(
            manifest,
            [python]);
        Assert.True(created.Success);
        return created.LockPath!;
    }

    private ManagedRuntimeEntry CreateEntry(
        RuntimeKind kind,
        string version,
        string executable,
        IReadOnlyCollection<string> channels,
        PackageHashAlgorithm hashAlgorithm = PackageHashAlgorithm.Sha256,
        string providerId = "test-provider",
        string? runtimeId = null,
        RuntimeArchitecture architecture = RuntimeArchitecture.X64)
    {
        RuntimeVersion parsed = RuntimeVersion.Parse(version);
        string installRoot = Path.Combine(
            _root,
            "managed",
            kind.ToString(),
            parsed.ToString(),
            architecture.ToString().ToLowerInvariant());
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, executable), string.Empty);
        return new ManagedRuntimeEntry(
            runtimeId ?? $"{kind}-{parsed}-x64",
            providerId,
            kind,
            parsed,
            architecture,
            installRoot,
            executable,
            new string(
                kind == RuntimeKind.Python ? 'a' : 'b',
                hashAlgorithm.HexLength()),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            channels,
            hashAlgorithm);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
