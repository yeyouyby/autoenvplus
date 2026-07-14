using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class PnpmRcServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Pnpm-{Guid.NewGuid():N}");

    [Fact]
    public void Read_ResolvesEnvironmentExpressionAndPreservesConfiguredStore()
    {
        string config = Path.Combine(_root, "config", "rc");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "registry=https://registry.example/\nstore-dir=${CACHE_ROOT}\\pnpm\n");
        CacheEnvironment environment = new(
            Path.Combine(_root, "local"),
            Path.Combine(_root, "profile"),
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CACHE_ROOT"] = Path.Combine(_root, "cache root"),
            });

        PnpmRcReadResult result = new PnpmRcService().Read(config, environment);

        Assert.Null(result.Error);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "cache root", "pnpm")),
            result.StoreDirectory);
        Assert.Contains("registry=", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMutation_PreservesCommentsOtherKeysAndNewLines()
    {
        string config = Path.Combine(_root, "config", "rc");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        string before = "# company registry\r\nregistry=https://registry.example/\r\nstore-dir=C:\\old-store\r\nstrict-ssl=true\r\n";
        File.WriteAllText(config, before);
        string destination = Path.Combine(_root, "new store");

        PnpmRcMutation mutation = new PnpmRcService().CreateMutation(config, destination);

        Assert.Equal(before, mutation.Before);
        Assert.Contains("# company registry\r\n", mutation.After, StringComparison.Ordinal);
        Assert.Contains("registry=https://registry.example/\r\n", mutation.After, StringComparison.Ordinal);
        Assert.Contains($"store-dir={destination}\r\n", mutation.After, StringComparison.Ordinal);
        Assert.Contains("strict-ssl=true\r\n", mutation.After, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\old-store", mutation.After, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAndMutation_RejectDuplicateOrUnresolvedStoreEntries()
    {
        string duplicate = Path.Combine(_root, "duplicate.rc");
        Directory.CreateDirectory(_root);
        File.WriteAllText(duplicate, "store-dir=C:\\one\nstore-dir=C:\\two\n");
        string unresolved = Path.Combine(_root, "unresolved.rc");
        File.WriteAllText(unresolved, "store-dir=${MISSING}\\pnpm\n");
        CacheEnvironment environment = new(
            Path.Combine(_root, "local"),
            Path.Combine(_root, "profile"),
            new Dictionary<string, string?>());

        PnpmRcReadResult duplicateRead = new PnpmRcService().Read(duplicate, environment);
        PnpmRcReadResult unresolvedRead = new PnpmRcService().Read(unresolved, environment);

        Assert.NotNull(duplicateRead.Error);
        Assert.NotNull(unresolvedRead.Error);
        Assert.Throws<InvalidDataException>(() => new PnpmRcService().CreateMutation(
            duplicate,
            Path.Combine(_root, "new")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
