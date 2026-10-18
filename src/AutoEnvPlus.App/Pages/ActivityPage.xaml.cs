using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AutoEnvPlus.App.Pages;

public sealed partial class ActivityPage : Page
{
    private readonly ObservableCollection<ActivityRow> _rows = [];
    private CancellationTokenSource _pageCancellation = new();
    private readonly ActivityLogStore? _store;
    private readonly string? _rootError;
    private IReadOnlyList<ActivityLogEntry> _entries = [];

    public ActivityPage(string? managedRoot = null)
    {
        InitializeComponent();
        ApplyLocalizedStrings();
        ActivityList.ItemsSource = _rows;
        TypeFilter.Items.Clear();
        TypeFilter.Items.Add(new ComboBoxItem { Content = Loc.Get("Activity.Filters.Type.All") });
        foreach (ActivityOperationType type in Enum.GetValues<ActivityOperationType>())
        {
            TypeFilter.Items.Add(new ComboBoxItem
            {
                Content = GetOperationText(type),
                Tag = type,
            });
        }

        StatusFilter.SelectedIndex = 0;
        TypeFilter.SelectedIndex = 0;
        if (managedRoot is null)
        {
            if (!ManagedRootResolver.TryResolve(
                    null,
                    out managedRoot,
                    out string? error)
                || managedRoot is null)
            {
                _rootError = error ?? Loc.Get("Activity.Messages.RootResolveFailed");
            }
        }

        if (_rootError is null && managedRoot is not null)
        {
            try
            {
                int retentionDays = Application.Current is App app
                    ? app.CurrentSettings.LogRetentionDays
                    : ActivityLogStore.DefaultRetentionDays;
                _store = new ActivityLogStore(managedRoot, retentionDays: retentionDays);
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                _rootError = exception.Message;
            }
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Applies every localized string for this page from the resource table.
    /// Unpackaged WinUI 3 apps cannot use x:Uid: the XAML loader resolves
    /// ms-resource property bags against the OS system language only, so it
    /// neither matches the standard dotted-key PRI layout nor honors the
    /// in-app language preference. Code-behind assignment through
    /// <see cref="Loc"/> is the only path that follows
    /// <c>Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c>.
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        PageTitleText.Text = Loc.Get("Activity.Header.Title");
        PageSubtitleText.Text = Loc.Get("Activity.Header.Subtitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshButton,
            Loc.Get("Activity.Refresh.AccessibleName"));
        ToolTipService.SetToolTip(
            RefreshButton,
            Loc.Get("Activity.Refresh.AccessibleName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StatusFilter,
            Loc.Get("Activity.Filters.Status.AccessibleName"));
        StatusFilter.Header = Loc.Get("Activity.Filters.Status.Header");
        StatusFilterAllItem.Content = Loc.Get("Activity.Filters.Status.All");
        StatusFilterSucceededItem.Content = Loc.Get("Activity.Status.Succeeded");
        StatusFilterFailedItem.Content = Loc.Get("Activity.Status.Failed");
        StatusFilterCancelledItem.Content = Loc.Get("Activity.Status.Cancelled");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            TypeFilter,
            Loc.Get("Activity.Filters.Type.AccessibleName"));
        TypeFilter.Header = Loc.Get("Activity.Filters.Type.Header");
        TypeFilterAllItem.Content = Loc.Get("Activity.Filters.Type.All");
        FilterNoteText.Text = Loc.Get("Activity.Filters.Note");
        EmptyText.Text = Loc.Get("Activity.Empty.Text");
    }

    private void OnCopySummaryButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            button.Content = Loc.Get("Activity.Item.CopySummary");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        // The shell caches this page across navigations; the previous visit's
        // Unloaded cancelled the token, so re-arm it on every load.
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
        _pageCancellation = new CancellationTokenSource();
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _pageCancellation.Cancel();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        ApplyFilter();
    }

    private async Task RefreshAsync()
    {
        if (_rootError is not null)
        {
            SetInfo(InfoBarSeverity.Error, Loc.Get("Activity.Messages.ReadFailedTitle"), _rootError);
            return;
        }

        if (_store is null)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Activity.Messages.ReadFailedTitle"),
                Loc.Get("Activity.Messages.StoreNotInitialized"));
            return;
        }

        RefreshButton.IsEnabled = false;
        try
        {
            ActivityLogLoadResult result = await _store.LoadAsync(_pageCancellation.Token);
            _entries = result.Entries;
            ApplyFilter();
            if (result.Errors.Count == 0)
            {
                SetInfo(
                    InfoBarSeverity.Informational,
                    Loc.Get("Activity.Messages.RefreshedTitle"),
                    Loc.Format(
                        "Activity.Messages.RefreshedSummary",
                        _entries.Count,
                        _store.LogPath));
            }
            else
            {
                SetInfo(
                    InfoBarSeverity.Warning,
                    Loc.Get("Activity.Messages.PartialLoadTitle"),
                    Loc.Format(
                        "Activity.Messages.PartialLoadSummary",
                        _entries.Count,
                        result.Errors.Count,
                        _store.LogPath));
            }
        }
        catch (OperationCanceledException)
        {
            SetInfo(
                InfoBarSeverity.Informational,
                Loc.Get("Activity.Messages.LoadCancelledTitle"),
                string.Empty);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Activity.Messages.ReadFailedTitle"),
                exception.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        ActivityStatus? status = StatusFilter.SelectedIndex switch
        {
            1 => ActivityStatus.Succeeded,
            2 => ActivityStatus.Failed,
            3 => ActivityStatus.Cancelled,
            _ => null,
        };
        ActivityOperationType? operation = TypeFilter.SelectedItem is ComboBoxItem
        {
            Tag: ActivityOperationType selectedType,
        }
            ? selectedType
            : null;

        _rows.Clear();
        foreach (ActivityLogEntry entry in _entries
            .Where(entry => status is null || entry.Status == status)
            .Where(entry => operation is null || entry.OperationType == operation))
        {
            _rows.Add(new ActivityRow(entry));
        }

        EmptyText.Visibility = _rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnCopyClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ActivityRow row })
        {
            return;
        }

        try
        {
            DataPackage package = new();
            package.SetText(row.CopyText);
            Clipboard.SetContent(package);
            SetInfo(
                InfoBarSeverity.Success,
                Loc.Get("Activity.Messages.CopySucceededTitle"),
                Loc.Get("Activity.Messages.CopySucceededSummary"));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("Activity.Messages.CopyFailedTitle"),
                exception.Message);
        }
    }

    private void SetInfo(InfoBarSeverity severity, string title, string message)
    {
        ActivityInfo.Severity = severity;
        ActivityInfo.Title = title;
        ActivityInfo.Message = message;
        ActivityInfo.IsOpen = true;
    }

    private static string GetOperationText(ActivityOperationType type) => type switch
    {
        ActivityOperationType.RuntimeInstall => Loc.Get("Activity.Operations.RuntimeInstall"),
        ActivityOperationType.RuntimeUninstall => Loc.Get("Activity.Operations.RuntimeUninstall"),
        ActivityOperationType.RuntimeSwitch => Loc.Get("Activity.Operations.RuntimeSwitch"),
        ActivityOperationType.PathChange => Loc.Get("Activity.Operations.PathChange"),
        ActivityOperationType.PathRollback => Loc.Get("Activity.Operations.PathRollback"),
        ActivityOperationType.CacheMigration => Loc.Get("Activity.Operations.CacheMigration"),
        ActivityOperationType.CacheRollback => Loc.Get("Activity.Operations.CacheRollback"),
        ActivityOperationType.CacheCleanup => Loc.Get("Activity.Operations.CacheCleanup"),
        ActivityOperationType.ToolchainInstall => Loc.Get("Activity.Operations.ToolchainInstall"),
        ActivityOperationType.PowerShellIntegration => Loc.Get("Activity.Operations.PowerShellIntegration"),
        ActivityOperationType.ProjectImport => Loc.Get("Activity.Operations.ProjectImport"),
        ActivityOperationType.DiagnosticExport => Loc.Get("Activity.Operations.DiagnosticExport"),
        ActivityOperationType.CMakePreset => Loc.Get("Activity.Operations.CMakePreset"),
        ActivityOperationType.SettingsChange => Loc.Get("Activity.Operations.SettingsChange"),
        ActivityOperationType.PackageDownload => Loc.Get("Activity.Operations.PackageDownload"),
        ActivityOperationType.PackageImport => Loc.Get("Activity.Operations.PackageImport"),
        ActivityOperationType.PackageInstall => Loc.Get("Activity.Operations.PackageInstall"),
        ActivityOperationType.ProviderPluginImport => Loc.Get("Activity.Operations.ProviderPluginImport"),
        ActivityOperationType.ProviderPluginStateChange => Loc.Get("Activity.Operations.ProviderPluginStateChange"),
        ActivityOperationType.ProviderPluginDelete => Loc.Get("Activity.Operations.ProviderPluginDelete"),
        _ => Loc.Get("Activity.Operations.Other"),
    };

    private static string GetStatusText(ActivityStatus status) => status switch
    {
        ActivityStatus.Succeeded => Loc.Get("Activity.Status.Succeeded"),
        ActivityStatus.Failed => Loc.Get("Activity.Status.Failed"),
        ActivityStatus.Cancelled => Loc.Get("Activity.Status.Cancelled"),
        _ => status.ToString(),
    };

    private sealed class ActivityRow
    {
        public ActivityRow(ActivityLogEntry entry)
        {
            Entry = entry;
            TimestampText = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            OperationText = GetOperationText(entry.OperationType);
            StatusText = GetStatusText(entry.Status);
            Summary = entry.Summary;
            AffectedPathsText = entry.AffectedPaths.Count == 0
                ? Loc.Get("Activity.Item.AffectedPathsNone")
                : Loc.Format(
                    "Activity.Item.AffectedPaths",
                    string.Join(Loc.Get("Activity.Item.PathSeparator"), entry.AffectedPaths));
            SnapshotText = entry.SnapshotPath is null
                ? Loc.Get("Activity.Item.SnapshotPathNone")
                : Loc.Format("Activity.Item.SnapshotPath", entry.SnapshotPath);
            RollbackText = entry.RollbackPath is null
                ? Loc.Get("Activity.Item.RollbackPathNone")
                : Loc.Format("Activity.Item.RollbackPath", entry.RollbackPath);
            CopyText = string.Join(
                System.Environment.NewLine,
                TimestampText,
                Loc.Format("Activity.Copy.Operation", OperationText),
                Loc.Format("Activity.Copy.Status", StatusText),
                Loc.Format("Activity.Copy.Summary", Summary),
                AffectedPathsText,
                SnapshotText,
                RollbackText);
        }

        public ActivityLogEntry Entry { get; }

        public string TimestampText { get; }

        public string OperationText { get; }

        public string CopyActionAutomationName =>
            Loc.Format("Activity.Item.CopyAction.AccessibleName", OperationText);

        public string StatusText { get; }

        public string Summary { get; }

        public string AffectedPathsText { get; }

        public string SnapshotText { get; }

        public string RollbackText { get; }

        public string CopyText { get; }
    }
}
