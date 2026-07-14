using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AutoEnvPlus.Core.Installation;

namespace AutoEnvPlus.Core.Tests;

public sealed class ResumableHttpDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Download-{Guid.NewGuid():N}");

    [Fact]
    public async Task DownloadAsync_RetainsTruncatedPartialAndResumesWithIfRange()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789");
        byte[] prefix = payload[..4];
        int requestCount = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                HttpResponseMessage truncated = StubHttpMessageHandler.Bytes(prefix);
                truncated.Headers.ETag = new EntityTagHeaderValue("\"asset-v1\"");
                truncated.Content.Headers.ContentLength = payload.Length;
                return truncated;
            }

            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(prefix.Length, range.From);
            Assert.Equal("\"asset-v1\"", request.Headers.IfRange!.EntityTag!.Tag);
            HttpResponseMessage partial = StubHttpMessageHandler.Bytes(payload[prefix.Length..]);
            partial.StatusCode = HttpStatusCode.PartialContent;
            partial.Headers.ETag = new EntityTagHeaderValue("\"asset-v1\"");
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefix.Length,
                payload.Length - 1,
                payload.Length);
            return partial;
        }));
        string target = Path.Combine(_root, "asset.zip");
        ResumableHttpDownloader downloader = new(client);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            new Uri("https://example.test/asset.zip"),
            target,
            100));
        Assert.True(File.Exists(target + ".partial"));
        Assert.True(File.Exists(target + ".partial.json"));

        ResumableDownloadResult result = await downloader.DownloadAsync(
            new Uri("https://example.test/asset.zip"),
            target,
            100);

        Assert.True(result.WasResumed);
        Assert.False(result.WasCached);
        Assert.Equal(payload, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".partial"));
        Assert.False(File.Exists(target + ".partial.json"));
    }

    [Fact]
    public async Task DownloadAsync_ServerIgnoringRangeRestartsFromZero()
    {
        byte[] payload = Encoding.UTF8.GetBytes("complete-payload");
        byte[] prefix = payload[..3];
        int requestCount = 0;
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                HttpResponseMessage truncated = StubHttpMessageHandler.Bytes(prefix);
                truncated.Headers.ETag = new EntityTagHeaderValue("\"old\"");
                truncated.Content.Headers.ContentLength = payload.Length;
                return truncated;
            }

            Assert.NotNull(request.Headers.Range);
            HttpResponseMessage replacement = StubHttpMessageHandler.Bytes(payload);
            replacement.Headers.ETag = new EntityTagHeaderValue("\"new\"");
            return replacement;
        }));
        string target = Path.Combine(_root, "asset.zip");
        ResumableHttpDownloader downloader = new(client);
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            new Uri("https://example.test/asset.zip"),
            target,
            100));

        ResumableDownloadResult result = await downloader.DownloadAsync(
            new Uri("https://example.test/asset.zip"),
            target,
            100);

        Assert.False(result.WasResumed);
        Assert.Equal(payload, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task DownloadAsync_UsesCompletedCacheWithoutAnotherRequest()
    {
        byte[] payload = Encoding.UTF8.GetBytes("cached");
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requests++;
            return StubHttpMessageHandler.Bytes(payload);
        }));
        string target = Path.Combine(_root, "asset.zip");
        ResumableHttpDownloader downloader = new(client);

        await downloader.DownloadAsync(new Uri("https://example.test/asset.zip"), target, 100);
        ResumableDownloadResult cached = await downloader.DownloadAsync(
            new Uri("https://example.test/asset.zip"),
            target,
            100);

        Assert.True(cached.WasCached);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DownloadAsync_RejectsDeclaredLengthOverLimit()
    {
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            HttpResponseMessage response = StubHttpMessageHandler.Bytes([1, 2, 3]);
            response.Content.Headers.ContentLength = 1_000;
            return response;
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ResumableHttpDownloader(client).DownloadAsync(
            new Uri("https://example.test/asset.zip"),
            Path.Combine(_root, "asset.zip"),
            100));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
