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
    public async Task UpsertAsync_TwoStoreInstancesDoNotLoseConcurrentUpdates()
    {
        ManagedRuntimeRegistry[] stores = Enumerable.Range(0, 24)
            .Select(_ => new ManagedRuntimeRegistry(_root))
            .ToArray();
        ManagedRuntimeEntry[] entries = Enumerable.Range(0, stores.Length)
            .Select(index => CreateEntry($"3.13.{index + 1}") with
            {
                Id = $"python-{index:D2}-x64",
            })
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) =>
            store.UpsertAsync(entries[index])));
        RegistryLoadResult loaded = await new ManagedRuntimeRegistry(_root).LoadAsync();

        Assert.Empty(loaded.Errors);
        Assert.Equal(entries.Length, loaded.Entries.Count);
        Assert.Equal(
            entries.Select(entry => entry.Id).Order(StringComparer.OrdinalIgnoreCase),
            loaded.Entries.Select(entry => entry.Id).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpsertAsync_WaitsForFixedInterprocessLock()
    {
        ManagedRuntimeRegistry first = new(_root);
        ManagedRuntimeRegistry second = new(_root);
        await first.LoadAsync();
        string lockPath = Path.Combine(
            _root,
            "state",
            "managed-runtime-registry.lock");
        Task<RegistryLoadResult> pendingUpsert;

        using (FileStream heldLock = new(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            pendingUpsert = second.UpsertAsync(CreateEntry("3.13.2"));
            await Task.Delay(150);
            Assert.False(pendingUpsert.IsCompleted);
        }

        RegistryLoadResult result = await pendingUpsert;
        Assert.Empty(result.Errors);
        Assert.Single(result.Entries);
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
    public async Task LoadAsync_RejectsRegistryAboveSizeLimitBeforeDeserialization()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        await using (FileStream stream = new(
            registry.RegistryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(ManagedRuntimeRegistry.MaximumRegistryBytes + 1);
        }

        RegistryLoadResult loaded = await registry.LoadAsync();

        Assert.Empty(loaded.Entries);
        Assert.Contains(
            "byte limit",
            Assert.Single(loaded.Errors),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsRegistryAboveEntryLimitBeforeEntryValidation()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        string installations = string.Join(
            ',',
            Enumerable.Repeat("{}", ManagedRuntimeRegistry.MaximumRegistryEntries + 1));
        await File.WriteAllTextAsync(
            registry.RegistryPath,
            $"{{\"schemaVersion\":2,\"installations\":[{installations}]}}");

        RegistryLoadResult loaded = await registry.LoadAsync();

        Assert.Empty(loaded.Entries);
        Assert.Contains(
            "entry limit",
            Assert.Single(loaded.Errors),
            StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task LoadAsync_ReportsEquivalentProviderVersionsUnderDifferentRuntimeIds()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        object CreateItem(string id, string version) => new
        {
            id,
            providerId = "plugin:reusable-provider",
            kind = "Python",
            version,
            architecture = "X64",
            installRoot = Path.Combine(_root, "runtimes", "python", id),
            executableRelativePath = "python.exe",
            packageHash = new string('a', 64),
            packageHashAlgorithm = "Sha256",
            installedAtUtc = DateTimeOffset.UtcNow,
            channels = Array.Empty<string>(),
        };
        string document = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            installations = new[]
            {
                CreateItem("first-runtime", "3.13.5+first"),
                CreateItem("replacement-runtime", "3.13.5+replacement"),
            },
        });
        await File.WriteAllTextAsync(registry.RegistryPath, document);

        RegistryLoadResult loaded = await registry.LoadAsync();

        string error = Assert.Single(loaded.Errors);
        Assert.Contains("equivalent versions", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first-runtime", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replacement-runtime", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_ReportsCaseInsensitiveDuplicateRuntimeIdsWithoutIdentityLeak()
    {
        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        object CreateItem(string id, string version) => new
        {
            id,
            providerId = "plugin:reusable-provider",
            kind = "Python",
            version,
            architecture = "X64",
            installRoot = Path.Combine(_root, "runtimes", "python", version),
            executableRelativePath = "python.exe",
            packageHash = new string('a', 64),
            packageHashAlgorithm = "Sha256",
            installedAtUtc = DateTimeOffset.UtcNow,
            channels = Array.Empty<string>(),
        };
        string document = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            installations = new[]
            {
                CreateItem("Duplicate-Runtime", "3.13.4"),
                CreateItem("duplicate-runtime", "3.13.5"),
            },
        });
        await File.WriteAllTextAsync(registry.RegistryPath, document);

        RegistryLoadResult loaded = await registry.LoadAsync();

        string error = Assert.Single(loaded.Errors);
        Assert.Contains("duplicate runtime ID", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate-runtime", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointStateDirectoryWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string externalState = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Registry-State-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(externalState);
        string stateLink = Path.Combine(_root, "state");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(stateLink, externalState);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            IOException unsafePathException = await Assert.ThrowsAnyAsync<IOException>(() =>
                new ManagedRuntimeRegistry(_root).LoadAsync());

            Assert.Contains(
                "reparse",
                unsafePathException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(stateLink))
            {
                Directory.Delete(stateLink);
            }

            if (Directory.Exists(externalState))
            {
                Directory.Delete(externalState, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointRegistryFileWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ManagedRuntimeRegistry registry = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.RegistryPath)!);
        string externalRegistry = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Registry-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            externalRegistry,
            "{\"schemaVersion\":2,\"installations\":[]}");
        try
        {
            try
            {
                File.CreateSymbolicLink(registry.RegistryPath, externalRegistry);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            IOException unsafePathException = await Assert.ThrowsAnyAsync<IOException>(() =>
                registry.LoadAsync());

            Assert.Contains(
                "reparse",
                unsafePathException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(registry.RegistryPath))
            {
                File.Delete(registry.RegistryPath);
            }

            if (File.Exists(externalRegistry))
            {
                File.Delete(externalRegistry);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointLockFileWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ManagedRuntimeRegistry registry = new(_root);
        string stateDirectory = Path.GetDirectoryName(registry.RegistryPath)!;
        Directory.CreateDirectory(stateDirectory);
        string lockPath = Path.Combine(stateDirectory, "managed-runtime-registry.lock");
        string externalLock = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Registry-Lock-{Guid.NewGuid():N}.lock");
        await File.WriteAllTextAsync(externalLock, "lock");
        try
        {
            try
            {
                File.CreateSymbolicLink(lockPath, externalLock);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            IOException unsafePathException = await Assert.ThrowsAnyAsync<IOException>(() =>
                registry.LoadAsync());

            Assert.Contains(
                "reparse",
                unsafePathException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }

            if (File.Exists(externalLock))
            {
                File.Delete(externalLock);
            }
        }
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
