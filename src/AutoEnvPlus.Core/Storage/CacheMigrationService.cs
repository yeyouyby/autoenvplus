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

    private readonly CacheDirectoryService _directoryService = new();
    private readonly MavenSettingsXmlService _mavenSettings = new();
    private readonly PnpmRcService _pnpmConfig = new();
    private readonly string? _managedRoot;
    private readonly string _authorizedMavenSettingsPath;
    private readonly string _authorizedPnpmConfigPath;

    public CacheMigrationService(
        string? managedRoot = null,
        string? mavenSettingsPath = null,
        string? pnpmConfigPath = null)
    {
        _managedRoot = managedRoot is null ? null : Path.GetFullPath(managedRoot);
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
                destination);
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
                mutation.After);
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
                mutation.After);
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
        CacheMigrationPlan validated = CreatePlan(plan.Source, plan.DestinationPath);
        string source = validated.Source.DirectoryPath;
        string destination = validated.DestinationPath;
        string destinationParent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The destination requires a parent directory.", nameof(plan));
        Directory.CreateDirectory(destinationParent);
        string staging = destination + $".autoenvplus-{Guid.NewGuid():N}.tmp";
        bool destinationCreated = false;
        bool retainDestinationAfterFailure = false;
        string? snapshotPath = null;

        try
        {
            progress?.Report(new CacheMigrationProgress("measure"));
            CacheDirectoryMeasurement sourceMeasurement = await _directoryService.MeasureAsync(
                validated.Source,
                cancellationToken).ConfigureAwait(false);
            if (sourceMeasurement.Errors.Count > 0)
            {
                throw new IOException(
                    "The source cache could not be measured completely: "
                    + string.Join("; ", sourceMeasurement.Errors));
            }

            EnsureFreeSpace(destinationParent, sourceMeasurement.TotalBytes);
            Directory.CreateDirectory(staging);
            progress?.Report(new CacheMigrationProgress(
                "copy",
                TotalBytes: sourceMeasurement.TotalBytes));
            CopySummary copied = await CopyTreeVerifiedAsync(
                source,
                staging,
                sourceMeasurement.TotalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (copied.FileCount != sourceMeasurement.FileCount
                || copied.TotalBytes != sourceMeasurement.TotalBytes)
            {
                throw new InvalidDataException(
                    "The cache changed during migration; file count or total size no longer matches.");
            }

            progress?.Report(new CacheMigrationProgress("commit"));
            Directory.Move(staging, destination);
            destinationCreated = true;

            progress?.Report(new CacheMigrationProgress("configure"));
            EnsureConfigurationUnchanged(validated, environmentStore);
            snapshotPath = await CreateSnapshotAsync(
                validated,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await ApplyConfigurationAsync(
                    validated,
                    environmentStore,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                bool restored = false;
                try
                {
                    await RestoreConfigurationAsync(
                        validated,
                        environmentStore,
                        CancellationToken.None).ConfigureAwait(false);
                    restored = true;
                }
                catch
                {
                    retainDestinationAfterFailure = true;
                }

                if (restored && snapshotPath is not null)
                {
                    TryDeleteCreatedFile(snapshotPath);
                    snapshotPath = null;
                }

                throw;
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
        catch (OperationCanceledException)
        {
            if (destinationCreated && !retainDestinationAfterFailure)
            {
                TryDeleteCreatedDirectory(destination);
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (destinationCreated && !retainDestinationAfterFailure)
            {
                TryDeleteCreatedDirectory(destination);
            }

            return new CacheMigrationResult(
                false,
                source,
                retainDestinationAfterFailure ? destination : null,
                true,
                exception.Message,
                snapshotPath);
        }
        finally
        {
            TryDeleteCreatedDirectory(staging);
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
            EnsureConfigurationMatchesAfter(rollbackPlan, environmentStore);
            await RestoreConfigurationAsync(
                rollbackPlan,
                environmentStore,
                cancellationToken).ConfigureAwait(false);
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

        bool exists = File.Exists(plan.ConfigurationTarget);
        string? currentContent = exists ? File.ReadAllText(plan.ConfigurationTarget) : null;
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

        if (!File.Exists(plan.ConfigurationTarget)
            || !File.ReadAllText(plan.ConfigurationTarget).Equals(
                plan.ConfigurationAfter,
                StringComparison.Ordinal))
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
            await environmentStore.SetAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationAfter,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (plan.ConfigurationKind == CacheConfigurationKind.PnpmRc)
        {
            await _pnpmConfig.WriteAtomicallyAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationAfter,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _mavenSettings.WriteAtomicallyAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationAfter,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RestoreConfigurationAsync(
        CacheMigrationPlan plan,
        IUserEnvironmentVariableStore environmentStore,
        CancellationToken cancellationToken)
    {
        if (plan.ConfigurationKind == CacheConfigurationKind.EnvironmentVariable)
        {
            await environmentStore.SetAsync(
                plan.ConfigurationTarget,
                plan.ConfigurationBefore,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (plan.ConfigurationTargetExisted)
        {
            if (plan.ConfigurationKind == CacheConfigurationKind.PnpmRc)
            {
                await _pnpmConfig.WriteAtomicallyAsync(
                    plan.ConfigurationTarget,
                    plan.ConfigurationBefore ?? string.Empty,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _mavenSettings.WriteAtomicallyAsync(
                    plan.ConfigurationTarget,
                    plan.ConfigurationBefore ?? string.Empty,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (File.Exists(plan.ConfigurationTarget))
        {
            File.Delete(plan.ConfigurationTarget);
        }
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

    private static async Task<CopySummary> CopyTreeVerifiedAsync(
        string sourceRoot,
        string destinationRoot,
        long totalBytes,
        IProgress<CacheMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Queue<(string Source, string Destination)> directories = new();
        directories.Enqueue((sourceRoot, destinationRoot));
        long completedBytes = 0;
        long fileCount = 0;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string sourceDirectory, string destinationDirectory) = directories.Dequeue();
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                sourceDirectory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Cache migration does not follow reparse points: {entry}");
                }

                string relativePath = Path.GetRelativePath(sourceRoot, entry);
                string target = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                EnsureChildPath(destinationRoot, target);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    directories.Enqueue((entry, target));
                    continue;
                }

                long copiedBytes = await CopyFileVerifiedAsync(
                    entry,
                    target,
                    cancellationToken).ConfigureAwait(false);
                completedBytes = checked(completedBytes + copiedBytes);
                fileCount++;
                progress?.Report(new CacheMigrationProgress(
                    "copy",
                    relativePath,
                    completedBytes,
                    totalBytes));
            }
        }

        return new CopySummary(fileCount, completedBytes);
    }

    private static async Task<long> CopyFileVerifiedAsync(
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
        return bytes;
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

    private static void TryDeleteCreatedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
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

    private sealed record CopySummary(long FileCount, long TotalBytes);
}
