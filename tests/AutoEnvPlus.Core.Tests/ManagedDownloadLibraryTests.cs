using AutoEnvPlus.Core.Downloads;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedDownloadLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Library-{Guid.NewGuid():N}");

    [Fact]
    public async Task ListFiles_IncludesOnlyAllowedRegularFilesAndSkipsReparsePoints()
    {
        ManagedDownloadLibrary library = new(_root);
        File.WriteAllBytes(Path.Combine(_root, "package.whl"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a package");
        Directory.CreateDirectory(Path.Combine(_root, "directory.zip"));
        string external = Path.Combine(Path.GetDirectoryName(_root)!, $"external-{Guid.NewGuid():N}.whl");
        File.WriteAllBytes(external, [9, 9, 9]);
        string link = Path.Combine(_root, "linked.whl");
        bool linkCreated = false;
        try
        {
            File.CreateSymbolicLink(link, external);
            linkCreated = true;
        }
        catch (Exception linkException) when (linkException is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
        }

        IReadOnlyList<ManagedDownloadLibraryItem> items = library.ListFiles();

        ManagedDownloadLibraryItem item = Assert.Single(items);
        Assert.Equal("package.whl", item.FileName);
        Assert.Equal(3, item.SizeBytes);
        if (linkCreated)
        {
            Assert.DoesNotContain(items, candidate => candidate.FileName == "linked.whl");
            await Assert.ThrowsAsync<InvalidDataException>(() => library.DeleteAsync("linked.whl"));
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(external));
            File.Delete(link);
        }

        File.Delete(external);
    }

    [Fact]
    public async Task DeleteAsync_DeletesRegularFileAndAtomicallyRemovesManifestEntry()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await new LocalPackageImportService(libraryRoot).ImportAsync(
            new LocalPackageImportRequest(source, null, 100));
        ManagedDownloadLibrary library = new(libraryRoot);

        ManagedDownloadDeleteResult result = await library.DeleteAsync("package.whl");

        Assert.True(result.Deleted);
        Assert.True(result.ManifestUpdated);
        Assert.False(File.Exists(Path.Combine(libraryRoot, "package.whl")));
        Assert.Empty(library.ListFiles());
        Assert.DoesNotContain("package.whl", File.ReadAllText(library.ManifestPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(library.StagingRoot));
    }

    [Fact]
    public async Task DeleteAsync_WaitsForCrossProcessTransactionLockAndCancelsSafely()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources-lock")).FullName;
        string libraryRoot = Path.Combine(_root, "library-lock");
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await new LocalPackageImportService(libraryRoot).ImportAsync(
            new LocalPackageImportRequest(source, null, 100));
        ManagedDownloadLibrary library = new(libraryRoot);
        await using FileStream externalLease = new(
            Path.Combine(libraryRoot, ".autoenvplus-library.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.DeleteAsync(
            "package.whl",
            cancellation.Token));

        Assert.True(File.Exists(Path.Combine(libraryRoot, "package.whl")));
        Assert.Contains("package.whl", await File.ReadAllTextAsync(library.ManifestPath));
    }

    [Fact]
    public async Task CorruptManifestMakesListAndDeleteFailWithoutDeletingEvidence()
    {
        ManagedDownloadLibrary library = new(_root);
        string packagePath = Path.Combine(_root, "package.whl");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        await File.WriteAllTextAsync(library.ManifestPath, "{ invalid json");

        Assert.Throws<InvalidDataException>(() => library.ListFiles());
        await Assert.ThrowsAsync<InvalidDataException>(() => library.DeleteAsync("package.whl"));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(packagePath));
        Assert.Equal("{ invalid json", File.ReadAllText(library.ManifestPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(library.StagingRoot));
    }

    [Fact]
    public async Task OversizedManifestMakesSynchronousAndAsyncReadsFailClosed()
    {
        ManagedDownloadLibrary library = new(_root);
        await using (FileStream stream = new(
            library.ManifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(ManagedDownloadLibrary.MaximumManifestBytes + 1);
        }

        InvalidDataException listException = Assert.Throws<InvalidDataException>(() =>
            library.ListFiles());
        InvalidDataException deleteException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            library.DeleteAsync("package.whl"));

        Assert.Contains("byte limit", listException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("byte limit", deleteException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExcessManifestEntriesMakeSynchronousAndAsyncReadsFailClosed()
    {
        ManagedDownloadLibrary library = new(_root);
        string entries = string.Join(
            ',',
            Enumerable.Repeat("{}", ManagedDownloadLibrary.MaximumManifestEntries + 1));
        await File.WriteAllTextAsync(
            library.ManifestPath,
            $"{{\"schemaVersion\":1,\"entries\":[{entries}]}}");

        InvalidDataException listException = Assert.Throws<InvalidDataException>(() =>
            library.ListFiles());
        InvalidDataException deleteException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            library.DeleteAsync("package.whl"));

        Assert.Contains("entry limit", listException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entry limit", deleteException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_RejectsAncestorReparsePointWhenSupported()
    {
        string realParent = Directory.CreateDirectory(Path.Combine(_root, "real-parent")).FullName;
        string linkedParent = Path.Combine(_root, "linked-parent");
        try
        {
            Directory.CreateSymbolicLink(linkedParent, realParent);
        }
        catch (Exception linkException) when (linkException is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new ManagedDownloadLibrary(Path.Combine(linkedParent, "library")));

        Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(realParent, "library")));
        Directory.Delete(linkedParent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
