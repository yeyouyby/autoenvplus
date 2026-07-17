using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;

namespace AutoEnvPlus.Core.State;

internal sealed class ManagedStateLock
{
    private const int LockRetryMilliseconds = 25;
    private const int LockTimeoutMilliseconds = 5_000;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _managedRoot;
    private readonly string _statePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate;

    public static ManagedStateLock CreateRuntimeTransaction(string managedRoot)
    {
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        return new ManagedStateLock(
            fullManagedRoot,
            Path.Combine(
                fullManagedRoot,
                "state",
                "managed-runtime-install.transaction"),
            "managed-runtime-install-state.lock");
    }

    public ManagedStateLock(
        string managedRoot,
        string statePath,
        string lockFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFileName);
        if (!Path.GetFileName(lockFileName).Equals(
                lockFileName,
                StringComparison.Ordinal)
            || !Path.GetExtension(lockFileName).Equals(
                ".lock",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A managed state lock must use a fixed .lock file name.",
                nameof(lockFileName));
        }

        _managedRoot = Path.GetFullPath(managedRoot);
        _statePath = Path.GetFullPath(statePath);
        _lockPath = Path.GetFullPath(Path.Combine(
            _managedRoot,
            "state",
            lockFileName));
        EnsureChildPath(_managedRoot, _statePath, "managed state file");
        EnsureChildPath(_managedRoot, _lockPath, "managed state lock");
        if (_statePath.Equals(_lockPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The managed state file cannot also be its lock file.",
                nameof(statePath));
        }

        _gate = Gates.GetOrAdd(_statePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperationalPaths();
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOperationalPaths();

                FileStream? stream = null;
                try
                {
                    stream = new FileStream(
                        _lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.WriteThrough);
                    EnsureSafeLockTarget();
                    if (stream.Length == 0)
                    {
                        stream.WriteByte(0);
                        stream.Flush(flushToDisk: true);
                    }

                    return new Lease(stream, _gate);
                }
                catch (UnsafeManagedStatePathException)
                {
                    stream?.Dispose();
                    throw;
                }
                catch (IOException)
                    when (elapsed.ElapsedMilliseconds < LockTimeoutMilliseconds)
                {
                    stream?.Dispose();
                    await Task.Delay(LockRetryMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsFileAccessException(exception))
                {
                    stream?.Dispose();
                    throw new IOException(
                        "The managed state lock could not be acquired within the timeout.",
                        exception);
                }
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public void EnsureStatePathSafe(bool createDirectory)
    {
        string directory = Path.GetDirectoryName(_statePath)!;
        EnsureNoReparsePointInPath(_managedRoot);
        EnsureNoReparsePointInPath(directory);
        EnsureNoReparsePointInPath(_statePath);
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
            EnsureNoReparsePointInPath(directory);
        }

        if (TryGetAttributes(directory) is FileAttributes directoryAttributes)
        {
            EnsureRegularDirectory(directory, directoryAttributes, "managed state directory");
        }

        if (TryGetAttributes(_statePath) is FileAttributes stateAttributes)
        {
            EnsureRegularFile(_statePath, stateAttributes, "managed state file");
        }
    }

    public void EnsureTemporaryFilePathSafe(string path)
    {
        EnsureChildPath(_managedRoot, path, "temporary managed state file");
        EnsureNoReparsePointInPath(path);
        if (TryGetAttributes(path) is FileAttributes attributes)
        {
            EnsureRegularFile(path, attributes, "temporary managed state file");
        }
    }

    private void EnsureOperationalPaths()
    {
        string lockDirectory = Path.GetDirectoryName(_lockPath)!;
        EnsureNoReparsePointInPath(_managedRoot);
        EnsureNoReparsePointInPath(lockDirectory);
        Directory.CreateDirectory(lockDirectory);
        EnsureNoReparsePointInPath(_managedRoot);
        EnsureNoReparsePointInPath(lockDirectory);
        EnsureRegularDirectory(
            _managedRoot,
            File.GetAttributes(_managedRoot),
            "managed root");
        EnsureRegularDirectory(
            lockDirectory,
            File.GetAttributes(lockDirectory),
            "managed state lock directory");
        EnsureStatePathSafe(createDirectory: false);
        EnsureSafeLockTarget();
    }

    private void EnsureSafeLockTarget()
    {
        EnsureNoReparsePointInPath(_lockPath);
        if (TryGetAttributes(_lockPath) is FileAttributes attributes)
        {
            EnsureRegularFile(_lockPath, attributes, "managed state lock");
        }
    }

    private static void EnsureRegularDirectory(
        string path,
        FileAttributes attributes,
        string description)
    {
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw UnsafePath($"The {description} must be an ordinary directory: {path}");
        }
    }

    private static void EnsureRegularFile(
        string path,
        FileAttributes attributes,
        string description)
    {
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw UnsafePath($"The {description} must be an ordinary file: {path}");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (TryGetAttributes(current) is FileAttributes attributes
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw UnsafePath("A managed state path crosses a reparse point.");
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

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

    private static bool IsFileAccessException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException;

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string rootPrefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {description} must remain inside the managed root.");
        }
    }

    private static UnsafeManagedStatePathException UnsafePath(string message) => new(message);

    internal sealed class Lease : IDisposable
    {
        private FileStream? _stream;
        private SemaphoreSlim? _gate;

        public Lease(FileStream stream, SemaphoreSlim gate)
        {
            _stream = stream;
            _gate = gate;
        }

        public void Dispose()
        {
            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
            try
            {
                stream?.Dispose();
            }
            finally
            {
                gate?.Release();
            }
        }
    }

    private sealed class UnsafeManagedStatePathException(string message)
        : IOException(message);
}
