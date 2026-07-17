using System.Security;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Storage;

public sealed class CacheDirectoryService
{
    public static IReadOnlyList<CacheDirectoryDefinition> Definitions { get; } =
    [
        new(
            "pip",
            "pip",
            "PIP_CACHE_DIR",
            environment => Path.Combine(environment.LocalApplicationData, "pip", "Cache"),
            true,
            SupportsSafeCleanup: true),
        new(
            "npm",
            "npm",
            "NPM_CONFIG_CACHE",
            environment => Path.Combine(environment.LocalApplicationData, "npm-cache"),
            true,
            SupportsSafeCleanup: true),
        new(
            "pnpm",
            "pnpm",
            null,
            environment => Path.Combine(environment.LocalApplicationData, "pnpm", "store"),
            true,
            CacheConfigurationKind.PnpmRc,
            environment => Path.Combine(environment.LocalApplicationData, "pnpm", "config", "rc"),
            SupportsSafeCleanup: true),
        new(
            "yarn",
            "Yarn Classic",
            "YARN_CACHE_FOLDER",
            environment => Path.Combine(environment.LocalApplicationData, "Yarn", "Cache"),
            true,
            SupportsSafeCleanup: true),
        new(
            "nuget",
            "NuGet global packages",
            "NUGET_PACKAGES",
            environment => Path.Combine(environment.UserProfile, ".nuget", "packages"),
            true,
            SupportsSafeCleanup: true),
        new(
            "nuget-http",
            "NuGet HTTP cache",
            "NUGET_HTTP_CACHE_PATH",
            environment => Path.Combine(environment.LocalApplicationData, "NuGet", "v3-cache"),
            true,
            SupportsSafeCleanup: true),
        new(
            "nuget-plugins",
            "NuGet plugin cache",
            "NUGET_PLUGINS_CACHE_PATH",
            environment => Path.Combine(environment.LocalApplicationData, "NuGet", "plugins-cache"),
            true,
            SupportsSafeCleanup: true),
        new(
            "maven",
            "Maven",
            null,
            environment => Path.Combine(environment.UserProfile, ".m2", "repository"),
            true,
            CacheConfigurationKind.MavenSettingsXml,
            environment => Path.Combine(environment.UserProfile, ".m2", "settings.xml"),
            SupportsSafeCleanup: true),
        new(
            "gradle",
            "Gradle",
            "GRADLE_USER_HOME",
            environment => Path.Combine(environment.UserProfile, ".gradle"),
            true),
        new(
            "vcpkg",
            "vcpkg Binary Cache",
            "VCPKG_DEFAULT_BINARY_CACHE",
            environment => Path.Combine(environment.LocalApplicationData, "vcpkg", "archives"),
            true,
            SupportsSafeCleanup: true),
        new(
            "conan",
            "Conan 2 Home",
            "CONAN_HOME",
            environment => Path.Combine(environment.UserProfile, ".conan2"),
            true),
    ];

    public IReadOnlyList<CacheDirectoryLocation> DiscoverCurrent()
    {
        Dictionary<string, string?> variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name)
            {
                variables[name] = variable.Value?.ToString();
            }
        }

        CacheEnvironment environment = new(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            variables);
        return Discover(environment)
            .Select(location => location.Definition.ConfigurationKind
                    == CacheConfigurationKind.EnvironmentVariable
                && location.Definition.ConfigurationEnvironmentVariable is string variable
                    ? location with
                    {
                        ConfigurationValue = System.Environment.GetEnvironmentVariable(
                            variable,
                            EnvironmentVariableTarget.User),
                        ConfigurationValueKnown = true,
                    }
                    : location)
            .ToArray();
    }

    public IReadOnlyList<CacheDirectoryLocation> Discover(CacheEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Definitions.Select(definition => Resolve(definition, environment)).ToArray();
    }

    public Task<CacheDirectoryMeasurement> MeasureAsync(
        CacheDirectoryLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        return Task.Run(
            () => Measure(location, null, null, cancellationToken),
            cancellationToken);
    }

    public Task<CacheDirectoryMeasurement> MeasureBoundedAsync(
        CacheDirectoryLocation location,
        int maximumEntries,
        int maximumDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (maximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        if (maximumDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        return Task.Run(
            () => Measure(location, maximumEntries, maximumDepth, cancellationToken),
            cancellationToken);
    }

    private static CacheDirectoryLocation Resolve(
        CacheDirectoryDefinition definition,
        CacheEnvironment environment)
    {
        if (definition.ConfigurationKind == CacheConfigurationKind.MavenSettingsXml)
        {
            return ResolveMaven(definition, environment);
        }

        if (definition.ConfigurationKind == CacheConfigurationKind.PnpmRc)
        {
            return ResolvePnpm(definition, environment);
        }

        string? configured = definition.ConfigurationEnvironmentVariable is string variable
            ? environment.GetVariable(variable)
            : null;
        bool isConfigured = !string.IsNullOrWhiteSpace(configured);
        string path = isConfigured
            ? configured!
            : definition.DefaultPathFactory(environment);
        path = System.Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        string fullPath = Path.GetFullPath(path);
        string source = isConfigured
            ? $"环境变量 {definition.ConfigurationEnvironmentVariable}"
            : "工具默认位置";
        return new CacheDirectoryLocation(
            definition,
            fullPath,
            source,
            Directory.Exists(fullPath),
            ConfigurationValue: configured,
            ConfigurationValueKnown: true);
    }

    private static CacheDirectoryLocation ResolveMaven(
        CacheDirectoryDefinition definition,
        CacheEnvironment environment)
    {
        string settingsPath = Path.GetFullPath(
            definition.ConfigurationFilePathFactory?.Invoke(environment)
                ?? throw new InvalidOperationException(
                    "The Maven cache definition requires a settings.xml path."));
        MavenSettingsReadResult settings = new MavenSettingsXmlService().Read(
            settingsPath,
            environment);
        string defaultPath = Path.GetFullPath(definition.DefaultPathFactory(environment));
        string path = settings.LocalRepository ?? defaultPath;
        string source = settings.Error is not null
            ? "settings.xml 无法读取"
            : settings.LocalRepository is not null
                ? $"Maven settings.xml ({settingsPath})"
                : "Maven 默认位置";
        return new CacheDirectoryLocation(
            definition,
            path,
            source,
            Directory.Exists(path),
            settingsPath,
            settings.Error,
            settings.Content,
            settings.Error is null);
    }

    private static CacheDirectoryLocation ResolvePnpm(
        CacheDirectoryDefinition definition,
        CacheEnvironment environment)
    {
        string configPath = Path.GetFullPath(
            definition.ConfigurationFilePathFactory?.Invoke(environment)
                ?? throw new InvalidOperationException(
                    "The pnpm cache definition requires a global config path."));
        PnpmRcReadResult config = new PnpmRcService().Read(configPath, environment);
        string defaultPath = Path.GetFullPath(definition.DefaultPathFactory(environment));
        string path = config.StoreDirectory ?? defaultPath;
        string source = config.Error is not null
            ? "pnpm 全局配置无法读取"
            : config.StoreDirectory is not null
                ? $"pnpm store-dir ({configPath})"
                : "pnpm 默认位置";
        return new CacheDirectoryLocation(
            definition,
            path,
            source,
            Directory.Exists(path),
            configPath,
            config.Error,
            config.Content,
            config.Error is null);
    }

    private static CacheDirectoryMeasurement Measure(
        CacheDirectoryLocation location,
        int? maximumEntries,
        int? maximumDepth,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(location.DirectoryPath);
        List<string> errors = [];
        FileAttributes? rootAttributes;
        try
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(root);
            rootAttributes = TryGetExistingAttributes(root);
            if (rootAttributes is null)
            {
                return new CacheDirectoryMeasurement(location with { Exists = false }, 0, 0, []);
            }

            if ((rootAttributes.Value & FileAttributes.Directory) == 0
                || (rootAttributes.Value & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "The cache root must be an ordinary directory without reparse points or device entries.");
            }

            ManagedPathSafety.EnsureNoReparsePointInPath(root);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            errors.Add($"{root}: {exception.Message}");
            return new CacheDirectoryMeasurement(location with { Exists = true }, 0, 0, errors);
        }

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        long fileCount = 0;
        long totalBytes = 0;
        long inspectedEntries = 0;
        bool depthLimitReported = false;
        Stack<(string Path, int Depth)> pending = [];
        pending.Push((root, 0));
        while (pending.TryPop(out (string Path, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ManagedPathSafety.EnsureNoReparsePointInPath(current.Path);
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                    current.Path,
                    "*",
                    options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inspectedEntries++;
                    if (maximumEntries is int entryLimit && inspectedEntries > entryLimit)
                    {
                        errors.Add(
                            $"{root}: measurement stopped after reaching the {entryLimit} entry safety limit.");
                        return new CacheDirectoryMeasurement(
                            location with { Exists = true },
                            fileCount,
                            totalBytes,
                            errors);
                    }

                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                        {
                            errors.Add($"{entry}: reparse point or device entry was not followed.");
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (maximumDepth is int depthLimit && current.Depth >= depthLimit)
                            {
                                if (!depthLimitReported)
                                {
                                    errors.Add(
                                        $"{root}: measurement stopped descending at the {depthLimit} level safety limit.");
                                    depthLimitReported = true;
                                }

                                continue;
                            }

                            pending.Push((entry, current.Depth + 1));
                            continue;
                        }

                        ManagedPathSafety.EnsureNoReparsePointInPath(entry);
                        totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                        fileCount++;
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException)
                    {
                        errors.Add($"{entry}: {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                errors.Add($"{current.Path}: {exception.Message}");
            }
        }

        return new CacheDirectoryMeasurement(
            location with { Exists = true },
            fileCount,
            totalBytes,
            errors);
    }

    private static FileAttributes? TryGetExistingAttributes(string path)
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
            throw new IOException("The cache path could not be inspected safely.", exception);
        }
    }
}
