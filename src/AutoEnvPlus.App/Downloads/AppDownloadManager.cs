using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.App.Downloads;

internal enum AppTransferKind
{
    NetworkDownload,
    LocalImport,
}

internal sealed record AppTransferSnapshot(
    Guid Id,
    AppTransferKind Kind,
    string FileName,
    string SourceDisplay,
    ManagedTransferPhase Phase,
    long CompletedBytes,
    long? TotalBytes,
    int CompletedSegments,
    int TotalSegments,
    bool IsSegmented,
    bool IsBusy,
    string? OutputPath,
    DownloadTransferMode? TransferMode,
    DownloadFallbackReason? FallbackReason,
    string? ContentSha256,
    string? VerifiedHash,
    string? Error);

internal sealed class AppDownloadManager
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private AppTransferSnapshot? _snapshot;

    public AppDownloadManager(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        LibraryRoot = Path.Combine(Path.GetFullPath(managedRoot), "downloads", "library");
        Library = new ManagedDownloadLibrary(LibraryRoot);
    }

    public event EventHandler? StateChanged;

    public string LibraryRoot { get; }

    public ManagedDownloadLibrary Library { get; }

    public AppTransferSnapshot? Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public async Task<SegmentedDownloadResult> DownloadAsync(
        SegmentedDownloadRequest request,
        EffectiveNetworkSettings networkSettings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(networkSettings);
        Guid id = Begin(
            AppTransferKind.NetworkDownload,
            request.FileName,
            SanitizeSource(request.SourceUri));
        try
        {
            using HttpClient client = NetworkHttpClientFactory.Create(
                networkSettings,
                Timeout.InfiniteTimeSpan);
            ManagedSegmentedDownloader downloader = new(client, LibraryRoot);
            SegmentedDownloadResult result = await downloader.DownloadAsync(
                request,
                CreateProgress(id),
                GetCancellationToken(id));
            Complete(
                id,
                result.FilePath,
                result.TransferMode,
                result.FallbackReason,
                result.ContentSha256,
                result.VerifiedHash,
                result.TotalBytes,
                result.SegmentCount);
            return result;
        }
        catch (OperationCanceledException)
        {
            Cancelled(id);
            throw;
        }
        catch (HttpRequestException exception)
        {
            Fail(id, DescribeNetworkFailure(exception));
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            Fail(id, exception.Message);
            throw;
        }
        finally
        {
            End(id);
        }
    }

    public async Task<LocalPackageImportResult> ImportAsync(LocalPackageImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileName(request.SourcePath)
            : request.FileName;
        Guid id = Begin(
            AppTransferKind.LocalImport,
            fileName,
            Path.GetFileName(request.SourcePath));
        try
        {
            LocalPackageImportResult result = await new LocalPackageImportService(LibraryRoot)
                .ImportAsync(request, CreateProgress(id), GetCancellationToken(id));
            Complete(
                id,
                result.FilePath,
                null,
                null,
                result.ContentSha256,
                result.VerifiedHash,
                result.TotalBytes,
                1);
            return result;
        }
        catch (OperationCanceledException)
        {
            Cancelled(id);
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            Fail(id, exception.Message);
            throw;
        }
        finally
        {
            End(id);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _activeCancellation?.Cancel();
        }
    }

    private Guid Begin(AppTransferKind kind, string fileName, string sourceDisplay)
    {
        Guid id = Guid.NewGuid();
        lock (_sync)
        {
            if (_activeCancellation is not null)
            {
                throw new InvalidOperationException("Another managed transfer is already active.");
            }

            _activeCancellation = new CancellationTokenSource();
            _snapshot = new AppTransferSnapshot(
                id,
                kind,
                fileName,
                sourceDisplay,
                ManagedTransferPhase.Probing,
                0,
                null,
                0,
                0,
                false,
                true,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        RaiseStateChanged();
        return id;
    }

    private CancellationToken GetCancellationToken(Guid id)
    {
        lock (_sync)
        {
            if (_snapshot?.Id != id || _activeCancellation is null)
            {
                throw new InvalidOperationException("The managed transfer is no longer active.");
            }

            return _activeCancellation.Token;
        }
    }

    private IProgress<ManagedTransferProgress> CreateProgress(Guid id) =>
        new Progress<ManagedTransferProgress>(progress =>
        {
            lock (_sync)
            {
                if (_snapshot?.Id != id
                    || _snapshot.Phase is ManagedTransferPhase.Completed
                        or ManagedTransferPhase.Cancelled
                    || _snapshot.Error is not null)
                {
                    return;
                }

                _snapshot = _snapshot with
                {
                    Phase = progress.Phase,
                    CompletedBytes = progress.CompletedBytes,
                    TotalBytes = progress.TotalBytes,
                    CompletedSegments = progress.CompletedSegments,
                    TotalSegments = progress.TotalSegments,
                    IsSegmented = progress.IsSegmented,
                };
            }

            RaiseStateChanged();
        });

    private void Complete(
        Guid id,
        string outputPath,
        DownloadTransferMode? transferMode,
        DownloadFallbackReason? fallbackReason,
        string contentSha256,
        string? verifiedHash,
        long totalBytes,
        int segmentCount)
    {
        lock (_sync)
        {
            if (_snapshot?.Id != id)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Phase = ManagedTransferPhase.Completed,
                CompletedBytes = totalBytes,
                TotalBytes = totalBytes,
                CompletedSegments = segmentCount,
                TotalSegments = segmentCount,
                IsSegmented = transferMode == DownloadTransferMode.Segmented,
                OutputPath = outputPath,
                TransferMode = transferMode,
                FallbackReason = fallbackReason,
                ContentSha256 = contentSha256,
                VerifiedHash = verifiedHash,
                Error = null,
            };
        }

        RaiseStateChanged();
    }

    private void Cancelled(Guid id)
    {
        lock (_sync)
        {
            if (_snapshot?.Id != id)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Phase = ManagedTransferPhase.Cancelled,
                Error = null,
            };
        }

        RaiseStateChanged();
    }

    private void Fail(Guid id, string error)
    {
        lock (_sync)
        {
            if (_snapshot?.Id != id)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Error = error,
            };
        }

        RaiseStateChanged();
    }

    private void End(Guid id)
    {
        lock (_sync)
        {
            if (_snapshot?.Id != id)
            {
                return;
            }

            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _snapshot = _snapshot with { IsBusy = false };
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string SanitizeSource(Uri source) => source.GetComponents(
        UriComponents.SchemeAndServer | UriComponents.Path,
        UriFormat.SafeUnescaped);

    internal static string DescribeNetworkFailure(HttpRequestException exception) =>
        exception.StatusCode is System.Net.HttpStatusCode statusCode
            ? $"下载端点返回 HTTP {(int)statusCode}。请求地址中的查询参数未显示。"
            : "无法建立受管下载连接。请求地址中的查询参数未显示。";
}
