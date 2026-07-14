using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class CacheMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-CacheMove-{Guid.NewGuid():N}");

    [Fact]
    public async Task MigrateAsync_CopiesVerifiesConfiguresAndRetainsSource()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "pip-cache");
        CacheDirectoryLocation location = CreateLocation(source);
        CacheMigrationService service = new();
        CacheMigrationPlan plan = service.CreatePlan(location, destination);
        FakeEnvironmentStore environment = new();

        CacheMigrationResult result = await service.MigrateAsync(plan, environment);

        Assert.True(result.Success);
        Assert.True(result.SourceRetained);
        Assert.True(Directory.Exists(source));
        Assert.Equal("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "nested", "two.txt")));
        Assert.Equal(destination, environment.Values["PIP_CACHE_DIR"]);
    }

    [Fact]
    public async Task MigrateAsync_GradleUsesUserHomeVariableAndCreatesRollbackSnapshot()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "gradle-home");
        CacheDirectoryLocation location = CreateLocation(source, "gradle", source);
        string managedRoot = Path.Combine(_root, "managed");
        CacheMigrationService service = new(managedRoot);
        CacheMigrationPlan plan = service.CreatePlan(location, destination);
        FakeEnvironmentStore environment = new();
        environment.Values["GRADLE_USER_HOME"] = source;

        CacheMigrationResult result = await service.MigrateAsync(plan, environment);

        Assert.True(result.Success);
        Assert.Equal(destination, environment.Values["GRADLE_USER_HOME"]);
        Assert.NotNull(result.SnapshotPath);
        Assert.True(File.Exists(result.SnapshotPath));

        CacheMigrationResult rollback = await service.RollbackAsync(
            result.SnapshotPath!,
            environment);
        Assert.True(rollback.Success);
        Assert.Equal(source, environment.Values["GRADLE_USER_HOME"]);
        Assert.True(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("vcpkg", "VCPKG_DEFAULT_BINARY_CACHE")]
    [InlineData("conan", "CONAN_HOME")]
    public async Task MigrateAsync_CppPackageStorageUsesAllowlistedVariableAndRollsBack(
        string storageId,
        string variable)
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", storageId);
        CacheDirectoryLocation location = CreateLocation(source, storageId, source);
        CacheMigrationService service = new(Path.Combine(_root, "managed"));
        CacheMigrationPlan plan = service.CreatePlan(location, destination);
        FakeEnvironmentStore environment = new();
        environment.Values[variable] = source;

        CacheMigrationResult result = await service.MigrateAsync(plan, environment);

        Assert.True(result.Success);
        Assert.Equal(variable, plan.ConfigurationTarget);
        Assert.Equal(destination, environment.Values[variable]);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
        Assert.NotNull(result.SnapshotPath);

        CacheMigrationResult rollback = await service.RollbackAsync(
            result.SnapshotPath!,
            environment);
        Assert.True(rollback.Success);
        Assert.Equal(source, environment.Values[variable]);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task MigrateAsync_MavenUpdatesSettingsPreservesContentAndRollsBack()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "maven-repository");
        string settings = Path.Combine(_root, "profile", ".m2", "settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        string original = "<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"><localRepository>"
            + source
            + "</localRepository><mirrors><mirror><id>keep</id></mirror></mirrors></settings>";
        File.WriteAllText(settings, original);
        CacheDirectoryLocation location = CreateMavenLocation(source, settings, original);
        CacheMigrationService service = new(
            Path.Combine(_root, "managed"),
            settings);
        CacheMigrationPlan plan = service.CreatePlan(location, destination);
        FakeEnvironmentStore environment = new();

        CacheMigrationResult result = await service.MigrateAsync(plan, environment);

        Assert.True(result.Success);
        string configured = File.ReadAllText(settings);
        Assert.Contains(destination, configured, StringComparison.Ordinal);
        Assert.Contains("keep", configured, StringComparison.Ordinal);
        Assert.NotNull(result.SnapshotPath);

        CacheMigrationResult rollback = await service.RollbackAsync(
            result.SnapshotPath!,
            environment);
        Assert.True(rollback.Success);
        Assert.Equal(original, File.ReadAllText(settings));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task MigrateAsync_PnpmUpdatesRcPreservesContentAndRollsBack()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "pnpm-store");
        string config = Path.Combine(_root, "local", "pnpm", "config", "rc");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        string original = $"# keep\r\nregistry=https://registry.example/\r\nstore-dir={source}\r\n";
        File.WriteAllText(config, original);
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == "pnpm");
        CacheDirectoryLocation location = new(
            definition,
            source,
            "test",
            true,
            config,
            ConfigurationValue: original,
            ConfigurationValueKnown: true);
        CacheMigrationService service = new(
            Path.Combine(_root, "managed"),
            pnpmConfigPath: config);
        CacheMigrationPlan plan = service.CreatePlan(location, destination);
        FakeEnvironmentStore environment = new();

        CacheMigrationResult result = await service.MigrateAsync(plan, environment);

        Assert.True(result.Success);
        string configured = File.ReadAllText(config);
        Assert.Contains(destination, configured, StringComparison.Ordinal);
        Assert.Contains("# keep", configured, StringComparison.Ordinal);
        Assert.Contains("registry=https://registry.example/", configured, StringComparison.Ordinal);

        CacheMigrationResult rollback = await service.RollbackAsync(
            result.SnapshotPath!,
            environment);
        Assert.True(rollback.Success);
        Assert.Equal(original, File.ReadAllText(config));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task MigrateAsync_ConfigurationFailureDeletesNewCopyAndKeepsOldValue()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "pip-cache");
        CacheMigrationPlan plan = new CacheMigrationService().CreatePlan(
            CreateLocation(source, configurationValue: source),
            destination);
        FakeEnvironmentStore environment = new(failFirstWrite: true);
        environment.Values["PIP_CACHE_DIR"] = source;

        CacheMigrationResult result = await new CacheMigrationService().MigrateAsync(plan, environment);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(source, environment.Values["PIP_CACHE_DIR"]);
    }

    [Fact]
    public async Task MigrateAsync_RefusesConfigurationChangedAfterPlanAndDeletesNewCopy()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "pip-cache");
        CacheMigrationPlan plan = new CacheMigrationService().CreatePlan(
            CreateLocation(source),
            destination);
        FakeEnvironmentStore environment = new();
        environment.Values["PIP_CACHE_DIR"] = "C:\\changed-after-plan";

        CacheMigrationResult result = await new CacheMigrationService().MigrateAsync(
            plan,
            environment);

        Assert.False(result.Success);
        Assert.Contains("changed after", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("C:\\changed-after-plan", environment.Values["PIP_CACHE_DIR"]);
    }

    [Fact]
    public async Task RollbackAsync_RejectsOutsideSnapshotAndNewerConfiguration()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "gradle-home");
        string managedRoot = Path.Combine(_root, "managed");
        CacheMigrationService service = new(managedRoot);
        FakeEnvironmentStore environment = new();
        environment.Values["GRADLE_USER_HOME"] = source;
        CacheMigrationResult migration = await service.MigrateAsync(
            service.CreatePlan(CreateLocation(source, "gradle", source), destination),
            environment);
        environment.Values["GRADLE_USER_HOME"] = "C:\\newer-value";

        CacheMigrationResult newer = await service.RollbackAsync(
            migration.SnapshotPath!,
            environment);
        CacheMigrationResult outside = await service.RollbackAsync(
            Path.Combine(_root, "outside.json"),
            environment);

        Assert.False(newer.Success);
        Assert.Contains("newer", newer.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("C:\\newer-value", environment.Values["GRADLE_USER_HOME"]);
        Assert.False(outside.Success);
        Assert.Contains("escaped", outside.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_RejectsSnapshotWhoseConfigurationTargetWasTampered()
    {
        string source = CreateSource();
        string destination = Path.Combine(_root, "destination", "gradle-home");
        string managedRoot = Path.Combine(_root, "managed");
        CacheMigrationService service = new(managedRoot);
        FakeEnvironmentStore environment = new();
        environment.Values["GRADLE_USER_HOME"] = source;
        CacheMigrationResult migration = await service.MigrateAsync(
            service.CreatePlan(CreateLocation(source, "gradle", source), destination),
            environment);
        string snapshot = File.ReadAllText(migration.SnapshotPath!);
        File.WriteAllText(
            migration.SnapshotPath!,
            snapshot.Replace(
                "GRADLE_USER_HOME",
                "PATH",
                StringComparison.Ordinal));

        CacheMigrationResult rollback = await service.RollbackAsync(
            migration.SnapshotPath!,
            environment);

        Assert.False(rollback.Success);
        Assert.Contains("invalid", rollback.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(destination, environment.Values["GRADLE_USER_HOME"]);
    }

    [Fact]
    public void CreatePlan_RejectsDestinationInsideSource()
    {
        string source = CreateSource();

        Assert.Throws<ArgumentException>(() => new CacheMigrationService().CreatePlan(
            CreateLocation(source),
            Path.Combine(source, "moved")));
    }

    private string CreateSource()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "source", "nested")).Parent!.FullName;
        File.WriteAllText(Path.Combine(source, "one.txt"), "first");
        File.WriteAllText(Path.Combine(source, "nested", "two.txt"), "second");
        return source;
    }

    private static CacheDirectoryLocation CreateLocation(
        string source,
        string id = "pip",
        string? configurationValue = null)
    {
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == id);
        return new CacheDirectoryLocation(
            definition,
            source,
            "test",
            true,
            ConfigurationValue: configurationValue,
            ConfigurationValueKnown: true);
    }

    private static CacheDirectoryLocation CreateMavenLocation(
        string source,
        string settings,
        string content)
    {
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.Single(
            item => item.Id == "maven");
        return new CacheDirectoryLocation(
            definition,
            source,
            "test",
            true,
            settings,
            ConfigurationValue: content,
            ConfigurationValueKnown: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeEnvironmentStore(bool failFirstWrite = false) : IUserEnvironmentVariableStore
    {
        private bool _failFirstWrite = failFirstWrite;

        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string name) => Values.TryGetValue(name, out string? value) ? value : null;

        public Task SetAsync(
            string name,
            string? value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failFirstWrite)
            {
                _failFirstWrite = false;
                throw new IOException("simulated configuration failure");
            }

            Values[name] = value;
            return Task.CompletedTask;
        }
    }
}
