using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class CacheDirectoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Cache-{Guid.NewGuid():N}");

    [Fact]
    public void Discover_UsesConfiguredVariablesAndToolDefaults()
    {
        string local = Path.Combine(_root, "local");
        string profile = Path.Combine(_root, "profile");
        string customPip = Path.Combine(_root, "custom-pip");
        string customNugetHttp = Path.Combine(_root, "nuget-http-cache");
        string customVcpkg = Path.Combine(_root, "vcpkg-binary-cache");
        CacheEnvironment environment = new(
            local,
            profile,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PIP_CACHE_DIR"] = customPip,
                ["NPM_CONFIG_CACHE"] = null,
                ["NUGET_PACKAGES"] = null,
                ["NUGET_HTTP_CACHE_PATH"] = customNugetHttp,
                ["NUGET_PLUGINS_CACHE_PATH"] = null,
                ["GRADLE_USER_HOME"] = null,
                ["VCPKG_DEFAULT_BINARY_CACHE"] = customVcpkg,
                ["CONAN_HOME"] = null,
            });

        IReadOnlyList<CacheDirectoryLocation> locations = new CacheDirectoryService().Discover(environment);

        CacheDirectoryLocation pip = locations.Single(item => item.Definition.Id == "pip");
        CacheDirectoryLocation npm = locations.Single(item => item.Definition.Id == "npm");
        CacheDirectoryLocation maven = locations.Single(item => item.Definition.Id == "maven");
        Assert.Equal(Path.GetFullPath(customPip), pip.DirectoryPath);
        Assert.Contains("PIP_CACHE_DIR", pip.ConfigurationSource);
        Assert.Equal(Path.GetFullPath(Path.Combine(local, "npm-cache")), npm.DirectoryPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(profile, ".m2", "repository")), maven.DirectoryPath);
        Assert.True(maven.Definition.SupportsMigration);
        CacheDirectoryLocation gradle = locations.Single(item => item.Definition.Id == "gradle");
        Assert.True(gradle.Definition.SupportsMigration);
        Assert.Equal(Path.GetFullPath(Path.Combine(profile, ".gradle")), gradle.DirectoryPath);
        CacheDirectoryLocation pnpm = locations.Single(item => item.Definition.Id == "pnpm");
        Assert.True(pnpm.Definition.SupportsMigration);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(local, "pnpm", "store")),
            pnpm.DirectoryPath);
        CacheDirectoryLocation yarn = locations.Single(item => item.Definition.Id == "yarn");
        Assert.True(yarn.Definition.SupportsMigration);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(local, "Yarn", "Cache")),
            yarn.DirectoryPath);
        CacheDirectoryLocation nuget = locations.Single(item => item.Definition.Id == "nuget");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(profile, ".nuget", "packages")),
            nuget.DirectoryPath);
        CacheDirectoryLocation nugetHttp = locations.Single(
            item => item.Definition.Id == "nuget-http");
        Assert.Equal(Path.GetFullPath(customNugetHttp), nugetHttp.DirectoryPath);
        Assert.Contains(
            "NUGET_HTTP_CACHE_PATH",
            nugetHttp.ConfigurationSource,
            StringComparison.Ordinal);
        CacheDirectoryLocation nugetPlugins = locations.Single(
            item => item.Definition.Id == "nuget-plugins");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(local, "NuGet", "plugins-cache")),
            nugetPlugins.DirectoryPath);
        CacheDirectoryLocation vcpkg = locations.Single(item => item.Definition.Id == "vcpkg");
        Assert.Equal(Path.GetFullPath(customVcpkg), vcpkg.DirectoryPath);
        Assert.Contains("VCPKG_DEFAULT_BINARY_CACHE", vcpkg.ConfigurationSource, StringComparison.Ordinal);
        Assert.True(vcpkg.Definition.SupportsMigration);
        CacheDirectoryLocation conan = locations.Single(item => item.Definition.Id == "conan");
        Assert.Equal(Path.GetFullPath(Path.Combine(profile, ".conan2")), conan.DirectoryPath);
        Assert.True(conan.Definition.SupportsMigration);
        Assert.Equal(11, locations.Count);
    }

    [Fact]
    public void Discover_UsesMavenSettingsLocalRepositoryAndReportsInvalidXml()
    {
        string local = Path.Combine(_root, "local");
        string profile = Path.Combine(_root, "profile");
        string m2 = Directory.CreateDirectory(Path.Combine(profile, ".m2")).FullName;
        string configured = Path.Combine(_root, "maven repository");
        File.WriteAllText(
            Path.Combine(m2, "settings.xml"),
            $"<?xml version=\"1.0\"?><settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"><localRepository>{configured}</localRepository><mirrors /></settings>");
        CacheEnvironment environment = new(
            local,
            profile,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        CacheDirectoryLocation discovered = new CacheDirectoryService()
            .Discover(environment)
            .Single(item => item.Definition.Id == "maven");

        Assert.Equal(Path.GetFullPath(configured), discovered.DirectoryPath);
        Assert.Contains("settings.xml", discovered.ConfigurationSource, StringComparison.Ordinal);
        Assert.Null(discovered.Warning);
        Assert.True(discovered.ConfigurationValueKnown);

        File.WriteAllText(Path.Combine(m2, "settings.xml"), "<settings><broken></settings>");
        CacheDirectoryLocation invalid = new CacheDirectoryService()
            .Discover(environment)
            .Single(item => item.Definition.Id == "maven");

        Assert.NotNull(invalid.Warning);
        Assert.False(invalid.ConfigurationValueKnown);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Warning));
    }

    [Fact]
    public async Task MeasureAsync_CountsNestedFilesAndBytes()
    {
        string cache = Directory.CreateDirectory(Path.Combine(_root, "pip", "nested")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(cache, "one.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(cache, "nested", "two.bin"), new byte[23]);
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == "pip");
        CacheDirectoryLocation location = new(definition, cache, "test", true);

        CacheDirectoryMeasurement measurement = await new CacheDirectoryService().MeasureAsync(location);

        Assert.Equal(2, measurement.FileCount);
        Assert.Equal(34, measurement.TotalBytes);
        Assert.Empty(measurement.Errors);
    }

    [Fact]
    public async Task MeasureBoundedAsync_StopsAtEntryLimit()
    {
        string cache = Directory.CreateDirectory(Path.Combine(_root, "bounded")).FullName;
        File.WriteAllText(Path.Combine(cache, "one.bin"), "1");
        File.WriteAllText(Path.Combine(cache, "two.bin"), "22");
        File.WriteAllText(Path.Combine(cache, "three.bin"), "333");
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == "pip");
        CacheDirectoryLocation location = new(definition, cache, "test", true);

        CacheDirectoryMeasurement measurement = await new CacheDirectoryService()
            .MeasureBoundedAsync(location, maximumEntries: 2, maximumDepth: 4);

        Assert.Equal(2, measurement.FileCount);
        Assert.Contains(measurement.Errors, error =>
            error.Contains("entry safety limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MeasureBoundedAsync_DoesNotFollowReparsePointRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        string external = Directory.CreateDirectory(Path.Combine(_root, "external-cache")).FullName;
        string sentinel = Path.Combine(external, "sentinel.bin");
        File.WriteAllText(sentinel, "external");
        string link = Path.Combine(_root, "cache-link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception linkException) when (linkException is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
                item => item.Id == "pip");
            CacheDirectoryLocation location = new(definition, link, "test", true);

            CacheDirectoryMeasurement measurement = await new CacheDirectoryService()
                .MeasureBoundedAsync(location, maximumEntries: 100, maximumDepth: 4);

            Assert.Equal(0, measurement.FileCount);
            Assert.Contains(measurement.Errors, error =>
                error.Contains("reparse", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("external", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
