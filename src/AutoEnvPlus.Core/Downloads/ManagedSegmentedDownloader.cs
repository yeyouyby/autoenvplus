using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;

namespace AutoEnvPlus.Core.Downloads;

public sealed class ManagedSegmentedDownloader
{
    private const int BufferSize = 81_920;
    private static readonly int[] ConnectionCounts = [1, 2, 4, 8, 16];
    private readonly HttpClient _httpClient;
    private readonly ManagedDownloadLibrary _library;

    public ManagedSegmentedDownloader(HttpClient httpClient, string libraryRoot)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _library = new ManagedDownloadLibrary(libraryRoot);
    }

    public static IReadOnlyList<int> SupportedConnectionCounts { get; } =
        Array.AsReadOnly(ConnectionCounts);

    public SegmentedDownloadCancellationBehavior CancellationBehavior =>
        SegmentedDownloadCancellationBehavior.DeleteStaging;

    public async Task<SegmentedDownloadResult> DownloadAsync(
        SegmentedDownloadRequest request,
        IProgress<ManagedTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        string targetPath = _library.ResolveTargetPath(request.FileName);
        SemaphoreSlim targetLock = ManagedDownloadTargetLocks.Get(targetPath);
        string? stagingDirectory = null;
        bool preserveStagingEvidence = false;

        try
        {
            await targetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReportCancelled(progress);
            throw;
        }

        try
        {
            _library.EnsureTargetCanBeWritten(targetPath, request.Overwrite);
            stagingDirectory = _library.CreateOperationStagingDirectory();
            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Probing,
                0,
                null,
                0,
                0,
                false));

            RemoteProbe probe = await ProbeAsync(
                request.SourceUri,
                request.ConnectionCount > 1,
                request.MaximumBytes,
                cancellationToken).ConfigureAwait(false);

            bool useSegments = request.ConnectionCount > 1
                && probe.SupportsRanges
                && probe.TotalLength is > 1
                && probe.Identity is not null;
            DownloadFallbackReason? fallbackReason = request.ConnectionCount > 1
                ? probe.FallbackReason
                : null;
            string stagedFile;
            int segmentCount;
            if (useSegments)
            {
                try
                {
                    (stagedFile, segmentCount) = await DownloadSegmentsAsync(
                        request,
                        probe,
                        stagingDirectory,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (RemoteEntityChangedException)
                {
                    DeleteAbandonedSegments(stagingDirectory);
                    useSegments = false;
                    fallbackReason = DownloadFallbackReason.EntityChanged;
                    stagedFile = await DownloadSingleStreamAsync(
                        request,
                        null,
                        stagingDirectory,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    segmentCount = 1;
                }
            }
            else
            {
                stagedFile = await DownloadSingleStreamAsync(
                    request,
                    probe.TotalLength,
                    stagingDirectory,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                segmentCount = 1;
            }

            long finalLength = new FileInfo(stagedFile).Length;
            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Verifying,
                finalLength,
                finalLength,
                segmentCount,
                segmentCount,
                useSegments));
            TransferIntegrityEvidence integrityEvidence =
                await PackageHashExpectationValidator.IdentifyAndVerifyAsync(
                stagedFile,
                request.Integrity,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Committing,
                finalLength,
                finalLength,
                segmentCount,
                segmentCount,
                useSegments));
            cancellationToken.ThrowIfCancellationRequested();
            await _library.ValidateManifestForCommitAsync(cancellationToken).ConfigureAwait(false);
            _library.EnsureTargetCanBeWritten(targetPath, request.Overwrite);
            await _library.CommitDownloadAsync(
                stagedFile,
                targetPath,
                stagingDirectory,
                request.SourceUri,
                useSegments ? DownloadTransferMode.Segmented : DownloadTransferMode.SingleStream,
                integrityEvidence,
                request.Overwrite,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Completed,
                finalLength,
                finalLength,
                segmentCount,
                segmentCount,
                useSegments));

            return new SegmentedDownloadResult(
                targetPath,
                finalLength,
                useSegments ? DownloadTransferMode.Segmented : DownloadTransferMode.SingleStream,
                segmentCount,
                fallbackReason,
                integrityEvidence.ContentSha256,
                integrityEvidence.ExpectedHashAlgorithm,
                integrityEvidence.ExpectedHash,
                integrityEvidence.VerifiedHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReportCancelled(progress);
            throw;
        }
        catch (ManagedDownloadCommitException exception)
        {
            preserveStagingEvidence = exception.PreserveStagingEvidence;
            throw;
        }
        finally
        {
            if (!preserveStagingEvidence)
            {
                ManagedDownloadLibrary.TryDeleteStagingDirectory(stagingDirectory);
            }

            targetLock.Release();
        }
    }

    private async Task<RemoteProbe> ProbeAsync(
        Uri sourceUri,
        bool testRanges,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        HeadSnapshot? head = await ProbeHeadAsync(
            sourceUri,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        if (!testRanges
            || head?.TotalLength == 0
            || head?.ExplicitlyRejectsRanges == true)
        {
            return new RemoteProbe(
                false,
                head?.TotalLength,
                null,
                testRanges ? DownloadFallbackReason.RangeNotSupported : null);
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, sourceUri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        RemoteEntityIdentity? headIdentity = CreateIdentity(
            head?.StrongEntityTag,
            head?.LastModifiedUtc,
            head?.TotalLength);
        AddIfRange(request, headIdentity);

        using HttpResponseMessage response = await SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ValidateMaximum(response.Content.Headers.ContentLength, maximumBytes);
            DownloadFallbackReason reason = headIdentity is not null
                && !EntityIdentityMatches(response, headIdentity)
                    ? DownloadFallbackReason.EntityChanged
                    : DownloadFallbackReason.RangeNotSupported;
            return new RemoteProbe(
                false,
                response.Content.Headers.ContentLength ?? head?.TotalLength,
                null,
                reason);
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException(
                $"The range probe returned unexpected HTTP status {(int)response.StatusCode}.");
        }

        ContentRangeHeaderValue range = response.Content.Headers.ContentRange
            ?? throw new InvalidDataException("The range probe omitted Content-Range.");
        if (!range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || range.From != 0
            || range.To != 0
            || range.Length is not long totalLength)
        {
            throw new InvalidDataException(
                "The range probe returned an invalid Content-Range value.");
        }

        if (response.Content.Headers.ContentLength is long probeLength && probeLength != 1)
        {
            throw new InvalidDataException(
                "The range probe returned an invalid response length.");
        }

        ValidateMaximum(totalLength, maximumBytes);
        if (head?.TotalLength is long headLength && headLength != totalLength)
        {
            throw new InvalidDataException(
                "The remote file length changed during the range probe.");
        }

        RemoteEntityIdentity? identity = headIdentity;
        if (identity is not null)
        {
            if (!EntityIdentityMatches(response, identity))
            {
                await ValidateExactBodyLengthAsync(response, 1, cancellationToken).ConfigureAwait(false);
                return new RemoteProbe(
                    false,
                    totalLength,
                    null,
                    DownloadFallbackReason.EntityChanged);
            }
        }
        else
        {
            identity = CreateIdentity(
                GetStrongEntityTag(response),
                response.Content.Headers.LastModified,
                totalLength);
        }

        await ValidateExactBodyLengthAsync(response, 1, cancellationToken).ConfigureAwait(false);
        return new RemoteProbe(
            identity is not null,
            totalLength,
            identity,
            identity is null ? DownloadFallbackReason.StableEntityUnavailable : null);
    }

    private async Task<HeadSnapshot?> ProbeHeadAsync(
        Uri sourceUri,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Head, sourceUri);
        using HttpResponseMessage response = await SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        long? totalLength = response.Content.Headers.ContentLength;
        ValidateMaximum(totalLength, maximumBytes);
        bool rejectsRanges = response.Headers.AcceptRanges.Any(value =>
            value.Equals("none", StringComparison.OrdinalIgnoreCase));
        return new HeadSnapshot(
            totalLength,
            GetStrongEntityTag(response),
            response.Content.Headers.LastModified,
            rejectsRanges);
    }

    private async Task<(string FilePath, int SegmentCount)> DownloadSegmentsAsync(
        SegmentedDownloadRequest request,
        RemoteProbe probe,
        string stagingDirectory,
        IProgress<ManagedTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        long totalLength = probe.TotalLength
            ?? throw new InvalidDataException("Segmented transfers require a known content length.");
        RemoteEntityIdentity identity = probe.Identity
            ?? throw new InvalidDataException("Segmented transfers require a stable entity identity.");
        int segmentCount = (int)Math.Min(request.ConnectionCount, totalLength);
        IReadOnlyList<ByteRange> ranges = CreateRanges(totalLength, segmentCount);
        long completedBytes = 0;
        int completedSegments = 0;
        progress?.Report(new ManagedTransferProgress(
            ManagedTransferPhase.Downloading,
            0,
            totalLength,
            0,
            segmentCount,
            true));

        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] tasks = ranges.Select(range => DownloadOneSegmentWithCancellationAsync(range)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            Exception? transferFailure = tasks
                .Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                .FirstOrDefault(exception => exception is not OperationCanceledException
                    and not RemoteEntityChangedException)
                ?? tasks
                    .Where(task => task.IsFaulted)
                    .SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                    .FirstOrDefault(exception => exception is RemoteEntityChangedException);
            if (transferFailure is not null)
            {
                ExceptionDispatchInfo.Capture(transferFailure).Throw();
            }

            throw;
        }

        string assembledPath = Path.Combine(stagingDirectory, "assembled.partial");
        await using (FileStream assembled = new(
            assembledPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            foreach (ByteRange range in ranges)
            {
                string segmentPath = GetSegmentPath(stagingDirectory, range.Index);
                await using FileStream segment = new(
                    segmentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (segment.Length != range.Length)
                {
                    throw new InvalidDataException(
                        $"Segment {range.Index} has an unexpected length before assembly.");
                }

                await segment.CopyToAsync(assembled, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            await assembled.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (assembled.Length != totalLength)
            {
                throw new InvalidDataException(
                    "The assembled download length does not match the probed entity length.");
            }
        }

        return (assembledPath, segmentCount);

        async Task DownloadOneSegmentWithCancellationAsync(ByteRange range)
        {
            try
            {
                await DownloadOneSegmentAsync(
                    request.SourceUri,
                    identity,
                    range,
                    stagingDirectory,
                    bytesWritten =>
                    {
                        long currentBytes = Interlocked.Add(ref completedBytes, bytesWritten);
                        progress?.Report(new ManagedTransferProgress(
                            ManagedTransferPhase.Downloading,
                            currentBytes,
                            totalLength,
                            Volatile.Read(ref completedSegments),
                            segmentCount,
                            true));
                    },
                    linkedCancellation.Token).ConfigureAwait(false);
                int currentSegments = Interlocked.Increment(ref completedSegments);
                progress?.Report(new ManagedTransferProgress(
                    ManagedTransferPhase.Downloading,
                    Volatile.Read(ref completedBytes),
                    totalLength,
                    currentSegments,
                    segmentCount,
                    true));
            }
            catch
            {
                await linkedCancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task DownloadOneSegmentAsync(
        Uri sourceUri,
        RemoteEntityIdentity identity,
        ByteRange range,
        string stagingDirectory,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, sourceUri);
        request.Headers.Range = new RangeHeaderValue(range.Start, range.End);
        AddIfRange(request, identity);
        using HttpResponseMessage response = await SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                throw new RemoteEntityChangedException(
                    $"Segment {range.Index} returned a full entity instead of its byte range.");
            }

            response.EnsureSuccessStatusCode();
            throw new InvalidDataException(
                $"Segment {range.Index} returned HTTP {(int)response.StatusCode} instead of 206.");
        }

        ContentRangeHeaderValue contentRange = response.Content.Headers.ContentRange
            ?? throw new InvalidDataException($"Segment {range.Index} omitted Content-Range.");
        if (!contentRange.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From != range.Start
            || contentRange.To != range.End
            || contentRange.Length != identity.TotalLength)
        {
            throw new InvalidDataException(
                $"Segment {range.Index} returned a mismatched Content-Range value.");
        }

        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength != range.Length)
        {
            throw new InvalidDataException(
                $"Segment {range.Index} declared an incorrect response length.");
        }

        ValidateEntityIdentity(response, identity);
        string path = GetSegmentPath(stagingDirectory, range.Index);
        await using Stream source = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        await using FileStream target = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        byte[] buffer = new byte[BufferSize];
        long completed = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            completed = checked(completed + read);
            if (completed > range.Length)
            {
                throw new InvalidDataException(
                    $"Segment {range.Index} exceeded its declared byte range.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            reportBytes(read);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (completed != range.Length)
        {
            throw new IOException(
                $"Segment {range.Index} ended after {completed} of {range.Length} bytes.");
        }
    }

    private async Task<string> DownloadSingleStreamAsync(
        SegmentedDownloadRequest request,
        long? probedLength,
        string stagingDirectory,
        IProgress<ManagedTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = CreateRequest(HttpMethod.Get, request.SourceUri);
        using HttpResponseMessage response = await SendAsync(
            message,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException(
                $"The single-stream download returned HTTP {(int)response.StatusCode} instead of 200.");
        }

        long? declaredLength = response.Content.Headers.ContentLength;
        ValidateMaximum(declaredLength, request.MaximumBytes);
        long? progressTotal = declaredLength ?? probedLength;
        string path = Path.Combine(stagingDirectory, "single.partial");
        progress?.Report(new ManagedTransferProgress(
            ManagedTransferPhase.Downloading,
            0,
            progressTotal,
            0,
            1,
            false));

        await using Stream source = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        await using FileStream target = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        byte[] buffer = new byte[BufferSize];
        long completed = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            completed = checked(completed + read);
            if (completed > request.MaximumBytes)
            {
                throw new InvalidDataException(
                    $"The download exceeded the {request.MaximumBytes}-byte limit.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Downloading,
                completed,
                progressTotal,
                0,
                1,
                false));
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (declaredLength is long expectedLength && completed != expectedLength)
        {
            throw new IOException(
                $"The response ended after {completed} of {expectedLength} bytes.");
        }

        progress?.Report(new ManagedTransferProgress(
            ManagedTransferPhase.Downloading,
            completed,
            completed,
            1,
            1,
            false));
        return path;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        Uri finalUri = response.RequestMessage?.RequestUri ?? request.RequestUri!;
        if (!finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            throw new InvalidDataException(
                "The download was redirected to a non-HTTPS URI.");
        }

        return response;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri sourceUri)
    {
        HttpRequestMessage request = new(method, sourceUri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return request;
    }

    private static void AddIfRange(
        HttpRequestMessage request,
        RemoteEntityIdentity? identity)
    {
        if (identity?.StrongEntityTag is string entityTag)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(
                new EntityTagHeaderValue(entityTag));
        }
        else if (identity?.LastModifiedUtc is DateTimeOffset lastModified)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
        }
    }

    private static void ValidateEntityIdentity(
        HttpResponseMessage response,
        RemoteEntityIdentity identity)
    {
        if (!EntityIdentityMatches(response, identity))
        {
            throw new RemoteEntityChangedException(
                "The remote entity identifier changed during the segmented transfer.");
        }
    }

    private static bool EntityIdentityMatches(
        HttpResponseMessage response,
        RemoteEntityIdentity identity)
    {
        if (identity.StrongEntityTag is string expectedEntityTag)
        {
            return expectedEntityTag.Equals(
                GetStrongEntityTag(response),
                StringComparison.Ordinal);
        }

        return identity.LastModifiedUtc is DateTimeOffset expectedLastModified
            && response.Content.Headers.LastModified == expectedLastModified;
    }

    private static string? GetStrongEntityTag(HttpResponseMessage response)
    {
        EntityTagHeaderValue? entityTag = response.Headers.ETag;
        return entityTag is not null && !entityTag.IsWeak
            ? entityTag.Tag
            : null;
    }

    private static RemoteEntityIdentity? CreateIdentity(
        string? strongEntityTag,
        DateTimeOffset? lastModifiedUtc,
        long? totalLength)
    {
        if (totalLength is not long length
            || (strongEntityTag is null && lastModifiedUtc is null))
        {
            return null;
        }

        return new RemoteEntityIdentity(
            strongEntityTag,
            strongEntityTag is null ? lastModifiedUtc : null,
            length);
    }

    private static IReadOnlyList<ByteRange> CreateRanges(long totalLength, int segmentCount)
    {
        List<ByteRange> ranges = new(segmentCount);
        long baseLength = totalLength / segmentCount;
        long remainder = totalLength % segmentCount;
        long start = 0;
        for (int index = 0; index < segmentCount; index++)
        {
            long length = baseLength + (index < remainder ? 1 : 0);
            ranges.Add(new ByteRange(index, start, checked(start + length - 1)));
            start = checked(start + length);
        }

        return ranges;
    }

    private static async Task ValidateExactBodyLengthAsync(
        HttpResponseMessage response,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[128];
        long completed = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            completed = checked(completed + read);
            if (completed > expectedLength)
            {
                break;
            }
        }

        if (completed != expectedLength)
        {
            throw new InvalidDataException(
                "The range probe body did not match its declared byte range.");
        }
    }

    private static string GetSegmentPath(string stagingDirectory, int index) =>
        Path.Combine(stagingDirectory, $"segment-{index:D2}.partial");

    private static void DeleteAbandonedSegments(string stagingDirectory)
    {
        foreach (string path in Directory.EnumerateFiles(
                     stagingDirectory,
                     "segment-*.partial",
                     SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "An abandoned segment unexpectedly became a reparse point.");
            }

            File.Delete(path);
        }
    }

    private static void ValidateMaximum(long? length, long maximumBytes)
    {
        if (length is long declaredLength && declaredLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"The remote file is {declaredLength} bytes, exceeding the {maximumBytes}-byte limit.");
        }
    }

    private static void ValidateRequest(SegmentedDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.SourceUri);
        if (!request.SourceUri.IsAbsoluteUri
            || !request.SourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(request.SourceUri.Host)
            || !string.IsNullOrEmpty(request.SourceUri.UserInfo))
        {
            throw new ArgumentException(
                "Managed downloads require an absolute HTTPS URI without embedded credentials.",
                nameof(request));
        }

        if (!ConnectionCounts.Contains(request.ConnectionCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Connection count must be one of 1, 2, 4, 8, or 16.");
        }

        if (request.MaximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum download size must be positive.");
        }

        PackageHashExpectationValidator.Validate(request.Integrity, nameof(request));
    }

    private static void ReportCancelled(IProgress<ManagedTransferProgress>? progress) =>
        progress?.Report(new ManagedTransferProgress(
            ManagedTransferPhase.Cancelled,
            0,
            null,
            0,
            0,
            false));

    private sealed record HeadSnapshot(
        long? TotalLength,
        string? StrongEntityTag,
        DateTimeOffset? LastModifiedUtc,
        bool ExplicitlyRejectsRanges);

    private sealed record RemoteProbe(
        bool SupportsRanges,
        long? TotalLength,
        RemoteEntityIdentity? Identity,
        DownloadFallbackReason? FallbackReason);

    private sealed record RemoteEntityIdentity(
        string? StrongEntityTag,
        DateTimeOffset? LastModifiedUtc,
        long TotalLength);

    private sealed record ByteRange(int Index, long Start, long End)
    {
        public long Length => checked(End - Start + 1);
    }

    private sealed class RemoteEntityChangedException(string message) : IOException(message);
}
