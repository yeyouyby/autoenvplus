using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoEnvPlus.Core.Storage;

internal sealed class DirectoryMutationLease : IDisposable
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileAttributeTagInfoClass = 9;

    private readonly Dictionary<string, SafeFileHandle> _handles = new(
        StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private DirectoryMutationLease()
    {
    }

    public static DirectoryMutationLease Acquire(IEnumerable<string> directoryPaths)
    {
        ArgumentNullException.ThrowIfNull(directoryPaths);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Cache directory mutations require Windows directory-handle locking.");
        }

        DirectoryMutationLease lease = new();
        try
        {
            foreach (string directoryPath in directoryPaths
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path.Length))
            {
                lease.AddPath(directoryPath);
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Release(string directoryPath)
    {
        ThrowIfDisposed();
        string normalizedPath = NormalizeDirectoryPath(directoryPath);
        if (_handles.Remove(normalizedPath, out SafeFileHandle? handle))
        {
            handle.Dispose();
        }
    }

    public void AddPath(string directoryPath)
    {
        ThrowIfDisposed();
        foreach (string component in EnumerateDirectoryComponents(directoryPath))
        {
            string normalizedComponent = NormalizeDirectoryPath(component);
            if (_handles.ContainsKey(normalizedComponent))
            {
                continue;
            }

            SafeFileHandle handle = OpenDirectory(normalizedComponent);
            _handles.Add(normalizedComponent, handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SafeFileHandle handle in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The directory path could not be locked safely: {path}",
                new Win32Exception(error));
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out FileAttributeTagInfo info,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The locked directory identity could not be verified: {path}",
                new Win32Exception(error));
        }

        if ((info.FileAttributes & FileAttributeDirectory) == 0)
        {
            handle.Dispose();
            throw new InvalidDataException($"The locked path is not a directory: {path}");
        }

        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException(
                $"Cache mutations refuse directory links and reparse points: {path}");
        }

        return handle;
    }

    private static IEnumerable<string> EnumerateDirectoryComponents(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("The directory path has no filesystem root.");
        yield return root;

        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("The directory path has no filesystem root.");
        return fullPath.Length == root.Length
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);
}
