using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AutoEnvPlus.Core.Installation;

public sealed record ResumableDownloadProgress(
    long CompletedBytes,
    long? TotalBytes,
    bool IsResumed);

public sealed record ResumableDownloadResult(
    string FilePath,
    long TotalBytes,
    bool WasResumed,
    bool WasCached);

public sealed class ResumableHttpDownloader
{
    private const int BufferSize = 81_920;
    private const int MaximumMetadataBytes = 32 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;

    public ResumableHttpDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResumableDownloadResult> DownloadAsync(
        Uri sourceUri,
        string completedPath,
        long maximumBytes,
        Action<ResumableDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedPath);
        if (!sourceUri.IsAbsoluteUri
            || sourceUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(sourceUri.UserInfo))
        {
            throw new ArgumentException(
                "Runtime downloads require an absolute HTTPS URI without embedded credentials.",
                nameof(sourceUri));
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        string finalPath = Path.GetFullPath(completedPath);
        string cacheDirectory = Path.GetDirectoryName(finalPath)!;
        EnsureNoReparsePointInPath(cacheDirectory);
        DirectoryInfo createdDirectory = Directory.CreateDirectory(cacheDirectory);
        EnsureRegularDirectory(createdDirectory.FullName, "runtime download cache directory");
        EnsureSafeCacheFilePath(finalPath, "completed runtime package");
        SemaphoreSlim pathLock = PathLocks.GetOrAdd(finalPath, _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath))
            {
                EnsureSafeCacheFilePath(finalPath, "completed runtime package");
                long cachedLength = new FileInfo(finalPath).Length;
                if (cachedLength > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"The cached download exceeds the {maximumBytes}-byte limit.");
                }

                progress?.Invoke(new ResumableDownloadProgress(cachedLength, cachedLength, false));
                return new ResumableDownloadResult(finalPath, cachedLength, false, true);
            }

            return await DownloadCoreAsync(
                sourceUri,
                finalPath,
                maximumBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pathLock.Release();
        }
    }

    private async Task<ResumableDownloadResult> DownloadCoreAsync(
        Uri sourceUri,
        string finalPath,
        long maximumBytes,
        Action<ResumableDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string partialPath = finalPath + ".partial";
        string metadataPath = partialPath + ".json";
        EnsureSafeCacheFilePath(finalPath, "completed runtime package");
        EnsureSafeCacheFilePath(partialPath, "partial runtime package");
        EnsureSafeCacheFilePath(metadataPath, "partial runtime metadata");
        DownloadMetadata? metadata = await ReadMetadataAsync(
            metadataPath,
            cancellationToken).ConfigureAwait(false);
        string safeSourceEndpoint = GetSafeSourceEndpoint(sourceUri);
        if (metadata is null
            || !string.Equals(
                metadata.SourceEndpoint,
                safeSourceEndpoint,
                StringComparison.Ordinal)
            || !File.Exists(partialPath))
        {
            ResetPartial(partialPath, metadataPath);
            metadata = null;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeCacheFilePath(partialPath, "partial runtime package");
            long existingLength = File.Exists(partialPath)
                ? new FileInfo(partialPath).Length
                : 0;
            if (existingLength > maximumBytes)
            {
                ResetPartial(partialPath, metadataPath);
                throw new InvalidDataException(
                    $"The partial download exceeds the {maximumBytes}-byte limit.");
            }

            using HttpRequestMessage request = new(HttpMethod.Get, sourceUri);
            bool requestedResume = existingLength > 0 && metadata is not null;
            if (requestedResume)
            {
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
                if (metadata!.EntityTag is string entityTag
                    && EntityTagHeaderValue.TryParse(entityTag, out EntityTagHeaderValue? parsedTag))
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(parsedTag);
                }
                else if (metadata.LastModifiedUtc is DateTimeOffset modified)
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(modified);
                }
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                && requestedResume)
            {
                ResetPartial(partialPath, metadataPath);
                metadata = null;
                continue;
            }

            response.EnsureSuccessStatusCode();
            bool acceptedResume = requestedResume
                && response.StatusCode == HttpStatusCode.PartialContent;
            if (acceptedResume
                && response.Content.Headers.ContentRange?.From != existingLength)
            {
                ResetPartial(partialPath, metadataPath);
                metadata = null;
                continue;
            }

            if (!acceptedResume)
            {
                existingLength = 0;
            }

            long? totalLength = ResolveTotalLength(response, existingLength);
            if (totalLength > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The download is {totalLength} bytes, exceeding the {maximumBytes}-byte limit.");
            }

            DownloadMetadata updatedMetadata = new(
                safeSourceEndpoint,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                totalLength);
            await WriteMetadataAsync(
                metadataPath,
                updatedMetadata,
                cancellationToken).ConfigureAwait(false);

            FileMode mode = acceptedResume ? FileMode.Append : FileMode.Create;
            EnsureSafeCacheFilePath(partialPath, "partial runtime package");
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream target = new(
                partialPath,
                mode,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[BufferSize];
            long completed = existingLength;
            progress?.Invoke(new ResumableDownloadProgress(completed, totalLength, acceptedResume));
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                completed = checked(completed + read);
                if (completed > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"The download exceeded the {maximumBytes}-byte limit.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Invoke(new ResumableDownloadProgress(completed, totalLength, acceptedResume));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (totalLength is long expectedLength && completed != expectedLength)
            {
                throw new IOException(
                    $"The HTTP response ended after {completed} of {expectedLength} bytes. The partial file was retained for resume.");
            }

            target.Close();
            EnsureSafeCacheFilePath(finalPath, "completed runtime package");
            EnsureSafeCacheFilePath(partialPath, "partial runtime package");
            File.Move(partialPath, finalPath, overwrite: true);
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }

            return new ResumableDownloadResult(
                finalPath,
                completed,
                acceptedResume,
                false);
        }

        throw new InvalidDataException(
            "The server returned an invalid range response twice; the partial download was reset.");
    }

    private static long? ResolveTotalLength(HttpResponseMessage response, long existingLength)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.Length is long rangeLength)
        {
            return rangeLength;
        }

        return response.Content.Headers.ContentLength is long contentLength
            ? checked(existingLength + contentLength)
            : null;
    }

    private static async Task<DownloadMetadata?> ReadMetadataAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            EnsureSafeCacheFilePath(metadataPath, "partial runtime metadata");
            if (new FileInfo(metadataPath).Length is <= 0 or > MaximumMetadataBytes)
            {
                return null;
            }

            await using FileStream stream = new(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8_192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<DownloadMetadata>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(
        string metadataPath,
        DownloadMetadata metadata,
        CancellationToken cancellationToken)
    {
        EnsureSafeCacheFilePath(metadataPath, "partial runtime metadata");
        string temporary = metadataPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8_192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    metadata,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureSafeCacheFilePath(metadataPath, "partial runtime metadata");
            File.Move(temporary, metadataPath, overwrite: true);
            EnsureSafeCacheFilePath(metadataPath, "partial runtime metadata");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ResetPartial(string partialPath, string metadataPath)
    {
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }
    }

    private static string GetSafeSourceEndpoint(Uri sourceUri) => sourceUri.GetComponents(
        UriComponents.SchemeAndServer | UriComponents.Path,
        UriFormat.UriEscaped);

    private static void EnsureSafeCacheFilePath(string path, string description)
    {
        EnsureNoReparsePointInPath(path);
        FileAttributes? attributes = TryGetAttributes(path);
        if (attributes is FileAttributes value
            && (value & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be a regular file and cannot be a reparse point.");
        }
    }

    private static void EnsureRegularDirectory(string path, string description)
    {
        EnsureNoReparsePointInPath(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be a regular directory and cannot be a reparse point.");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            FileAttributes? attributes = TryGetAttributes(current.FullName);
            if (attributes is FileAttributes value
                && (value & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "A runtime download cache path cannot traverse a reparse point.");
            }

            current = current.Parent;
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private sealed record DownloadMetadata(
        string SourceEndpoint,
        string? EntityTag,
        DateTimeOffset? LastModifiedUtc,
        long? TotalLength);
}
