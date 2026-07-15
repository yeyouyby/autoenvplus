namespace AutoEnvPlus.Core.Environment;

public static class ManagedRootResolver
{
    public const string EnvironmentVariableName = "AUTOENVPLUS_HOME";
    public const string DefaultDirectoryName = "AutoEnvPlus";

    public static bool TryResolve(
        string? explicitRoot,
        out string? managedRoot,
        out string? error)
    {
        return TryResolve(
            explicitRoot,
            System.Environment.GetEnvironmentVariable(EnvironmentVariableName),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            out managedRoot,
            out error);
    }

    public static bool TryResolve(
        string? explicitRoot,
        string? environmentRoot,
        string? localApplicationData,
        out string? managedRoot,
        out string? error)
    {
        managedRoot = null;
        error = null;

        string? candidate;
        string source;
        if (explicitRoot is not null)
        {
            candidate = explicitRoot;
            source = "the --root option";
        }
        else if (environmentRoot is not null)
        {
            candidate = environmentRoot;
            source = $"the {EnvironmentVariableName} environment variable";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                error = "AutoEnvPlus could not determine the local application-data directory.";
                return false;
            }

            candidate = Path.Combine(localApplicationData, DefaultDirectoryName);
            source = "the local application-data directory";
        }

        if (!TryNormalize(candidate, out managedRoot, out string? normalizationError))
        {
            error = $"The managed root from {source} is invalid: {normalizationError}";
            managedRoot = null;
            return false;
        }

        return true;
    }

    public static string ResolveOrThrow(string? explicitRoot = null)
    {
        if (TryResolve(explicitRoot, out string? managedRoot, out string? error)
            && managedRoot is not null)
        {
            return managedRoot;
        }

        throw new InvalidOperationException(error ?? "AutoEnvPlus could not resolve its managed root.");
    }

    public static bool TryNormalize(
        string? candidate,
        out string? normalizedPath,
        out string? error)
    {
        normalizedPath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "the path is empty.";
            return false;
        }

        string value = candidate.Trim();
        if (!Path.IsPathFullyQualified(value))
        {
            error = "the path must be absolute.";
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(value);
            string? pathRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(pathRoot))
            {
                error = "the path has no recognizable root.";
                return false;
            }

            string comparablePath = fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string comparableRoot = pathRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.Equals(comparablePath, comparableRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "a drive or network-share root cannot be used as the managed root.";
                return false;
            }

            if (fullPath.Length > pathRoot.Length)
            {
                fullPath = fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            if (!Path.IsPathFullyQualified(fullPath))
            {
                error = "the normalized path must be absolute.";
                return false;
            }

            normalizedPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            error = exception.Message;
            return false;
        }
    }
}
