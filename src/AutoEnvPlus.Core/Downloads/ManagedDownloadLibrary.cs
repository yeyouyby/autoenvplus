using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Downloads;

public sealed record ManagedDownloadLibraryItem(
    string FileName,
    string FilePath,
    string Extension,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    ManagedDownloadOrigin? Origin = null,
    string? Source = null,
    DownloadTransferMode? TransferMode = null,
    string? ContentSha256 = null,
    PackageHashAlgorithm? ExpectedHashAlgorithm = null,
    string? ExpectedHash = null,
    string? VerifiedHash = null,
    DateTimeOffset? AddedUtc = null,
    bool ContentIdentityRevalidated = false,
    bool ContentIdentityChanged = false)
{
    [JsonIgnore]
    public PackageHashAlgorithm? HashAlgorithm => ExpectedHashAlgorithm;

    public bool HasRecordedExpectedHashEvidence =>
        ExpectedHashAlgorithm is not null
        && ExpectedHash is not null
        && VerifiedHash is not null;

    public bool HasVerifiedExpectedHash =>
        HasRecordedExpectedHashEvidence
        && ContentIdentityRevalidated
        && !ContentIdentityChanged;
}

public sealed record ManagedDownloadRecordedIdentity(
    string FileName,
    long SizeBytes,
    string ContentSha256,
    PackageHashAlgorithm? ExpectedHashAlgorithm,
    string? ExpectedHash,
    string? VerifiedHash)
{
    public bool HasVerifiedExpectedHashAtCommit =>
        ExpectedHashAlgorithm is not null
        && ExpectedHash is not null
        && VerifiedHash is not null;
}

public sealed class ManagedDownloadLibrary
{
    public const long MaximumManifestBytes = 8 * 1024 * 1024;
    public const int MaximumManifestEntries = 8_192;

    private static readonly char[] DirectorySeparators =
    [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
    ];

    private static readonly string[] SupportedExtensions =
    [
        ".7z",
        ".bz2",
        ".conda",
        ".exe",
        ".gz",
        ".jar",
        ".msi",
        ".msix",
        ".nupkg",
        ".tar",
        ".tgz",
        ".whl",
        ".xz",
        ".zip",
    ];

    private static readonly string[] ReservedWindowsNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ManifestLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TransactionLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _rootWithSeparator;

    public ManagedDownloadLibrary(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        LibraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        if (string.IsNullOrWhiteSpace(LibraryRoot))
        {
            throw new ArgumentException("The managed download library root is invalid.", nameof(libraryRoot));
        }

        _rootWithSeparator = Path.EndsInDirectorySeparator(LibraryRoot)
            ? LibraryRoot
            : LibraryRoot + Path.DirectorySeparatorChar;
        StagingRoot = Path.Combine(LibraryRoot, ".autoenvplus-staging");
        ManifestPath = Path.Combine(LibraryRoot, ".autoenvplus-library.json");
        TransactionLockPath = Path.Combine(LibraryRoot, ".autoenvplus-library.lock");
        EnsureOperationalDirectories();
    }

    public string LibraryRoot { get; }

    public string StagingRoot { get; }

    public string ManifestPath { get; }

    internal string TransactionLockPath { get; }

    public static IReadOnlyList<string> AllowedExtensions { get; } =
        Array.AsReadOnly(SupportedExtensions);

    public IReadOnlyList<ManagedDownloadLibraryItem> ListFiles()
    {
        EnsureOperationalDirectories();
        IReadOnlyDictionary<string, ManifestEntry> manifestEntries = ReadManifest()
            .Entries
            .ToDictionary(entry => entry.FileName, StringComparer.OrdinalIgnoreCase);
        List<ManagedDownloadLibraryItem> items = [];
        foreach (string path in Directory.EnumerateFiles(
                     LibraryRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            string extension = Path.GetExtension(path);
            if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                FileInfo file = new(path);
                FileAttributes attributes = file.Attributes;
                if ((attributes & (FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0)
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(file.FullName);
                if (!IsDirectChild(fullPath))
                {
                    continue;
                }

                manifestEntries.TryGetValue(file.Name, out ManifestEntry? manifestEntry);
                if (manifestEntry?.SizeBytes != file.Length)
                {
                    manifestEntry = null;
                }

                items.Add(new ManagedDownloadLibraryItem(
                    file.Name,
                    fullPath,
                    extension.ToLowerInvariant(),
                    file.Length,
                    file.LastWriteTimeUtc,
                    manifestEntry?.Origin,
                    manifestEntry?.Source,
                    manifestEntry?.TransferMode,
                    manifestEntry?.ContentSha256,
                    manifestEntry?.ExpectedHashAlgorithm,
                    manifestEntry?.ExpectedHash,
                    manifestEntry?.VerifiedHash,
                    manifestEntry?.AddedUtc));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                // A file can disappear or become inaccessible while the library is being listed.
            }
        }

        return items
            .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<ManagedDownloadLibraryItem>> ListFilesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ManagedDownloadLibraryItem> items = ListFiles();
        List<ManagedDownloadLibraryItem> revalidated = new(items.Count);
        foreach (ManagedDownloadLibraryItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ContentSha256 is null || !item.HasRecordedExpectedHashEvidence)
            {
                revalidated.Add(item);
                continue;
            }

            try
            {
                string actualSha256 = await PackageHashAlgorithm.Sha256.ComputeFileHashAsync(
                    item.FilePath,
                    cancellationToken).ConfigureAwait(false);
                revalidated.Add(item with
                {
                    ContentIdentityRevalidated = true,
                    ContentIdentityChanged = !actualSha256.Equals(
                        item.ContentSha256,
                        StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                revalidated.Add(item with
                {
                    ContentIdentityRevalidated = true,
                    ContentIdentityChanged = true,
                });
            }
        }

        return revalidated;
    }

    public ManagedDownloadRecordedIdentity? GetRecordedIdentity(string fileName)
    {
        string targetPath = ResolveTargetPath(fileName);
        ManifestEntry? entry = ReadManifest().Entries.FirstOrDefault(candidate =>
            candidate.FileName.Equals(
                Path.GetFileName(targetPath),
                StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? null
            : new ManagedDownloadRecordedIdentity(
                entry.FileName,
                entry.SizeBytes,
                entry.ContentSha256,
                entry.ExpectedHashAlgorithm,
                entry.ExpectedHash,
                entry.VerifiedHash);
    }

    public async Task<ManagedDownloadDeleteResult> DeleteAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        string targetPath = ResolveTargetPath(fileName);
        SemaphoreSlim targetLock = ManagedDownloadTargetLocks.Get(targetPath);
        await targetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        bool preserveStagingEvidence = false;
        try
        {
            EnsureOperationalDirectories();
            await using LibraryTransactionLease transaction =
                await AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
            FileAttributes? attributes = TryGetAttributes(targetPath);
            if (attributes is null)
            {
                bool staleEntryRemoved = await RemoveManifestEntryAsync(
                    fileName,
                    cancellationToken).ConfigureAwait(false);
                return new ManagedDownloadDeleteResult(
                    Path.GetFileName(targetPath),
                    false,
                    staleEntryRemoved);
            }

            if ((attributes.Value & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "Only regular non-reparse files can be deleted from the download library.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            stagingDirectory = CreateOperationStagingDirectory();
            string quarantinedPath = Path.Combine(stagingDirectory, "deleted-file.quarantine");
            File.Move(targetPath, quarantinedPath, overwrite: false);
            bool manifestUpdated;
            try
            {
                manifestUpdated = await RemoveManifestEntryAsync(
                    fileName,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception manifestException)
            {
                try
                {
                    File.Move(quarantinedPath, targetPath, overwrite: false);
                }
                catch (Exception restoreException) when (restoreException is IOException
                    or UnauthorizedAccessException)
                {
                    preserveStagingEvidence = true;
                    throw new AggregateException(
                        "The library manifest update failed and the quarantined file could not be "
                        + $"restored. Recovery evidence was retained at '{quarantinedPath}'.",
                        manifestException,
                        restoreException);
                }

                throw;
            }

            File.Delete(quarantinedPath);
            return new ManagedDownloadDeleteResult(
                Path.GetFileName(targetPath),
                true,
                manifestUpdated);
        }
        finally
        {
            if (!preserveStagingEvidence)
            {
                TryDeleteStagingDirectory(stagingDirectory);
            }

            targetLock.Release();
        }
    }

    internal async Task RecordDownloadAsync(
        string targetPath,
        Uri sourceUri,
        DownloadTransferMode transferMode,
        TransferIntegrityEvidence integrityEvidence,
        CancellationToken cancellationToken)
    {
        string safeSource = sourceUri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
        await RecordEntryAsync(
            targetPath,
            ManagedDownloadOrigin.Network,
            safeSource,
            transferMode,
            integrityEvidence,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task RecordImportAsync(
        string targetPath,
        string sourcePath,
        TransferIntegrityEvidence integrityEvidence,
        CancellationToken cancellationToken) =>
        RecordEntryAsync(
            targetPath,
            ManagedDownloadOrigin.LocalImport,
            Path.GetFileName(sourcePath),
            null,
            integrityEvidence,
            cancellationToken);

    internal Task CommitDownloadAsync(
        string stagedPath,
        string targetPath,
        string stagingDirectory,
        Uri sourceUri,
        DownloadTransferMode transferMode,
        TransferIntegrityEvidence integrityEvidence,
        bool overwrite,
        CancellationToken cancellationToken) => CommitFileAndManifestAsync(
            stagedPath,
            targetPath,
            stagingDirectory,
            overwrite,
            () => RecordDownloadAsync(
                targetPath,
                sourceUri,
                transferMode,
                integrityEvidence,
                CancellationToken.None),
            cancellationToken);

    internal Task CommitImportAsync(
        string stagedPath,
        string targetPath,
        string stagingDirectory,
        string sourcePath,
        TransferIntegrityEvidence integrityEvidence,
        bool overwrite,
        CancellationToken cancellationToken) => CommitFileAndManifestAsync(
            stagedPath,
            targetPath,
            stagingDirectory,
            overwrite,
            () => RecordImportAsync(
                targetPath,
                sourcePath,
                integrityEvidence,
                CancellationToken.None),
            cancellationToken);

    internal string ResolveTargetPath(string fileName)
    {
        ValidateSafeFileName(fileName);
        string targetPath = Path.GetFullPath(Path.Combine(LibraryRoot, fileName));
        if (!IsDirectChild(targetPath))
        {
            throw new ArgumentException(
                "The destination must be a direct child of the managed download library.",
                nameof(fileName));
        }

        return targetPath;
    }

    private async Task CommitFileAndManifestAsync(
        string stagedPath,
        string targetPath,
        string stagingDirectory,
        bool overwrite,
        Func<Task> recordManifestAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(recordManifestAsync);
        EnsureOperationalDirectories();

        await using LibraryTransactionLease transaction =
            await AcquireTransactionLockAsync(cancellationToken).ConfigureAwait(false);
        _ = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);

        string fullStagingDirectory = Path.GetFullPath(stagingDirectory);
        string fullStagedPath = Path.GetFullPath(stagedPath);
        string fullTargetPath = Path.GetFullPath(targetPath);
        if (!Path.GetDirectoryName(fullStagingDirectory)!.Equals(
                StagingRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(fullStagedPath)!.Equals(
                fullStagingDirectory,
                StringComparison.OrdinalIgnoreCase)
            || !IsDirectChild(fullTargetPath))
        {
            throw new InvalidDataException(
                "A download-library commit path escaped its reviewed staging or target root.");
        }

        EnsureRegularDirectory(fullStagingDirectory, "download operation staging directory");
        FileAttributes stagedAttributes = File.GetAttributes(fullStagedPath);
        if ((stagedAttributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "Only a regular staged file can be committed to the download library.");
        }

        EnsureTargetCanBeWritten(fullTargetPath, overwrite);
        string previousPath = Path.Combine(fullStagingDirectory, "previous-file.quarantine");
        string failedNewPath = Path.Combine(fullStagingDirectory, "failed-new-file.quarantine");
        bool previousQuarantined = false;
        bool newFileCommitted = false;
        try
        {
            if (File.Exists(fullTargetPath))
            {
                File.Move(fullTargetPath, previousPath, overwrite: false);
                previousQuarantined = true;
            }

            File.Move(fullStagedPath, fullTargetPath, overwrite: false);
            newFileCommitted = true;
            await recordManifestAsync().ConfigureAwait(false);

            if (previousQuarantined)
            {
                try
                {
                    File.Delete(previousPath);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                    // The new target and its manifest entry are already committed. The outer
                    // best-effort staging cleanup can retry removal without misreporting a
                    // successful library transaction as a failed overwrite.
                }
            }
        }
        catch (Exception commitException) when (commitException is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            Exception? rollbackException = null;
            try
            {
                if (newFileCommitted && File.Exists(fullTargetPath))
                {
                    File.Move(fullTargetPath, failedNewPath, overwrite: false);
                }

                if (previousQuarantined && File.Exists(previousPath))
                {
                    File.Move(previousPath, fullTargetPath, overwrite: false);
                }

                if (File.Exists(failedNewPath))
                {
                    File.Delete(failedNewPath);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                rollbackException = exception;
            }

            if (rollbackException is not null)
            {
                throw new ManagedDownloadCommitException(
                    "The download-library manifest commit failed and the previous target could "
                    + $"not be fully restored. Recovery evidence remains in '{fullStagingDirectory}'.",
                    preserveStagingEvidence: true,
                    new AggregateException(commitException, rollbackException));
            }

            throw;
        }
    }

    private async Task<LibraryTransactionLease> AcquireTransactionLockAsync(
        CancellationToken cancellationToken)
    {
        SemaphoreSlim processLock = TransactionLocks.GetOrAdd(
            TransactionLockPath,
            _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long deadline = System.Environment.TickCount64 + 5_000;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureTransactionLockPathIsSafe();
                try
                {
                    FileStream stream = new(
                        TransactionLockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.WriteThrough);
                    try
                    {
                        EnsureTransactionLockPathIsSafe();
                        return new LibraryTransactionLease(processLock, stream);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (IOException exception)
                {
                    if (System.Environment.TickCount64 >= deadline)
                    {
                        throw new IOException(
                            "The download library is busy in another AutoEnvPlus process.",
                            exception);
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    private void EnsureTransactionLockPathIsSafe()
    {
        EnsureNoReparsePointInPath(LibraryRoot);
        FileAttributes? attributes = TryGetAttributes(TransactionLockPath);
        if (attributes is FileAttributes value
            && (value & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The download-library transaction lock must be a regular file.");
        }
    }

    internal void EnsureTargetCanBeWritten(string targetPath, bool overwrite)
    {
        EnsureOperationalDirectories();
        string fullTarget = Path.GetFullPath(targetPath);
        if (!IsDirectChild(fullTarget))
        {
            throw new InvalidDataException(
                "The destination escaped the managed download library root.");
        }

        FileAttributes? attributes = TryGetAttributes(fullTarget);
        if (attributes is FileAttributes existingAttributes)
        {
            if ((existingAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The destination is a reparse point and cannot be replaced.");
            }

            if ((existingAttributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    "The destination is not a regular file.");
            }

            if (!overwrite)
            {
                throw new IOException($"The destination '{fullTarget}' already exists.");
            }
        }
    }

    internal string CreateOperationStagingDirectory()
    {
        EnsureOperationalDirectories();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string path = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            DirectoryInfo created = Directory.CreateDirectory(path);
            if ((created.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "The transfer staging directory unexpectedly became a reparse point.");
            }

            return created.FullName;
        }

        throw new IOException("Could not allocate a unique transfer staging directory.");
    }

    internal void EnsureOperationalDirectories()
    {
        EnsureNoReparsePointInPath(LibraryRoot);
        Directory.CreateDirectory(LibraryRoot);
        EnsureNoReparsePointInPath(LibraryRoot);
        EnsureRegularDirectory(LibraryRoot, "managed download library root");
        EnsureNoReparsePointInPath(StagingRoot);
        Directory.CreateDirectory(StagingRoot);
        EnsureNoReparsePointInPath(StagingRoot);
        EnsureRegularDirectory(StagingRoot, "managed download staging root");
    }

    internal async Task ValidateManifestForCommitAsync(CancellationToken cancellationToken)
    {
        SemaphoreSlim manifestLock = ManifestLocks.GetOrAdd(
            ManifestPath,
            _ => new SemaphoreSlim(1, 1));
        await manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            manifestLock.Release();
        }
    }

    private async Task RecordEntryAsync(
        string targetPath,
        ManagedDownloadOrigin origin,
        string source,
        DownloadTransferMode? transferMode,
        TransferIntegrityEvidence integrityEvidence,
        CancellationToken cancellationToken)
    {
        EnsureOperationalDirectories();
        string fullTarget = Path.GetFullPath(targetPath);
        if (!IsDirectChild(fullTarget))
        {
            throw new InvalidDataException(
                "A library manifest entry escaped the managed download library root.");
        }

        FileInfo target = new(fullTarget);
        if (!target.Exists
            || (target.Attributes & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "Only committed regular files can be recorded in the download library manifest.");
        }

        SemaphoreSlim manifestLock = ManifestLocks.GetOrAdd(
            ManifestPath,
            _ => new SemaphoreSlim(1, 1));
        await manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LibraryManifest current = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
            List<ManifestEntry> entries = current.Entries
                .Where(entry => !entry.FileName.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                .Where(entry => IsCurrentRegularLibraryFile(entry.FileName))
                .ToList();
            entries.Add(new ManifestEntry(
                target.Name,
                target.Length,
                origin,
                source,
                transferMode,
                integrityEvidence.ContentSha256,
                integrityEvidence.ExpectedHashAlgorithm,
                integrityEvidence.ExpectedHash,
                integrityEvidence.VerifiedHash,
                DateTimeOffset.UtcNow));
            LibraryManifest updated = new(
                1,
                entries
                    .OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            await WriteManifestAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            manifestLock.Release();
        }
    }

    private async Task<bool> RemoveManifestEntryAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ManifestPath))
        {
            EnsureMissingManifestHasNoFileSystemEntry();
            return false;
        }

        SemaphoreSlim manifestLock = ManifestLocks.GetOrAdd(
            ManifestPath,
            _ => new SemaphoreSlim(1, 1));
        await manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LibraryManifest current = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
            bool removed = current.Entries.Any(entry =>
                entry.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (!removed)
            {
                return false;
            }

            LibraryManifest updated = new(
                1,
                current.Entries
                    .Where(entry => !entry.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    .Where(entry => IsCurrentRegularLibraryFile(entry.FileName))
                    .OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            await WriteManifestAsync(updated, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            manifestLock.Release();
        }
    }

    private LibraryManifest ReadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            EnsureMissingManifestHasNoFileSystemEntry();
            return LibraryManifest.Empty;
        }

        EnsureManifestIsRegularFile();
        try
        {
            using FileStream stream = new(
                ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8_192,
                FileOptions.SequentialScan);
            EnsureManifestLength(stream);
            LibraryManifest manifest = JsonSerializer.Deserialize<LibraryManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("The download library manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The download library manifest is invalid JSON.", exception);
        }
    }

    private async Task<LibraryManifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ManifestPath))
        {
            EnsureMissingManifestHasNoFileSystemEntry();
            return LibraryManifest.Empty;
        }

        EnsureManifestIsRegularFile();
        try
        {
            await using FileStream stream = new(
                ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8_192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            EnsureManifestLength(stream);
            LibraryManifest manifest = await JsonSerializer.DeserializeAsync<LibraryManifest>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The download library manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The download library manifest is invalid JSON.", exception);
        }
    }

    private async Task WriteManifestAsync(
        LibraryManifest manifest,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        string temporaryPath = Path.Combine(
            StagingRoot,
            $"manifest-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8_192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumManifestBytes)
                {
                    throw new InvalidDataException(
                        $"The download library manifest cannot exceed {MaximumManifestBytes} bytes.");
                }
            }

            EnsureManifestCanBeReplaced();
            File.Move(temporaryPath, ManifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void ValidateManifest(LibraryManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Entries is null)
        {
            throw new InvalidDataException(
                "The download library manifest schema is not supported.");
        }

        if (manifest.Entries.Count > MaximumManifestEntries)
        {
            throw new InvalidDataException(
                $"The download library manifest exceeds the {MaximumManifestEntries}-entry limit.");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestEntry entry in manifest.Entries)
        {
            try
            {
                ValidateSafeFileName(entry.FileName);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The download library manifest contains an invalid file name.",
                    exception);
            }

            if (!names.Add(entry.FileName)
                || entry.SizeBytes < 0
                || string.IsNullOrWhiteSpace(entry.Source)
                || !Enum.IsDefined(entry.Origin)
                || (entry.TransferMode is DownloadTransferMode mode && !Enum.IsDefined(mode))
                || (entry.Origin == ManagedDownloadOrigin.Network && entry.TransferMode is null)
                || (entry.Origin == ManagedDownloadOrigin.LocalImport && entry.TransferMode is not null)
                || !PackageHashAlgorithm.Sha256.IsValidHash(entry.ContentSha256)
                || !HasValidExpectedHashEvidence(entry))
            {
                throw new InvalidDataException(
                    "The download library manifest contains an invalid entry.");
            }
        }
    }

    private static void EnsureManifestLength(FileStream stream)
    {
        if (stream.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The download library manifest exceeds the {MaximumManifestBytes}-byte limit.");
        }
    }

    private static bool HasValidExpectedHashEvidence(ManifestEntry entry)
    {
        if (entry.ExpectedHashAlgorithm is null
            && entry.ExpectedHash is null
            && entry.VerifiedHash is null)
        {
            return true;
        }

        return entry.ExpectedHashAlgorithm is PackageHashAlgorithm algorithm
            && Enum.IsDefined(algorithm)
            && algorithm.IsValidHash(entry.ExpectedHash)
            && algorithm.IsValidHash(entry.VerifiedHash)
            && entry.ExpectedHash!.Equals(entry.VerifiedHash, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentRegularLibraryFile(string fileName)
    {
        string path;
        try
        {
            path = ResolveTargetPath(fileName);
        }
        catch (ArgumentException)
        {
            return false;
        }

        FileAttributes? attributes = TryGetAttributes(path);
        return attributes is FileAttributes value
            && (value & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) == 0;
    }

    private void EnsureManifestCanBeReplaced()
    {
        FileAttributes? attributes = TryGetAttributes(ManifestPath);
        if (attributes is FileAttributes value
            && (value & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The download library manifest is not a regular file.");
        }
    }

    private void EnsureManifestIsRegularFile()
    {
        FileAttributes attributes = File.GetAttributes(ManifestPath);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The download library manifest is not a regular file.");
        }
    }

    private void EnsureMissingManifestHasNoFileSystemEntry()
    {
        if (TryGetAttributes(ManifestPath) is not null)
        {
            throw new InvalidDataException(
                "The download library manifest path is not a regular file.");
        }
    }

    internal static void TryDeleteStagingDirectory(string? stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
        {
            return;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(stagingDirectory);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            // Cleanup is best effort and must not hide the original transfer result.
        }
    }

    private static void ValidateSafeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Length > 240
            || fileName is "." or ".."
            || fileName.IndexOfAny(DirectorySeparators) >= 0
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName.EndsWith(' ')
            || fileName.EndsWith('.'))
        {
            throw new ArgumentException(
                "The destination file name must be a safe file name without a path.",
                nameof(fileName));
        }

        string baseName = fileName.Split('.')[0];
        if (ReservedWindowsNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The destination file name is reserved by Windows.",
                nameof(fileName));
        }

        string extension = Path.GetExtension(fileName);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The destination must use an approved package or archive extension.",
                nameof(fileName));
        }
    }

    private bool IsDirectChild(string candidate)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(fullCandidate)!.Equals(
                LibraryRoot,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureRegularDirectory(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & ReparseOrDevice) != 0)
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
            if (attributes is FileAttributes existingAttributes
                && (existingAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The managed download library path crosses reparse point '{current.FullName}'.");
            }

            current = current.Parent;
        }
    }

    private static FileAttributes ReparseOrDevice =>
        FileAttributes.ReparsePoint | FileAttributes.Device;

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private sealed record LibraryManifest(
        int SchemaVersion,
        IReadOnlyList<ManifestEntry> Entries)
    {
        public static LibraryManifest Empty { get; } = new(1, []);
    }

    private sealed record ManifestEntry(
        string FileName,
        long SizeBytes,
        ManagedDownloadOrigin Origin,
        string Source,
        DownloadTransferMode? TransferMode,
        string ContentSha256,
        PackageHashAlgorithm? ExpectedHashAlgorithm,
        string? ExpectedHash,
        string? VerifiedHash,
        DateTimeOffset AddedUtc);

    private sealed class LibraryTransactionLease(
        SemaphoreSlim processLock,
        FileStream stream) : IAsyncDisposable
    {
        private SemaphoreSlim? _processLock = processLock;
        private FileStream? _stream = stream;

        public ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _stream = null;
            Interlocked.Exchange(ref _processLock, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class ManagedDownloadTargetLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim Get(string targetPath) =>
        Locks.GetOrAdd(Path.GetFullPath(targetPath), _ => new SemaphoreSlim(1, 1));
}

internal sealed class ManagedDownloadCommitException(
    string message,
    bool preserveStagingEvidence,
    Exception innerException) : IOException(message, innerException)
{
    public bool PreserveStagingEvidence { get; } = preserveStagingEvidence;
}
