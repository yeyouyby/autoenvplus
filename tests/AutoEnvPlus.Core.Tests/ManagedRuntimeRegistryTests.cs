using System.Text.Json;
using AutoEnvPlus.Core.Providers;
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

    [Fact]
    public async Task LoadAsync_MigratesSchemaOnePackageSha256()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        string installRoot = Path.Combine(_root, "runtimes", "python", "3.13.2", "x64");
        string legacyHash = new('a', 64);
        string document = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            installations = new[]
            {
                new
                {
                    id = "python-x64",
                    providerId = "python-org",
                    kind = "Python",
                    version = "3.13.2",
                    architecture = "X64",
                    installRoot,
                    executableRelativePath = "python.exe",
                    packageSha256 = legacyHash,
                    packageHash = new string('b', 128),
                    packageHashAlgorithm = "Sha512",
                    installedAtUtc = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
                    channels = Array.Empty<string>(),
                },
            },
        });
        await File.WriteAllTextAsync(registry.RegistryPath, document);

        RegistryLoadResult loaded = await registry.LoadAsync();

        ManagedRuntimeEntry entry = Assert.Single(loaded.Entries);
        Assert.Empty(loaded.Errors);
        Assert.Equal(PackageHashAlgorithm.Sha256, entry.PackageHashAlgorithm);
        Assert.Equal(legacyHash, entry.PackageHash);
    }

    [Fact]
    public async Task UpsertAsync_PersistsSchemaTwoSha512RoundTrip()
    {
        ManagedRuntimeRegistry registry = new(_root);
        ManagedRuntimeEntry expected = new(
            "dotnet-10.0.302-x64",
            "microsoft-dotnet-sdk",
            RuntimeKind.DotNet,
            RuntimeVersion.Parse("10.0.302"),
            RuntimeArchitecture.X64,
            Path.Combine(_root, "runtimes", "dotnet", "sdk", "10.0.302", "x64"),
            "dotnet.exe",
            new string('c', 128),
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            ["sdk", "lts"],
            PackageHashAlgorithm.Sha512);

        await registry.UpsertAsync(expected);
        RegistryLoadResult loaded = await new ManagedRuntimeRegistry(_root).LoadAsync();
        string json = await File.ReadAllTextAsync(registry.RegistryPath);

        ManagedRuntimeEntry actual = Assert.Single(loaded.Entries);
        Assert.Empty(loaded.Errors);
        Assert.Equal(expected.PackageHashAlgorithm, actual.PackageHashAlgorithm);
        Assert.Equal(expected.PackageHash, actual.PackageHash);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"packageHashAlgorithm\": \"Sha512\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("packageSha256", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RejectsSchemaTwoWithoutHashAlgorithm()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        string document = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            installations = new[]
            {
                new
                {
                    id = "python-x64",
                    providerId = "python-org",
                    kind = "Python",
                    version = "3.13.2",
                    architecture = "X64",
                    installRoot = Path.Combine(_root, "runtimes", "python", "3.13.2", "x64"),
                    executableRelativePath = "python.exe",
                    packageHash = new string('a', 64),
                    installedAtUtc = DateTimeOffset.UtcNow,
                    channels = Array.Empty<string>(),
                },
            },
        });
        await File.WriteAllTextAsync(registry.RegistryPath, document);

        RegistryLoadResult loaded = await registry.LoadAsync();

        Assert.Empty(loaded.Entries);
        Assert.Contains(loaded.Errors, error =>
            error.Contains("hash algorithm", StringComparison.OrdinalIgnoreCase));
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
