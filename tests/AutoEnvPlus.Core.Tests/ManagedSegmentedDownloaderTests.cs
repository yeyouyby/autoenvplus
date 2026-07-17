using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedSegmentedDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Segmented-{Guid.NewGuid():N}");

    [Fact]
    public async Task DownloadAsync_DownloadsValidatedRangesAndPersistsManifest()
    {
        byte[] payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        ConcurrentBag<(long From, long To)> requestedRanges = [];
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(payload.Length, "\"asset-v1\"");
            }

            if (request.Headers.Range is not null)
            {
                RangeItemHeaderValue requested = Assert.Single(request.Headers.Range.Ranges);
                long from = Assert.IsType<long>(requested.From);
                long to = Assert.IsType<long>(requested.To);
                requestedRanges.Add((from, to));
                Assert.Equal("\"asset-v1\"", request.Headers.IfRange?.EntityTag?.Tag);
                return CreateRange(payload, from, to, "\"asset-v1\"");
            }

            throw new Xunit.Sdk.XunitException("A segmented transfer must not issue a full GET.");
        }));
        string hash = Convert.ToHexString(SHA512.HashData(payload)).ToLowerInvariant();
        string contentSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        ManagedSegmentedDownloader downloader = new(client, _root);

        SegmentedDownloadResult result = await downloader.DownloadAsync(new SegmentedDownloadRequest(
            new Uri("https://packages.example.test/torch.whl?temporary-secret=redacted"),
            "torch.whl",
            4,
            1_024,
            new PackageHashExpectation(PackageHashAlgorithm.Sha512, hash)));

        Assert.Equal(DownloadTransferMode.Segmented, result.TransferMode);
        Assert.Equal(4, result.SegmentCount);
        Assert.False(result.WasRangeFallback);
        Assert.Equal(contentSha256, result.ContentSha256);
        Assert.Equal(PackageHashAlgorithm.Sha512, result.ExpectedHashAlgorithm);
        Assert.Equal(hash, result.ExpectedHash);
        Assert.Equal(hash, result.VerifiedHash);
        Assert.True(result.HasVerifiedExpectedHash);
        Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
        Assert.Contains((0, 0), requestedRanges);
        Assert.Equal(5, requestedRanges.Count);
        ManagedDownloadLibrary library = new(_root);
        ManagedDownloadLibraryItem item = Assert.Single(await library.ListFilesAsync());
        Assert.Equal(ManagedDownloadOrigin.Network, item.Origin);
        Assert.Equal("https://packages.example.test/torch.whl", item.Source);
        Assert.Equal(contentSha256, item.ContentSha256);
        Assert.Equal(hash, item.ExpectedHash);
        Assert.True(item.HasVerifiedExpectedHash);
        Assert.DoesNotContain("temporary-secret", File.ReadAllText(library.ManifestPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(library.StagingRoot));
    }

    [Fact]
    public async Task DownloadAsync_FallsBackWhenRangeIsIgnored()
    {
        byte[] payload = Enumerable.Range(0, 24).Select(value => (byte)value).ToArray();
        int fullGets = 0;
        int rangeGets = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(payload.Length, "\"asset-v1\"");
            }

            if (request.Headers.Range is not null)
            {
                rangeGets++;
                HttpResponseMessage ignored = StubHttpMessageHandler.Bytes(payload);
                ignored.Headers.ETag = new EntityTagHeaderValue("\"asset-v1\"");
                return ignored;
            }

            fullGets++;
            HttpResponseMessage full = StubHttpMessageHandler.Bytes(payload);
            full.Headers.ETag = new EntityTagHeaderValue("\"asset-v1\"");
            return full;
        }));

        SegmentedDownloadResult result = await new ManagedSegmentedDownloader(client, _root)
            .DownloadAsync(new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                8,
                1_024));

        Assert.Equal(DownloadTransferMode.SingleStream, result.TransferMode);
        Assert.Equal(DownloadFallbackReason.RangeNotSupported, result.FallbackReason);
        Assert.True(result.WasRangeFallback);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            result.ContentSha256);
        Assert.False(result.HasVerifiedExpectedHash);
        Assert.Equal(1, rangeGets);
        Assert.Equal(1, fullGets);
        Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
        ManagedDownloadLibraryItem item = Assert.Single(
            new ManagedDownloadLibrary(_root).ListFiles());
        Assert.Equal(result.ContentSha256, item.ContentSha256);
        Assert.False(item.HasVerifiedExpectedHash);
    }

    [Fact]
    public async Task DownloadAsync_UsesLastModifiedAsSegmentEntityIdentity()
    {
        byte[] payload = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        DateTimeOffset lastModified = new(2026, 7, 15, 1, 2, 3, TimeSpan.Zero);
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(payload.Length, null, lastModified);
            }

            RangeItemHeaderValue requested = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(lastModified, request.Headers.IfRange?.Date);
            long from = Assert.IsType<long>(requested.From);
            long to = Assert.IsType<long>(requested.To);
            return CreateRange(payload, from, to, null, lastModified);
        }));

        SegmentedDownloadResult result = await new ManagedSegmentedDownloader(client, _root)
            .DownloadAsync(new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                2,
                1_024));

        Assert.Equal(DownloadTransferMode.Segmented, result.TransferMode);
        Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_StableIdentityUnavailableFallsBackWithoutCombiningProbeByte()
    {
        byte[] payload = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        int fullGets = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(payload.Length, null);
            }

            if (request.Headers.Range is not null)
            {
                RangeItemHeaderValue requested = Assert.Single(request.Headers.Range.Ranges);
                return CreateRange(
                    payload,
                    Assert.IsType<long>(requested.From),
                    Assert.IsType<long>(requested.To),
                    null);
            }

            fullGets++;
            return StubHttpMessageHandler.Bytes(payload);
        }));

        SegmentedDownloadResult result = await new ManagedSegmentedDownloader(client, _root)
            .DownloadAsync(new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                4,
                1_024));

        Assert.Equal(DownloadFallbackReason.StableEntityUnavailable, result.FallbackReason);
        Assert.Equal(1, fullGets);
        Assert.Equal(payload, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_RejectsMismatchedContentRangeAndDeletesStaging()
    {
        byte[] payload = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(payload.Length, "\"asset-v1\"");
            }

            RangeItemHeaderValue requested = Assert.Single(request.Headers.Range!.Ranges);
            long from = Assert.IsType<long>(requested.From);
            long to = Assert.IsType<long>(requested.To);
            if (from == 0 && to == 0)
            {
                return CreateRange(payload, from, to, "\"asset-v1\"");
            }

            HttpResponseMessage invalid = CreateRange(payload, from, to, "\"asset-v1\"");
            invalid.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                from + 1,
                to + 1,
                payload.Length);
            return invalid;
        }));
        ManagedSegmentedDownloader downloader = new(client, _root);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                4,
                1_024)));

        Assert.False(File.Exists(Path.Combine(_root, "asset.zip")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(_root, ".autoenvplus-staging")));
    }

    [Fact]
    public async Task DownloadAsync_EntityTagChangeDiscardsSegmentsAndFallsBackFromZero()
    {
        byte[] original = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] replacement = Enumerable.Range(100, 19).Select(value => (byte)value).ToArray();
        int fullGets = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(original.Length, "\"asset-v1\"");
            }

            if (request.Headers.Range is not null)
            {
                RangeItemHeaderValue requested = Assert.Single(request.Headers.Range.Ranges);
                long from = Assert.IsType<long>(requested.From);
                long to = Assert.IsType<long>(requested.To);
                return from == 0 && to == 0
                    ? CreateRange(original, from, to, "\"asset-v1\"")
                    : CreateRange(original, from, to, "\"asset-v2\"");
            }

            fullGets++;
            HttpResponseMessage full = StubHttpMessageHandler.Bytes(replacement);
            full.Headers.ETag = new EntityTagHeaderValue("\"asset-v2\"");
            return full;
        }));

        SegmentedDownloadResult result = await new ManagedSegmentedDownloader(client, _root)
            .DownloadAsync(new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                4,
                1_024));

        Assert.Equal(DownloadTransferMode.SingleStream, result.TransferMode);
        Assert.Equal(DownloadFallbackReason.EntityChanged, result.FallbackReason);
        Assert.Equal(1, fullGets);
        Assert.Equal(replacement, File.ReadAllBytes(result.FilePath));
    }

    [Fact]
    public async Task DownloadAsync_HashMismatchDeletesUncommittedFile()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        using HttpClient client = new(new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Head
                ? CreateHead(payload.Length, "\"asset-v1\"")
                : StubHttpMessageHandler.Bytes(payload)));
        string incorrectHash = new('0', 64);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ManagedSegmentedDownloader(client, _root).DownloadAsync(
                new SegmentedDownloadRequest(
                    new Uri("https://packages.example.test/asset.zip"),
                    "asset.zip",
                    1,
                    1_024,
                    new PackageHashExpectation(PackageHashAlgorithm.Sha256, incorrectHash))));

        Assert.False(File.Exists(Path.Combine(_root, "asset.zip")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(_root, ".autoenvplus-staging")));
    }

    [Fact]
    public async Task DownloadAsync_CancellationDeletesAllStagingAndReportsCancelled()
    {
        byte[] payload = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        TaskCompletionSource segmentsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient client = new(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return CreateHead(payload.Length, "\"asset-v1\"");
            }

            RangeItemHeaderValue requested = Assert.Single(request.Headers.Range!.Ranges);
            long from = Assert.IsType<long>(requested.From);
            long to = Assert.IsType<long>(requested.To);
            if (from == 0 && to == 0)
            {
                return CreateRange(payload, from, to, "\"asset-v1\"");
            }

            segmentsStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new Xunit.Sdk.XunitException("The cancelled request unexpectedly resumed.");
        }));
        ManagedSegmentedDownloader downloader = new(client, _root);
        ConcurrentBag<ManagedTransferProgress> updates = [];
        SynchronousProgress progress = new(updates.Add);
        using CancellationTokenSource cancellation = new();
        Task<SegmentedDownloadResult> transfer = downloader.DownloadAsync(
            new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                4,
                1_024),
            progress,
            cancellation.Token);
        await segmentsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);

        Assert.Equal(
            SegmentedDownloadCancellationBehavior.DeleteStaging,
            downloader.CancellationBehavior);
        Assert.Contains(updates, update => update.Phase == ManagedTransferPhase.Cancelled);
        Assert.False(File.Exists(Path.Combine(_root, "asset.zip")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(_root, ".autoenvplus-staging")));
    }

    [Fact]
    public async Task DownloadAsync_RejectsDestinationPathEscapeBeforeNetworkAccess()
    {
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requests++;
            return StubHttpMessageHandler.Bytes([1]);
        }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ManagedSegmentedDownloader(client, _root).DownloadAsync(
                new SegmentedDownloadRequest(
                    new Uri("https://packages.example.test/asset.zip"),
                    "..\\escaped.whl",
                    4,
                    1_024)));

        Assert.Equal(0, requests);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.whl")));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonHttpsAndUnsupportedConnectionCount()
    {
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requests++;
            return StubHttpMessageHandler.Bytes([1]);
        }));
        ManagedSegmentedDownloader downloader = new(client, _root);

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(
            new SegmentedDownloadRequest(
                new Uri("http://packages.example.test/asset.zip"),
                "asset.zip",
                4,
                100)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => downloader.DownloadAsync(
            new SegmentedDownloadRequest(
                new Uri("https://packages.example.test/asset.zip"),
                "asset.zip",
                3,
                100)));

        Assert.Equal(0, requests);
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }, ManagedSegmentedDownloader.SupportedConnectionCounts);
    }

    [Fact]
    public async Task DownloadAsync_RejectsProbedLengthOverMaximumBeforeBodyDownload()
    {
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Head, request.Method);
            return CreateHead(1_000, "\"asset-v1\"");
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ManagedSegmentedDownloader(client, _root).DownloadAsync(
                new SegmentedDownloadRequest(
                    new Uri("https://packages.example.test/asset.zip"),
                    "asset.zip",
                    16,
                    100)));

        Assert.Equal(1, requests);
        Assert.False(File.Exists(Path.Combine(_root, "asset.zip")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HttpResponseMessage CreateHead(
        long contentLength,
        string? entityTag,
        DateTimeOffset? lastModified = null)
    {
        HttpResponseMessage response = StubHttpMessageHandler.Bytes([]);
        response.Content.Headers.ContentLength = contentLength;
        response.Headers.AcceptRanges.Add("bytes");
        if (entityTag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(entityTag);
        }

        response.Content.Headers.LastModified = lastModified;
        return response;
    }

    private static HttpResponseMessage CreateRange(
        byte[] payload,
        long from,
        long to,
        string? entityTag,
        DateTimeOffset? lastModified = null)
    {
        byte[] segment = payload[(int)from..checked((int)to + 1)];
        HttpResponseMessage response = StubHttpMessageHandler.Bytes(segment);
        response.StatusCode = HttpStatusCode.PartialContent;
        if (entityTag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(entityTag);
        }

        response.Content.Headers.LastModified = lastModified;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
            from,
            to,
            payload.Length);
        return response;
    }

    private sealed class AsyncHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await responder(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class SynchronousProgress(Action<ManagedTransferProgress> report)
        : IProgress<ManagedTransferProgress>
    {
        public void Report(ManagedTransferProgress value) => report(value);
    }
}
