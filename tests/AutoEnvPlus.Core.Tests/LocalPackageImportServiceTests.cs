using System.Security.Cryptography;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Tests;

public sealed class LocalPackageImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Import-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportAsync_StreamsVerifiesCommitsAndPersistsManifest()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        byte[] payload = Enumerable.Range(0, 200).Select(value => (byte)value).ToArray();
        string source = Path.Combine(sources, "torch.whl");
        await File.WriteAllBytesAsync(source, payload);
        string hash = Convert.ToHexString(SHA512.HashData(payload)).ToLowerInvariant();
        string contentSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        LocalPackageImportService service = new(libraryRoot);

        LocalPackageImportResult result = await service.ImportAsync(new LocalPackageImportRequest(
            source,
            null,
            1_024,
            new PackageHashExpectation(PackageHashAlgorithm.Sha512, hash)));

        Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
        Assert.Equal(contentSha256, result.ContentSha256);
        Assert.Equal(hash, result.ExpectedHash);
        Assert.Equal(hash, result.VerifiedHash);
        Assert.True(result.HasVerifiedExpectedHash);
        ManagedDownloadLibrary library = new(libraryRoot);
        ManagedDownloadLibraryItem item = Assert.Single(await library.ListFilesAsync());
        Assert.Equal(ManagedDownloadOrigin.LocalImport, item.Origin);
        Assert.Equal("torch.whl", item.Source);
        Assert.Equal(PackageHashAlgorithm.Sha512, item.HashAlgorithm);
        Assert.Equal(contentSha256, item.ContentSha256);
        Assert.Equal(hash, item.ExpectedHash);
        Assert.Equal(hash, item.VerifiedHash);
        Assert.True(item.HasVerifiedExpectedHash);
        Assert.True(File.Exists(library.ManifestPath));
    }

    [Fact]
    public async Task ImportAsync_HashMismatchLeavesNoDestinationOrStaging()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalPackageImportService(libraryRoot).ImportAsync(
                new LocalPackageImportRequest(
                    source,
                    "package.whl",
                    100,
                    new PackageHashExpectation(PackageHashAlgorithm.Sha256, new string('0', 64)))));

        Assert.False(File.Exists(Path.Combine(libraryRoot, "package.whl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(libraryRoot, ".autoenvplus-staging")));
    }

    [Fact]
    public async Task ImportAsync_RejectsDestinationEscape()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LocalPackageImportService(libraryRoot).ImportAsync(
                new LocalPackageImportRequest(
                    source,
                    "..\\outside.whl",
                    100)));

        Assert.False(File.Exists(Path.Combine(_root, "outside.whl")));
    }

    [Fact]
    public async Task ImportAsync_RequiresExplicitOverwriteAndUpdatesManifest()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string first = Path.Combine(sources, "first.whl");
        string second = Path.Combine(sources, "second.whl");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5, 6, 7]);
        LocalPackageImportService service = new(libraryRoot);
        await service.ImportAsync(new LocalPackageImportRequest(first, "package.whl", 100));

        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(
            new LocalPackageImportRequest(second, "package.whl", 100)));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(libraryRoot, "package.whl")));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(
            new LocalPackageImportRequest(
                second,
                "package.whl",
                100,
                new PackageHashExpectation(PackageHashAlgorithm.Sha256, new string('0', 64)),
                Overwrite: true)));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(libraryRoot, "package.whl")));

        await service.ImportAsync(new LocalPackageImportRequest(
            second,
            "package.whl",
            100,
            Overwrite: true));

        Assert.Equal(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(Path.Combine(libraryRoot, "package.whl")));
        ManagedDownloadLibraryItem item = Assert.Single(
            new ManagedDownloadLibrary(libraryRoot).ListFiles());
        Assert.Equal("second.whl", item.Source);
        Assert.Equal(4, item.SizeBytes);
    }

    [Fact]
    public async Task ImportAsync_CorruptManifestFailsBeforeDestinationCommit()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        ManagedDownloadLibrary library = new(libraryRoot);
        await File.WriteAllTextAsync(library.ManifestPath, "not-json");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalPackageImportService(libraryRoot).ImportAsync(
                new LocalPackageImportRequest(source, null, 100)));

        Assert.False(File.Exists(Path.Combine(libraryRoot, "package.whl")));
        Assert.Equal("not-json", File.ReadAllText(library.ManifestPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(library.StagingRoot));
    }

    [Fact]
    public async Task ImportAsync_ManifestCommitFailureRestoresOverwrittenFile()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string first = Path.Combine(sources, "first.whl");
        string second = Path.Combine(sources, "second.whl");
        byte[] original = [1, 2, 3];
        await File.WriteAllBytesAsync(first, original);
        await File.WriteAllBytesAsync(second, [4, 5, 6, 7]);
        LocalPackageImportService service = new(libraryRoot);
        await service.ImportAsync(new LocalPackageImportRequest(
            first,
            "package.whl",
            100));
        ManagedDownloadLibrary library = new(libraryRoot);

        await using (FileStream manifestLock = new(
            library.ManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => service.ImportAsync(
                new LocalPackageImportRequest(
                    second,
                    "package.whl",
                    100,
                    Overwrite: true)));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(libraryRoot, "package.whl")));
        ManagedDownloadLibraryItem item = Assert.Single(library.ListFiles());
        Assert.Equal("first.whl", item.Source);
        Assert.Equal(original.Length, item.SizeBytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(library.StagingRoot));
    }

    [Fact]
    public async Task ListFilesAsync_DoesNotReuseExpectedHashEvidenceAfterSameLengthReplacement()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        byte[] original = [1, 2, 3];
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, original);
        string expected = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        LocalPackageImportService service = new(libraryRoot);
        LocalPackageImportResult imported = await service.ImportAsync(new(
            source,
            "package.whl",
            100,
            new PackageHashExpectation(PackageHashAlgorithm.Sha256, expected)));
        await File.WriteAllBytesAsync(imported.FilePath, new byte[] { 4, 5, 6 });

        ManagedDownloadLibraryItem item = Assert.Single(
            await new ManagedDownloadLibrary(libraryRoot).ListFilesAsync());

        Assert.True(item.HasRecordedExpectedHashEvidence);
        Assert.True(item.ContentIdentityRevalidated);
        Assert.True(item.ContentIdentityChanged);
        Assert.False(item.HasVerifiedExpectedHash);
    }

    [Fact]
    public async Task ImportAsync_CancellationDeletesCopiedStagingFile()
    {
        string sources = Directory.CreateDirectory(Path.Combine(_root, "sources")).FullName;
        string libraryRoot = Path.Combine(_root, "library");
        string source = Path.Combine(sources, "package.whl");
        await File.WriteAllBytesAsync(source, new byte[1_000_000]);
        using CancellationTokenSource cancellation = new();
        CallbackProgress progress = new(update =>
        {
            if (update.Phase == ManagedTransferPhase.Copying && update.CompletedBytes > 0)
            {
                cancellation.Cancel();
            }
        });
        LocalPackageImportService service = new(libraryRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(
            new LocalPackageImportRequest(source, null, 2_000_000),
            progress,
            cancellation.Token));

        Assert.Equal(
            SegmentedDownloadCancellationBehavior.DeleteStaging,
            service.CancellationBehavior);
        Assert.False(File.Exists(Path.Combine(libraryRoot, "package.whl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(libraryRoot, ".autoenvplus-staging")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CallbackProgress(Action<ManagedTransferProgress> callback)
        : IProgress<ManagedTransferProgress>
    {
        public void Report(ManagedTransferProgress value) => callback(value);
    }
}
