namespace AutoEnvPlus.Core.Downloads;

public sealed class LocalPackageImportService
{
    private const int BufferSize = 81_920;
    private readonly ManagedDownloadLibrary _library;

    public LocalPackageImportService(string libraryRoot)
    {
        _library = new ManagedDownloadLibrary(libraryRoot);
    }

    public SegmentedDownloadCancellationBehavior CancellationBehavior =>
        SegmentedDownloadCancellationBehavior.DeleteStaging;

    public async Task<LocalPackageImportResult> ImportAsync(
        LocalPackageImportRequest request,
        IProgress<ManagedTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        string sourcePath = Path.GetFullPath(request.SourcePath);
        FileInfo source = ValidateSource(sourcePath, request.MaximumBytes);
        string fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? source.Name
            : request.FileName;
        string targetPath = _library.ResolveTargetPath(fileName);
        if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The import source is already the destination file.",
                nameof(request));
        }

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
            string stagedPath = Path.Combine(stagingDirectory, "import.partial");
            long expectedLength = source.Length;
            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Copying,
                0,
                expectedLength,
                0,
                1,
                false));

            await using (FileStream input = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[BufferSize];
                long completed = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    completed = checked(completed + read);
                    if (completed > request.MaximumBytes)
                    {
                        throw new InvalidDataException(
                            $"The imported file exceeded the {request.MaximumBytes}-byte limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new ManagedTransferProgress(
                        ManagedTransferPhase.Copying,
                        completed,
                        expectedLength,
                        0,
                        1,
                        false));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (completed != expectedLength || input.Length != expectedLength)
                {
                    throw new IOException(
                        "The source file length changed while it was being imported.");
                }
            }

            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Verifying,
                expectedLength,
                expectedLength,
                1,
                1,
                false));
            TransferIntegrityEvidence integrityEvidence =
                await PackageHashExpectationValidator.IdentifyAndVerifyAsync(
                stagedPath,
                request.Integrity,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Committing,
                expectedLength,
                expectedLength,
                1,
                1,
                false));
            cancellationToken.ThrowIfCancellationRequested();
            await _library.ValidateManifestForCommitAsync(cancellationToken).ConfigureAwait(false);
            _library.EnsureTargetCanBeWritten(targetPath, request.Overwrite);
            await _library.CommitImportAsync(
                stagedPath,
                targetPath,
                stagingDirectory,
                sourcePath,
                integrityEvidence,
                request.Overwrite,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new ManagedTransferProgress(
                ManagedTransferPhase.Completed,
                expectedLength,
                expectedLength,
                1,
                1,
                false));
            return new LocalPackageImportResult(
                targetPath,
                expectedLength,
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

    private static FileInfo ValidateSource(string sourcePath, long maximumBytes)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(sourcePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw new FileNotFoundException("The local package to import was not found.", sourcePath);
        }

        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The local import source must be a regular file and cannot be a reparse point.");
        }

        FileInfo source = new(sourcePath);
        if (source.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The local file is {source.Length} bytes, exceeding the {maximumBytes}-byte limit.");
        }

        return source;
    }

    private static void ValidateRequest(LocalPackageImportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        if (request.MaximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum import size must be positive.");
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
}
