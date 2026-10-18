using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Diagnostics;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Overview;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Storage;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AutoEnvPlus.App.Services;

/// <summary>
/// App-lifetime monitor that keeps the environment overview snapshot fresh in
/// the background, independent of page navigation. Previously the snapshot was
/// only gathered while the dashboard page drove a refresh, so the statistics
/// went stale the moment the user navigated away. The monitor owns collection:
/// a periodic timer performs cheap quick refreshes from managed state, pages
/// subscribe to <see cref="SnapshotChanged"/> to re-render, and user-initiated
/// refreshes (including expensive full scans) go through
/// <see cref="RefreshAsync"/> so their results are shared and persisted no
/// matter which page is visible.
/// </summary>
public sealed class OverviewMonitorService : IDisposable
{
    private static readonly LanguageOverviewDefinition[] OverviewLanguages =
    [
        new("python", "Python", [RuntimeKind.Python]),
        new("javascript", "JavaScript / TypeScript", [RuntimeKind.NodeJs]),
        new("java", "Java", [RuntimeKind.Java]),
        new("cpp", "C / C++", [
            RuntimeKind.Msvc,
            RuntimeKind.Llvm,
            RuntimeKind.Mingw,
            RuntimeKind.CMake,
            RuntimeKind.Ninja,
        ]),
        new("csharp", ".NET", [RuntimeKind.DotNet]),
    ];

    private readonly AppDownloadManager _downloadManager;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeRefresh;
    private System.Threading.Timer? _autoRefreshTimer;
    private string? _autoRefreshRoot;
    private OverviewSnapshot? _currentSnapshot;
    private bool _disposed;

    internal OverviewMonitorService(AppDownloadManager downloadManager)
    {
        _downloadManager = downloadManager ?? throw new ArgumentNullException(nameof(downloadManager));
    }

    /// <summary>
    /// The most recent snapshot known to the monitor: loaded from the persisted
    /// store at startup, then updated by every completed refresh. Never null
    /// after <see cref="LoadCachedAsync"/> finds a persisted snapshot.
    /// </summary>
    public OverviewSnapshot? CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _currentSnapshot;
            }
        }
    }

    /// <summary>
    /// Raised whenever a refresh completes and a new snapshot is available.
    /// May fire on any thread; subscribers must marshal to their own UI thread.
    /// </summary>
    public event EventHandler<OverviewSnapshot>? SnapshotChanged;

    /// <summary>
    /// Loads the persisted snapshot (if any) into <see cref="CurrentSnapshot"/>.
    /// Does not overwrite a snapshot that a completed refresh already produced.
    /// </summary>
    public async Task<OverviewSnapshot?> LoadCachedAsync(
        string managedRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        OverviewSnapshot? snapshot = await new OverviewSnapshotStore(managedRoot)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        bool adopted = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentSnapshot is null
                || _currentSnapshot.CapturedAtUtc < snapshot.CapturedAtUtc)
            {
                _currentSnapshot = snapshot;
                adopted = true;
            }
        }

        if (adopted)
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }

        return snapshot;
    }

    /// <summary>
    /// Gathers a fresh overview snapshot and persists it. The collection runs
    /// on a monitor-owned cancellation token: navigating away from the page
    /// that started the refresh does not abort it, so statistics keep
    /// updating in the background. A new refresh coalesces by cancelling the
    /// in-flight one. Full scans add PATH probing, runtime discovery, and
    /// cache measurement; quick refreshes only read managed state.
    /// </summary>
    public async Task<OverviewRefreshResult> RefreshAsync(string managedRoot, bool fullScan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        CancellationTokenSource refresh = new();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeRefresh?.Cancel();
            _activeRefresh?.Dispose();
            _activeRefresh = refresh;
        }

        CancellationToken cancellationToken = refresh.Token;
        try
        {
            OverviewSnapshot? previous;
            lock (_gate)
            {
                previous = _currentSnapshot;
            }

            OverviewSnapshot snapshot = await CollectAsync(
                managedRoot,
                fullScan,
                previous,
                cancellationToken).ConfigureAwait(false);
            await new OverviewSnapshotStore(managedRoot)
                .SaveAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (!_disposed)
                {
                    _currentSnapshot = snapshot;
                }
            }

            SnapshotChanged?.Invoke(this, snapshot);
            return new OverviewRefreshResult(snapshot, snapshot.Errors);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeRefresh, refresh))
                {
                    _activeRefresh = null;
                }
            }

            refresh.Dispose();
        }
    }

    /// <summary>
    /// Cancels the in-flight refresh (if any). Used by the dashboard's cancel
    /// affordance; the next timer tick or user refresh starts a new one.
    /// </summary>
    public void CancelActiveRefresh()
    {
        lock (_gate)
        {
            _activeRefresh?.Cancel();
        }
    }

    /// <summary>
    /// Starts periodic background quick refreshes. Also runs one immediate
    /// quick refresh so statistics exist from app start even if the user
    /// never opens the dashboard. Full scans stay user-initiated: they probe
    /// processes and walk cache directories, which is too expensive to run
    /// unattended on a timer.
    /// </summary>
    public void StartAutoRefresh(string managedRoot, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _autoRefreshRoot = managedRoot;
            _autoRefreshTimer?.Dispose();
            _autoRefreshTimer = new System.Threading.Timer(
                OnAutoRefreshTimer,
                null,
                interval,
                interval);
        }

        _ = RunAutoRefreshAsync(managedRoot);
    }

    /// <summary>Stops the periodic background refreshes.</summary>
    public void StopAutoRefresh()
    {
        lock (_gate)
        {
            _autoRefreshTimer?.Dispose();
            _autoRefreshTimer = null;
            _autoRefreshRoot = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeRefresh?.Cancel();
            _activeRefresh?.Dispose();
            _activeRefresh = null;
            _autoRefreshTimer?.Dispose();
            _autoRefreshTimer = null;
            _autoRefreshRoot = null;
        }
    }

    private void OnAutoRefreshTimer(object? state)
    {
        string? managedRoot;
        lock (_gate)
        {
            if (_disposed || _autoRefreshTimer is null)
            {
                return;
            }

            // Never preempt an in-flight refresh (a user-initiated full scan
            // must not be cancelled by a timer tick); the next tick retries.
            if (_activeRefresh is not null)
            {
                return;
            }

            managedRoot = _autoRefreshRoot;
        }

        if (managedRoot is not null)
        {
            _ = RunAutoRefreshAsync(managedRoot);
        }
    }

    private async Task RunAutoRefreshAsync(string managedRoot)
    {
        try
        {
            await RefreshAsync(managedRoot, fullScan: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            // Background refreshes are best effort: a transient failure (for
            // example a locked state file) must never crash the app. The next
            // tick or user refresh retries.
        }
    }

    private async Task<OverviewSnapshot> CollectAsync(
        string managedRoot,
        bool fullScan,
        OverviewSnapshot? previous,
        CancellationToken cancellationToken)
    {
        Task<PathInspectionReport>? pathTask = fullScan
            ? Task.Run(
                () => new PathInspector().InspectCurrent(
                    [
                        "python", "node", "npm", "java", "javac", "dotnet", "cl",
                        "gcc", "g++", "clang", "clang++", "cmake", "ninja",
                    ]),
                cancellationToken)
            : null;
        Task<IReadOnlyList<DiscoveredRuntime>>? discoveredTask = fullScan
            ? new RuntimeDiscoveryService().DiscoverCurrentAsync(cancellationToken)
            : null;
        Task<RegistryLoadResult> registryTask =
            new ManagedRuntimeRegistry(managedRoot).LoadAsync(cancellationToken);
        Task<RuntimeProfile> globalTask =
            new GlobalRuntimeProfileStore(managedRoot).LoadAsync(cancellationToken);
        Task<IReadOnlyList<KnownProject>> projectsTask =
            new KnownProjectStore(managedRoot).LoadAsync(cancellationToken);
        Task<ActivityLogLoadResult> activityTask =
            new ActivityLogStore(
                managedRoot,
                retentionDays: CurrentActivityRetentionDays())
                .LoadAsync(cancellationToken);
        Task<OverviewStorageStatus>? storageTask = fullScan
            ? LoadStorageOverviewAsync(managedRoot, cancellationToken)
            : null;
        Task<NetworkSettingsLoadResult> networkTask =
            new NetworkSettingsStore(managedRoot).LoadAsync(cancellationToken);
        Task<DownloadOverview> downloadsTask = Task.Run(
            () => LoadDownloadOverview(_downloadManager),
            cancellationToken);
        Task<RuntimeProviderPluginListResult> pluginsTask =
            new RuntimeProviderPluginStore(managedRoot).ListAsync(cancellationToken);

        List<string> errors = [];
        PathInspectionReport? path = pathTask is null
            ? null
            : await TryLoadAsync(pathTask, "PATH", errors, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DiscoveredRuntime>? discovered = discoveredTask is null
            ? null
            : await TryLoadAsync(
                discoveredTask,
                Loc.Get("Dashboard.Area.RuntimeDiscovery"),
                errors,
                cancellationToken).ConfigureAwait(false);
        RegistryLoadResult? registry = await TryLoadAsync(
            registryTask,
            Loc.Get("Dashboard.Area.ManagedRuntimes"),
            errors,
            cancellationToken).ConfigureAwait(false);
        RuntimeProfile? global = await TryLoadAsync(
            globalTask,
            Loc.Get("Dashboard.Area.GlobalSelection"),
            errors,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<KnownProject>? projects = await TryLoadAsync(
            projectsTask,
            Loc.Get("Dashboard.RecentProjects.Title"),
            errors,
            cancellationToken).ConfigureAwait(false);
        ActivityLogLoadResult? activity = await TryLoadAsync(
            activityTask,
            Loc.Get("Dashboard.QuickAction.Activity.Label"),
            errors,
            cancellationToken).ConfigureAwait(false);
        OverviewStorageStatus? storage = storageTask is null
            ? null
            : await TryLoadAsync(
                storageTask,
                Loc.Get("Dashboard.Area.CacheStats"),
                errors,
                cancellationToken).ConfigureAwait(false);
        NetworkSettingsLoadResult? network = await TryLoadAsync(
            networkTask,
            Loc.Get("Dashboard.Area.NetworkSettings"),
            errors,
            cancellationToken).ConfigureAwait(false);
        DownloadOverview? downloads = await TryLoadAsync(
            downloadsTask,
            Loc.Get("Dashboard.Area.DownloadLibrary"),
            errors,
            cancellationToken).ConfigureAwait(false);
        RuntimeProviderPluginListResult? plugins = await TryLoadAsync(
            pluginsTask,
            Loc.Get("Dashboard.Area.ProviderPlugins"),
            errors,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (registry?.Errors.Count > 0)
        {
            errors.AddRange(registry.Errors.Select(error =>
                Loc.Format(
                    "Dashboard.Errors.AreaMessage",
                    Loc.Get("Dashboard.Area.ManagedRuntimes"),
                    error)));
        }

        if (activity?.Errors.Count > 0)
        {
            errors.AddRange(activity.Errors.Select(error =>
                Loc.Format(
                    "Dashboard.Errors.AreaMessage",
                    Loc.Get("Dashboard.QuickAction.Activity.Label"),
                    error)));
        }

        if (network is { Success: false })
        {
            errors.AddRange(network.Errors.Select(error =>
                Loc.Format(
                    "Dashboard.Errors.AreaMessage",
                    Loc.Get("Dashboard.Area.NetworkSettings"),
                    error.Message)));
        }

        if (plugins?.Errors.Count > 0)
        {
            errors.Add(Loc.Format(
                "Dashboard.Errors.ProviderPlugins",
                plugins.Errors.Count));
        }

        DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
        return new OverviewSnapshot(
            capturedAtUtc,
            fullScan ? OverviewSnapshotDepth.Full : OverviewSnapshotDepth.Quick,
            fullScan ? capturedAtUtc : previous?.LastFullScanAtUtc,
            managedRoot,
            CreateLanguageOverview(
                registry?.Entries ?? [],
                global ?? RuntimeProfile.Empty,
                discovered,
                fullScan),
            fullScan ? CreatePathStatus(path) : previous?.Path,
            fullScan ? storage : previous?.Storage,
            CreateNetworkStatus(network?.Settings),
            new OverviewDownloadStatus(
                downloads?.FileCount ?? 0,
                downloads?.TotalBytes ?? 0),
            CreateProviderStatus(plugins),
            (projects ?? [])
                .OrderByDescending(project => project.LastSeenUtc)
                .ThenBy(project => project.ProjectRoot, StringComparer.OrdinalIgnoreCase)
                .Take(OverviewSnapshot.MaximumProjectCount)
                .Select(project => new OverviewProjectStatus(
                    project.ProjectRoot,
                    project.LastSeenUtc))
                .ToArray(),
            (activity?.Entries ?? [])
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(OverviewSnapshot.MaximumActivityCount)
                .Select(entry => new OverviewActivityStatus(
                    OperationText(entry.OperationType),
                    entry.Summary,
                    entry.TimestampUtc))
                .ToArray(),
            errors.Take(OverviewSnapshot.MaximumErrorCount).ToArray());
    }

    private static int CurrentActivityRetentionDays() =>
        Application.Current is App app
            ? app.CurrentSettings.LogRetentionDays
            : ActivityLogStore.DefaultRetentionDays;

    private static async Task<T?> TryLoadAsync<T>(
        Task<T> task,
        string area,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception)
        {
            errors.Add(Loc.Format("Dashboard.Errors.AreaMessage", area, exception.Message));
            return default;
        }
    }

    private static async Task<OverviewStorageStatus> LoadStorageOverviewAsync(
        string managedRoot,
        CancellationToken cancellationToken)
    {
        CacheDirectoryService service = new();
        CacheDirectoryLocation[] locations = service.DiscoverCurrent().ToArray();
        List<CacheDirectoryMeasurement> measurements = [];
        foreach (CacheDirectoryLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            measurements.Add(await service.MeasureBoundedAsync(
                location,
                EnvironmentDiagnosticOptions.DefaultMaximumCacheEntries,
                EnvironmentDiagnosticOptions.DefaultMaximumCacheDepth,
                cancellationToken).ConfigureAwait(false));
        }

        long totalBytes = measurements.Sum(measurement => measurement.TotalBytes);
        long totalFiles = measurements.Sum(measurement => measurement.FileCount);
        CacheDirectoryMeasurement? largest = measurements
            .Where(measurement => measurement.TotalBytes > 0)
            .OrderByDescending(measurement => measurement.TotalBytes)
            .FirstOrDefault();
        long? freeBytes = TryGetFreeBytes(managedRoot);
        return new OverviewStorageStatus(
            totalBytes,
            totalFiles,
            measurements.Count(measurement => measurement.Location.Exists),
            largest?.Location.Definition.DisplayName,
            largest?.TotalBytes ?? 0,
            freeBytes,
            measurements.SelectMany(measurement => measurement.Errors).ToArray());
    }

    private static DownloadOverview LoadDownloadOverview(AppDownloadManager manager)
    {
        ManagedDownloadLibraryItem[] items = manager.Library.ListFiles().ToArray();
        return new DownloadOverview(
            items.Length,
            items.Sum(item => item.SizeBytes),
            manager.Snapshot);
    }

    private static OverviewNetworkStatus CreateNetworkStatus(NetworkSettings? settings)
    {
        if (settings is null)
        {
            return new OverviewNetworkStatus(
                0,
                0,
                Loc.Get("Dashboard.Network.UnavailableSummary"),
                Loc.Get("Dashboard.Network.UnavailableDetail"));
        }

        GlobalNetworkSettings global = settings.Global ?? new GlobalNetworkSettings();
        int overrideCount = settings.Tools?.Count ?? 0;
        int proxyCount = (global.HttpProxy is null ? 0 : 1)
            + (global.HttpsProxy is null ? 0 : 1);
        string summary = proxyCount == 0
            ? Loc.Get("Dashboard.Network.DirectSummary")
            : Loc.Format("Dashboard.Network.ProxySummary", proxyCount);
        string detail = overrideCount == 0
            ? Loc.Get("Dashboard.Network.DefaultDetail")
            : Loc.Format(
                "Dashboard.Network.OverrideDetail",
                overrideCount,
                global.NoProxy?.Count ?? 0);
        return new OverviewNetworkStatus(proxyCount, overrideCount, summary, detail);
    }

    private static OverviewProviderStatus CreateProviderStatus(
        RuntimeProviderPluginListResult? plugins)
    {
        int builtInCount = BuiltInLanguageCatalog.Current.Tools.Sum(tool => tool.Providers.Count);
        if (plugins is null)
        {
            return new OverviewProviderStatus(builtInCount, 0, 0, 1);
        }

        return new OverviewProviderStatus(
            builtInCount,
            plugins.Plugins.Count,
            plugins.EnabledCount,
            plugins.Errors.Count);
    }

    private static IReadOnlyList<OverviewLanguageStatus> CreateLanguageOverview(
        IReadOnlyList<ManagedRuntimeEntry> entries,
        RuntimeProfile global,
        IReadOnlyList<DiscoveredRuntime>? discovered,
        bool fullScan)
    {
        RuntimeInstallation[] installations = entries
            .Select(entry => entry.ToRuntimeInstallation())
            .ToArray();
        List<OverviewLanguageStatus> rows = [];
        foreach (LanguageOverviewDefinition language in OverviewLanguages)
        {
            ManagedRuntimeEntry[] managed = entries
                .Where(entry => language.Kinds.Contains(entry.Kind))
                .ToArray();
            List<string> selectedVersions = [];
            int configuredSelections = 0;
            foreach (RuntimeKind kind in language.Kinds)
            {
                if (global.Selections.ContainsKey(kind))
                {
                    configuredSelections++;
                }

                RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
                    kind,
                    new RuntimeResolutionContext(Global: global),
                    installations,
                    CurrentArchitecture());
                if (resolution.Success)
                {
                    selectedVersions.Add(resolution.Installation!.Version.ToString());
                }
            }

            DiscoveredRuntime[] pathTools = discovered?
                .Where(candidate => language.Kinds.Contains(candidate.Kind) && candidate.IsHealthy)
                .ToArray() ?? [];
            string version = selectedVersions
                .Concat(pathTools.Select(tool => tool.Version?.ToString()))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? Loc.Get("Dashboard.Language.NotInstalled");
            string detail = managed.Length > 0
                ? Loc.Format(
                    "Dashboard.Language.ManagedDetail",
                    managed.Length,
                    configuredSelections)
                : fullScan && pathTools.Length > 0
                    ? Loc.Format("Dashboard.Language.PathDetail", pathTools.Length)
                    : fullScan
                        ? Loc.Get("Dashboard.Language.FullScanNone")
                        : Loc.Get("Dashboard.Language.QuickNoProbe");
            rows.Add(new OverviewLanguageStatus(
                language.Id,
                language.DisplayName,
                version,
                detail));
        }

        return rows;
    }

    private static OverviewPathStatus? CreatePathStatus(PathInspectionReport? report)
    {
        if (report is null)
        {
            return null;
        }

        return new OverviewPathStatus(
            report.Entries.Count,
            report.MissingCount,
            report.DuplicateCount,
            report.Conflicts.Count,
            report.IsHealthy
            ? Loc.Get("Dashboard.PathStatus.Healthy")
            : Loc.Get("Dashboard.PathStatus.Issues"));
    }

    private static long? TryGetFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private static RuntimeArchitecture CurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => RuntimeArchitecture.X86,
            Architecture.Arm64 => RuntimeArchitecture.Arm64,
            _ => RuntimeArchitecture.X64,
        };

    private static string OperationText(ActivityOperationType type) => type switch
    {
        ActivityOperationType.RuntimeInstall => Loc.Get("Dashboard.Operation.RuntimeInstall"),
        ActivityOperationType.RuntimeUninstall => Loc.Get("Dashboard.Operation.RuntimeUninstall"),
        ActivityOperationType.RuntimeSwitch => Loc.Get("Dashboard.Operation.RuntimeSwitch"),
        ActivityOperationType.PathChange => Loc.Get("Dashboard.Operation.PathChange"),
        ActivityOperationType.PathRollback => Loc.Get("Dashboard.Operation.PathRollback"),
        ActivityOperationType.CacheMigration => Loc.Get("Dashboard.Operation.CacheMigration"),
        ActivityOperationType.CacheRollback => Loc.Get("Dashboard.Operation.CacheRollback"),
        ActivityOperationType.CacheCleanup => Loc.Get("Dashboard.Operation.CacheCleanup"),
        ActivityOperationType.ToolchainInstall => Loc.Get("Dashboard.Operation.ToolchainInstall"),
        ActivityOperationType.ProjectImport => Loc.Get("Dashboard.Operation.ProjectImport"),
        ActivityOperationType.CMakePreset => Loc.Get("Dashboard.Operation.CMakePreset"),
        ActivityOperationType.DiagnosticExport => Loc.Get("Dashboard.Operation.DiagnosticExport"),
        ActivityOperationType.SettingsChange => Loc.Get("Dashboard.Operation.SettingsChange"),
        ActivityOperationType.PackageDownload => Loc.Get("Dashboard.Operation.PackageDownload"),
        ActivityOperationType.PackageImport => Loc.Get("Dashboard.Operation.PackageImport"),
        ActivityOperationType.PackageInstall => Loc.Get("Dashboard.Operation.PackageInstall"),
        ActivityOperationType.ProviderPluginImport => Loc.Get("Dashboard.Operation.ProviderPluginImport"),
        ActivityOperationType.ProviderPluginStateChange => Loc.Get("Dashboard.Operation.ProviderPluginStateChange"),
        ActivityOperationType.ProviderPluginDelete => Loc.Get("Dashboard.Operation.ProviderPluginDelete"),
        _ => Loc.Get("Dashboard.Operation.Other"),
    };

    private sealed record LanguageOverviewDefinition(
        string Id,
        string DisplayName,
        IReadOnlyList<RuntimeKind> Kinds);

    private sealed record DownloadOverview(
        int FileCount,
        long TotalBytes,
        AppTransferSnapshot? Snapshot);
}

/// <summary>Outcome of a monitor-driven overview refresh.</summary>
public sealed record OverviewRefreshResult(
    OverviewSnapshot Snapshot,
    IReadOnlyList<string> Errors);
