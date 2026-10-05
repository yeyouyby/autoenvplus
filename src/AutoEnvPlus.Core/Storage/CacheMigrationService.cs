using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoEnvPlus.Core.Storage;

public sealed class CacheMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MavenSettingsXmlService _mavenSettings = new();
    private readonly PnpmRcService _pnpmConfig = new();
    private readonly string? _managedRoot;
    private readonly string _configurationLockRoot;
    private readonly string _authorizedMavenSettingsPath;
    private readonly string _authorizedPnpmConfigPath;

    public CacheMigrationService(
        string? managedRoot = null,
        string? mavenSettingsPath = null,
        string? pnpmConfigPath = null)
    {
        _managedRoot = managedRoot is null ? null : Path.GetFullPath(managedRoot);
        string localApplicationData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);
        _configurationLockRoot = Path.Combine(
            string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.Combine(Path.GetTempPath(), "AutoEnvPlus")
                : Path.Combine(localApplicationData, "AutoEnvPlus"),
            "state",
            "cache-configuration-locks");
        _authorizedMavenSettingsPath = Path.GetFullPath(
            mavenSettingsPath ?? Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".m2",
                "settings.xml"));
        _authorizedPnpmConfigPath = Path.GetFullPath(
            pnpmConfigPath ?? Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "pnpm",
                "config",
                "rc"));
    }

    public CacheMigrationPlan CreatePlan(
        CacheDirectoryLocation source,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!source.Definition.SupportsMigration)
        {
            throw new NotSupportedException(
                $"{source.Definition.DisplayName} does not support cache migration.");
        }

        string sourcePath = Path.GetFullPath(source.DirectoryPath);
        string destination = Path.GetFullPath(destinationPath);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Cache source does not exist: {sourcePath}");
        }

        if (PathsEqual(sourcePath, destination)
            || IsChildPath(sourcePath, destination)
            || IsChildPath(destination, sourcePath))
        {
            throw new ArgumentException(
                "The cache destination cannot equal, contain, or be contained by the source directory.",
                nameof(destinationPath));
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"The cache destination already exists: {destination}");
        }

        CacheMigrationSourceManifest sourceManifest;
        using (DirectoryMutationLease.Acquire([sourcePath]))
        {
            sourceManifest = CreateSourceManifest(sourcePath, CancellationToken.None);
        }

        CacheConfigurationKind configurationKind = source.Definition.ConfigurationKind;
        if (configurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            string variable = source.Definition.ConfigurationEnvironmentVariable
                ?? throw new InvalidOperationException(
                    $"{source.Definition.DisplayName} does not define its configuration variable.");
            if (!source.ConfigurationValueKnown)
            {
                throw new InvalidOperationException(
                    $"The current {variable} value was not captured during discovery; refresh storage before migrating.");
            }

            return new CacheMigrationPlan(
                source with { DirectoryPath = sourcePath, Exists = true },
                destination,
                configurationKind,
                variable,
                true,
                false,
                source.ConfigurationValue,
                destination)
            {
                SourceManifest = sourceManifest,
            };
        }

        if (configurationKind == CacheConfigurationKind.MavenSettingsXml)
        {
            if (!string.IsNullOrWhiteSpace(source.Warning))
            {
                throw new InvalidDataException(source.Warning);
            }

            string settingsPath = source.ConfigurationFilePath
                ?? throw new InvalidOperationException(
                    "Maven storage discovery did not capture the settings.xml path.");
            if (!Path.GetFullPath(settingsPath).Equals(
                _authorizedMavenSettingsPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Maven migration may only update the authorized user settings.xml: {_authorizedMavenSettingsPath}");
            }

            MavenSettingsMutation mutation = _mavenSettings.CreateMutation(
                settingsPath,
                destination);
            if (!source.ConfigurationValueKnown
                || !string.Equals(
                    mutation.Before,
                    source.ConfigurationValue,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Maven settings.xml changed after storage discovery; refresh and review the new plan.");
            }

            return new CacheMigrationPlan(
                source with { DirectoryPath = sourcePath, Exists = true },
                destination,
                configurationKind,
                mutation.SettingsPath,
                true,
                mutation.Existed,
                mutation.Before,
                mutation.After)
            {
                SourceManifest = sourceManifest,
            };
        }

        if (configurationKind == CacheConfigurationKind.PnpmRc)
        {
            if (!string.IsNullOrWhiteSpace(source.Warning))
            {
                throw new InvalidDataException(source.Warning);
            }

            string configPath = source.ConfigurationFilePath
                ?? throw new InvalidOperationException(
                    "pnpm storage discovery did not capture the global config path.");
            if (!Path.GetFullPath(configPath).Equals(
                _authorizedPnpmConfigPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"pnpm migration may only update the authorized global config: {_authorizedPnpmConfigPath}");
            }

            PnpmRcMutation mutation = _pnpmConfig.CreateMutation(
                configPath,
                destination);
            if (!source.ConfigurationValueKnown
                || !string.Equals(
                    mutation.Before,
                    source.ConfigurationValue,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The pnpm global config changed after storage discovery; refresh and review the new plan.");
            }

            return new CacheMigrationPlan(
                source with { DirectoryPath = sourcePath, Exists = true },
                destination,
                configurationKind,
                mutation.ConfigPath,
                true,
                mutation.Existed,
                mutation.Before,
                mutation.After)
            {
                SourceManifest = sourceManifest,
            };
        }

        throw new NotSupportedException(
            $"Unsupported cache configuration kind: {configurationKind}");
    }

    public async Task<CacheMigrationResult> MigrateAsync(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore,
        IProgress<CacheMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(environmentStore);
        string source = Path.GetFullPath(plan.Source.DirectoryPath);
        string destination = Path.GetFullPath(plan.DestinationPath);
        string? staging = null;
        bool destinationPublished = false;
        string? snapshotPath = null;

        try
        {
            CacheMigrationPlan validated = CreatePlan(plan.Source, plan.DestinationPath);
            if (!SourceManifestsEqual(plan.SourceManifest, validated.SourceManifest))
            {
                throw new InvalidOperationException(
                    "The cache changed after the migration preview was created; refresh and review a new plan.");
            }

            source = validated.Source.DirectoryPath;
            destination = validated.DestinationPath;
            string destinationParent = Path.GetDirectoryName(destination)
                ?? throw new ArgumentException("The destination requires a parent directory.", nameof(plan));
            Directory.CreateDirectory(destinationParent);
            using DirectoryMutationLease mutationLease = DirectoryMutationLease.Acquire(
                [source, destinationParent]);

            progress?.Report(new CacheMigrationProgress("measure"));
            CacheMigrationSourceManifest currentManifest = CreateSourceManifest(
                source,
                cancellationToken);
            if (!SourceManifestsEqual(plan.SourceManifest, currentManifest))
            {
                throw new InvalidOperationException(
                    "The cache changed while the migration source was being locked; refresh and review a new plan.");
            }

            EnsureFreeSpace(destinationParent, currentManifest.TotalBytes);
            staging = destination + $".autoenvplus-{Guid.NewGuid():N}.tmp";
            Directory.CreateDirectory(staging);
            EnsureDirectoryNotReparse(staging, "cache migration staging directory");
            mutationLease.AddPath(staging);
            progress?.Report(new CacheMigrationProgress(
                "copy",
                TotalBytes: currentManifest.TotalBytes));
            CacheMigrationSourceManifest copiedManifest = await CopyTreeVerifiedAsync(
                source,
                staging,
                currentManifest.TotalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!SourceManifestsEqual(plan.SourceManifest, copiedManifest))
            {
                throw new InvalidDataException(
                    "The cache changed during migration; the copied file manifest no longer matches the reviewed source.");
            }

            CacheMigrationSourceManifest stagedManifest = CreateSourceManifest(
                staging,
                cancellationToken);
            if (!SourceManifestsEqual(plan.SourceManifest, stagedManifest))
            {
                throw new InvalidDataException(
                    "The staged cache changed before commit; the published destination was not created.");
            }

            CacheMigrationSourceManifest sourceBeforeCommit = CreateSourceManifest(
                source,
                cancellationToken);
            if (!SourceManifestsEqual(plan.SourceManifest, sourceBeforeCommit))
            {
                throw new InvalidOperationException(
                    "The cache changed while migration was copying it; refresh and review a new plan.");
            }

            progress?.Report(new CacheMigrationProgress("commit"));
            EnsureDirectoryNotReparse(staging, "cache migration staging directory");
            Directory.Move(staging, destination);
            destinationPublished = true;

            progress?.Report(new CacheMigrationProgress("configure"));
            CacheMigrationSourceManifest publishedManifest = CreateSourceManifest(
                destination,
                cancellationToken);
            if (!SourceManifestsEqual(plan.SourceManifest, publishedManifest))
            {
                throw new InvalidDataException(
                    "The published cache changed before configuration commit; the configuration was not switched.");
            }

            using (ConfigurationMutationLease configurationLease =
                   await AcquireConfigurationLeaseAsync(validated, cancellationToken)
                       .ConfigureAwait(false))
            {
                EnsureConfigurationUnchanged(validated, environmentStore);
                snapshotPath = await CreateSnapshotAsync(
                    validated,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    CacheMigrationSourceManifest sourceBeforeConfigurationCommit =
                        CreateSourceManifest(source, cancellationToken);
                    if (!SourceManifestsEqual(
                            plan.SourceManifest,
                            sourceBeforeConfigurationCommit))
                    {
                        throw new InvalidOperationException(
                            "The source cache changed before configuration commit; the configuration was not switched.");
                    }

                    CacheMigrationSourceManifest destinationBeforeConfigurationCommit =
                        CreateSourceManifest(destination, cancellationToken);
                    if (!SourceManifestsEqual(
                            plan.SourceManifest,
                            destinationBeforeConfigurationCommit))
                    {
                        throw new InvalidDataException(
                            "The published cache changed before configuration commit; the configuration was not switched.");
                    }

                    await ApplyConfigurationAsync(
                        validated,
                        environmentStore,
                        cancellationToken).ConfigureAwait(false);

                    CacheMigrationSourceManifest sourceAfterConfigurationCommit =
                        CreateSourceManifest(source, cancellationToken);
                    if (!SourceManifestsEqual(
                            plan.SourceManifest,
                            sourceAfterConfigurationCommit))
                    {
                        throw new InvalidOperationException(
                            "The source cache changed while configuration was being committed; the configuration will be restored.");
                    }

                    CacheMigrationSourceManifest destinationAfterConfigurationCommit =
                        CreateSourceManifest(destination, cancellationToken);
                    if (!SourceManifestsEqual(
                            plan.SourceManifest,
                            destinationAfterConfigurationCommit))
                    {
                        throw new InvalidDataException(
                            "The published cache changed while configuration was being committed; the configuration will be restored.");
                    }
                }
                catch (Exception applyException)
                {
                    try
                    {
                        await RestoreConfigurationAfterFailedApplyAsync(
                            validated,
                            environmentStore).ConfigureAwait(false);
                    }
                    catch (Exception restoreException)
                    {
                        throw new IOException(
                            $"{applyException.Message} Configuration compensation was refused or failed: {restoreException.Message}",
                            applyException);
                    }

                    if (snapshotPath is not null)
                    {
                        TryDeleteCreatedFile(snapshotPath);
                        snapshotPath = null;
                    }

                    throw;
                }
            }

            progress?.Report(new CacheMigrationProgress("complete"));
            return new CacheMigrationResult(
                true,
                source,
                destination,
                true,
                null,
                snapshotPath);
        }
        catch (OperationCanceledException exception)
        {
            string? residualPath = GetResidualPath(
                destinationPublished,
                destination,
                staging);
            if (residualPath is not null)
            {
                throw new OperationCanceledException(
                    $"Cache migration was cancelled. The incomplete or published copy was preserved at {residualPath}.",
                    exception,
                    cancellationToken);
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            string? residualPath = GetResidualPath(
                destinationPublished,
                destination,
                staging);
            string error = residualPath is null
                ? exception.Message
                : $"{exception.Message} The incomplete or published copy was preserved at {residualPath}.";

            return new CacheMigrationResult(
                false,
                source,
                residualPath,
                true,
                error,
                snapshotPath);
        }
    }

    public async Task<CacheMigrationResult> RollbackAsync(
        string snapshotPath,
        IUserEnvironmentVariableStore environmentStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentNullException.ThrowIfNull(environmentStore);
        if (_managedRoot is null)
        {
            return new CacheMigrationResult(
                false,
                string.Empty,
                null,
                true,
                "Cache migration rollback requires an AutoEnvPlus managed root.");
        }

        string fullSnapshot = Path.GetFullPath(snapshotPath);
        try
        {
            EnsureChildPath(GetSnapshotDirectory(), fullSnapshot);
            if (!File.Exists(fullSnapshot))
            {
                throw new FileNotFoundException(
                    "The cache migration snapshot does not exist.",
                    fullSnapshot);
            }

            CacheMigrationSnapshot? snapshot = JsonSerializer.Deserialize<CacheMigrationSnapshot>(
                await File.ReadAllTextAsync(fullSnapshot, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (!IsValidSnapshot(snapshot, fullSnapshot))
            {
                throw new InvalidDataException("The cache migration snapshot is invalid.");
            }

            CacheMigrationPlan rollbackPlan = new(
                CreateSnapshotLocation(snapshot!),
                snapshot!.DestinationPath,
                snapshot.ConfigurationKind,
                snapshot.ConfigurationTarget,
                true,
                snapshot.ConfigurationTargetExisted,
                snapshot.ConfigurationBefore,
                snapshot.ConfigurationAfter);
            using (ConfigurationMutationLease configurationLease =
                   await AcquireConfigurationLeaseAsync(rollbackPlan, cancellationToken)
                       .ConfigureAwait(false))
            {
                EnsureConfigurationMatchesAfter(rollbackPlan, environmentStore);
                await RestoreConfigurationAsync(
                    rollbackPlan,
                    environmentStore,
                    rollbackPlan.ConfigurationAfter,
                    cancellationToken).ConfigureAwait(false);
            }
            return new CacheMigrationResult(
                true,
                snapshot.SourcePath,
                snapshot.DestinationPath,
                true,
                null,
                fullSnapshot);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or JsonException)
        {
            return new CacheMigrationResult(
                false,
                string.Empty,
                null,
                true,
                exception.Message,
                fullSnapshot);
        }
    }

    private void EnsureConfigurationUnchanged(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            string? current = environmentStore.Get(plan.ConfigurationTarget);
            if (!string.Equals(
                    current,
                    plan.ConfigurationBefore,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{plan.ConfigurationTarget} changed after the migration plan was created; refresh and review the new plan.");
            }

            return;
        }

        (bool exists, string? currentContent) = ReadConfigurationFileState(
            plan.ConfigurationTarget);
        if (exists != plan.ConfigurationTargetExisted
            || !string.Equals(
                currentContent,
                plan.ConfigurationBefore,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(plan.ConfigurationKind switch
            {
                CacheConfigurationKind.PnpmRc =>
                    "The pnpm global config changed after the migration plan was created; refresh and review the new plan.",
                _ =>
                    "Maven settings.xml changed after the migration plan was created; refresh and review the new plan.",
            });
        }
    }

    private static void EnsureConfigurationMatchesAfter(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            if (!string.Equals(
                    environmentStore.Get(plan.ConfigurationTarget),
                    plan.ConfigurationAfter,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{plan.ConfigurationTarget} changed after the migration; automatic rollback would overwrite a newer value.");
            }

            return;
        }

        (bool exists, string? content) = ReadConfigurationFileState(plan.ConfigurationTarget);
        if (!exists
            || !string.Equals(content, plan.ConfigurationAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(plan.ConfigurationKind switch
            {
                CacheConfigurationKind.PnpmRc =>
                    "The pnpm global config changed after the migration; automatic rollback would overwrite newer changes.",
                _ =>
                    "Maven settings.xml changed after the migration; automatic rollback would overwrite newer changes.",
            });
        }
    }

    private async Task ApplyConfigurationAsync(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore,
        CancellationToken cancellationToken)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            bool updated = await environmentStore.CompareExchangeAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationBefore,
                plan.ConfigurationAfter,
                cancellationToken).ConfigureAwait(false);
            if (!updated)
            {
                throw new InvalidOperationException(
                    $"{plan.ConfigurationTarget} changed before configuration commit; the newer value was preserved.");
            }

            if (!string.Equals(
                    environmentStore.Get(plan.ConfigurationTarget),
                    plan.ConfigurationAfter,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"{plan.ConfigurationTarget} did not retain the reviewed migration value.");
            }

            return;
        }

        if (plan.ConfigurationKind == CacheConfigurationKind.PnpmRc)
        {
            await _pnpmConfig.WriteAtomicallyIfUnchangedAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationTargetExisted,
                plan.ConfigurationBefore,
                plan.ConfigurationAfter,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _mavenSettings.WriteAtomicallyIfUnchangedAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationTargetExisted,
                plan.ConfigurationBefore,
                plan.ConfigurationAfter,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RestoreConfigurationAsync(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore,
        string expectedCurrent,
        CancellationToken cancellationToken)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            bool restored = await environmentStore.CompareExchangeAsync(
                plan.ConfigurationTarget,
                expectedCurrent,
                plan.ConfigurationBefore,
                cancellationToken).ConfigureAwait(false);
            if (!restored)
            {
                throw new InvalidOperationException(
                    $"{plan.ConfigurationTarget} changed before compensation; the newer value was preserved.");
            }

            if (!string.Equals(
                    environmentStore.Get(plan.ConfigurationTarget),
                    plan.ConfigurationBefore,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"{plan.ConfigurationTarget} could not be restored to the reviewed value.");
            }

            return;
        }

        if (plan.ConfigurationTargetExisted)
        {
            if (plan.ConfigurationKind == CacheConfigurationKind.PnpmRc)
            {
                await _pnpmConfig.WriteAtomicallyIfUnchangedAsync(
                    plan.ConfigurationTarget,
                    expectedExisted: true,
                    expectedContent: expectedCurrent,
                    content: plan.ConfigurationBefore ?? string.Empty,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _mavenSettings.WriteAtomicallyIfUnchangedAsync(
                    plan.ConfigurationTarget,
                    expectedExisted: true,
                    expectedContent: expectedCurrent,
                    content: plan.ConfigurationBefore ?? string.Empty,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            DeleteConfigurationFileIfUnchanged(plan.ConfigurationTarget, expectedCurrent);
        }
    }

    private async Task RestoreConfigurationAfterFailedApplyAsync(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore)
    {
        if (ConfigurationMatchesBefore(plan, environmentStore))
        {
            return;
        }

        if (!ConfigurationMatchesAfter(plan, environmentStore))
        {
            throw new InvalidOperationException(
                "The configuration changed while migration was being applied; the newer value was preserved.");
        }

        await RestoreConfigurationAsync(
            plan,
            environmentStore,
            plan.ConfigurationAfter,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static bool ConfigurationMatchesBefore(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            return string.Equals(
                environmentStore.Get(plan.ConfigurationTarget),
                plan.ConfigurationBefore,
                StringComparison.Ordinal);
        }

        (bool exists, string? content) = ReadConfigurationFileState(plan.ConfigurationTarget);
        return exists == plan.ConfigurationTargetExisted
            && string.Equals(content, plan.ConfigurationBefore, StringComparison.Ordinal);
    }

    private static bool ConfigurationMatchesAfter(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            return string.Equals(
                environmentStore.Get(plan.ConfigurationTarget),
                plan.ConfigurationAfter,
                StringComparison.Ordinal);
        }

        (bool exists, string? content) = ReadConfigurationFileState(plan.ConfigurationTarget);
        return exists && string.Equals(content, plan.ConfigurationAfter, StringComparison.Ordinal);
    }

    private static (bool Exists, string? Content) ReadConfigurationFileState(string path)
    {
        bool exists = StorageFileSafety.EnsureOrdinaryFileOrMissing(
            path,
            "cache configuration file");
        return (exists, exists ? File.ReadAllText(path) : null);
    }

    private static void DeleteConfigurationFileIfUnchanged(
        string path,
        string expectedCurrent)
    {
        (bool exists, string? content) = ReadConfigurationFileState(path);
        if (!exists || !string.Equals(content, expectedCurrent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The cache configuration changed before compensation; the newer file was preserved.");
        }

        _ = StorageFileSafety.EnsureOrdinaryFileOrMissing(path, "cache configuration file");
        File.Delete(path);
    }

    private Task<ConfigurationMutationLease> AcquireConfigurationLeaseAsync(
        CacheMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        string normalizedTarget = plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable
            ? plan.ConfigurationTarget.ToUpperInvariant()
            : Path.GetFullPath(plan.ConfigurationTarget).ToUpperInvariant();
        string key = $"{plan.ConfigurationKind}\0{normalizedTarget}";
        string lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            + ".lock";
        string lockPath = Path.Combine(_configurationLockRoot, lockName);
        return ConfigurationMutationLease.AcquireAsync(lockPath, cancellationToken);
    }

    private async Task<string?> CreateSnapshotAsync(
        CacheMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        if (_managedRoot is null)
        {
            return null;
        }

        CacheMigrationSnapshot snapshot = new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            plan.Source.Definition.Id,
            plan.ConfigurationKind,
            plan.ConfigurationTarget,
            plan.ConfigurationTargetExisted,
            plan.ConfigurationBefore,
            plan.ConfigurationAfter,
            plan.Source.DirectoryPath,
            plan.DestinationPath);
        string directory = GetSnapshotDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, snapshot.Id + ".json");
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string GetSnapshotDirectory() => Path.Combine(
        _managedRoot!,
        "state",
        "cache-migration-snapshots");

    private bool IsValidSnapshot(
        CacheMigrationSnapshot? snapshot,
        string snapshotPath)
    {
        if (snapshot is null
            || !Guid.TryParseExact(snapshot.Id, "N", out _)
            || !snapshot.Id.Equals(
                Path.GetFileNameWithoutExtension(snapshotPath),
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(snapshot.ConfigurationTarget)
            || string.IsNullOrWhiteSpace(snapshot.CacheId)
            || string.IsNullOrWhiteSpace(snapshot.SourcePath)
            || string.IsNullOrWhiteSpace(snapshot.DestinationPath)
            || !Path.IsPathFullyQualified(snapshot.SourcePath)
            || !Path.IsPathFullyQualified(snapshot.DestinationPath))
        {
            return false;
        }

        CacheDirectoryDefinition? definition = CacheDirectoryService.Definitions.FirstOrDefault(
            candidate => candidate.Id.Equals(snapshot.CacheId, StringComparison.OrdinalIgnoreCase));
        if (definition is null
            || definition.ConfigurationKind != snapshot.ConfigurationKind)
        {
            return false;
        }

        if (snapshot.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            return definition.ConfigurationEnvironmentVariable is not null
                && definition.ConfigurationEnvironmentVariable.Equals(
                    snapshot.ConfigurationTarget,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (!Path.IsPathFullyQualified(snapshot.ConfigurationTarget))
        {
            return false;
        }

        string authorizedPath = snapshot.ConfigurationKind switch
        {
            CacheConfigurationKind.MavenSettingsXml => _authorizedMavenSettingsPath,
            CacheConfigurationKind.PnpmRc => _authorizedPnpmConfigPath,
            _ => string.Empty,
        };
        return authorizedPath.Length > 0
            && Path.GetFullPath(snapshot.ConfigurationTarget).Equals(
                authorizedPath,
                StringComparison.OrdinalIgnoreCase);
    }

    private static CacheDirectoryLocation CreateSnapshotLocation(
        CacheMigrationSnapshot snapshot)
    {
        CacheDirectoryDefinition definition = CacheDirectoryService.Definitions.FirstOrDefault(
            candidate => candidate.Id.Equals(snapshot.CacheId, StringComparison.OrdinalIgnoreCase))
            ?? CacheDirectoryService.Definitions[0];
        return new CacheDirectoryLocation(
            definition,
            snapshot.SourcePath,
            "snapshot",
            Directory.Exists(snapshot.SourcePath));
    }

    private static CacheMigrationSourceManifest CreateSourceManifest(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(sourceRoot);
        EnsureDirectoryNotReparse(root, "cache migration source directory");
        Queue<string> pending = new();
        pending.Enqueue(root);
        List<string> directories = [];
        List<CacheMigrationFileIdentity> files = [];
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Dequeue();
            EnsureDirectoryNotReparse(directory, "cache migration source directory");
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureChildPath(root, entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidDataException(
                        $"Cache migration does not follow reparse points or device entries: {entry}");
                }

                string relativePath = NormalizeRelativePath(Path.GetRelativePath(root, entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relativePath);
                    pending.Enqueue(entry);
                    continue;
                }

                using FileStream stream = new(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81_920,
                    FileOptions.SequentialScan);
                FileAttributes lockedAttributes = File.GetAttributes(entry);
                if ((lockedAttributes & (FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidDataException(
                        $"Cache migration requires ordinary source files: {entry}");
                }

                long length = stream.Length;
                string sha256 = Convert.ToHexString(SHA256.HashData(stream));
                files.Add(new CacheMigrationFileIdentity(relativePath, length, sha256));
                totalBytes = checked(totalBytes + length);
            }
        }

        return new CacheMigrationSourceManifest(
            directories.Order(StringComparer.Ordinal).ToArray(),
            files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            totalBytes);
    }

    private static async Task<CacheMigrationSourceManifest> CopyTreeVerifiedAsync(
        string sourceRoot,
        string destinationRoot,
        long totalBytes,
        IProgress<CacheMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Queue<(string Source, string Destination)> directories = new();
        directories.Enqueue((sourceRoot, destinationRoot));
        List<string> copiedDirectories = [];
        List<CacheMigrationFileIdentity> copiedFiles = [];
        long completedBytes = 0;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string sourceDirectory, string destinationDirectory) = directories.Dequeue();
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         sourceDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Cache migration does not follow reparse points: {entry}");
                }

                string relativePath = NormalizeRelativePath(
                    Path.GetRelativePath(sourceRoot, entry));
                string target = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                EnsureChildPath(destinationRoot, target);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    copiedDirectories.Add(relativePath);
                    directories.Enqueue((entry, target));
                    continue;
                }

                (long copiedBytes, string sha256) = await CopyFileVerifiedAsync(
                    entry,
                    target,
                    cancellationToken).ConfigureAwait(false);
                completedBytes = checked(completedBytes + copiedBytes);
                copiedFiles.Add(new CacheMigrationFileIdentity(
                    relativePath,
                    copiedBytes,
                    sha256));
                progress?.Report(new CacheMigrationProgress(
                    "copy",
                    relativePath,
                    completedBytes,
                    totalBytes));
            }
        }

        return new CacheMigrationSourceManifest(
            copiedDirectories.Order(StringComparer.Ordinal).ToArray(),
            copiedFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            completedBytes);
    }

    private static async Task<(long Length, string Sha256)> CopyFileVerifiedAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using IncrementalHash sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        await using (FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[81_920];
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                sourceHash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytes = checked(bytes + read);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        byte[] expectedHash = sourceHash.GetHashAndReset();
        await using FileStream verification = new(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actualHash = await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException($"SHA-256 verification failed after copying '{source}'.");
        }

        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        return (bytes, Convert.ToHexString(expectedHash));
    }

    private static void EnsureFreeSpace(string destinationParent, long requiredBytes)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(destinationParent));
        if (root is null)
        {
            return;
        }

        DriveInfo drive = new(root);
        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"The destination drive has {drive.AvailableFreeSpace} bytes free, but {requiredBytes} bytes are required.");
        }
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A cache entry escaped the migration destination.");
        }
    }

    private static void EnsureDirectoryNotReparse(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be an ordinary directory: {path}");
        }
    }

    private static bool SourceManifestsEqual(
        CacheMigrationSourceManifest left,
        CacheMigrationSourceManifest right)
    {
        if (left.TotalBytes != right.TotalBytes
            || !left.Directories.SequenceEqual(right.Directories, StringComparer.Ordinal)
            || left.Files.Count != right.Files.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Files.Count; index++)
        {
            CacheMigrationFileIdentity leftFile = left.Files[index];
            CacheMigrationFileIdentity rightFile = right.Files[index];
            if (!leftFile.RelativePath.Equals(rightFile.RelativePath, StringComparison.Ordinal)
                || leftFile.Length != rightFile.Length
                || !leftFile.Sha256.Equals(rightFile.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeRelativePath(string path) => path
        .Replace(Path.DirectorySeparatorChar, '/')
        .Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static bool IsChildPath(string root, string candidate)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetResidualPath(
        bool destinationPublished,
        string destination,
        string? staging)
    {
        if (destinationPublished)
        {
            return destination;
        }

        if (staging is not null
            && (Directory.Exists(staging) || File.Exists(staging)))
        {
            return staging;
        }

        return null;
    }

    private static void TryDeleteCreatedFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class ConfigurationMutationLease : IDisposable
    {
        private const int RetryMilliseconds = 25;
        private const int TimeoutMilliseconds = 5_000;

        private FileStream? _stream;

        private ConfigurationMutationLease(FileStream stream)
        {
            _stream = stream;
        }

        public static async Task<ConfigurationMutationLease> AcquireAsync(
            string lockPath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
            string fullPath = Path.GetFullPath(lockPath);
            _ = StorageFileSafety.PrepareOrdinaryParentForWrite(
                fullPath,
                "cache configuration lock");
            long deadline = System.Environment.TickCount64 + TimeoutMilliseconds;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(
                        fullPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                    _ = StorageFileSafety.EnsureOrdinaryFileOrMissing(
                        fullPath,
                        "cache configuration lock");
                    return new ConfigurationMutationLease(stream);
                }
                catch (IOException) when (System.Environment.TickCount64 < deadline)
                {
                    stream?.Dispose();
                    await Task.Delay(RetryMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    stream?.Dispose();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
    }

}
