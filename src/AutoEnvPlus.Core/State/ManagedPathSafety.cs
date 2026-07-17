using System.Security;

namespace AutoEnvPlus.Core.State;

internal static class ManagedPathSafety
{
    public static void EnsureOrdinaryDirectoryTree(
        string managedRoot,
        string directoryPath,
        string description,
        bool allowMissing)
    {
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        string fullDirectoryPath = Path.GetFullPath(directoryPath);
        EnsureChildPath(fullManagedRoot, fullDirectoryPath, description);
        EnsureNoReparsePointInPath(fullManagedRoot);
        EnsureNoReparsePointInPath(fullDirectoryPath);

        FileAttributes? targetAttributes = TryGetAttributes(fullDirectoryPath);
        if (targetAttributes is null)
        {
            if (allowMissing)
            {
                return;
            }

            throw new IOException($"The {description} does not exist.");
        }

        EnsureRegularDirectory(targetAttributes.Value, description);
        Stack<string> pending = new();
        pending.Push(fullDirectoryPath);
        EnumerationOptions options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        while (pending.TryPop(out string? current))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                current,
                "*",
                options))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException(
                        $"The {description} contains a reparse point or device entry.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    public static void CreateOrdinaryDirectoryPath(
        string managedRoot,
        string directoryPath,
        string description)
    {
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        string fullDirectoryPath = Path.GetFullPath(directoryPath);
        EnsureChildPath(fullManagedRoot, fullDirectoryPath, description);
        EnsureNoReparsePointInPath(fullManagedRoot);
        EnsureNoReparsePointInPath(fullDirectoryPath);
        Directory.CreateDirectory(fullDirectoryPath);
        EnsureNoReparsePointInPath(fullManagedRoot);
        EnsureNoReparsePointInPath(fullDirectoryPath);
        EnsureRegularDirectory(
            File.GetAttributes(fullDirectoryPath),
            description);
    }

    public static void EnsureOrdinaryFile(
        string managedRoot,
        string filePath,
        string description)
    {
        string fullManagedRoot = Path.GetFullPath(managedRoot);
        string fullFilePath = Path.GetFullPath(filePath);
        EnsureChildPath(fullManagedRoot, fullFilePath, description);
        EnsureNoReparsePointInPath(fullManagedRoot);
        EnsureNoReparsePointInPath(fullFilePath);
        FileAttributes? attributes = TryGetAttributes(fullFilePath);
        if (attributes is null
            || (attributes.Value & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException($"The {description} must be an ordinary file.");
        }
    }

    public static void EnsureNoReparsePointInPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (TryGetAttributes(current) is FileAttributes attributes
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A managed path crosses a reparse point.");
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
        catch (Exception exception) when (exception is SecurityException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new IOException("A managed path could not be inspected safely.", exception);
        }
    }

    private static void EnsureRegularDirectory(
        FileAttributes attributes,
        string description)
    {
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException($"The {description} must be an ordinary directory.");
        }
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {description} must remain inside the managed root.");
        }
    }
}
