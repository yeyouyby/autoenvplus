using System.Security;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Storage;

internal static class StorageFileSafety
{
    public static bool EnsureOrdinaryFileOrMissing(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        FileAttributes? attributes = TryGetAttributes(fullPath);
        if (attributes is null)
        {
            return false;
        }

        if ((attributes.Value & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be an ordinary file without reparse points or device entries.");
        }

        ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        return true;
    }

    public static string PrepareOrdinaryParentForWrite(string path, string description)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"The {description} requires a parent directory.");
        ManagedPathSafety.EnsureNoReparsePointInPath(directory);
        Directory.CreateDirectory(directory);
        ManagedPathSafety.EnsureNoReparsePointInPath(directory);
        FileAttributes attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} parent must be an ordinary directory.");
        }

        _ = EnsureOrdinaryFileOrMissing(fullPath, description);
        return directory;
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
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new IOException("The storage configuration path could not be inspected safely.", exception);
        }
    }
}
