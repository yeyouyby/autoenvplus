using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoEnvPlus.Core.Storage;

public sealed class CacheCleanupService
{
    public const string TrashDirectoryName = ".autoenvplus-cache-trash";

    private const string ManifestFileName = "cleanup.json";
    private const int ManifestSchemaVersion = 1;
    private const long MaximumManifestBytes = 4 * 1024 * 1024;
    private const string MovingState = "moving";
    private const string RecoverableState = "recoverable";
    private const string RestorePendingState = "restore-pending";
    private const string PurgePendingState = "purge-pending";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly object _ownerToken = new();
    private readonly string? _managedRoot;

    public CacheCleanupService(string? managedRoot = null)
    {
        if (managedRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
            if (!Path.IsPathFullyQualified(managedRoot))
            {
                throw new ArgumentException("The managed root must be an absolute path.", nameof(managedRoot));
            }

            _managedRoot = Path.GetFullPath(managedRoot);
        }
    }

    public Task<CacheCleanupPlan> CreatePlanAsync(
        CacheDirectoryLocation source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Task.Run(
            () => CreatePlan(source, cancellationToken),
            cancellationToken);
    }

    public Task<CacheCleanupOperationResult> CleanupAsync(
        CacheCleanupPlan plan,
        IProgress<CacheCleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(
            () => Cleanup(plan, progress, cancellationToken),
            CancellationToken.None);
    }

    public Task<CacheCleanupCatalog> DiscoverItemsAsync(
        IEnumerable<CacheDirectoryLocation> locations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locations);
        CacheDirectoryLocation[] captured = locations.ToArray();
        return Task.Run(
            () => DiscoverItems(captured, cancellationToken),
            cancellationToken);
    }

    public Task<CacheCleanupOperationResult> RestoreAsync(
        CacheCleanupItem item,
        IProgress<CacheCleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(
            () => Restore(item, progress, cancellationToken),
            CancellationToken.None);
    }

    public Task<CacheCleanupOperationResult> PurgeAsync(
        CacheCleanupItem item,
        IProgress<CacheCleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(
            () => Purge(item, progress, cancellationToken),
            CancellationToken.None);
    }

    private CacheCleanupPlan CreatePlan(
        CacheDirectoryLocation source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheDirectoryLocation normalized = ValidateLocation(source);
        using DirectoryMutationLease lease = DirectoryMutationLease.Acquire(
            [normalized.DirectoryPath]);
        Inventory inventory = ScanInventory(normalized.DirectoryPath, cancellationToken);
        if (inventory.TopLevelEntries.Count == 0)
        {
            throw new InvalidOperationException("The cache directory is already empty.");
        }

        string id = Guid.NewGuid().ToString("N");
        string trashRoot = GetTrashRoot(normalized.DirectoryPath);
        ValidateTrashRoot(trashRoot, mustExist: false);
        string trashPath = Path.GetFullPath(Path.Combine(trashRoot, id));
        EnsureDirectChild(trashRoot, trashPath, "cleanup transaction");
        if (Directory.Exists(trashPath) || File.Exists(trashPath))
        {
            throw new IOException($"The cleanup transaction already exists: {trashPath}");
        }

        return new CacheCleanupPlan(
            _ownerToken,
            id,
            DateTimeOffset.UtcNow,
            normalized,
            trashPath,
            inventory.TopLevelEntries,
            inventory.FileCount,
            inventory.TotalBytes);
    }

    private CacheCleanupOperationResult Cleanup(
        CacheCleanupPlan plan,
        IProgress<CacheCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOwned(plan.OwnerToken, "cleanup plan");
        CacheDirectoryLocation source = ValidateLocation(plan.Source);
        string trashRoot = GetTrashRoot(source.DirectoryPath);
        string expectedTrashPath = Path.GetFullPath(Path.Combine(trashRoot, plan.Id));
        if (!PathsEqual(expectedTrashPath, plan.TrashPath))
        {
            throw new InvalidOperationException("The cleanup plan trash identity is invalid.");
        }

        EnsureValidId(plan.Id);
        EnsureDirectChild(trashRoot, expectedTrashPath, "cleanup transaction");
        ValidateTrashRoot(trashRoot, mustExist: false);
        if (Directory.Exists(expectedTrashPath) || File.Exists(expectedTrashPath))
        {
            throw new IOException($"The cleanup transaction already exists: {expectedTrashPath}");
        }

        Inventory current = ScanInventory(source.DirectoryPath, cancellationToken);
        if (!EntriesEqual(plan.Entries, current.TopLevelEntries)
            || plan.FileCount != current.FileCount
            || plan.TotalBytes != current.TotalBytes)
        {
            throw new InvalidOperationException(
                "The cache changed after the cleanup plan was created; refresh and review a new plan.");
        }

        string contentPath = Path.Combine(expectedTrashPath, "content");
        string manifestPath = Path.Combine(expectedTrashPath, ManifestFileName);
        CleanupManifest manifest = CreateManifest(plan, source.DirectoryPath, MovingState, []);
        bool transactionCreated = false;
        DirectoryMutationLease? mutationLease = null;
        try
        {
            mutationLease = DirectoryMutationLease.Acquire([source.DirectoryPath]);
            Directory.CreateDirectory(trashRoot);
            ValidateTrashRoot(trashRoot, mustExist: true);
            mutationLease.AddPath(trashRoot);
            Directory.CreateDirectory(expectedTrashPath);
            EnsureDirectoryNotReparse(expectedTrashPath, "cleanup transaction");
            mutationLease.AddPath(expectedTrashPath);
            transactionCreated = true;
            Directory.CreateDirectory(contentPath);
            EnsureDirectoryNotReparse(contentPath, "cleanup content directory");
            mutationLease.AddPath(contentPath);
            WriteManifest(manifestPath, manifest);

            Inventory lockedCurrent = ScanInventory(source.DirectoryPath, cancellationToken);
            if (!EntriesEqual(plan.Entries, lockedCurrent.TopLevelEntries)
                || plan.FileCount != lockedCurrent.FileCount
                || plan.TotalBytes != lockedCurrent.TotalBytes)
            {
                throw new InvalidOperationException(
                    "The cache changed while its directory path was being locked; refresh and review a new plan.");
            }

            List<string> movedNames = [];
            long completedBytes = 0;
            int completedEntries = 0;
            foreach (CacheCleanupEntryIdentity entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MoveTopLevelEntry(source.DirectoryPath, contentPath, entry.Name);
                movedNames.Add(entry.Name);
                completedBytes = checked(completedBytes + entry.TotalBytes);
                completedEntries++;
                manifest = manifest with { ContentEntryNames = movedNames.ToArray() };
                WriteManifest(manifestPath, manifest);
                progress?.Report(new CacheCleanupProgress(
                    "move",
                    entry.Name,
                    completedEntries,
                    plan.TopLevelEntryCount,
                    completedBytes,
                    plan.TotalBytes));
            }

            Inventory remainingSource = ScanInventory(source.DirectoryPath, CancellationToken.None);
            Inventory isolated = ScanInventory(contentPath, CancellationToken.None);
            if (remainingSource.TopLevelEntries.Count != 0
                || !EntriesEqual(plan.Entries, isolated.TopLevelEntries)
                || isolated.FileCount != plan.FileCount
                || isolated.TotalBytes != plan.TotalBytes)
            {
                throw new InvalidOperationException(
                    "The cache changed while cleanup was running; the operation will be rolled back.");
            }

            manifest = manifest with
            {
                State = RecoverableState,
                ContentEntryNames = isolated.TopLevelEntries.Select(entry => entry.Name).ToArray(),
            };
            WriteManifest(manifestPath, manifest);
            progress?.Report(new CacheCleanupProgress(
                "complete",
                TotalEntries: plan.TopLevelEntryCount,
                CompletedEntries: plan.TopLevelEntryCount,
                CompletedBytes: plan.TotalBytes,
                TotalBytes: plan.TotalBytes));
            return new CacheCleanupOperationResult(
                true,
                false,
                source.DirectoryPath,
                plan.Id,
                manifestPath,
                true,
                false,
                plan.FileCount,
                plan.TotalBytes,
                null);
        }
        catch (Exception exception) when (IsExpectedFileOperation(exception))
        {
            bool cancelled = exception is OperationCanceledException;
            return RollBackInterruptedCleanup(
                source,
                plan,
                trashRoot,
                expectedTrashPath,
                contentPath,
                manifestPath,
                manifest,
                transactionCreated,
                cancelled,
                exception.Message);
        }
        finally
        {
            mutationLease?.Dispose();
        }
    }

    private CacheCleanupOperationResult RollBackInterruptedCleanup(
        CacheDirectoryLocation source,
        CacheCleanupPlan plan,
        string trashRoot,
        string trashPath,
        string contentPath,
        string manifestPath,
        CleanupManifest manifest,
        bool transactionCreated,
        bool cancelled,
        string error)
    {
        if (!transactionCreated || !Directory.Exists(contentPath))
        {
            TryRemoveEmptyTransaction(trashRoot, trashPath, contentPath, manifestPath);
            return FailedResult(source.DirectoryPath, plan.Id, cancelled, false, false, 0, 0, error);
        }

        List<string> rollbackErrors = [];
        try
        {
            Inventory isolated = ScanInventory(contentPath, CancellationToken.None);
            HashSet<string> plannedNames = plan.Entries
                .Select(entry => entry.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (CacheCleanupEntryIdentity entry in isolated.TopLevelEntries.Reverse())
            {
                if (!plannedNames.Contains(entry.Name))
                {
                    rollbackErrors.Add($"Unexpected isolation entry was retained: {entry.Name}");
                    continue;
                }

                string destination = GetDirectChild(source.DirectoryPath, entry.Name);
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    rollbackErrors.Add($"A newer source entry blocked rollback: {entry.Name}");
                    continue;
                }

                try
                {
                    MoveTopLevelEntry(contentPath, source.DirectoryPath, entry.Name);
                }
                catch (Exception exception) when (IsExpectedFileOperation(exception))
                {
                    rollbackErrors.Add($"{entry.Name}: {exception.Message}");
                }
            }

            Inventory remaining = ScanInventory(contentPath, CancellationToken.None);
            if (remaining.TopLevelEntries.Count == 0)
            {
                TryRemoveEmptyTransaction(trashRoot, trashPath, contentPath, manifestPath);
                string message = rollbackErrors.Count == 0
                    ? error
                    : $"{error} Rollback warnings: {string.Join("; ", rollbackErrors)}";
                return FailedResult(
                    source.DirectoryPath,
                    plan.Id,
                    cancelled,
                    false,
                    false,
                    0,
                    0,
                    message);
            }

            manifest = manifest with
            {
                State = RestorePendingState,
                ContentEntryNames = remaining.TopLevelEntries.Select(entry => entry.Name).ToArray(),
            };
            WriteManifest(manifestPath, manifest);
            string retainedMessage = $"{error} Some entries could not be returned and remain recoverable at {trashPath}.";
            if (rollbackErrors.Count > 0)
            {
                retainedMessage += $" Rollback warnings: {string.Join("; ", rollbackErrors)}";
            }

            return FailedResult(
                source.DirectoryPath,
                plan.Id,
                cancelled,
                true,
                false,
                remaining.FileCount,
                remaining.TotalBytes,
                retainedMessage,
                manifestPath);
        }
        catch (Exception rollbackException) when (IsExpectedFileOperation(rollbackException))
        {
            string combined = $"{error} Cleanup rollback could not be completed: {rollbackException.Message}";
            return FailedResult(
                source.DirectoryPath,
                plan.Id,
                cancelled,
                Directory.Exists(contentPath),
                false,
                0,
                0,
                combined,
                File.Exists(manifestPath) ? manifestPath : null);
        }
    }

    private CacheCleanupCatalog DiscoverItems(
        IReadOnlyList<CacheDirectoryLocation> locations,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];
        List<CacheDirectoryLocation> normalizedLocations = [];
        foreach (CacheDirectoryLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetCanonicalDefinition(location.Definition.Id).SupportsSafeCleanup)
            {
                continue;
            }

            try
            {
                normalizedLocations.Add(ValidateLocation(
                    location,
                    requireExistingDirectory: false));
            }
            catch (Exception exception) when (IsExpectedValidationException(exception))
            {
                errors.Add($"{location.Definition.DisplayName}: {exception.Message}");
            }
        }

        Dictionary<string, CacheDirectoryLocation> expected = normalizedLocations.ToDictionary(
            location => GetLocationKey(location.Definition.Id, location.DirectoryPath),
            StringComparer.OrdinalIgnoreCase);
        string[] trashRoots = normalizedLocations
            .Select(location => GetTrashRoot(location.DirectoryPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<CacheCleanupItem> items = [];
        foreach (string trashRoot in trashRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(trashRoot))
            {
                continue;
            }

            try
            {
                ValidateTrashRoot(trashRoot, mustExist: true);
            }
            catch (Exception exception) when (IsExpectedValidationException(exception))
            {
                errors.Add($"{trashRoot}: {exception.Message}");
                continue;
            }

            IEnumerable<string> transactions;
            try
            {
                transactions = Directory.EnumerateDirectories(
                        trashRoot,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{trashRoot}: {exception.Message}");
                continue;
            }

            foreach (string transactionPath in transactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string manifestPath = Path.Combine(transactionPath, ManifestFileName);
                try
                {
                    CleanupManifest manifest = ReadAndValidateManifestIdentity(
                        trashRoot,
                        transactionPath,
                        manifestPath);
                    string locationKey = GetLocationKey(manifest.CacheId, manifest.SourcePath);
                    if (!expected.TryGetValue(locationKey, out CacheDirectoryLocation? location))
                    {
                        throw new InvalidDataException(
                            "The cleanup manifest does not match a currently discovered cache location.");
                    }

                    LoadedCleanupItem loaded = LoadManifest(location, transactionPath, manifestPath, manifest);
                    items.Add(CreateItem(location, transactionPath, manifestPath, loaded));
                }
                catch (Exception exception) when (IsExpectedValidationException(exception))
                {
                    errors.Add($"{manifestPath}: {exception.Message}");
                }
            }
        }

        return new CacheCleanupCatalog(
            items.OrderByDescending(item => item.CreatedAtUtc).ToArray(),
            errors);
    }

    private CacheCleanupOperationResult Restore(
        CacheCleanupItem item,
        IProgress<CacheCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOwned(item.OwnerToken, "cleanup item");
        LoadedCleanupItem loaded;
        try
        {
            loaded = ReloadItem(item);
        }
        catch (Exception exception) when (IsExpectedValidationException(exception))
        {
            return FailedResult(
                item.SourcePath,
                item.Id,
                false,
                true,
                item.State == CacheCleanupItemState.PurgePending,
                item.FileCount,
                item.TotalBytes,
                exception.Message,
                item.ManifestPath);
        }

        if (loaded.State == CacheCleanupItemState.PurgePending)
        {
            return FailedResult(
                item.SourcePath,
                item.Id,
                false,
                true,
                true,
                loaded.Content.FileCount,
                loaded.Content.TotalBytes,
                "Permanent purge has already started for this item; restoration is no longer allowed.",
                item.ManifestPath);
        }

        if (loaded.RestoreBlockedReason is not null)
        {
            return FailedResult(
                item.SourcePath,
                item.Id,
                false,
                true,
                false,
                loaded.Content.FileCount,
                loaded.Content.TotalBytes,
                loaded.RestoreBlockedReason,
                item.ManifestPath);
        }

        CleanupManifest manifest = loaded.Manifest with
        {
            State = RestorePendingState,
            ContentEntryNames = loaded.Content.TopLevelEntries.Select(entry => entry.Name).ToArray(),
        };
        DirectoryMutationLease? mutationLease = null;
        try
        {
            mutationLease = DirectoryMutationLease.Acquire(
                [item.SourcePath, loaded.ContentPath]);
            WriteManifest(item.ManifestPath, manifest);
            List<CacheCleanupEntryIdentity> remaining = loaded.Content.TopLevelEntries.ToList();
            long completedBytes = 0;
            int completedEntries = 0;
            foreach (CacheCleanupEntryIdentity entry in loaded.Content.TopLevelEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = GetDirectChild(item.SourcePath, entry.Name);
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw new IOException(
                        $"A newer cache entry blocks restore and will not be overwritten: {destination}");
                }

                MoveTopLevelEntry(loaded.ContentPath, item.SourcePath, entry.Name);
                remaining.RemoveAll(candidate => candidate.Name.Equals(
                    entry.Name,
                    StringComparison.OrdinalIgnoreCase));
                completedBytes = checked(completedBytes + entry.TotalBytes);
                completedEntries++;
                manifest = manifest with
                {
                    ContentEntryNames = remaining.Select(candidate => candidate.Name).ToArray(),
                };
                WriteManifest(item.ManifestPath, manifest);
                progress?.Report(new CacheCleanupProgress(
                    "restore",
                    entry.Name,
                    completedEntries,
                    loaded.Content.TopLevelEntries.Count,
                    completedBytes,
                    loaded.Content.TotalBytes));
            }

            Inventory restoredSource = ScanInventory(item.SourcePath, CancellationToken.None);
            Inventory remainingContent = ScanInventory(loaded.ContentPath, CancellationToken.None);
            ValidateRecoverableInventories(
                manifest.PlannedEntries,
                remainingContent,
                restoredSource);
            if (remainingContent.TopLevelEntries.Count != 0)
            {
                throw new InvalidOperationException("Some isolated entries remain after restore.");
            }

            TryRemoveEmptyTransaction(
                loaded.TrashRoot,
                item.TrashPath,
                loaded.ContentPath,
                item.ManifestPath,
                throwOnFailure: true);
            progress?.Report(new CacheCleanupProgress(
                "restore-complete",
                TotalEntries: loaded.Manifest.PlannedEntries.Count,
                CompletedEntries: loaded.Manifest.PlannedEntries.Count,
                CompletedBytes: loaded.Manifest.OriginalTotalBytes,
                TotalBytes: loaded.Manifest.OriginalTotalBytes));
            return new CacheCleanupOperationResult(
                true,
                false,
                item.SourcePath,
                item.Id,
                null,
                false,
                false,
                loaded.Manifest.OriginalFileCount,
                loaded.Manifest.OriginalTotalBytes,
                null);
        }
        catch (Exception exception) when (IsExpectedFileOperation(exception))
        {
            return PreserveInterruptedOperation(
                item,
                loaded,
                manifest,
                RestorePendingState,
                exception is OperationCanceledException,
                exception.Message);
        }
        finally
        {
            mutationLease?.Dispose();
        }
    }

    private CacheCleanupOperationResult Purge(
        CacheCleanupItem item,
        IProgress<CacheCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureOwned(item.OwnerToken, "cleanup item");
        LoadedCleanupItem loaded;
        try
        {
            loaded = ReloadItem(item, requireExistingSourceDirectory: false);
        }
        catch (Exception exception) when (IsExpectedValidationException(exception))
        {
            return FailedResult(
                item.SourcePath,
                item.Id,
                false,
                item.State != CacheCleanupItemState.PurgePending,
                item.State == CacheCleanupItemState.PurgePending,
                item.FileCount,
                item.TotalBytes,
                exception.Message,
                item.ManifestPath);
        }

        CleanupManifest manifest = loaded.Manifest with
        {
            State = PurgePendingState,
            ContentEntryNames = loaded.Content.TopLevelEntries.Select(entry => entry.Name).ToArray(),
        };
        DirectoryMutationLease? mutationLease = null;
        bool purgeStarted = loaded.State == CacheCleanupItemState.PurgePending;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> purgeDirectories = Directory.Exists(loaded.ContentPath)
                ? loaded.Content.Nodes
                    .Where(node => node.IsDirectory)
                    .Select(node => node.FullPath)
                    .Prepend(loaded.ContentPath)
                : [item.TrashPath];
            mutationLease = DirectoryMutationLease.Acquire(
                purgeDirectories);
            cancellationToken.ThrowIfCancellationRequested();
            WriteManifest(item.ManifestPath, manifest);
            purgeStarted = true;
            int totalFiles = loaded.Content.Nodes.Count(node => !node.IsDirectory);
            int deletedFiles = 0;
            long deletedBytes = 0;
            foreach (InventoryNode file in loaded.Content.Nodes
                         .Where(node => !node.IsDirectory)
                         .OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureChildPath(loaded.ContentPath, file.FullPath, "purge entry");
                FileAttributes attributes = File.GetAttributes(file.FullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Permanent purge refuses reparse points: {file.FullPath}");
                }

                File.Delete(file.FullPath);
                deletedFiles++;
                deletedBytes = checked(deletedBytes + file.Length);
                progress?.Report(new CacheCleanupProgress(
                    "purge",
                    file.RelativePath,
                    deletedFiles,
                    totalFiles,
                    deletedBytes,
                    loaded.Content.TotalBytes));
            }

            foreach (InventoryNode directory in loaded.Content.Nodes
                         .Where(node => node.IsDirectory)
                         .OrderByDescending(node => GetPathDepth(node.RelativePath))
                         .ThenByDescending(node => node.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureChildPath(loaded.ContentPath, directory.FullPath, "purge directory");
                FileAttributes attributes = File.GetAttributes(directory.FullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Permanent purge refuses reparse points: {directory.FullPath}");
                }

                mutationLease.Release(directory.FullPath);
                Directory.Delete(directory.FullPath, recursive: false);
            }

            Inventory remaining = Directory.Exists(loaded.ContentPath)
                ? ScanInventory(loaded.ContentPath, CancellationToken.None)
                : Inventory.Empty;
            if (remaining.TopLevelEntries.Count != 0)
            {
                throw new IOException("The isolation directory was not empty after permanent purge.");
            }

            CompletePurgedTransaction(
                loaded.TrashRoot,
                item.TrashPath,
                loaded.ContentPath,
                item.ManifestPath,
                mutationLease);
            progress?.Report(new CacheCleanupProgress(
                "purge-complete",
                TotalEntries: totalFiles,
                CompletedEntries: totalFiles,
                CompletedBytes: loaded.Content.TotalBytes,
                TotalBytes: loaded.Content.TotalBytes));
            return new CacheCleanupOperationResult(
                true,
                false,
                item.SourcePath,
                item.Id,
                null,
                false,
                false,
                loaded.Content.FileCount,
                loaded.Content.TotalBytes,
                null);
        }
        catch (Exception exception) when (IsExpectedFileOperation(exception))
        {
            if (!purgeStarted)
            {
                return FailedResult(
                    item.SourcePath,
                    item.Id,
                    exception is OperationCanceledException,
                    true,
                    false,
                    loaded.Content.FileCount,
                    loaded.Content.TotalBytes,
                    exception.Message,
                    item.ManifestPath);
            }

            return PreserveInterruptedOperation(
                item,
                loaded,
                manifest,
                PurgePendingState,
                exception is OperationCanceledException,
                exception.Message);
        }
        finally
        {
            mutationLease?.Dispose();
        }
    }

    private CacheCleanupOperationResult PreserveInterruptedOperation(
        CacheCleanupItem item,
        LoadedCleanupItem loaded,
        CleanupManifest manifest,
        string state,
        bool cancelled,
        string error)
    {
        try
        {
            Inventory remaining = Directory.Exists(loaded.ContentPath)
                ? ScanInventory(loaded.ContentPath, CancellationToken.None)
                : Inventory.Empty;
            CleanupManifest updated = manifest with
            {
                State = state,
                ContentEntryNames = remaining.TopLevelEntries.Select(entry => entry.Name).ToArray(),
            };
            WriteManifest(item.ManifestPath, updated);
            return FailedResult(
                item.SourcePath,
                item.Id,
                cancelled,
                state != PurgePendingState,
                state == PurgePendingState,
                remaining.FileCount,
                remaining.TotalBytes,
                error,
                item.ManifestPath);
        }
        catch (Exception preserveException) when (IsExpectedFileOperation(preserveException))
        {
            return FailedResult(
                item.SourcePath,
                item.Id,
                cancelled,
                state != PurgePendingState && Directory.Exists(loaded.ContentPath),
                state == PurgePendingState,
                0,
                0,
                $"{error} The isolation state could not be refreshed: {preserveException.Message}",
                File.Exists(item.ManifestPath) ? item.ManifestPath : null);
        }
    }

    private LoadedCleanupItem ReloadItem(
        CacheCleanupItem item,
        bool requireExistingSourceDirectory = true)
    {
        CacheDirectoryLocation location = ValidateLocation(
            item.Location,
            requireExistingSourceDirectory);
        string trashRoot = GetTrashRoot(location.DirectoryPath);
        ValidateTrashRoot(trashRoot, mustExist: true);
        EnsureDirectChild(trashRoot, item.TrashPath, "cleanup transaction");
        if (!Path.GetFileName(item.TrashPath).Equals(item.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected cleanup item identity is invalid.");
        }

        string expectedManifestPath = Path.Combine(item.TrashPath, ManifestFileName);
        if (!PathsEqual(expectedManifestPath, item.ManifestPath))
        {
            throw new InvalidDataException("The selected cleanup manifest path is invalid.");
        }

        CleanupManifest manifest = ReadAndValidateManifestIdentity(
            trashRoot,
            item.TrashPath,
            item.ManifestPath);
        if (!manifest.Id.Equals(item.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cleanup manifest identity changed after discovery.");
        }

        return LoadManifest(location, item.TrashPath, item.ManifestPath, manifest);
    }

    private LoadedCleanupItem LoadManifest(
        CacheDirectoryLocation location,
        string transactionPath,
        string manifestPath,
        CleanupManifest manifest)
    {
        if (!manifest.CacheId.Equals(location.Definition.Id, StringComparison.Ordinal)
            || !PathsEqual(manifest.SourcePath, location.DirectoryPath))
        {
            throw new InvalidDataException(
                "The cleanup manifest cache identity does not match the discovered cache.");
        }

        ValidateManifestEntries(manifest);
        string contentPath = Path.Combine(transactionPath, "content");
        Inventory content;
        if (Directory.Exists(contentPath))
        {
            EnsureDirectoryNotReparse(contentPath, "cleanup content directory");
            content = ScanInventory(contentPath, CancellationToken.None);
        }
        else if (manifest.State.Equals(PurgePendingState, StringComparison.Ordinal))
        {
            content = Inventory.Empty;
        }
        else
        {
            throw new InvalidDataException("The cleanup content directory is missing.");
        }

        CacheCleanupItemState state;
        string? restoreBlockedReason = null;
        if (manifest.State.Equals(PurgePendingState, StringComparison.Ordinal))
        {
            state = CacheCleanupItemState.PurgePending;
            ValidatePurgeInventory(manifest.PlannedEntries, content);
        }
        else if (manifest.State.Equals(RecoverableState, StringComparison.Ordinal))
        {
            state = CacheCleanupItemState.Recoverable;
            ValidateRecoverableContent(manifest.PlannedEntries, content);
            restoreBlockedReason = GetRestoreBlockedReason(
                manifest.PlannedEntries,
                content,
                location.DirectoryPath);
        }
        else if (manifest.State.Equals(MovingState, StringComparison.Ordinal)
                 || manifest.State.Equals(RestorePendingState, StringComparison.Ordinal))
        {
            state = CacheCleanupItemState.RestorePending;
            ValidateRecoverableContent(manifest.PlannedEntries, content);
            restoreBlockedReason = GetRestoreBlockedReason(
                manifest.PlannedEntries,
                content,
                location.DirectoryPath);
        }
        else
        {
            throw new InvalidDataException($"Unknown cleanup state: {manifest.State}");
        }

        return new LoadedCleanupItem(
            manifest,
            GetTrashRoot(location.DirectoryPath),
            contentPath,
            content,
            state,
            restoreBlockedReason);
    }

    private static void ValidateRecoverableContent(
        IReadOnlyList<CacheCleanupEntryIdentity> planned,
        Inventory content)
    {
        Dictionary<string, CacheCleanupEntryIdentity> expected = planned.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
        foreach (CacheCleanupEntryIdentity entry in content.TopLevelEntries)
        {
            if (!expected.TryGetValue(entry.Name, out CacheCleanupEntryIdentity? plannedEntry)
                || !EntryEquals(entry, plannedEntry))
            {
                throw new InvalidDataException(
                    $"The isolated cache entry changed after cleanup: {entry.Name}");
            }
        }
    }

    private static string? GetRestoreBlockedReason(
        IReadOnlyList<CacheCleanupEntryIdentity> planned,
        Inventory content,
        string sourcePath)
    {
        try
        {
            Inventory source = ScanInventory(sourcePath, CancellationToken.None);
            ValidateRecoverableInventories(planned, content, source);
            return null;
        }
        catch (Exception exception) when (IsExpectedValidationException(exception))
        {
            return $"Restore is blocked because the cache source is no longer unchanged: {exception.Message}";
        }
    }

    private static void ValidateRecoverableInventories(
        IReadOnlyList<CacheCleanupEntryIdentity> planned,
        Inventory content,
        Inventory source)
    {
        Dictionary<string, CacheCleanupEntryIdentity> expected = planned.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> observed = new(StringComparer.OrdinalIgnoreCase);
        foreach (CacheCleanupEntryIdentity entry in content.TopLevelEntries)
        {
            if (!expected.TryGetValue(entry.Name, out CacheCleanupEntryIdentity? plannedEntry)
                || !EntryEquals(entry, plannedEntry))
            {
                throw new InvalidDataException(
                    $"The isolated cache entry changed after cleanup: {entry.Name}");
            }

            observed.Add(entry.Name);
        }

        foreach (CacheCleanupEntryIdentity entry in source.TopLevelEntries)
        {
            if (!expected.TryGetValue(entry.Name, out CacheCleanupEntryIdentity? plannedEntry)
                || !EntryEquals(entry, plannedEntry))
            {
                throw new InvalidDataException(
                    $"The cache contains newer data and cannot be restored safely: {entry.Name}");
            }

            if (!observed.Add(entry.Name))
            {
                throw new InvalidDataException(
                    $"The same cache entry exists in both the source and isolation directories: {entry.Name}");
            }
        }

        if (observed.Count != expected.Count)
        {
            throw new InvalidDataException(
                "The cleanup inventory is incomplete; restoration was refused.");
        }
    }

    private static void ValidatePurgeInventory(
        IReadOnlyList<CacheCleanupEntryIdentity> planned,
        Inventory content)
    {
        HashSet<string> expected = planned
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (CacheCleanupEntryIdentity entry in content.TopLevelEntries)
        {
            if (!expected.Contains(entry.Name))
            {
                throw new InvalidDataException(
                    $"An unexpected top-level entry appeared in the isolation directory: {entry.Name}");
            }
        }
    }

    private CacheCleanupItem CreateItem(
        CacheDirectoryLocation location,
        string transactionPath,
        string manifestPath,
        LoadedCleanupItem loaded) => new(
            _ownerToken,
            location,
            loaded.Manifest.Id,
            loaded.Manifest.CreatedAtUtc,
            manifestPath,
            transactionPath,
            loaded.State,
            loaded.RestoreBlockedReason,
            loaded.Content.FileCount,
            loaded.Content.TotalBytes);

    private CacheDirectoryLocation ValidateLocation(
        CacheDirectoryLocation location,
        bool requireExistingDirectory = true)
    {
        CacheDirectoryDefinition definition = GetCanonicalDefinition(location.Definition.Id);
        if (!definition.SupportsSafeCleanup)
        {
            throw new NotSupportedException(
                $"{definition.DisplayName} contains configuration or other non-cache data and cannot be cleaned as a whole directory.");
        }

        if (!Path.IsPathFullyQualified(location.DirectoryPath))
        {
            throw new ArgumentException("The cache directory must be an absolute path.");
        }

        string sourcePath = Path.GetFullPath(location.DirectoryPath);
        EnsureNotFileSystemRoot(sourcePath);
        EnsureNotInsideTrash(sourcePath);
        bool sourceExists = Directory.Exists(sourcePath);
        if (!sourceExists && File.Exists(sourcePath))
        {
            throw new InvalidDataException($"The cache path is not a directory: {sourcePath}");
        }

        if (!sourceExists && requireExistingDirectory)
        {
            throw new DirectoryNotFoundException($"Cache directory does not exist: {sourcePath}");
        }

        if (sourceExists)
        {
            EnsureDirectoryNotReparse(sourcePath, "cache directory");
        }
        if (!string.IsNullOrWhiteSpace(location.ConfigurationFilePath))
        {
            if (!Path.IsPathFullyQualified(location.ConfigurationFilePath))
            {
                throw new InvalidDataException("The cache configuration file path must be absolute.");
            }

            string configurationPath = Path.GetFullPath(location.ConfigurationFilePath);
            if (PathsEqual(sourcePath, configurationPath)
                || IsChildPath(sourcePath, configurationPath))
            {
                throw new InvalidDataException(
                    "The cache directory contains its configuration file and cannot be safely cleaned.");
            }
        }

        if (_managedRoot is not null
            && (PathsEqual(sourcePath, _managedRoot)
                || IsChildPath(sourcePath, _managedRoot)
                || IsChildPath(_managedRoot, sourcePath)))
        {
            throw new InvalidDataException(
                "The cache directory overlaps the AutoEnvPlus managed root and cannot be cleaned.");
        }

        EnsureDoesNotContainProtectedSystemLocation(sourcePath);

        return location with
        {
            Definition = definition,
            DirectoryPath = sourcePath,
            Exists = sourceExists,
        };
    }

    private static CacheDirectoryDefinition GetCanonicalDefinition(string id) =>
        CacheDirectoryService.Definitions.FirstOrDefault(definition =>
            definition.Id.Equals(id, StringComparison.Ordinal))
        ?? throw new NotSupportedException($"Unknown cache definition: {id}");

    private static CleanupManifest CreateManifest(
        CacheCleanupPlan plan,
        string sourcePath,
        string state,
        IReadOnlyList<string> contentEntryNames) => new(
            ManifestSchemaVersion,
            plan.Id,
            plan.CreatedAtUtc,
            plan.Source.Definition.Id,
            sourcePath,
            state,
            plan.Entries,
            contentEntryNames,
            plan.FileCount,
            plan.TotalBytes);

    private static CleanupManifest ReadAndValidateManifestIdentity(
        string trashRoot,
        string transactionPath,
        string manifestPath)
    {
        EnsureDirectChild(trashRoot, transactionPath, "cleanup transaction");
        EnsureDirectoryNotReparse(transactionPath, "cleanup transaction");
        string directoryId = Path.GetFileName(Path.TrimEndingDirectorySeparator(transactionPath));
        EnsureValidId(directoryId);
        string expectedManifest = Path.Combine(transactionPath, ManifestFileName);
        if (!PathsEqual(expectedManifest, manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidDataException("The cleanup manifest is missing or escaped its transaction directory.");
        }

        FileAttributes attributes = File.GetAttributes(manifestPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The cleanup manifest cannot be a reparse point.");
        }

        FileInfo manifestInfo = new(manifestPath);
        if (manifestInfo.Length <= 0 || manifestInfo.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The cleanup manifest has an invalid size.");
        }

        CleanupManifest? manifest;
        using (FileStream stream = new(
                   manifestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   16_384,
                   FileOptions.SequentialScan))
        {
            manifest = JsonSerializer.Deserialize<CleanupManifest>(stream, JsonOptions);
        }

        if (manifest is null
            || manifest.SchemaVersion != ManifestSchemaVersion
            || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.CacheId)
            || string.IsNullOrWhiteSpace(manifest.SourcePath)
            || string.IsNullOrWhiteSpace(manifest.State)
            || !string.Equals(manifest.Id, directoryId, StringComparison.Ordinal)
            || manifest.CreatedAtUtc == default)
        {
            throw new InvalidDataException("The cleanup manifest identity is invalid.");
        }

        EnsureValidId(manifest.Id);
        return manifest;
    }

    private static void ValidateManifestEntries(CleanupManifest manifest)
    {
        if (manifest.PlannedEntries is null
            || manifest.ContentEntryNames is null
            || manifest.PlannedEntries.Count == 0
            || manifest.OriginalFileCount < 0
            || manifest.OriginalTotalBytes < 0)
        {
            throw new InvalidDataException("The cleanup manifest inventory is invalid.");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        long fileCount = 0;
        long totalBytes = 0;
        foreach (CacheCleanupEntryIdentity entry in manifest.PlannedEntries)
        {
            if (entry is null)
            {
                throw new InvalidDataException("The cleanup manifest contains a null entry.");
            }

            EnsureSimpleName(entry.Name);
            if (!names.Add(entry.Name)
                || entry.FileCount < 0
                || entry.TotalBytes < 0
                || string.IsNullOrWhiteSpace(entry.Fingerprint)
                || entry.Fingerprint.Length != 64
                || entry.Fingerprint.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("The cleanup manifest contains an invalid entry.");
            }

            fileCount = checked(fileCount + entry.FileCount);
            totalBytes = checked(totalBytes + entry.TotalBytes);
        }

        if (fileCount != manifest.OriginalFileCount || totalBytes != manifest.OriginalTotalBytes)
        {
            throw new InvalidDataException("The cleanup manifest totals do not match its entries.");
        }

        HashSet<string> contentNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in manifest.ContentEntryNames)
        {
            EnsureSimpleName(name);
            if (!contentNames.Add(name) || !names.Contains(name))
            {
                throw new InvalidDataException("The cleanup manifest content list is invalid.");
            }
        }

        if (!Path.IsPathFullyQualified(manifest.SourcePath))
        {
            throw new InvalidDataException("The cleanup manifest source path is not absolute.");
        }
    }

    private static void WriteManifest(string manifestPath, CleanupManifest manifest)
    {
        string transactionPath = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The cleanup manifest directory is invalid.");
        EnsureDirectoryNotReparse(transactionPath, "cleanup transaction");
        string temporaryPath = Path.Combine(
            transactionPath,
            $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        EnsureDirectChild(transactionPath, temporaryPath, "temporary cleanup manifest");
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16_384,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, manifest, JsonOptions);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumManifestBytes)
                {
                    throw new InvalidDataException("The cleanup manifest is too large.");
                }
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static Inventory ScanInventory(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectoryNotReparse(root, "inventory root");
        string[] topLevelPaths = Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<CacheCleanupEntryIdentity> topLevelEntries = [];
        List<InventoryNode> allNodes = [];
        foreach (string topLevelPath in topLevelPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EntryInventory entry = ScanTopLevelEntry(root, topLevelPath, cancellationToken);
            topLevelEntries.Add(entry.Identity);
            allNodes.AddRange(entry.Nodes);
        }

        long fileCount = 0;
        long totalBytes = 0;
        foreach (CacheCleanupEntryIdentity entry in topLevelEntries)
        {
            fileCount = checked(fileCount + entry.FileCount);
            totalBytes = checked(totalBytes + entry.TotalBytes);
        }

        return new Inventory(topLevelEntries, allNodes, fileCount, totalBytes);
    }

    private static EntryInventory ScanTopLevelEntry(
        string root,
        string topLevelPath,
        CancellationToken cancellationToken)
    {
        EnsureChildPath(root, topLevelPath, "cache entry");
        string topLevelName = Path.GetFileName(Path.TrimEndingDirectorySeparator(topLevelPath));
        EnsureSimpleName(topLevelName);
        Stack<string> pending = new();
        pending.Push(topLevelPath);
        List<InventoryNode> nodes = [];
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = pending.Pop();
            EnsureChildPath(root, path, "cache entry");
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals(
                    TrashDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"A cache directory contains an AutoEnvPlus isolation root and cannot be cleaned: {path}");
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Cache cleanup refuses reparse points: {path}");
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            string relativePath = Path.GetRelativePath(root, path);
            if (relativePath.Equals(".", StringComparison.Ordinal)
                || Path.IsPathFullyQualified(relativePath)
                || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A cache entry escaped its source directory.");
            }

            long length = isDirectory ? 0 : new FileInfo(path).Length;
            long lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
            nodes.Add(new InventoryNode(
                path,
                NormalizeRelativePath(relativePath),
                isDirectory,
                length,
                lastWriteTicks));
            if (isDirectory)
            {
                string[] children = Directory.EnumerateFileSystemEntries(
                        path,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .OrderDescending(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (string child in children)
                {
                    pending.Push(child);
                }
            }
        }

        InventoryNode[] ordered = nodes
            .OrderBy(node => node.RelativePath, StringComparer.Ordinal)
            .ToArray();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long fileCount = 0;
        long totalBytes = 0;
        foreach (InventoryNode node in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fingerprintLine = string.Join(
                '\n',
                node.IsDirectory ? "D" : "F",
                node.RelativePath.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.RelativePath,
                node.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.LastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(fingerprintLine));
            if (!node.IsDirectory)
            {
                fileCount++;
                totalBytes = checked(totalBytes + node.Length);
            }
        }

        CacheCleanupEntryIdentity identity = new(
            topLevelName,
            ordered[0].IsDirectory,
            fileCount,
            totalBytes,
            Convert.ToHexString(hash.GetHashAndReset()));
        return new EntryInventory(identity, ordered);
    }

    private static void MoveTopLevelEntry(string sourceRoot, string destinationRoot, string name)
    {
        string source = GetDirectChild(sourceRoot, name);
        string destination = GetDirectChild(destinationRoot, name);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"The destination entry already exists: {destination}");
        }

        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Cache cleanup refuses reparse points: {source}");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static bool EntriesEqual(
        IReadOnlyList<CacheCleanupEntryIdentity> left,
        IReadOnlyList<CacheCleanupEntryIdentity> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        Dictionary<string, CacheCleanupEntryIdentity> rightByName = right.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
        return left.All(entry => rightByName.TryGetValue(entry.Name, out CacheCleanupEntryIdentity? other)
            && EntryEquals(entry, other));
    }

    private static bool EntryEquals(
        CacheCleanupEntryIdentity left,
        CacheCleanupEntryIdentity right) =>
        left.Name.Equals(right.Name, StringComparison.Ordinal)
        && left.IsDirectory == right.IsDirectory
        && left.FileCount == right.FileCount
        && left.TotalBytes == right.TotalBytes
        && left.Fingerprint.Equals(right.Fingerprint, StringComparison.Ordinal);

    private static string GetTrashRoot(string sourcePath)
    {
        string parent = Directory.GetParent(Path.GetFullPath(sourcePath))?.FullName
            ?? throw new InvalidDataException("The cache directory does not have a safe parent directory.");
        string trashRoot = Path.GetFullPath(Path.Combine(parent, TrashDirectoryName));
        string sourceRoot = Path.GetPathRoot(sourcePath)
            ?? throw new InvalidDataException("The cache volume could not be determined.");
        string trashVolume = Path.GetPathRoot(trashRoot)
            ?? throw new InvalidDataException("The isolation volume could not be determined.");
        if (!sourceRoot.Equals(trashVolume, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cache cleanup isolation must remain on the source volume.");
        }

        if (PathsEqual(sourcePath, trashRoot))
        {
            throw new InvalidDataException("A cache trash directory cannot clean itself.");
        }

        return trashRoot;
    }

    private static void ValidateTrashRoot(string trashRoot, bool mustExist)
    {
        if (File.Exists(trashRoot))
        {
            throw new InvalidDataException("The cache trash root is a file.");
        }

        if (!Directory.Exists(trashRoot))
        {
            if (mustExist)
            {
                throw new DirectoryNotFoundException($"Cache trash root does not exist: {trashRoot}");
            }

            return;
        }

        EnsureDirectoryNotReparse(trashRoot, "cache trash root");
    }

    private static void EnsureDirectoryNotReparse(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException($"The {description} is not a directory.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {description} cannot be a reparse point: {path}");
        }
    }

    private static void EnsureNotFileSystemRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root is null || PathsEqual(root, path))
        {
            throw new InvalidDataException("A file-system root cannot be used as a cache cleanup target.");
        }
    }

    private static void EnsureNotInsideTrash(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Name.Equals(TrashDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Cache cleanup cannot target an isolation directory.");
            }

            current = current.Parent;
        }
    }

    private static void EnsureDoesNotContainProtectedSystemLocation(string sourcePath)
    {
        System.Environment.SpecialFolder[] protectedFolders =
        [
            System.Environment.SpecialFolder.UserProfile,
            System.Environment.SpecialFolder.LocalApplicationData,
            System.Environment.SpecialFolder.ApplicationData,
            System.Environment.SpecialFolder.Windows,
            System.Environment.SpecialFolder.ProgramFiles,
            System.Environment.SpecialFolder.ProgramFilesX86,
            System.Environment.SpecialFolder.CommonProgramFiles,
            System.Environment.SpecialFolder.CommonProgramFilesX86,
        ];
        foreach (System.Environment.SpecialFolder folder in protectedFolders)
        {
            string protectedPath = System.Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(protectedPath))
            {
                continue;
            }

            if (PathsEqual(sourcePath, protectedPath)
                || IsChildPath(sourcePath, protectedPath))
            {
                throw new InvalidDataException(
                    $"The cache directory equals or contains a protected system location: {protectedPath}");
            }
        }
    }

    private static void EnsureValidId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("The cleanup transaction id is invalid.");
        }
    }

    private static void EnsureSimpleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Equals(".", StringComparison.Ordinal)
            || name.Equals("..", StringComparison.Ordinal)
            || !Path.GetFileName(name).Equals(name, StringComparison.Ordinal)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("A cleanup entry name is invalid.");
        }
    }

    private static string GetDirectChild(string root, string name)
    {
        EnsureSimpleName(name);
        string path = Path.GetFullPath(Path.Combine(root, name));
        EnsureDirectChild(root, path, "cache entry");
        return path;
    }

    private static void EnsureDirectChild(string root, string candidate, string description)
    {
        EnsureChildPath(root, candidate, description);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate));
        if (parent is null || !PathsEqual(root, parent))
        {
            throw new InvalidDataException($"The {description} must be a direct child of its trusted root.");
        }
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {description} escaped its trusted root.");
        }
    }

    private static bool IsChildPath(string root, string candidate)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string GetLocationKey(string cacheId, string sourcePath) =>
        $"{cacheId}\0{Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}";

    private static int GetPathDepth(string relativePath) =>
        relativePath.Count(character => character == '/' || character == '\\');

    private void EnsureOwned(object token, string description)
    {
        if (!ReferenceEquals(token, _ownerToken))
        {
            throw new InvalidOperationException(
                $"The {description} was not created or discovered by this cleanup service instance.");
        }
    }

    private static void TryRemoveEmptyTransaction(
        string trashRoot,
        string transactionPath,
        string contentPath,
        string manifestPath,
        bool throwOnFailure = false)
    {
        try
        {
            ValidateTrashRoot(trashRoot, mustExist: Directory.Exists(trashRoot));
            if (Directory.Exists(transactionPath))
            {
                EnsureDirectChild(trashRoot, transactionPath, "cleanup transaction");
                EnsureDirectoryNotReparse(transactionPath, "cleanup transaction");
            }

            if (Directory.Exists(contentPath))
            {
                EnsureDirectoryNotReparse(contentPath, "cleanup content directory");
                if (Directory.EnumerateFileSystemEntries(contentPath).Any())
                {
                    throw new IOException("The cleanup content directory is not empty.");
                }

                Directory.Delete(contentPath, recursive: false);
            }

            TryDeleteFile(manifestPath);
            if (Directory.Exists(transactionPath))
            {
                foreach (string temporaryManifest in Directory.EnumerateFiles(
                             transactionPath,
                             $"{ManifestFileName}.*.tmp",
                             SearchOption.TopDirectoryOnly))
                {
                    EnsureDirectChild(transactionPath, temporaryManifest, "temporary cleanup manifest");
                    TryDeleteFile(temporaryManifest);
                }

                if (Directory.EnumerateFileSystemEntries(transactionPath).Any())
                {
                    throw new IOException("The cleanup transaction directory is not empty.");
                }

                Directory.Delete(transactionPath, recursive: false);
            }

            if (Directory.Exists(trashRoot))
            {
                EnsureDirectoryNotReparse(trashRoot, "cache trash root");
                if (!Directory.EnumerateFileSystemEntries(trashRoot).Any())
                {
                    Directory.Delete(trashRoot, recursive: false);
                }
            }
        }
        catch (Exception exception) when (!throwOnFailure && IsExpectedFileOperation(exception))
        {
        }
    }

    private static void CompletePurgedTransaction(
        string trashRoot,
        string transactionPath,
        string contentPath,
        string manifestPath,
        DirectoryMutationLease mutationLease)
    {
        EnsureDirectChild(trashRoot, transactionPath, "cleanup transaction");
        EnsureDirectoryNotReparse(trashRoot, "cache trash root");
        EnsureDirectoryNotReparse(transactionPath, "cleanup transaction");
        if (Directory.Exists(contentPath))
        {
            EnsureDirectoryNotReparse(contentPath, "cleanup content directory");
            if (Directory.EnumerateFileSystemEntries(contentPath).Any())
            {
                throw new IOException("The cleanup content directory is not empty.");
            }
        }

        string[] transactionEntries = Directory.EnumerateFileSystemEntries(
                transactionPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        foreach (string entry in transactionEntries)
        {
            if (PathsEqual(entry, contentPath) || PathsEqual(entry, manifestPath))
            {
                continue;
            }

            string fileName = Path.GetFileName(entry);
            if (!fileName.StartsWith($"{ManifestFileName}.", StringComparison.Ordinal)
                || !fileName.EndsWith(".tmp", StringComparison.Ordinal)
                || Directory.Exists(entry))
            {
                throw new IOException(
                    $"An unexpected entry blocked cleanup transaction removal: {entry}");
            }
        }

        if (Directory.Exists(contentPath))
        {
            mutationLease.Release(contentPath);
            EnsureDirectoryNotReparse(contentPath, "cleanup content directory");
            Directory.Delete(contentPath, recursive: false);
        }
        DeleteTrustedFile(manifestPath, transactionPath);
        foreach (string entry in transactionEntries)
        {
            if (!PathsEqual(entry, contentPath) && !PathsEqual(entry, manifestPath))
            {
                DeleteTrustedFile(entry, transactionPath);
            }
        }

        if (Directory.EnumerateFileSystemEntries(transactionPath).Any())
        {
            throw new IOException("The cleanup transaction directory is not empty.");
        }

        mutationLease.Release(transactionPath);
        EnsureDirectoryNotReparse(transactionPath, "cleanup transaction");
        Directory.Delete(transactionPath, recursive: false);
        if (Directory.Exists(trashRoot)
            && !Directory.EnumerateFileSystemEntries(trashRoot).Any())
        {
            mutationLease.Release(trashRoot);
            EnsureDirectoryNotReparse(trashRoot, "cache trash root");
            Directory.Delete(trashRoot, recursive: false);
        }
    }

    private static void DeleteTrustedFile(string path, string trustedDirectory)
    {
        EnsureDirectChild(trustedDirectory, path, "cleanup transaction file");
        if (!File.Exists(path))
        {
            return;
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The cleanup transaction file is not a regular file: {path}");
        }

        File.Delete(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static CacheCleanupOperationResult FailedResult(
        string sourcePath,
        string? itemId,
        bool cancelled,
        bool recoveryAvailable,
        bool purgePending,
        long fileCount,
        long totalBytes,
        string error,
        string? manifestPath = null) => new(
            false,
            cancelled,
            sourcePath,
            itemId,
            manifestPath,
            recoveryAvailable,
            purgePending,
            fileCount,
            totalBytes,
            error);

    private static bool IsExpectedValidationException(Exception exception) =>
        exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or JsonException
            or OverflowException;

    private static bool IsExpectedFileOperation(Exception exception) =>
        IsExpectedValidationException(exception)
        || exception is InvalidOperationException or OperationCanceledException;

    private sealed record CleanupManifest(
        int SchemaVersion,
        string Id,
        DateTimeOffset CreatedAtUtc,
        string CacheId,
        string SourcePath,
        string State,
        IReadOnlyList<CacheCleanupEntryIdentity> PlannedEntries,
        IReadOnlyList<string> ContentEntryNames,
        long OriginalFileCount,
        long OriginalTotalBytes);

    private sealed record LoadedCleanupItem(
        CleanupManifest Manifest,
        string TrashRoot,
        string ContentPath,
        Inventory Content,
        CacheCleanupItemState State,
        string? RestoreBlockedReason);

    private sealed record EntryInventory(
        CacheCleanupEntryIdentity Identity,
        IReadOnlyList<InventoryNode> Nodes);

    private sealed record Inventory(
        IReadOnlyList<CacheCleanupEntryIdentity> TopLevelEntries,
        IReadOnlyList<InventoryNode> Nodes,
        long FileCount,
        long TotalBytes)
    {
        public static Inventory Empty { get; } = new([], [], 0, 0);
    }

    private sealed record InventoryNode(
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        long Length,
        long LastWriteTicks);
}
