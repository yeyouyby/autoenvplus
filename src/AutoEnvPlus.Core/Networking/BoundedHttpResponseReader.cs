using System.Text.Json;

namespace AutoEnvPlus.Core.Networking;

public static class BoundedHttpResponseReader
{
    internal static readonly TimeSpan DefaultBodyTimeout = TimeSpan.FromSeconds(30);

    public static Task<byte[]> GetBytesAsync(
        HttpClient httpClient,
        Uri uri,
        int maximumBytes,
        string description,
        CancellationToken cancellationToken = default) =>
        GetBytesAsync(
            httpClient,
            uri,
            maximumBytes,
            DefaultBodyTimeout,
            description,
            cancellationToken);

    internal static async Task<byte[]> GetBytesAsync(
        HttpClient httpClient,
        Uri uri,
        int maximumBytes,
        TimeSpan bodyTimeout,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (bodyTimeout <= TimeSpan.Zero || bodyTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyTimeout));
        }

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBytesAsync(
            response.Content,
            maximumBytes,
            bodyTimeout,
            description,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonDocument> GetJsonAsync(
        HttpClient httpClient,
        Uri uri,
        int maximumBytes,
        int maximumDepth,
        string description,
        CancellationToken cancellationToken = default)
    {
        if (maximumDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        byte[] content = await GetBytesAsync(
            httpClient,
            uri,
            maximumBytes,
            description,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        JsonDocument document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth,
            });
        if (cancellationToken.IsCancellationRequested)
        {
            document.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return document;
    }

    internal static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        int maximumBytes,
        TimeSpan bodyTimeout,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (bodyTimeout <= TimeSpan.Zero || bodyTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyTimeout));
        }

        if (content.Headers.ContentLength is long declaredLength && declaredLength > maximumBytes)
        {
            throw TooLarge(description, maximumBytes);
        }

        using CancellationTokenSource bodyTimeoutCancellation = new(bodyTimeout);
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                bodyTimeoutCancellation.Token);
        try
        {
            await using Stream source = await content.ReadAsStreamAsync(
                linkedCancellation.Token).ConfigureAwait(false);
            int initialCapacity = content.Headers.ContentLength is long contentLength
                ? checked((int)Math.Min(contentLength, maximumBytes))
                : 0;
            using MemoryStream target = new(initialCapacity);
            byte[] buffer = new byte[81_920];
            while (true)
            {
                int readLength = (int)Math.Min(
                    buffer.Length,
                    maximumBytes - target.Length + 1);
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    linkedCancellation.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return target.ToArray();
                }

                if (target.Length > maximumBytes - read)
                {
                    throw TooLarge(description, maximumBytes);
                }

                target.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && bodyTimeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The {description} response body did not complete within {bodyTimeout}.",
                exception);
        }
    }

    private static InvalidDataException TooLarge(string description, int maximumBytes) =>
        new($"The {description} response exceeds the {maximumBytes}-byte limit.");
}
