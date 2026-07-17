namespace AutoEnvPlus.Core.Installation;

public sealed record StagingCleanupResult(
    int DeletedDirectories,
    int RetainedDirectories,
    IReadOnlyList<string> Errors);

public sealed class StagingDirectoryReclaimer
{
    public StagingCleanupResult Reclaim(
        string managedRoot,
        TimeSpan minimumAge,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        if (minimumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        }

        string fullManagedRoot = Path.GetFullPath(managedRoot);
        string stagingRoot = Path.Combine(fullManagedRoot, ".staging");
        EnsureChildPath(fullManagedRoot, stagingRoot);
        if (!Directory.Exists(stagingRoot))
        {
            return new StagingCleanupResult(0, 0, []);
        }

        FileAttributes stagingAttributes = File.GetAttributes(stagingRoot);
        if ((stagingAttributes & FileAttributes.Directory) == 0
            || (stagingAttributes & (FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            return new StagingCleanupResult(
                0,
                0,
                ["The managed staging root is not a regular directory or is a reparse point."]);
        }

        DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;
        int deleted = 0;
        int retained = 0;
        List<string> errors = [];

        foreach (string directory in Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                EnsureChildPath(stagingRoot, directory);
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    retained++;
                    errors.Add("A staging entry is a reparse point and was retained.");
                    continue;
                }

                DateTimeOffset lastWrite = new(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
                if (currentTime - lastWrite < minimumAge)
                {
                    retained++;
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                retained++;
                errors.Add($"{directory}: {exception.Message}");
            }
        }

        return new StagingCleanupResult(deleted, retained, errors);
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        string rootPrefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The staging path must remain inside the managed root.");
        }
    }
}
