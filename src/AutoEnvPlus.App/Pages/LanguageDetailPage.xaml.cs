using System.Runtime.InteropServices;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.RuntimeCatalogs;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Plugins;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.DotNet;
using AutoEnvPlus.Core.Providers.Java;
using AutoEnvPlus.Core.Providers.NodeJs;
using AutoEnvPlus.Core.Providers.Python;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Settings;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Toolchains;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class LanguageDetailPage : Page
{
    private readonly string _managedRoot;
    private readonly LanguageCatalog _catalog;
    private readonly LanguageCatalog _availableCatalog;
    private readonly LanguageDefinition _language;
    private IReadOnlySet<string> _detectedCommands;
    private readonly ProviderSourcePreferenceStore _sourceStore;
    private readonly RuntimeProviderPluginStore _pluginStore;
    private readonly CancellationTokenSource _pageCancellation = new();
    private CancellationTokenSource? _operationCancellation;
    private ToolRow[] _toolRows = [];
    private CppToolchainDiscoveryResult? _cppDiscovery;
    private bool _loaded;

    public LanguageDetailPage()
        : this(
            "python",
            BuiltInLanguageCatalog.Current,
            BuiltInLanguageCatalog.Current,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase))
    {
    }

    internal LanguageDetailPage(
        string languageId,
        LanguageCatalog catalog,
        LanguageCatalog availableCatalog,
        IReadOnlySet<string> detectedCommands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _availableCatalog = availableCatalog
            ?? throw new ArgumentNullException(nameof(availableCatalog));
        _detectedCommands = detectedCommands
            ?? throw new ArgumentNullException(nameof(detectedCommands));
        if (!_catalog.TryGetLanguage(languageId, out LanguageDefinition? language))
        {
            throw new ArgumentException("The requested language is not available.", nameof(languageId));
        }

        _language = language!;
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _sourceStore = new ProviderSourcePreferenceStore(_managedRoot);
        _pluginStore = new RuntimeProviderPluginStore(_managedRoot);
        InitializeComponent();
        ApplyLocalizedStrings();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? BackRequested;

    public event EventHandler? InventoryUpdated;

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
        AutomationProperties.SetName(
            BackButton,
            Loc.Get("LanguageDetail.Back.AccessibleName"));
        ToolTipService.SetToolTip(
            BackButton,
            Loc.Get("LanguageDetail.Back.AccessibleName"));
        AutomationProperties.SetName(
            OperationProgress,
            Loc.Get("LanguageDetail.Progress.AccessibleName"));
        AutomationProperties.SetName(
            ScanLanguageButton,
            Loc.Get("LanguageDetail.Scan.AccessibleName"));
        ToolTipService.SetToolTip(
            ScanLanguageButton,
            Loc.Get("LanguageDetail.Scan.ToolTip"));
        AutomationProperties.SetName(
            CancelOperationButton,
            Loc.Get("LanguageDetail.CancelOperation.AccessibleName"));
        ToolTipService.SetToolTip(
            CancelOperationButton,
            Loc.Get("LanguageDetail.CancelOperation.AccessibleName"));
        HomepageButton.Content = Loc.Get("LanguageDetail.Homepage.Label");
        OverviewPivotItem.Header = Loc.Get("LanguageDetail.Pivot.Overview");
        ToolsPivotItem.Header = Loc.Get("LanguageDetail.Pivot.Tools");
        SourcesPivotItem.Header = Loc.Get("LanguageDetail.Pivot.Sources");
        ProjectPivotItem.Header = Loc.Get("LanguageDetail.Pivot.Project");
        PluginsPivotItem.Header = Loc.Get("LanguageDetail.Pivot.Plugins");
        OverviewToolCountLabel.Text = Loc.Get("LanguageDetail.Common.LanguageTool");
        OverviewDetectedLabel.Text = Loc.Get("LanguageDetail.Overview.DetectedLabel");
        OverviewManagedLabel.Text = Loc.Get("LanguageDetail.Overview.ManagedLabel");
        LanguageDefinitionTitle.Text = Loc.Get("LanguageDetail.Overview.DefinitionTitle");
        HierarchyInfo.Message = Loc.Get("LanguageDetail.Overview.HierarchyInfoMessage");
        ToolCapabilityInfo.Title = Loc.Get("LanguageDetail.Tools.InfoTitle");
        ToolCapabilityInfo.Message = Loc.Get("LanguageDetail.Tools.InfoMessage");
        AutomationProperties.SetName(
            ToolList,
            Loc.Get("LanguageDetail.Tools.ListAccessibleName"));
        AutomationProperties.SetName(
            EmptyToolState,
            Loc.Get("LanguageDetail.Tools.EmptyAccessibleName"));
        EmptyToolTitle.Text = Loc.Get("LanguageDetail.Tools.EmptyTitle");
        EmptyToolMessage.Text = Loc.Get("LanguageDetail.Tools.EmptyMessage");
        MirrorInfo.Title = Loc.Get("LanguageDetail.Sources.InfoTitle");
        AutomationProperties.SetName(
            AddSourceButton,
            Loc.Get("LanguageDetail.Sources.AddAccessibleName"));
        AddSourceText.Text = Loc.Get("LanguageDetail.Sources.AddCustom");
        AutomationProperties.SetName(
            MirrorList,
            Loc.Get("LanguageDetail.Sources.ListAccessibleName"));
        AutomationProperties.SetName(
            EmptyMirrorState,
            Loc.Get("LanguageDetail.Sources.EmptyAccessibleName"));
        EmptyMirrorTitle.Text = Loc.Get("LanguageDetail.Sources.EmptyTitle");
        EmptyMirrorMessage.Text = Loc.Get("LanguageDetail.Sources.EmptyMessage");
        AutomationProperties.SetName(
            ProjectPathBox,
            Loc.Get("LanguageDetail.Project.PathHeader"));
        ProjectPathBox.Header = Loc.Get("LanguageDetail.Project.PathHeader");
        ProjectPathBox.PlaceholderText = Loc.Get("LanguageDetail.Project.PathPlaceholder");
        AutomationProperties.SetName(
            ChooseProjectFolderButton,
            Loc.Get("LanguageDetail.Project.ChooseAccessibleName"));
        ToolTipService.SetToolTip(
            ChooseProjectFolderButton,
            Loc.Get("LanguageDetail.Project.ChooseAccessibleName"));
        ParseProjectText.Text = Loc.Get("LanguageDetail.Project.ParseLabel");
        ProjectInfo.Title = Loc.Get("LanguageDetail.Project.InfoTitle");
        ProjectInfo.Message = Loc.Get("LanguageDetail.Project.InfoMessage");
        PluginSectionTitle.Text = Loc.Get("LanguageDetail.Plugins.SectionTitle");
        PluginSectionSubtitle.Text = Loc.Get("LanguageDetail.Plugins.SectionSubtitle");
        AutomationProperties.SetName(
            ImportProviderButton,
            Loc.Get("LanguageDetail.Plugins.ImportAccessibleName"));
        ImportProviderText.Text = Loc.Get("LanguageDetail.Plugins.ImportLabel");
        PluginTrustInfo.Title = Loc.Get("LanguageDetail.Plugins.TrustInfoTitle");
        PluginTrustInfo.Message = Loc.Get("LanguageDetail.Plugins.TrustInfoMessage");
        AutomationProperties.SetName(
            ProviderPluginList,
            Loc.Get("LanguageDetail.Plugins.ListAccessibleName"));
        AutomationProperties.SetName(
            EmptyProviderPluginText,
            Loc.Get("LanguageDetail.Plugins.EmptyAccessibleName"));
        EmptyProviderPluginText.Text = Loc.Get("LanguageDetail.Plugins.EmptyText");
    }

    // x:Name inside a DataTemplate generates no code-behind field, so every
    // templated element assigns its localized text, tooltip, or automation
    // name through these Loaded handlers.

    private void OnDetectedBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge)
        {
            badge.Text = Loc.Get("LanguageDetail.Tool.Badge.Detected");
        }
    }

    private void OnManagedBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge)
        {
            badge.Text = Loc.Get("LanguageDetail.Tool.Badge.Managed");
        }
    }

    private void OnMetadataBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge)
        {
            badge.Text = Loc.Get("LanguageDetail.Tool.Badge.Metadata");
        }
    }

    private void OnMetadataLinksLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ItemsControl links)
        {
            AutomationProperties.SetName(
                links,
                Loc.Get("LanguageDetail.Tool.MetadataLinksAccessibleName"));
        }
    }

    private void OnSwitchVersionButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(
                button,
                Loc.Get("LanguageDetail.Tool.SwitchToolTip"));
        }
    }

    private void OnUninstallButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(
                button,
                Loc.Get("LanguageDetail.Tool.UninstallToolTip"));
        }
    }

    private void OnDeletePluginButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(
                button,
                Loc.Get("LanguageDetail.Plugin.DeleteToolTip"));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        RenderLanguageHeader();
        await LoadDetailAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _operationCancellation?.Cancel();
        _pageCancellation.Cancel();
    }

    private void OnBackClicked(object sender, RoutedEventArgs args) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelOperationClicked(object sender, RoutedEventArgs args) =>
        _operationCancellation?.Cancel();

    private async void OnScanLanguageClicked(object sender, RoutedEventArgs args)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        try
        {
            SetOperationState(
                true,
                Loc.Get("LanguageDetail.Scan.BusyTitle"),
                Loc.Get("LanguageDetail.Scan.BusyMessage"));
            string[] toolIds = _catalog.GetToolsForLanguage(_language.Id)
                .Select(tool => tool.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Task<LanguageToolInventorySnapshot> inventoryTask =
                new LanguageToolInventoryScanner().ScanPathAsync(
                    _catalog,
                    toolIds,
                    cancellationToken);
            Task<CppToolchainDiscoveryResult?> cppTask = _language.Id is "c" or "cpp"
                ? DiscoverCppAsync(cancellationToken)
                : Task.FromResult<CppToolchainDiscoveryResult?>(null);
            await Task.WhenAll(inventoryTask, cppTask);

            LanguageToolInventoryStore store = new(_managedRoot);
            LanguageToolInventorySnapshot? current;
            try
            {
                current = await store.LoadAsync(cancellationToken);
            }
            catch (InvalidDataException)
            {
                current = null;
            }

            LanguageToolInventorySnapshot merged = LanguageToolInventorySnapshot.Merge(
                _catalog,
                current,
                await inventoryTask);
            await store.SaveAsync(merged, cancellationToken);
            _detectedCommands = merged.GetDetectedCommands(_catalog);
            _cppDiscovery = await cppTask;
            await RefreshToolRowsAsync(cancellationToken);
            InventoryUpdated?.Invoke(this, EventArgs.Empty);

            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Success;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.Scan.CompletedTitle");
            DetailStatusInfo.Message = Loc.Format(
                "LanguageDetail.Scan.CompletedMessage",
                _language.DisplayName,
                _toolRows.Count(row => row.IsDetected));
        }
        catch (OperationCanceledException)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.Scan.CancelledTitle");
            DetailStatusInfo.Message = Loc.Get("LanguageDetail.Scan.CancelledMessage");
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError(Loc.Get("LanguageDetail.Scan.FailedTitle"), exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private void RenderLanguageHeader()
    {
        LanguageTitle.Text = _language.DisplayName;
        LanguageSubtitle.Text = Loc.Format(
            "LanguageDetail.Header.Subtitle",
            _language.Id);
        HomepageButton.NavigateUri = _language.Homepage;
        LanguageIdText.Text = Loc.Format("LanguageDetail.Definition.Id", _language.Id);
        LanguageAliasesText.Text = Loc.Get("LanguageDetail.Definition.AliasesPrefix")
            + (_language.Aliases.Count == 0
                ? Loc.Get("LanguageDetail.Definition.NoneValue")
                : string.Join(Loc.Get("LanguageDetail.AliasSeparator"), _language.Aliases));
        LanguageExtensionsText.Text = Loc.Get("LanguageDetail.Definition.ExtensionsPrefix")
            + (_language.FileExtensions.Count == 0
                ? Loc.Get("LanguageDetail.Definition.NoExtensions")
                : string.Join(" ", _language.FileExtensions));
        LanguageVisibilityText.Text = _language.DefaultEnabled
            ? Loc.Get("LanguageDetail.Definition.VisibilityDefault")
            : Loc.Get("LanguageDetail.Definition.VisibilityOptIn");
    }

    private async Task LoadDetailAsync()
    {
        SetOperationState(
            true,
            Loc.Get("LanguageDetail.Load.BusyTitle"),
            Loc.Get("LanguageDetail.Load.BusyMessage"));
        try
        {
            Task<RegistryLoadResult> registryTask = new ManagedRuntimeRegistry(_managedRoot)
                .LoadAsync(_pageCancellation.Token);
            Task<CppToolchainDiscoveryResult?> cppTask =
                Task.FromResult(_cppDiscovery);
            Task<RuntimeProviderPluginListResult> pluginsTask = _pluginStore.ListAsync(
                _pageCancellation.Token);
            Task<RuntimeProfile> globalProfileTask = new GlobalRuntimeProfileStore(_managedRoot)
                .LoadAsync(_pageCancellation.Token);
            Task<AutoEnvPlusApplicationSettings> settingsTask =
                new AutoEnvPlusApplicationSettingsStore(_managedRoot).LoadAsync(
                    _pageCancellation.Token);
            await Task.WhenAll(
                registryTask,
                cppTask,
                pluginsTask,
                globalProfileTask,
                settingsTask);

            RegistryLoadResult registry = await registryTask;
            CppToolchainDiscoveryResult? cpp = await cppTask;
            AutoEnvPlusApplicationSettings settings = await settingsTask;
            IReadOnlyList<LanguageToolDefinition> tools = _catalog.GetToolsForLanguage(_language.Id)
                .Where(tool => settings.ShowExperimentalTools || !IsExperimentalTool(tool))
                .ToArray();
            RenderToolRows(tools, registry.Entries, cpp, await globalProfileTask);

            string[] packageManagers = tools
                .Where(tool => tool.Roles.Contains(LanguageToolRole.PackageManager))
                .Select(tool => tool.DisplayName)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            PackageManagerSummary.Text = packageManagers.Length == 0
                ? Loc.Get("LanguageDetail.PackageManager.None")
                : Loc.Get("LanguageDetail.PackageManager.SummaryPrefix")
                    + string.Join(" · ", packageManagers);

            await RenderMirrorSourcesAsync();
            RenderProviderPlugins(await pluginsTask);
            SetOperationState(
                false,
                Loc.Get("LanguageDetail.Load.ReadyTitle"),
                Loc.Format(
                    "LanguageDetail.Load.ReadyMessage",
                    _toolRows.Length,
                    _toolRows.Count(row => row.IsDetected),
                    _toolRows.Count(row => row.IsManagedInstall),
                    _toolRows.Count(row => !row.IsManagedInstall)),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError(Loc.Get("LanguageDetail.Load.FailedTitle"), exception);
        }
        finally
        {
            OperationProgress.IsActive = false;
            CancelOperationButton.IsEnabled = false;
        }
    }

    private static async Task<CppToolchainDiscoveryResult?> DiscoverCppAsync(
        CancellationToken cancellationToken) =>
        await new CppToolchainDiscoveryService().DiscoverAsync(cancellationToken);

    private ToolRow CreateToolRow(
        LanguageToolDefinition tool,
        IReadOnlyList<ManagedRuntimeEntry> registry,
        CppToolchainDiscoveryResult? cpp,
        RuntimeProfile globalProfile)
    {
        bool pathDetected = tool.DiscoveryCommands.Any(_detectedCommands.Contains);
        LanguageToolRuntimeBridgeDefinition? bridge = LanguageToolRuntimeBridge.Definitions
            .FirstOrDefault(candidate => candidate.ToolId.Equals(
                tool.Id,
                StringComparison.OrdinalIgnoreCase));
        ManagedRuntimeEntry[] managed = bridge is null
            ? []
            : registry.Where(entry => entry.Kind == bridge.RuntimeKind)
                .OrderByDescending(entry => entry.Version)
                .ThenBy(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Architecture)
                .ToArray();
        ToolchainComponent? component = ToolchainComponentFor(tool.Id);
        string? discoverySummary = null;
        if (tool.Id.Equals(LanguageToolRuntimeBridge.WindowsSdkToolId, StringComparison.OrdinalIgnoreCase))
        {
            WindowsSdkInstallation[] sdks = cpp?.WindowsSdks
                .OrderByDescending(sdk => sdk.Version)
                .ToArray() ?? [];
            pathDetected = pathDetected || sdks.Length > 0;
            discoverySummary = sdks.Length == 0
                ? Loc.Get("LanguageDetail.Tool.WindowsSdkNone")
                : string.Join(
                    Loc.Get("LanguageDetail.Separator"),
                    sdks.Select(sdk =>
                        $"{sdk.Version} ({string.Join(", ", sdk.Architectures)})"));
        }
        else if (tool.Id.Equals("msvc-build-tools", StringComparison.OrdinalIgnoreCase)
            && cpp is not null)
        {
            VisualCppInstallation[] installations = cpp.VisualStudioInstallations
                .Where(installation => installation.IsComplete)
                .ToArray();
            pathDetected = pathDetected || installations.Length > 0;
            if (installations.Length > 0)
            {
                discoverySummary = Loc.Get("LanguageDetail.Tool.DiscoveredPrefix") + string.Join(
                    Loc.Get("LanguageDetail.Separator"),
                    installations.Select(installation =>
                        $"{installation.DisplayName} · MSVC "
                        + (installation.MsvcToolsVersion
                            ?? Loc.Get("LanguageDetail.Tool.VersionUnknown"))));
            }
        }
        else if (bridge is not null && cpp is not null)
        {
            var discoveredTools = cpp.BuildTools
                .Where(discovered => discovered.Kind == bridge.RuntimeKind)
                .ToArray();
            pathDetected = pathDetected || discoveredTools.Length > 0;
            if (discoveredTools.Length > 0)
            {
                discoverySummary = Loc.Get("LanguageDetail.Tool.DiscoveredPrefix") + string.Join(
                    Loc.Get("LanguageDetail.Separator"),
                    discoveredTools.Select(discovered =>
                        $"{discovered.Version?.ToString()
                            ?? Loc.Get("LanguageDetail.Tool.VersionUnknown")} · "
                        + discovered.ExecutablePath));
            }
        }

        string versionSummary = discoverySummary
            ?? (managed.Length > 0
                ? Loc.Format("LanguageDetail.Tool.ManagedVersionsPrefix", managed.Length)
                    + string.Join(
                        Loc.Get("LanguageDetail.Separator"),
                        managed.Take(4).Select(entry =>
                            $"{entry.Version} / {entry.ProviderId} / "
                            + $"{ArchitectureLabel(entry.Architecture)} / {entry.Id}"))
                    + (managed.Length > 4
                        ? Loc.Get("LanguageDetail.Separator") + "…"
                        : string.Empty)
                : pathDetected
                    ? Loc.Format(
                        "LanguageDetail.Tool.PathSummary",
                        string.Join(", ", tool.DiscoveryCommands.Where(_detectedCommands.Contains)))
                    : Loc.Get("LanguageDetail.Tool.NoVersions"));
        if (bridge is not null
            && globalProfile.Selections.TryGetValue(
                bridge.RuntimeKind,
                out VersionSelector? globalSelector))
        {
            versionSummary += Loc.Format("LanguageDetail.Tool.GlobalSelection", globalSelector);
            if (globalProfile.ExactSelections.TryGetValue(
                    bridge.RuntimeKind,
                    out RuntimeSelectionIdentity? identity))
            {
                versionSummary += $" · {identity.RuntimeId} / {identity.ProviderId}";
            }
        }

        ToolManagementKind management = LanguageToolUiPolicy.GetManagementKind(tool);
        return new ToolRow(
            _language,
            tool,
            pathDetected,
            versionSummary,
            bridge?.RuntimeKind,
            component,
            management,
            managed);
    }

    private async void OnManageToolClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ToolRow row }
            || row.ManagementKind == ToolManagementKind.None
            || _operationCancellation is not null)
        {
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        try
        {
            RuntimeKind kind = row.RuntimeKind
                ?? (row.ToolchainComponent is ToolchainComponent mappedComponent
                    ? ToolchainRuntimeProviderPolicy.GetRuntimeKind(mappedComponent)
                    : throw new InvalidOperationException(
                        "The language tool has no Provider RuntimeKind mapping."));
            ToolInstallSource? source = await SelectToolInstallSourceAsync(
                row,
                kind,
                cancellationToken);
            if (source is null)
            {
                return;
            }

            if (source.PluginProvider is not null)
            {
                await InstallArchiveToolAsync(
                    row,
                    kind,
                    source.PluginProvider,
                    cancellationToken);
            }
            else if (row.ManagementKind == ToolManagementKind.WinGet
                && row.ToolchainComponent is ToolchainComponent component)
            {
                await InstallWinGetToolAsync(row, component, cancellationToken);
            }
            else
            {
                await InstallArchiveToolAsync(row, kind, null, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.Operation.CancelledTitle");
            DetailStatusInfo.Message = Loc.Get("LanguageDetail.Operation.CancelledMessage");
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError(Loc.Get("LanguageDetail.Install.FailedTitle"), exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnUninstallManagedRuntimeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ToolRow row }
            || row.ManagedEntries.Count == 0
            || _operationCancellation is not null)
        {
            return;
        }

        ManagedRuntimeEntry? selectedEntry = null;
        CancellationToken cancellationToken = BeginOperation();
        try
        {
            selectedEntry = await SelectManagedRuntimeForUninstallAsync(row);
            if (selectedEntry is null)
            {
                SetOperationState(
                    false,
                    Loc.Get("LanguageDetail.Uninstall.CancelledTitle"),
                    Loc.Get("LanguageDetail.Uninstall.CancelledMessage"));
                return;
            }

            SetOperationState(
                true,
                Loc.Get("LanguageDetail.Uninstall.CheckingTitle"),
                RuntimeIdentitySummary(selectedEntry));
            ManagedRuntimeUninstaller uninstaller = new(_managedRoot);
            ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(
                selectedEntry.Id,
                cancellationToken);
            bool confirmed = await ConfirmManagedRuntimeUninstallAsync(plan);
            if (!confirmed)
            {
                if (plan.IsReferenced)
                {
                    SetOperationState(
                        false,
                        Loc.Get("LanguageDetail.Uninstall.ReferencedTitle"),
                        Loc.Format(
                            "LanguageDetail.Uninstall.ReferencedMessage",
                            plan.References.Count),
                        InfoBarSeverity.Warning);
                    await LogRuntimeUninstallAsync(
                        selectedEntry,
                        ActivityStatus.Failed,
                        Loc.Format(
                            "LanguageDetail.Uninstall.ReferencedActivity",
                            plan.References.Count));
                }
                else
                {
                    SetOperationState(
                        false,
                        Loc.Get("LanguageDetail.Uninstall.CancelledTitle"),
                        Loc.Get("LanguageDetail.Uninstall.ConfirmClosedMessage"));
                    await LogRuntimeUninstallAsync(
                        selectedEntry,
                        ActivityStatus.Cancelled,
                        Loc.Get("LanguageDetail.Uninstall.CancelledActivity"));
                }

                return;
            }

            SetOperationState(
                true,
                Loc.Get("LanguageDetail.Uninstall.BusyTitle"),
                RuntimeIdentitySummary(plan.Runtime));
            ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(
                plan,
                force: false,
                cancellationToken: cancellationToken);
            if (!result.Success)
            {
                bool recoveryIncomplete = result.RemovedFromRegistry
                    || result.PendingTrashCleanup;
                SetOperationState(
                    false,
                    recoveryIncomplete
                        ? Loc.Get("LanguageDetail.Uninstall.IncompleteTitle")
                        : Loc.Get("LanguageDetail.Uninstall.FailedTitle"),
                    recoveryIncomplete
                        ? Loc.Get("LanguageDetail.Uninstall.IncompleteMessage")
                        : Loc.Get("LanguageDetail.Uninstall.FailedMessage"),
                    InfoBarSeverity.Error);
                await LogRuntimeUninstallAsync(
                    plan.Runtime,
                    ActivityStatus.Failed,
                    recoveryIncomplete
                        ? Loc.Get("LanguageDetail.Uninstall.IncompleteActivity")
                        : Loc.Get("LanguageDetail.Uninstall.FailedActivity"));
                return;
            }

            _operationCancellation?.Dispose();
            _operationCancellation = null;
            CancelOperationButton.IsEnabled = false;
            SetOperationState(
                false,
                result.PendingTrashCleanup
                    ? Loc.Get("LanguageDetail.Uninstall.RemovedPendingTitle")
                    : Loc.Get("LanguageDetail.Uninstall.SucceededTitle"),
                RuntimeIdentitySummary(plan.Runtime)
                + (result.PendingTrashCleanup
                    ? Loc.Get("LanguageDetail.Uninstall.RemovedPendingSuffix")
                    : Loc.Get("LanguageDetail.Uninstall.SucceededSuffix")),
                result.PendingTrashCleanup
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success);
            await LogRuntimeUninstallAsync(
                plan.Runtime,
                ActivityStatus.Succeeded,
                result.PendingTrashCleanup
                    ? Loc.Get("LanguageDetail.Uninstall.RemovedPendingActivity")
                    : Loc.Get("LanguageDetail.Uninstall.SucceededActivity"));
            try
            {
                await RefreshToolRowsAsync(CancellationToken.None);
            }
            catch (Exception exception) when (IsExpectedException(exception)
                || exception is OperationCanceledException)
            {
                SetOperationState(
                    false,
                    Loc.Get("LanguageDetail.Uninstall.RefreshFailedTitle"),
                    RuntimeIdentitySummary(plan.Runtime)
                    + Loc.Get("LanguageDetail.Uninstall.RefreshFailedSuffix"),
                    InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            SetOperationState(
                false,
                Loc.Get("LanguageDetail.Uninstall.CancelledTitle"),
                Loc.Get("LanguageDetail.Uninstall.CancelledLateMessage"),
                InfoBarSeverity.Informational);
            if (selectedEntry is not null)
            {
                await LogRuntimeUninstallAsync(
                    selectedEntry,
                    ActivityStatus.Cancelled,
                    Loc.Get("LanguageDetail.Uninstall.CancelledLateActivity"));
            }
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError(Loc.Get("LanguageDetail.Uninstall.ErrorTitle"), exception);
            if (selectedEntry is not null)
            {
                await LogRuntimeUninstallAsync(
                    selectedEntry,
                    ActivityStatus.Failed,
                    Loc.Get("LanguageDetail.Uninstall.ErrorActivity"));
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<ManagedRuntimeEntry?> SelectManagedRuntimeForUninstallAsync(ToolRow row)
    {
        ManagedRuntimeChoice[] choices = row.ManagedEntries
            .Select(entry => new ManagedRuntimeChoice(entry))
            .ToArray();
        ComboBox selector = new()
        {
            Header = Loc.Get("LanguageDetail.Uninstall.SelectorHeader"),
            ItemsSource = choices,
            DisplayMemberPath = nameof(ManagedRuntimeChoice.Label),
            SelectedIndex = choices.Length == 0 ? -1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            selector,
            Loc.Get("LanguageDetail.Uninstall.SelectorHeader"));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("LanguageDetail.Uninstall.SelectTitle", row.DisplayName),
            Content = new StackPanel
            {
                MaxWidth = 680,
                Spacing = 10,
                Children =
                {
                    selector,
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = true,
                        Severity = InfoBarSeverity.Informational,
                        Title = Loc.Get("LanguageDetail.Uninstall.SelectInfoTitle"),
                        Message = Loc.Get("LanguageDetail.Uninstall.SelectInfoMessage"),
                    },
                },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.Uninstall.CheckReferencesButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? (selector.SelectedItem as ManagedRuntimeChoice)?.Entry
            : null;
    }

    private async Task<bool> ConfirmManagedRuntimeUninstallAsync(
        ManagedRuntimeUninstallPlan plan)
    {
        StackPanel content = new()
        {
            MaxWidth = 680,
            Spacing = 8,
            Children =
            {
                Detail("Runtime ID", plan.Runtime.Id),
                Detail("Provider ID", plan.Runtime.ProviderId),
                Detail(
                    Loc.Get("LanguageDetail.Uninstall.DetailVersionArch"),
                    $"{plan.Runtime.Version} / {ArchitectureLabel(plan.Runtime.Architecture)}"),
                Detail(Loc.Get("LanguageDetail.Uninstall.DetailInstallRoot"), plan.Runtime.InstallRoot),
            },
        };
        if (plan.IsReferenced)
        {
            content.Children.Add(new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Warning,
                Title = Loc.Format(
                    "LanguageDetail.Uninstall.ReferencedInfoTitle",
                    plan.References.Count),
                Message = Loc.Get("LanguageDetail.Uninstall.ReferencedInfoMessage"),
            });
            foreach (RuntimeReference reference in plan.References)
            {
                content.Children.Add(Detail(
                    ReferenceKindName(reference.Kind),
                    $"{reference.Owner} · {reference.Detail}"));
            }
        }
        else
        {
            content.Children.Add(new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Warning,
                Title = Loc.Get("LanguageDetail.Uninstall.NoReferencesInfoTitle"),
                Message = Loc.Get("LanguageDetail.Uninstall.NoReferencesInfoMessage"),
            });
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = plan.IsReferenced
                ? Loc.Get("LanguageDetail.Uninstall.ReferencedDialogTitle")
                : Loc.Get("LanguageDetail.Uninstall.ConfirmDialogTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            },
            CloseButtonText = plan.IsReferenced
                ? Loc.Get("LanguageDetail.Dialogs.Back")
                : Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (!plan.IsReferenced)
        {
            confirmation.PrimaryButtonText = Loc.Get("LanguageDetail.Uninstall.ConfirmButton");
        }

        return await confirmation.ShowAsync() == ContentDialogResult.Primary
            && !plan.IsReferenced;
    }

    private static Task LogRuntimeUninstallAsync(
        ManagedRuntimeEntry entry,
        ActivityStatus status,
        string outcome) => AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeUninstall,
            status,
            Loc.Format(
                "LanguageDetail.Uninstall.LogIdentityOutcome",
                $"{entry.Id} / {entry.ProviderId} / {ArchitectureLabel(entry.Architecture)}",
                outcome),
            []);

    private static string RuntimeIdentitySummary(ManagedRuntimeEntry entry) =>
        $"{entry.Version} · Runtime ID {entry.Id} · Provider {entry.ProviderId} · "
        + ArchitectureLabel(entry.Architecture);

    private static string ReferenceKindName(RuntimeReferenceKind kind) => kind switch
    {
        RuntimeReferenceKind.GlobalProfile => Loc.Get("LanguageDetail.Reference.GlobalProfile"),
        RuntimeReferenceKind.ProjectManifest => Loc.Get("LanguageDetail.Reference.ProjectManifest"),
        RuntimeReferenceKind.ProjectLock => Loc.Get("LanguageDetail.Reference.ProjectLock"),
        _ => kind.ToString(),
    };

    private async void OnSwitchGlobalVersionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ToolRow row }
            || !SupportsVersionSwitch(row.RuntimeKind)
            || row.ManagedEntries.Count == 0
            || _operationCancellation is not null)
        {
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        try
        {
            SetOperationState(
                true,
                Loc.Get("LanguageDetail.Switch.BusyTitle"),
                Loc.Format("LanguageDetail.Switch.BusyMessage", row.DisplayName));
            RegistryLoadResult registry = await new ManagedRuntimeRegistry(_managedRoot).LoadAsync(
                cancellationToken);
            if (registry.Errors.Count > 0)
            {
                throw new InvalidDataException("The managed language tool registry failed validation.");
            }

            ManagedRuntimeEntry[] entries = registry.Entries
                .Where(entry => entry.Kind == row.RuntimeKind)
                .OrderByDescending(entry => entry.Version)
                .ThenBy(entry => entry.Architecture)
                .ThenBy(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (entries.Length == 0)
            {
                DetailStatusInfo.IsOpen = true;
                DetailStatusInfo.Severity = InfoBarSeverity.Warning;
                DetailStatusInfo.Title = Loc.Get("LanguageDetail.Switch.NoVersionsTitle");
                DetailStatusInfo.Message = Loc.Get("LanguageDetail.Switch.NoVersionsMessage");
                return;
            }

            GlobalRuntimeVersionChoice[] choices = entries
                .Select(entry => new GlobalRuntimeVersionChoice(
                    entry,
                    File.Exists(entry.ExecutablePath)))
                .ToArray();
            ComboBox versionPicker = new()
            {
                Header = Loc.Get("LanguageDetail.Switch.VersionHeader"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 420,
            };
            AutomationProperties.SetName(
                versionPicker,
                Loc.Get("LanguageDetail.Switch.VersionHeader"));
            ComboBoxItem? firstSelectable = null;
            foreach (GlobalRuntimeVersionChoice choice in choices)
            {
                ComboBoxItem item = new()
                {
                    Content = choice.Label,
                    IsEnabled = choice.CanSelect,
                    Tag = choice,
                };
                ToolTipService.SetToolTip(item, choice.HelpText);
                versionPicker.Items.Add(item);
                if (choice.CanSelect && firstSelectable is null)
                {
                    firstSelectable = item;
                }
            }

            versionPicker.SelectedItem = firstSelectable;
            VersionSelectionScopeChoice[] scopeChoices =
            [
                new(
                    VersionSelectionScope.Global,
                    Loc.Get("LanguageDetail.Scope.GlobalLabel"),
                    Loc.Get("LanguageDetail.Scope.GlobalDescription")),
                new(
                    VersionSelectionScope.Project,
                    Loc.Get("LanguageDetail.Scope.ProjectLabel"),
                    Loc.Get("LanguageDetail.Scope.ProjectDescription")),
                new(
                    VersionSelectionScope.NewTerminalSession,
                    Loc.Get("LanguageDetail.Scope.SessionLabel"),
                    Loc.Get("LanguageDetail.Scope.SessionDescription")),
            ];
            ComboBox scopePicker = new()
            {
                Header = Loc.Get("LanguageDetail.Scope.Header"),
                ItemsSource = scopeChoices,
                DisplayMemberPath = nameof(VersionSelectionScopeChoice.Label),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(
                scopePicker,
                Loc.Get("LanguageDetail.Scope.Header"));
            TextBlock scopeDescription = new()
            {
                Text = scopeChoices[0].Description,
                TextWrapping = TextWrapping.Wrap,
            };
            bool hasUnavailableChoices = choices.Any(choice => !choice.CanSelect);
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("LanguageDetail.Switch.SelectTitle", row.DisplayName),
                Content = new StackPanel
                {
                    MaxWidth = 680,
                    Spacing = 10,
                    Children =
                    {
                        versionPicker,
                        scopePicker,
                        scopeDescription,
                        new InfoBar
                        {
                            IsClosable = false,
                            IsOpen = true,
                            Severity = InfoBarSeverity.Informational,
                            Title = Loc.Get("LanguageDetail.Switch.PriorityInfoTitle"),
                            Message = Loc.Get("LanguageDetail.Switch.PriorityInfoMessage"),
                        },
                        new InfoBar
                        {
                            IsClosable = false,
                            IsOpen = hasUnavailableChoices,
                            Severity = InfoBarSeverity.Warning,
                            Title = Loc.Get("LanguageDetail.Switch.UnavailableInfoTitle"),
                            Message = Loc.Get("LanguageDetail.Switch.UnavailableInfoMessage"),
                        },
                    },
                },
                PrimaryButtonText = Loc.Get("LanguageDetail.Switch.SetGlobalButton"),
                CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = firstSelectable is not null,
            };
            void UpdateSelectionDialog()
            {
                dialog.IsPrimaryButtonEnabled = versionPicker.SelectedItem is ComboBoxItem
                {
                    IsEnabled: true,
                    Tag: GlobalRuntimeVersionChoice { CanSelect: true },
                };
                if (scopePicker.SelectedItem is VersionSelectionScopeChoice scope)
                {
                    scopeDescription.Text = scope.Description;
                    dialog.PrimaryButtonText = scope.Scope switch
                    {
                        VersionSelectionScope.Global => Loc.Get("LanguageDetail.Switch.SetGlobalButton"),
                        VersionSelectionScope.Project => Loc.Get("LanguageDetail.Switch.PreviewProjectButton"),
                        VersionSelectionScope.NewTerminalSession => Loc.Get(
                            "LanguageDetail.Switch.PreviewSessionButton"),
                        _ => Loc.Get("LanguageDetail.Dialogs.Continue"),
                    };
                }
            }

            versionPicker.SelectionChanged += (_, _) => UpdateSelectionDialog();
            scopePicker.SelectionChanged += (_, _) => UpdateSelectionDialog();
            UpdateSelectionDialog();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || versionPicker.SelectedItem is not ComboBoxItem
                {
                    Tag: GlobalRuntimeVersionChoice selected,
                }
                || !selected.CanSelect
                || scopePicker.SelectedItem is not VersionSelectionScopeChoice selectedScope)
            {
                DetailStatusInfo.IsOpen = true;
                DetailStatusInfo.Severity = InfoBarSeverity.Informational;
                DetailStatusInfo.Title = Loc.Get("LanguageDetail.Switch.NotChangedTitle");
                DetailStatusInfo.Message = Loc.Get("LanguageDetail.Switch.UnchangedMessage");
                return;
            }

            ManagedRuntimeEntry entry = selected.Entry;
            if (selectedScope.Scope == VersionSelectionScope.Project)
            {
                await ApplyProjectRuntimeSelectionAsync(row, entry, cancellationToken);
                return;
            }

            if (selectedScope.Scope == VersionSelectionScope.NewTerminalSession)
            {
                await OpenRuntimeSessionTerminalAsync(row, entry, cancellationToken);
                return;
            }

            SetOperationState(
                true,
                Loc.Get("LanguageDetail.Switch.BusyGlobalTitle"),
                $"{entry.Version} · {ArchitectureLabel(entry.Architecture)} · {entry.ProviderId}");
            ManagedGlobalRuntimeSelectionResult result =
                await new ManagedGlobalRuntimeSelectionService(_managedRoot).SetAsync(
                    entry.Kind,
                    new VersionSelector(VersionSelectorKind.Exact, entry.Version),
                    entry.Architecture,
                    expectedEntry: entry,
                    cancellationToken: cancellationToken);
            if (!result.Success)
            {
                DetailStatusInfo.IsOpen = true;
                DetailStatusInfo.Severity = InfoBarSeverity.Error;
                DetailStatusInfo.Title = Loc.Get("LanguageDetail.Switch.FailedTitle");
                DetailStatusInfo.Message = Loc.Get("LanguageDetail.Switch.FailedMessage");
                return;
            }

            await RefreshToolRowsAsync(cancellationToken);
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Success;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.Switch.SucceededTitle");
            DetailStatusInfo.Message = Loc.Format(
                "LanguageDetail.Switch.SucceededMessage",
                row.DisplayName,
                entry.Version,
                ArchitectureLabel(entry.Architecture),
                entry.ProviderId);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.RuntimeSwitch,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "LanguageDetail.Switch.SucceededActivity",
                    row.DisplayName,
                    entry.Version,
                    ArchitectureLabel(entry.Architecture),
                    entry.ProviderId),
                [entry.ExecutablePath]);
        }
        catch (OperationCanceledException)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.Switch.CancelledTitle");
            DetailStatusInfo.Message = Loc.Get("LanguageDetail.Switch.UnchangedMessage");
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError(Loc.Get("LanguageDetail.Switch.ErrorTitle"), exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ApplyProjectRuntimeSelectionAsync(
        ToolRow row,
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken)
    {
        string? projectRoot = await SelectProjectRootAsync();
        if (projectRoot is null)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.ProjectVersion.NotChangedTitle");
            DetailStatusInfo.Message = Loc.Get("LanguageDetail.ProjectVersion.NoProjectMessage");
            return;
        }

        ProjectPathBox.Text = projectRoot;
        SetOperationState(
            true,
            Loc.Get("LanguageDetail.ProjectVersion.BusyTitle"),
            Loc.Format(
                "LanguageDetail.ProjectVersion.BusyMessage",
                row.DisplayName,
                entry.Version));
        VersionSelector selector = new(VersionSelectorKind.Exact, entry.Version);
        ProjectToolSelectionService service = new(_managedRoot, projectRoot);
        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(
            entry,
            selector,
            cancellationToken);
        TextBox beforePreview = CreateManifestPreviewBox(
            plan.ManifestExisted
                ? plan.Before
                : Loc.Get("LanguageDetail.ProjectVersion.ManifestMissing"));
        TextBox afterPreview = CreateManifestPreviewBox(plan.After);
        StackPanel preview = new()
        {
            MaxWidth = 760,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.Get("LanguageDetail.ProjectVersion.BeforeLabel"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                beforePreview,
                new TextBlock
                {
                    Text = Loc.Get("LanguageDetail.ProjectVersion.AfterLabel"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                afterPreview,
                new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Informational,
                    Title = Loc.Get("LanguageDetail.ProjectVersion.SafeWriteInfoTitle"),
                    Message = Loc.Get("LanguageDetail.ProjectVersion.SafeWriteInfoMessage"),
                },
            },
        };
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = plan.ManifestExisted
                ? Loc.Format("LanguageDetail.ProjectVersion.UpdateTitle", row.DisplayName)
                : Loc.Format("LanguageDetail.ProjectVersion.AddTitle", row.DisplayName),
            Content = new ScrollViewer
            {
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = preview,
            },
            PrimaryButtonText = plan.Changed
                ? Loc.Get("LanguageDetail.ProjectVersion.ApplyButton")
                : Loc.Get("LanguageDetail.Dialogs.Confirm"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.ProjectVersion.NotChangedTitle");
            DetailStatusInfo.Message = Loc.Get("LanguageDetail.ProjectVersion.PreviewClosedMessage");
            return;
        }

        ProjectToolSelectionResult result = await service.ApplyAsync(plan, cancellationToken);
        if (!result.Success)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Error;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.ProjectVersion.FailedTitle");
            DetailStatusInfo.Message = result.Error
                ?? Loc.Get("LanguageDetail.ProjectVersion.FailedMessage");
            return;
        }

        ProjectInfo.IsOpen = true;
        ProjectInfo.Severity = InfoBarSeverity.Success;
        ProjectInfo.Title = Loc.Get("LanguageDetail.ProjectVersion.SavedTitle");
        ProjectInfo.Message = $"{row.DisplayName} {entry.Version} · {entry.Id} / {entry.ProviderId}";
        DetailStatusInfo.IsOpen = true;
        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = result.Changed
            ? Loc.Get("LanguageDetail.ProjectVersion.UpdatedTitle")
            : Loc.Get("LanguageDetail.ProjectVersion.AlreadySelectedTitle");
        DetailStatusInfo.Message = Loc.Format(
            "LanguageDetail.ProjectVersion.SavedMessage",
            row.DisplayName,
            entry.Version,
            projectRoot);
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeSwitch,
            ActivityStatus.Succeeded,
            Loc.Format(
                "LanguageDetail.ProjectVersion.SavedActivity",
                row.DisplayName,
                entry.Version,
                entry.Id,
                entry.ProviderId),
            [result.ManifestPath]);
    }

    private async Task OpenRuntimeSessionTerminalAsync(
        ToolRow row,
        ManagedRuntimeEntry entry,
        CancellationToken cancellationToken)
    {
        string workingDirectory = Directory.Exists(ProjectPathBox.Text)
            ? Path.GetFullPath(ProjectPathBox.Text)
            : _managedRoot;
        ProjectTerminalService service = new(_managedRoot);
        ProjectTerminalHost[] availableHosts = service.IsHostAvailable(
            ProjectTerminalHost.WindowsTerminal)
                ? [ProjectTerminalHost.WindowsTerminal, ProjectTerminalHost.WindowsPowerShell]
                : [ProjectTerminalHost.WindowsPowerShell];
        SessionTerminalHostChoice[] hostChoices = availableHosts
            .Select(host => new SessionTerminalHostChoice(host, TerminalHostLabel(host)))
            .ToArray();
        Dictionary<ProjectTerminalHost, RuntimeSessionTerminalPlan> plans = [];
        foreach (ProjectTerminalHost host in availableHosts)
        {
            plans[host] = await service.CreateRuntimeSessionPlanAsync(
                entry,
                workingDirectory,
                host,
                cancellationToken);
        }

        ComboBox hostPicker = new()
        {
            Header = Loc.Get("LanguageDetail.Session.HostHeader"),
            ItemsSource = hostChoices,
            DisplayMemberPath = nameof(SessionTerminalHostChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            hostPicker,
            Loc.Get("LanguageDetail.Session.HostHeader"));
        TextBlock previewText = new()
        {
            IsTextSelectionEnabled = true,
            MinWidth = 520,
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel content = new()
        {
            MaxWidth = 720,
            Spacing = 10,
            Children =
            {
                hostPicker,
                new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Informational,
                    Title = Loc.Get("LanguageDetail.Session.InfoTitle"),
                    Message = Loc.Get("LanguageDetail.Session.InfoMessage"),
                },
                previewText,
            },
        };
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format(
                "LanguageDetail.Session.DialogTitle",
                row.DisplayName,
                entry.Version),
            Content = new ScrollViewer
            {
                MaxHeight = 580,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            },
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        RuntimeSessionTerminalPlan selectedPlan = plans[hostChoices[0].Host];
        void UpdateTerminalPreview()
        {
            if (hostPicker.SelectedItem is not SessionTerminalHostChoice choice)
            {
                return;
            }

            selectedPlan = plans[choice.Host];
            previewText.Text = CreateRuntimeSessionPreview(selectedPlan);
            confirmation.PrimaryButtonText = selectedPlan.EffectiveHost
                == ProjectTerminalHost.WindowsTerminal
                    ? Loc.Get("LanguageDetail.Session.OpenInWindowsTerminal")
                    : Loc.Get("LanguageDetail.Session.OpenNewPowerShell");
            confirmation.IsPrimaryButtonEnabled = selectedPlan.CanLaunch;
        }

        hostPicker.SelectionChanged += (_, _) => UpdateTerminalPreview();
        UpdateTerminalPreview();
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = Loc.Get("LanguageDetail.Session.NotOpenedTitle");
            DetailStatusInfo.Message = Loc.Get("LanguageDetail.Session.NotOpenedMessage");
            return;
        }

        int processId = await service.LaunchRuntimeSessionAsync(
            selectedPlan,
            cancellationToken);
        DetailStatusInfo.IsOpen = true;
        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = Loc.Get("LanguageDetail.Session.LaunchedTitle");
        DetailStatusInfo.Message = Loc.Format(
            "LanguageDetail.Session.LaunchedMessage",
            processId,
            row.DisplayName,
            entry.Version,
            entry.Id,
            entry.ProviderId);
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeSwitch,
            ActivityStatus.Succeeded,
            Loc.Format(
                "LanguageDetail.Session.LaunchedActivity",
                row.DisplayName,
                entry.Version,
                entry.Id,
                entry.ProviderId),
            [entry.ExecutablePath, selectedPlan.ShimDirectory]);
    }

    private async Task<string?> SelectProjectRootAsync()
    {
        if (!string.IsNullOrWhiteSpace(ProjectPathBox.Text)
            && Directory.Exists(ProjectPathBox.Text))
        {
            return Path.GetFullPath(ProjectPathBox.Text);
        }

        if (((App)Application.Current).MainWindowInstance is not Window window)
        {
            return null;
        }

        FolderPicker picker = new() { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static TextBox CreateManifestPreviewBox(string text)
    {
        TextBox preview = new()
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MinHeight = 96,
            MaxHeight = 220,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
        return preview;
    }

    private static string CreateRuntimeSessionPreview(RuntimeSessionTerminalPlan plan)
    {
        System.Text.StringBuilder preview = new();
        preview.AppendLine(Loc.Format(
            "LanguageDetail.Session.Preview.WorkingDirectory",
            plan.WorkingDirectory));
        preview.AppendLine(Loc.Format(
            "LanguageDetail.Session.Preview.Host",
            TerminalHostLabel(plan.EffectiveHost)));
        if (plan.Selection is ProjectTerminalSelection selection)
        {
            preview.AppendLine(
                Loc.Format(
                    "LanguageDetail.Session.Preview.Selection",
                    selection.Kind,
                    selection.ResolvedVersion,
                    selection.RuntimeId,
                    selection.ProviderId));
        }

        preview.AppendLine($"\nShim\n{plan.ShimDirectory}");
        preview.AppendLine(
            Loc.Format(
                "LanguageDetail.Session.Preview.Network",
                plan.NetworkSummary.Applied
                    ? Loc.Get("LanguageDetail.Session.NetworkApplied")
                    : Loc.Get("LanguageDetail.Session.NetworkNone")));
        if (plan.Warnings.Count > 0)
        {
            preview.AppendLine(Loc.Get("LanguageDetail.Session.Preview.WarningsHeader"));
            foreach (string warning in plan.Warnings)
            {
                preview.AppendLine("• " + warning);
            }
        }

        if (plan.Errors.Count > 0)
        {
            preview.AppendLine(Loc.Get("LanguageDetail.Session.Preview.ErrorsHeader"));
            foreach (string error in plan.Errors)
            {
                preview.AppendLine("• " + error);
            }
        }

        return preview.ToString().TrimEnd();
    }

    private static string TerminalHostLabel(ProjectTerminalHost host) => host switch
    {
        ProjectTerminalHost.WindowsTerminal => "Windows Terminal",
        ProjectTerminalHost.WindowsPowerShell => "Windows PowerShell",
        _ => host.ToString(),
    };

    private async Task RefreshToolRowsAsync(CancellationToken cancellationToken)
    {
        Task<RegistryLoadResult> registryTask = new ManagedRuntimeRegistry(_managedRoot)
            .LoadAsync(cancellationToken);
        Task<RuntimeProfile> globalProfileTask = new GlobalRuntimeProfileStore(_managedRoot)
            .LoadAsync(cancellationToken);
        Task<CppToolchainDiscoveryResult?> cppTask = Task.FromResult(_cppDiscovery);
        await Task.WhenAll(registryTask, globalProfileTask, cppTask);

        RegistryLoadResult registry = await registryTask;
        if (registry.Errors.Count > 0)
        {
            throw new InvalidDataException("The managed language tool registry failed validation.");
        }

        LanguageToolDefinition[] visibleTools = _toolRows.Select(row => row.Tool).ToArray();
        RenderToolRows(
            visibleTools,
            registry.Entries,
            await cppTask,
            await globalProfileTask);
    }

    private void RenderToolRows(
        IReadOnlyList<LanguageToolDefinition> tools,
        IReadOnlyList<ManagedRuntimeEntry> registry,
        CppToolchainDiscoveryResult? cpp,
        RuntimeProfile globalProfile)
    {
        _toolRows = tools.Select(tool => CreateToolRow(tool, registry, cpp, globalProfile))
            .OrderByDescending(row => row.IsManagedInstall)
            .ThenByDescending(row => row.IsDetected)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        ToolList.ItemsSource = _toolRows;
        ToolList.Visibility = _toolRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyToolState.Visibility = _toolRows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OverviewToolCount.Text = _toolRows.Length.ToString();
        OverviewDetectedCount.Text = _toolRows.Count(row => row.IsDetected).ToString();
        OverviewManagedCount.Text = _toolRows.Count(row => row.IsManagedInstall).ToString();
    }

    private async Task<ToolInstallSource?> SelectToolInstallSourceAsync(
        ToolRow row,
        RuntimeKind kind,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DeclarativeRuntimeCatalogProvider> plugins =
            await new RuntimeProviderPluginRegistry(_pluginStore).ResolveByToolAsync(
                row.Tool.Id,
                cancellationToken);
        ToolInstallSource builtIn = row.ManagementKind == ToolManagementKind.WinGet
            ? new ToolInstallSource(
                Loc.Get("LanguageDetail.InstallSource.WinGetLabel"),
                Loc.Get("LanguageDetail.InstallSource.WinGetDetail"),
                null)
            : new ToolInstallSource(
                Loc.Get("LanguageDetail.InstallSource.BuiltInLabel"),
                OfficialTrustDescription(kind),
                null);
        ToolInstallSource[] choices =
        [
            builtIn,
            .. plugins.Select(provider => new ToolInstallSource(
                $"{provider.Manifest.DisplayName} · {provider.Id}",
                Loc.Format(
                    "LanguageDetail.InstallSource.PluginDetail",
                    provider.Manifest.Vendor,
                    provider.Manifest.Releases.Count),
                provider)),
        ];
        ComboBox selector = new()
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(ToolInstallSource.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            selector,
            Loc.Get("LanguageDetail.InstallSource.SelectorHeader"));
        TextBlock detail = new()
        {
            Text = builtIn.Detail,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        InfoBar thirdParty = new()
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Warning,
            Title = Loc.Get("LanguageDetail.InstallSource.ThirdPartyInfoTitle"),
            Message = Loc.Get("LanguageDetail.InstallSource.ThirdPartyInfoMessage"),
        };
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is ToolInstallSource selected)
            {
                detail.Text = selected.Detail;
                thirdParty.IsOpen = selected.PluginProvider is not null;
            }
        };
        StackPanel content = new()
        {
            MaxWidth = 640,
            Spacing = 10,
            Children = { selector, detail, thirdParty },
        };
        if (plugins.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = Loc.Get("LanguageDetail.InstallSource.NoPluginsText"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("LanguageDetail.InstallSource.SelectTitle", row.DisplayName),
            Content = content,
            PrimaryButtonText = Loc.Get("LanguageDetail.Dialogs.Continue"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? selector.SelectedItem as ToolInstallSource
            : null;
    }

    private async Task InstallWinGetToolAsync(
        ToolRow row,
        ToolchainComponent component,
        CancellationToken cancellationToken)
    {
        WingetToolchainInstaller installer = new();
        string? winget = installer.FindWinget();
        if (winget is null)
        {
            throw new InvalidOperationException(
                "WinGet was not found; cannot run the fixed allow-list install plan.");
        }

        ExternalToolInstallPlan plan = installer.CreatePlan(component, winget);
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("LanguageDetail.WinGet.InstallTitle", row.DisplayName),
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 8,
                Children =
                {
                    Detail(Loc.Get("LanguageDetail.WinGet.DetailPackageId"), plan.PackageId),
                    Detail(Loc.Get("LanguageDetail.WinGet.DetailSource"), "WinGet allow-list"),
                    Detail(
                        Loc.Get("LanguageDetail.WinGet.DetailElevation"),
                        plan.MayRequireElevation
                            ? Loc.Get("LanguageDetail.WinGet.MayRequireElevation")
                            : Loc.Get("LanguageDetail.WinGet.UsuallyNoElevation")),
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = true,
                        Severity = InfoBarSeverity.Informational,
                        Title = Loc.Get("LanguageDetail.WinGet.InfoTitle"),
                        Message = Loc.Get("LanguageDetail.WinGet.InfoMessage"),
                    },
                },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.WinGet.StartButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetOperationState(
            true,
            Loc.Format("LanguageDetail.WinGet.BusyTitle", row.DisplayName),
            plan.PackageId);
        ExternalToolInstallResult result = await installer.InstallAsync(plan, cancellationToken);
        if (!result.Success)
        {
            throw new WinGetExitCodeException(
                Loc.Format("LanguageDetail.WinGet.ExitCodeError", result.ExitCode));
        }

        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = Loc.Format(
            "LanguageDetail.WinGet.SucceededTitle",
            row.DisplayName);
        DetailStatusInfo.Message = Loc.Format(
            "LanguageDetail.WinGet.SucceededMessage",
            plan.PackageId);
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.ToolchainInstall,
            ActivityStatus.Succeeded,
            Loc.Format(
                "LanguageDetail.WinGet.SucceededActivity",
                row.DisplayName,
                plan.PackageId),
            []);
    }

    private async Task InstallArchiveToolAsync(
        ToolRow row,
        RuntimeKind kind,
        DeclarativeRuntimeCatalogProvider? pluginProvider,
        CancellationToken cancellationToken)
    {
        RuntimeArchitecture architecture = CurrentArchitecture();
        Uri? providerEndpoint = pluginProvider is null
            ? await SelectManagedProviderEndpointAsync(row, cancellationToken)
            : null;
        EffectiveNetworkSettings network = await LoadTransportSettingsAsync(kind, cancellationToken);
        using HttpClient httpClient = NetworkHttpClientFactory.Create(
            network with { Mirror = providerEndpoint },
            TimeSpan.FromMinutes(10));
        int? javaFeature = null;
        if (kind == RuntimeKind.Java && pluginProvider is null)
        {
            JavaFeatureReleaseCatalogSnapshot features = await new AdoptiumFeatureReleaseCatalog(
                httpClient,
                providerEndpoint).GetAsync(cancellationToken);
            javaFeature = features.RecommendedFeatureRelease;
        }

        IArchiveRuntimeProvider provider = pluginProvider ?? CreateOfficialProvider(
            httpClient,
            kind,
            architecture,
            javaFeature,
            providerEndpoint);
        bool isThirdParty = pluginProvider is not null;
        SetOperationState(
            true,
            isThirdParty
                ? Loc.Get("LanguageDetail.Archive.LoadingThirdPartyTitle")
                : Loc.Get("LanguageDetail.Archive.LoadingOfficialTitle"),
            provider.Id);
        RuntimeRelease[] releases = (await provider.GetReleasesAsync(cancellationToken))
            .Where(release => release.Architecture == architecture)
            .Take(30)
            .ToArray();
        if (releases.Length == 0)
        {
            throw new InvalidDataException(
                "The selected Provider has no stable release available for the current architecture.");
        }

        ComboBox releasePicker = new()
        {
            ItemsSource = releases.Select(release => new RuntimeReleaseChoice(release)).ToArray(),
            DisplayMemberPath = nameof(RuntimeReleaseChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            releasePicker,
            Loc.Get("LanguageDetail.Archive.ReleasePickerHeader"));
        ContentDialog selectDialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("LanguageDetail.Archive.SelectTitle", row.DisplayName),
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 10,
                Children =
                {
                    releasePicker,
                    new TextBlock
                    {
                        Text = kind == RuntimeKind.Java && !isThirdParty
                            ? Loc.Format("LanguageDetail.Archive.JavaFeatureLine", javaFeature)
                            : Loc.Format(
                                "LanguageDetail.Archive.ArchitectureProvider",
                                architecture,
                                provider.Id),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.Archive.PreviewButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await selectDialog.ShowAsync() != ContentDialogResult.Primary
            || releasePicker.SelectedItem is not RuntimeReleaseChoice selected)
        {
            return;
        }

        RuntimePackageAsset asset = await provider.GetAssetAsync(
            selected.Release,
            cancellationToken);
        ArchiveInstallPlan plan = provider.CreateInstallPlan(asset, _managedRoot);
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(_managedRoot).LoadAsync(
            cancellationToken);
        if (registry.Errors.Count > 0)
        {
            throw new InvalidDataException("The managed language tool registry failed validation.");
        }

        bool providerCollision = registry.Entries.Any(entry =>
            entry.Kind == selected.Release.Kind
            && entry.Version == selected.Release.Version
            && entry.Architecture == selected.Release.Architecture
            && !entry.ProviderId.Equals(selected.Release.ProviderId, StringComparison.Ordinal));
        bool supportsGlobalSelection = kind is RuntimeKind.Python
            or RuntimeKind.NodeJs
            or RuntimeKind.Java
            or RuntimeKind.DotNet;
        CheckBox setGlobalDefaultChoice = new()
        {
            Content = Loc.Get("LanguageDetail.Archive.SetGlobalCheckBox"),
            IsChecked = supportsGlobalSelection && !providerCollision && !isThirdParty,
            IsEnabled = supportsGlobalSelection && !providerCollision,
        };
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format(
                "LanguageDetail.Archive.InstallTitle",
                row.DisplayName,
                selected.Release.Version),
            Content = new StackPanel
            {
                MaxWidth = 680,
                Spacing = 8,
                Children =
                {
                    Detail("Provider", selected.Release.ProviderId),
                    Detail(Loc.Get("LanguageDetail.Archive.DetailFile"), asset.FileName),
                    Detail(asset.HashAlgorithm.DisplayName(), asset.PackageHash),
                    Detail(
                        Loc.Get("LanguageDetail.Archive.DetailInstallRoot"),
                        plan.DestinationRoot),
                    Detail(
                        Loc.Get("LanguageDetail.Archive.DetailEntry"),
                        plan.ExpectedExecutableRelativePath),
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = isThirdParty,
                        Severity = InfoBarSeverity.Warning,
                        Title = Loc.Get("LanguageDetail.Archive.ThirdPartyChecksumTitle"),
                        Message = Loc.Get("LanguageDetail.Archive.ThirdPartyChecksumMessage"),
                    },
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = providerCollision,
                        Severity = InfoBarSeverity.Warning,
                        Title = Loc.Get("LanguageDetail.Archive.ProviderCollisionTitle"),
                        Message = Loc.Get("LanguageDetail.Archive.ProviderCollisionMessage"),
                    },
                    setGlobalDefaultChoice,
                    new TextBlock
                    {
                        Text = !supportsGlobalSelection
                            ? Loc.Get("LanguageDetail.Archive.NoGlobalSelectorText")
                            : isThirdParty
                                ? Loc.Get("LanguageDetail.Archive.ThirdPartyGlobalText")
                                : Loc.Get("LanguageDetail.Archive.BuiltInGlobalText"),
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.Archive.StartButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        bool setGlobalDefault = setGlobalDefaultChoice.IsChecked == true;

        Progress<InstallProgress> progress = new(value =>
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = StageTitle(value.Stage, asset.HashAlgorithm);
            DetailStatusInfo.Message = value.TotalBytes > 0 && value.CompletedBytes is long completed
                ? $"{completed / 1_048_576d:F1} / {value.TotalBytes / 1_048_576d:F1} MB"
                : asset.FileName;
        });
        string entryId = pluginProvider is null
            ? $"{selected.Release.Kind.ToString().ToLowerInvariant()}-"
                + $"{selected.Release.Version}-"
                + selected.Release.Architecture.ToString().ToLowerInvariant()
            : pluginProvider.CreateManagedRuntimeId(selected.Release);
        ManagedRuntimeEntry entry = new(
            entryId,
            selected.Release.ProviderId,
            selected.Release.Kind,
            selected.Release.Version,
            selected.Release.Architecture,
            plan.DestinationRoot,
            plan.ExpectedExecutableRelativePath,
            asset.PackageHash,
            DateTimeOffset.UtcNow,
            selected.Release.Channels,
            asset.HashAlgorithm);
        ManagedRuntimeInstallTransactionResult result = await new ManagedRuntimeInstallCoordinator(
            _managedRoot,
            httpClient).InstallAsync(
                new ManagedRuntimeInstallRequest(plan, entry, SetGlobalDefault: setGlobalDefault),
                progress,
                cancellationToken);
        if (!result.Success)
        {
            // The coordinator's error already names the concrete failure
            // class (hash mismatch, signature failure, registry conflict,
            // disk); surface it instead of collapsing to a generic message.
            throw new ManagedInstallFailureException(
                result.Error ?? Loc.Get("LanguageDetail.Archive.FailedFallback"));
        }

        string hashEvidence = asset.PackageHash is { Length: > 0 } packageHash
            ? Loc.Format(
                "LanguageDetail.Archive.HashVerified",
                asset.HashAlgorithm,
                packageHash[..Math.Min(12, packageHash.Length)])
            : Loc.Get("LanguageDetail.Archive.HashVerifiedNoDigest");
        string globalDefaultEvidence = setGlobalDefault
            ? result.GlobalDefaultUpdated
                ? Loc.Get("LanguageDetail.Archive.GlobalDefaultSet")
                : Loc.Get("LanguageDetail.Archive.GlobalDefaultUnchanged")
            : string.Empty;
        string pendingCleanupEvidence = result.PendingCleanup
            ? Loc.Get("LanguageDetail.Archive.PendingCleanup")
            : string.Empty;
        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = result.InstallOutcome == InstallOutcome.AlreadyInstalled
            ? Loc.Get("LanguageDetail.Archive.AlreadyInstalledTitle")
            : Loc.Get("LanguageDetail.Install.Completed");
        DetailStatusInfo.Message = string.Join(" ", new[]
        {
            $"{row.DisplayName} {selected.Release.Version} · {selected.Release.ProviderId} · {result.InstallRoot}",
            hashEvidence,
            globalDefaultEvidence,
            pendingCleanupEvidence,
        }.Where(part => part.Length > 0));
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeInstall,
            ActivityStatus.Succeeded,
            Loc.Format(
                "LanguageDetail.Archive.SucceededActivity",
                _language.DisplayName,
                row.DisplayName,
                selected.Release.Version),
            [result.InstallRoot ?? plan.DestinationRoot]);
        try
        {
            // Close the feedback loop: without this refresh the row's managed
            // version summary and the switch/uninstall buttons stay stale
            // until the user manually rescans.
            await RefreshToolRowsAsync(CancellationToken.None);
            InventoryUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (IsExpectedException(exception)
            || exception is OperationCanceledException)
        {
            DetailStatusInfo.Message += Loc.Get("LanguageDetail.Archive.RefreshFailedSuffix");
        }
    }

    private async Task<Uri> SelectManagedProviderEndpointAsync(
        ToolRow row,
        CancellationToken cancellationToken)
    {
        LanguageToolProviderDefinition managedProvider = row.Tool.Providers.FirstOrDefault(
            provider => provider.ManagedInstallSupported)
            ?? throw new InvalidOperationException(
                "The language tool has no Provider that supports built-in archive installs.");
        ProviderSourceListResolutionResult resolved = await _sourceStore.ResolveProviderAsync(
            _availableCatalog,
            row.Tool.Id,
            managedProvider.Id,
            cancellationToken);
        if (!resolved.Success)
        {
            throw new ProviderSourcePreferenceException(resolved.Errors[0]);
        }

        ResolvedProviderSource[] sources = resolved.Sources
            .Where(source => source.EndpointKind == ProviderMirrorEndpointKind.GenericDownload
                && source.IsEnabled)
            .OrderBy(source => source.Origin == ProviderSourceOrigin.Custom ? 1 : 0)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException(
                "The built-in Provider has no enabled compatible download/metadata endpoint.");
        }

        if (sources.Length == 1)
        {
            if (!await ConfirmProviderEndpointTrustAsync(row, sources[0]))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return sources[0].ConfiguredEndpoint;
        }

        ProviderEndpointChoice[] choices = sources.Select(source => new ProviderEndpointChoice(
            source,
            $"{source.DisplayName} · {SourceOriginName(source.Origin)} · "
                + source.ConfiguredEndpoint.AbsoluteUri)).ToArray();
        ComboBox selector = new()
        {
            Header = Loc.Get("LanguageDetail.Endpoint.SelectorHeader"),
            ItemsSource = choices,
            DisplayMemberPath = nameof(ProviderEndpointChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            selector,
            Loc.Get("LanguageDetail.Endpoint.SelectorHeader"));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("LanguageDetail.Endpoint.SelectTitle", row.DisplayName),
            Content = new StackPanel
            {
                MaxWidth = 680,
                Spacing = 8,
                Children =
                {
                    selector,
                    new TextBlock
                    {
                        Text = Loc.Get("LanguageDetail.Endpoint.SelectHint"),
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.Endpoint.UseButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || selector.SelectedItem is not ProviderEndpointChoice selected)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (!await ConfirmProviderEndpointTrustAsync(row, selected.Source))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return selected.Source.ConfiguredEndpoint;
    }

    private async Task<bool> ConfirmProviderEndpointTrustAsync(
        ToolRow row,
        ResolvedProviderSource source)
    {
        if (source.Origin == ProviderSourceOrigin.CatalogDefault)
        {
            return true;
        }

        bool dotNet = row.RuntimeKind == RuntimeKind.DotNet;
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("LanguageDetail.Endpoint.NonDefaultTitle"),
            Content = new StackPanel
            {
                MaxWidth = 640,
                Spacing = 8,
                Children =
                {
                    Detail(
                        Loc.Get("LanguageDetail.Endpoint.DetailEndpoint"),
                        source.ConfiguredEndpoint.AbsoluteUri),
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = true,
                        Severity = dotNet ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                        Title = dotNet
                            ? Loc.Get("LanguageDetail.Endpoint.DotNetInfoTitle")
                            : Loc.Get("LanguageDetail.Endpoint.SignatureInfoTitle"),
                        Message = dotNet
                            ? Loc.Get("LanguageDetail.Endpoint.DotNetInfoMessage")
                            : Loc.Get("LanguageDetail.Endpoint.SignatureInfoMessage"),
                    },
                },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.Endpoint.ContinueButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RenderMirrorSourcesAsync()
    {
        List<MirrorRow> rows = [];
        foreach (LanguageToolDefinition tool in _catalog.GetToolsForLanguage(_language.Id))
        {
            foreach (LanguageToolProviderDefinition provider in tool.Providers)
            {
                ProviderSourceListResolutionResult resolved = await _sourceStore.ResolveProviderAsync(
                    _availableCatalog,
                    tool.Id,
                    provider.Id,
                    _pageCancellation.Token);
                if (!resolved.Success)
                {
                    throw new ProviderSourcePreferenceException(resolved.Errors[0]);
                }

                foreach (ResolvedProviderSource source in resolved.Sources)
                {
                    ProviderMirrorSlotDefinition? slot = provider.MirrorSlots.FirstOrDefault(
                        candidate => candidate.Id.Equals(
                            source.Owner.SlotId,
                            StringComparison.OrdinalIgnoreCase));
                    rows.Add(new MirrorRow(
                        _language,
                        tool,
                        provider,
                        source,
                        slot?.UserOverridable ?? true));
                }
            }
        }

        MirrorRow[] ordered = rows
            .OrderBy(row => row.ToolDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ProviderDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        MirrorList.ItemsSource = ordered;
        MirrorList.Visibility = ordered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyMirrorState.Visibility = ordered.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MirrorInfo.Message = ordered.Length == 0
            ? Loc.Get("LanguageDetail.Sources.EmptyInfoMessage")
            : Loc.Format(
                "LanguageDetail.Sources.Summary",
                ordered.Length,
                ordered.Count(row => row.Source.Origin == ProviderSourceOrigin.UserOverride),
                ordered.Count(row => row.Source.Origin == ProviderSourceOrigin.Custom));
    }

    private async void OnEditMirrorClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row })
        {
            return;
        }

        try
        {
            if (row.Source.Origin == ProviderSourceOrigin.Custom)
            {
                await _sourceStore.SetCustomSourceEnabledAsync(
                    _availableCatalog,
                    row.Source.Owner,
                    !row.Source.IsEnabled,
                    _pageCancellation.Token);
            }
            else
            {
                TextBox endpoint = new()
                {
                    Header = Loc.Get("LanguageDetail.Sources.EndpointHeader"),
                    Text = row.Source.ConfiguredEndpoint.AbsoluteUri,
                    PlaceholderText = "https://mirror.example/",
                };
                ContentDialog dialog = new()
                {
                    XamlRoot = XamlRoot,
                    Title = Loc.Format("LanguageDetail.Sources.EditTitle", row.DisplayName),
                    Content = endpoint,
                    PrimaryButtonText = Loc.Get("LanguageDetail.Sources.SaveOverrideButton"),
                    CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                await _sourceStore.SetBuiltInOverrideAsync(
                    _availableCatalog,
                    row.Source.Owner,
                    endpoint.Text,
                    _pageCancellation.Token);
            }

            await RenderMirrorSourcesAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            ShowSafeError(Loc.Get("LanguageDetail.Sources.SaveFailedTitle"), exception);
        }
    }

    private async void OnResetMirrorClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row })
        {
            return;
        }

        bool isCustomDeletion = row.Source.Origin == ProviderSourceOrigin.Custom;
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = isCustomDeletion
                ? Loc.Format("LanguageDetail.Sources.DeleteTitle", row.DisplayName)
                : Loc.Format("LanguageDetail.Sources.ResetTitle", row.DisplayName),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = isCustomDeletion
                    ? Loc.Format("LanguageDetail.Sources.DeleteContent", row.Endpoint)
                    : Loc.Format("LanguageDetail.Sources.ResetContent", row.Endpoint),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = isCustomDeletion
                ? Loc.Get("LanguageDetail.Actions.DeleteCustom")
                : Loc.Get("LanguageDetail.Sources.ResetButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (isCustomDeletion)
            {
                await _sourceStore.DeleteCustomSourceAsync(
                    _availableCatalog,
                    row.Source.Owner,
                    _pageCancellation.Token);
            }
            else
            {
                await _sourceStore.RestoreBuiltInDefaultAsync(
                    _availableCatalog,
                    row.Source.Owner,
                    _pageCancellation.Token);
            }

            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                isCustomDeletion
                    ? Loc.Format(
                        "LanguageDetail.Sources.DeletedActivity",
                        _language.DisplayName,
                        row.DisplayName)
                    : Loc.Format(
                        "LanguageDetail.Sources.RestoredActivity",
                        _language.DisplayName,
                        row.DisplayName),
                [row.Source.Owner.ToString()]);
            await RenderMirrorSourcesAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            ShowSafeError(Loc.Get("LanguageDetail.Sources.UpdateFailedTitle"), exception);
        }
    }

    private async void OnAddCustomSourceClicked(object sender, RoutedEventArgs args)
    {
        ProviderChoice[] providers = _catalog.GetToolsForLanguage(_language.Id)
            .SelectMany(tool => tool.Providers.Select(provider => new ProviderChoice(tool, provider)))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (providers.Length == 0)
        {
            return;
        }

        ComboBox providerPicker = new()
        {
            Header = Loc.Get("LanguageDetail.Sources.ProviderHeader"),
            ItemsSource = providers,
            DisplayMemberPath = nameof(ProviderChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            providerPicker,
            Loc.Get("LanguageDetail.Sources.ProviderHeader"));
        TextBox slotId = new()
        {
            Header = Loc.Get("LanguageDetail.Sources.SlotIdHeader"),
            PlaceholderText = "company-mirror",
        };
        TextBox displayName = new()
        {
            Header = Loc.Get("LanguageDetail.Sources.NameHeader"),
            PlaceholderText = Loc.Get("LanguageDetail.Sources.NamePlaceholder"),
        };
        TextBox endpoint = new()
        {
            Header = Loc.Get("LanguageDetail.Sources.EndpointHeader"),
            PlaceholderText = "https://mirror.example/",
        };
        TextBox purpose = new()
        {
            Header = Loc.Get("LanguageDetail.Sources.PurposeHeader"),
            PlaceholderText = Loc.Get("LanguageDetail.Sources.PurposePlaceholder"),
        };
        ComboBox kindPicker = new()
        {
            Header = Loc.Get("LanguageDetail.Sources.KindHeader"),
            ItemsSource = Enum.GetValues<ProviderMirrorEndpointKind>(),
            SelectedItem = ProviderMirrorEndpointKind.GenericDownload,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            kindPicker,
            Loc.Get("LanguageDetail.Sources.KindHeader"));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("LanguageDetail.Sources.AddTitle"),
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 8,
                Children = { providerPicker, slotId, displayName, endpoint, purpose, kindPicker },
            },
            PrimaryButtonText = Loc.Get("LanguageDetail.Sources.AddConfirmButton"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || providerPicker.SelectedItem is not ProviderChoice selected
            || kindPicker.SelectedItem is not ProviderMirrorEndpointKind endpointKind)
        {
            return;
        }

        try
        {
            await _sourceStore.AddCustomSourceAsync(
                _availableCatalog,
                new ProviderSourceOwner(selected.Tool.Id, selected.Provider.Id, slotId.Text),
                displayName.Text,
                endpoint.Text,
                endpointKind,
                purpose.Text,
                enabled: true,
                _pageCancellation.Token);
            await RenderMirrorSourcesAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            ShowSafeError(Loc.Get("LanguageDetail.Sources.AddFailedTitle"), exception);
        }
    }

    private async void OnChooseProjectFolderClicked(object sender, RoutedEventArgs args)
    {
        if (((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        FolderPicker picker = new() { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ProjectPathBox.Text = folder.Path;
        }
    }

    private async void OnParseProjectClicked(object sender, RoutedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(ProjectPathBox.Text))
        {
            ProjectInfo.Severity = InfoBarSeverity.Warning;
            ProjectInfo.Title = Loc.Get("LanguageDetail.ProjectParse.SelectFolderTitle");
            ProjectInfo.Message = string.Empty;
            return;
        }

        try
        {
            ProjectInfo.Severity = InfoBarSeverity.Informational;
            ProjectInfo.Title = Loc.Get("LanguageDetail.ProjectParse.BusyTitle");
            ProjectInfo.Message = Loc.Get("LanguageDetail.ProjectParse.BusyMessage");
            string projectPath = ProjectPathBox.Text;
            ProjectVirtualEnvironmentDiscoveryResult result = await Task.Run(
                () => new ProjectVirtualEnvironmentDiscoveryService().Discover(
                    projectPath,
                    cancellationToken: _pageCancellation.Token),
                _pageCancellation.Token);
            string environmentLanguageId = ProjectEnvironmentLanguageId(_language.Id);
            ProjectEnvironmentRow[] rows = result.Environments
                .Where(environment => environment.LanguageId.Equals(
                    environmentLanguageId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(environment => new ProjectEnvironmentRow(environment))
                .ToArray();
            ProjectEnvironmentList.ItemsSource = rows;
            ProjectInfo.Severity = rows.Any(row => row.Health != ProjectVirtualEnvironmentHealth.Healthy)
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            ProjectInfo.Title = rows.Length == 0
                ? Loc.Format(
                    "LanguageDetail.ProjectParse.NoneFoundTitle",
                    _language.DisplayName)
                : Loc.Format(
                    "LanguageDetail.ProjectParse.FoundTitle",
                    rows.Length,
                    _language.DisplayName);
            ProjectInfo.Message = Loc.Format(
                "LanguageDetail.ProjectParse.CheckedPaths",
                result.InspectedPathCount)
                + (result.ScanLimitReached
                    ? Loc.Get("LanguageDetail.ProjectParse.ScanLimitSuffix")
                    : string.Empty)
                + (result.Warnings.Count > 0
                    ? Loc.Format(
                        "LanguageDetail.ProjectParse.WarningCountSuffix",
                        result.Warnings.Count)
                    : string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = Loc.Get("LanguageDetail.ProjectParse.FailedTitle");
            ProjectInfo.Message = Loc.Get("LanguageDetail.ProjectParse.FailedMessage");
        }
    }

    private void RenderProviderPlugins(RuntimeProviderPluginListResult result)
    {
        IReadOnlySet<string> toolIds = _catalog.GetToolsForLanguage(_language.Id)
            .Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProviderPluginRow[] rows = result.Plugins
            .Where(plugin => toolIds.Contains(plugin.Manifest.LanguageToolId))
            .Select(plugin => new ProviderPluginRow(_language, plugin))
            .OrderBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        ProviderPluginList.ItemsSource = rows;
        ProviderPluginList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyProviderPluginText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnImportProviderPluginClicked(object sender, RoutedEventArgs args)
    {
        if (((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = Loc.Get("LanguageDetail.Plugins.PickerCommitButton"),
        };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            RuntimeProviderPluginImportPreview preview = await _pluginStore.PreviewImportAsync(
                file.Path,
                _pageCancellation.Token);
            if (!_catalog.GetToolsForLanguage(_language.Id).Any(tool => tool.Id.Equals(
                    preview.Manifest.LanguageToolId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new RuntimeProviderPluginException(
                    RuntimeProviderPluginErrorCode.InvalidManifest,
                    "The Provider plugin language tool does not belong to this language.");
            }

            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("LanguageDetail.Plugins.ImportTitle", preview.Manifest.DisplayName),
                Content = new StackPanel
                {
                    MaxWidth = 620,
                    Spacing = 8,
                    Children =
                    {
                        Detail(Loc.Get("LanguageDetail.Plugins.DetailLanguage"), _language.DisplayName),
                        Detail(
                            Loc.Get("LanguageDetail.Common.LanguageTool"),
                            preview.Manifest.LanguageToolId),
                        Detail(
                            Loc.Get("LanguageDetail.Plugins.DetailAdapterKind"),
                            preview.Manifest.Kind.ToString()),
                        Detail("Provider", preview.Manifest.ProviderId),
                        Detail(
                            Loc.Get("LanguageDetail.Plugins.DetailContents"),
                            Loc.Format(
                                "LanguageDetail.Plugins.ContentsValue",
                                preview.ReleaseCount,
                                preview.AssetCount)),
                        new InfoBar
                        {
                            IsClosable = false,
                            IsOpen = true,
                            Severity = InfoBarSeverity.Warning,
                            Title = Loc.Get("LanguageDetail.Plugins.ThirdPartyInfoTitle"),
                            Message = Loc.Get("LanguageDetail.Plugins.ThirdPartyInfoMessage"),
                        },
                    },
                },
                PrimaryButtonText = Loc.Get("LanguageDetail.Plugins.ImportButton"),
                CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _pluginStore.ImportAsync(preview, _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProviderPluginImport,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "LanguageDetail.Plugins.ImportedActivity",
                    _language.DisplayName,
                    preview.Manifest.DisplayName),
                [preview.Manifest.LanguageToolId]);
            RenderProviderPlugins(await _pluginStore.ListAsync(_pageCancellation.Token));
        }
        catch (RuntimeProviderPluginException exception)
        {
            ShowSafeError(Loc.Get("LanguageDetail.Plugins.ImportFailedTitle"), exception);
        }
    }

    private async void OnToggleProviderPluginClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ProviderPluginRow row })
        {
            return;
        }

        try
        {
            if (row.IsEnabled)
            {
                await _pluginStore.DisableAsync(row.Id, _pageCancellation.Token);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.ProviderPluginStateChange,
                    ActivityStatus.Succeeded,
                    Loc.Format(
                        "LanguageDetail.Plugins.DisabledActivity",
                        _language.DisplayName,
                        row.DisplayName),
                    [row.ToolId]);
            }
            else
            {
                ContentDialog confirmation = new()
                {
                    XamlRoot = XamlRoot,
                    Title = Loc.Format("LanguageDetail.Plugins.EnableTitle", row.DisplayName),
                    Content = Loc.Get("LanguageDetail.Plugins.EnableContent"),
                    PrimaryButtonText = Loc.Get("LanguageDetail.Plugins.EnableButton"),
                    CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                await _pluginStore.EnableAsync(row.Id, _pageCancellation.Token);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.ProviderPluginStateChange,
                    ActivityStatus.Succeeded,
                    Loc.Format(
                        "LanguageDetail.Plugins.EnabledActivity",
                        _language.DisplayName,
                        row.DisplayName),
                    [row.ToolId]);
            }

            RenderProviderPlugins(await _pluginStore.ListAsync(_pageCancellation.Token));
        }
        catch (RuntimeProviderPluginException exception)
        {
            ShowSafeError(Loc.Get("LanguageDetail.Plugins.ToggleFailedTitle"), exception);
        }
    }

    private async void OnDeleteProviderPluginClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: ProviderPluginRow row })
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("LanguageDetail.Plugins.DeleteTitle", row.DisplayName),
            Content = Loc.Get("LanguageDetail.Plugins.DeleteContent"),
            PrimaryButtonText = Loc.Get("LanguageDetail.Dialogs.Delete"),
            CloseButtonText = Loc.Get("LanguageDetail.Dialogs.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _pluginStore.DeleteAsync(row.Id, _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProviderPluginDelete,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "LanguageDetail.Plugins.DeletedActivity",
                    _language.DisplayName,
                    row.DisplayName),
                [row.ToolId]);
            RenderProviderPlugins(await _pluginStore.ListAsync(_pageCancellation.Token));
        }
        catch (RuntimeProviderPluginException exception)
        {
            ShowSafeError(Loc.Get("LanguageDetail.Plugins.DeleteFailedTitle"), exception);
        }
    }

    private async Task<EffectiveNetworkSettings> LoadTransportSettingsAsync(
        RuntimeKind kind,
        CancellationToken cancellationToken)
    {
        NetworkSettingsLoadResult loaded = await new NetworkSettingsStore(_managedRoot).LoadAsync(
            cancellationToken);
        if (!loaded.Success || loaded.Settings is null)
        {
            throw new InvalidDataException("The network transport settings failed validation.");
        }

        if (!NetworkToolIds.TryGetRuntimeScope(kind, out string toolId))
        {
            throw new NotSupportedException("The language tool has no network scope.");
        }

        NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(
            loaded.Settings,
            toolId);
        return resolved.Success && resolved.EffectiveSettings is not null
            ? resolved.EffectiveSettings
            : throw new InvalidDataException("The network transport policy could not be resolved.");
    }

    private static IArchiveRuntimeProvider CreateOfficialProvider(
        HttpClient httpClient,
        RuntimeKind kind,
        RuntimeArchitecture architecture,
        int? javaFeature,
        Uri? providerEndpoint) => kind switch
        {
            RuntimeKind.Python => new PythonOrgCatalogProvider(
                httpClient,
                architecture,
                apiBaseUri: providerEndpoint),
            RuntimeKind.NodeJs => new NodeJsCatalogProvider(httpClient, baseUri: providerEndpoint),
            RuntimeKind.Java => new AdoptiumCatalogProvider(
                httpClient,
                javaFeature ?? throw new InvalidOperationException("Missing JDK feature release line."),
                architecture,
                baseUri: providerEndpoint),
            RuntimeKind.DotNet => new DotNetSdkCatalogProvider(
                httpClient,
                architecture,
                indexUri: providerEndpoint),
            _ => throw new NotSupportedException("This tool has no built-in official archive Provider."),
        };

    private static string OfficialTrustDescription(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Python =>
            Loc.Get("LanguageDetail.Trust.Python"),
        RuntimeKind.NodeJs =>
            Loc.Get("LanguageDetail.Trust.NodeJs"),
        RuntimeKind.Java =>
            Loc.Get("LanguageDetail.Trust.Java"),
        RuntimeKind.DotNet =>
            Loc.Get("LanguageDetail.Trust.DotNet"),
        _ => Loc.Get("LanguageDetail.Trust.Default"),
    };

    private static ToolchainComponent? ToolchainComponentFor(string toolId) => toolId switch
    {
        "msvc-build-tools" => ToolchainComponent.MsvcBuildTools,
        "clang" => ToolchainComponent.Llvm,
        "gcc" => ToolchainComponent.MinGw,
        "cmake" => ToolchainComponent.CMake,
        "ninja" => ToolchainComponent.Ninja,
        _ => null,
    };

    private static bool IsExperimentalTool(LanguageToolDefinition tool) =>
        tool.WindowsSupport is LanguageToolWindowsSupport.Conditional
            or LanguageToolWindowsSupport.Unsupported;

    private static bool SupportsVersionSwitch(RuntimeKind? kind) => kind is
        RuntimeKind.Python
            or RuntimeKind.NodeJs
            or RuntimeKind.Java
            or RuntimeKind.DotNet
            or RuntimeKind.Msvc
            or RuntimeKind.Llvm
            or RuntimeKind.Mingw
            or RuntimeKind.CMake
            or RuntimeKind.Ninja;

    private static string ArchitectureLabel(RuntimeArchitecture architecture) =>
        architecture.ToString().ToLowerInvariant();

    private CancellationToken BeginOperation()
    {
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        OperationProgress.IsActive = true;
        CancelOperationButton.IsEnabled = true;
        return _operationCancellation.Token;
    }

    private void EndOperation()
    {
        OperationProgress.IsActive = false;
        CancelOperationButton.IsEnabled = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private void SetOperationState(
        bool busy,
        string title,
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        OperationProgress.IsActive = busy;
        CancelOperationButton.IsEnabled = busy && _operationCancellation is not null;
        DetailStatusInfo.IsOpen = true;
        DetailStatusInfo.Severity = severity;
        DetailStatusInfo.Title = title;
        DetailStatusInfo.Message = message;
    }

    private void ShowSafeError(string title, Exception exception)
    {
        DetailStatusInfo.IsOpen = true;
        DetailStatusInfo.Severity = InfoBarSeverity.Error;
        DetailStatusInfo.Title = title;
        DetailStatusInfo.Message = exception switch
        {
            HttpRequestException http when http.StatusCode is not null =>
                Loc.Format("LanguageDetail.Errors.HttpError", (int)http.StatusCode.Value),
            HttpRequestException => Loc.Get("LanguageDetail.Errors.HttpConnectionFailed"),
            RuntimeProviderPluginException plugin =>
                Loc.Format("LanguageDetail.Errors.PluginValidationFailed", plugin.Code),
            ProviderSourcePreferenceException source =>
                Loc.Format("LanguageDetail.Errors.SourceValidationFailed", source.Error.Code),
            LanguagePackException pack =>
                Loc.Format("LanguageDetail.Errors.LanguagePackValidationFailed", pack.Code),
            // The coordinator's message names the concrete failure class and
            // is produced for user display; echo it verbatim (localized).
            ManagedInstallFailureException => CoreErrorText.Localize(exception.Message),
            // The WinGet exit code message is already localized when thrown.
            WinGetExitCodeException => exception.Message,
            _ => Loc.Get("LanguageDetail.Errors.Generic"),
        };
    }

    private sealed class ManagedInstallFailureException(string message)
        : InvalidOperationException(message);

    private sealed class WinGetExitCodeException(string message)
        : InvalidOperationException(message);

    private static bool IsExpectedException(Exception exception) => exception is HttpRequestException
        or InvalidDataException
        or InvalidOperationException
        or NotSupportedException
        or KeyNotFoundException
        or IOException
        or UnauthorizedAccessException
        or RuntimeProviderPluginException
        or ProviderSourcePreferenceException
        or LanguagePackException
        or System.ComponentModel.Win32Exception
        or System.Security.SecurityException;

    private static StackPanel Detail(string label, string value) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            new TextBlock
            {
                Text = value,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            },
        },
    };

    private static RuntimeArchitecture CurrentArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => RuntimeArchitecture.X86,
            Architecture.Arm64 => RuntimeArchitecture.Arm64,
            _ => RuntimeArchitecture.X64,
        };

    private static string StageTitle(string stage, PackageHashAlgorithm algorithm) => stage switch
    {
        "download" => Loc.Get("LanguageDetail.Stage.Downloading"),
        "verify" => Loc.Format("LanguageDetail.Stage.Verifying", algorithm.DisplayName()),
        "extract" => Loc.Get("LanguageDetail.Stage.Extracting"),
        "commit" => Loc.Get("LanguageDetail.Stage.Committing"),
        "complete" => Loc.Get("LanguageDetail.Install.Completed"),
        _ => stage,
    };

    private static string ProjectEnvironmentLanguageId(string languageId) => languageId switch
    {
        "javascript" or "typescript" => "nodejs",
        "csharp" or "fsharp" or "visual-basic" => "dotnet",
        _ => languageId,
    };

    private static string SourceOriginName(ProviderSourceOrigin origin) => origin switch
    {
        ProviderSourceOrigin.CatalogDefault => Loc.Get("LanguageDetail.SourceOrigin.Default"),
        ProviderSourceOrigin.UserOverride => Loc.Get("LanguageDetail.SourceOrigin.Override"),
        ProviderSourceOrigin.Custom => Loc.Get("LanguageDetail.SourceOrigin.Custom"),
        _ => origin.ToString(),
    };

    private enum VersionSelectionScope
    {
        Global,
        Project,
        NewTerminalSession,
    }

    private sealed record VersionSelectionScopeChoice(
        VersionSelectionScope Scope,
        string Label,
        string Description);

    private sealed record SessionTerminalHostChoice(
        ProjectTerminalHost Host,
        string Label);

    private sealed record GlobalRuntimeVersionChoice(
        ManagedRuntimeEntry Entry,
        bool ExecutableExists)
    {
        public bool CanSelect => ExecutableExists;

        public string Label => $"{Entry.Version} · {ArchitectureLabel(Entry.Architecture)} · "
            + Entry.ProviderId
            + (CanSelect ? string.Empty : Loc.Get("LanguageDetail.VersionChoice.UnavailableSuffix"));

        public string HelpText => !ExecutableExists
                ? Loc.Get("LanguageDetail.VersionChoice.MissingEntryHelp")
                : Loc.Format(
                    "LanguageDetail.VersionChoice.Help",
                    Entry.Id,
                    Entry.ProviderId);
    }

    private sealed record ManagedRuntimeChoice(ManagedRuntimeEntry Entry)
    {
        public string Label => $"{Entry.Version} · Runtime ID {Entry.Id} · "
            + $"Provider {Entry.ProviderId} · {ArchitectureLabel(Entry.Architecture)}";
    }

    private sealed record ToolMetadataLink(
        string Label,
        Uri NavigateUri,
        string AutomationName);

    private sealed class ToolRow
    {
        public ToolRow(
            LanguageDefinition language,
            LanguageToolDefinition tool,
            bool detected,
            string versionSummary,
            RuntimeKind? runtimeKind,
            ToolchainComponent? toolchainComponent,
            ToolManagementKind managementKind,
            IReadOnlyList<ManagedRuntimeEntry> managedEntries)
        {
            Tool = tool;
            IsDetected = detected;
            VersionSummary = versionSummary;
            RuntimeKind = runtimeKind;
            ToolchainComponent = toolchainComponent;
            ManagementKind = managementKind;
            ManagedEntries = managedEntries;
            HierarchySummary = $"Language {LanguageLabel(language)} → Language Tool {tool.DisplayName} [{tool.Id}]";
            RoleSummary = string.Join(" · ", tool.Roles.Select(RoleName));
            CatalogCapabilitySummary = BuildCapabilitySummary(tool.Capabilities);
            ToolMetadataSummary = Loc.Format(
                "LanguageDetail.ToolRow.MetadataSummary",
                tool.Vendor,
                tool.License,
                WindowsSupportName(tool.WindowsSupport));
            OperationSummary = managementKind switch
            {
                ToolManagementKind.OfficialArchive => Loc.Format(
                    "LanguageDetail.ToolRow.OperationOfficialArchive",
                    managedEntries.Count),
                ToolManagementKind.WinGet =>
                    Loc.Get("LanguageDetail.ToolRow.OperationWinGet"),
                _ => Loc.Get("LanguageDetail.ToolRow.OperationNone"),
            };
            ProviderSummary = tool.Providers.Count == 0
                ? Loc.Get("LanguageDetail.ToolRow.NoProviders")
                : Loc.Get("LanguageDetail.ToolRow.ProvidersPrefix") + string.Join(
                    " · ",
                    tool.Providers.Select(provider => BuildProviderSummary(
                        LanguageToolProviderProfile.Create(tool, provider))));
            MetadataLinks =
            [
                new ToolMetadataLink(
                    Loc.Get("LanguageDetail.ToolRow.VendorSiteLink"),
                    tool.Homepage,
                    Loc.Format("LanguageDetail.ToolRow.VendorSiteLinkAutomation", tool.Vendor)),
                new ToolMetadataLink(
                    Loc.Get("LanguageDetail.ToolRow.LicenseLink"),
                    LanguageToolUiPolicy.GetLicenseReferenceUri(tool),
                    Loc.Format("LanguageDetail.ToolRow.LicenseLinkAutomation", tool.License)),
                new ToolMetadataLink(
                    Loc.Get("LanguageDetail.Homepage.Label"),
                    tool.Homepage,
                    Loc.Format("LanguageDetail.ToolRow.HomepageLinkAutomation", tool.DisplayName)),
                new ToolMetadataLink(
                    Loc.Get("LanguageDetail.ToolRow.ManualInstallLink"),
                    tool.Homepage,
                    Loc.Format(
                        "LanguageDetail.ToolRow.ManualInstallLinkAutomation",
                        tool.DisplayName)),
            ];
        }

        public LanguageToolDefinition Tool { get; }

        public string DisplayName => Tool.DisplayName;

        public bool IsDetected { get; }

        public bool IsManagedInstall => ManagementKind != ToolManagementKind.None
            && Tool.Capabilities.Install;

        public string RoleSummary { get; }

        public string HierarchySummary { get; }

        public string VersionSummary { get; }

        public string CatalogCapabilitySummary { get; }

        public string ToolMetadataSummary { get; }

        public string OperationSummary { get; }

        public string ProviderSummary { get; }

        public IReadOnlyList<ToolMetadataLink> MetadataLinks { get; }

        public RuntimeKind? RuntimeKind { get; }

        public ToolchainComponent? ToolchainComponent { get; }

        public ToolManagementKind ManagementKind { get; }

        public IReadOnlyList<ManagedRuntimeEntry> ManagedEntries { get; }

        public Visibility DetectedVisibility => IsDetected
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility ManagedVisibility => IsManagedInstall
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility MetadataVisibility => IsManagedInstall
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility ManageActionVisibility => IsManagedInstall
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility SwitchActionVisibility =>
            SupportsVersionSwitch(RuntimeKind) && ManagedEntries.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility UninstallActionVisibility => ManagedEntries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string ManageActionText => ManagementKind == ToolManagementKind.WinGet
            ? Loc.Get("LanguageDetail.ToolRow.ManageWinGetText")
            : Loc.Get("LanguageDetail.ToolRow.ManageInstallText");

        public string AutomationName => $"{DisplayName}。{OperationSummary}。{VersionSummary}";

        public string SwitchActionAutomationName => Loc.Format(
            "LanguageDetail.ToolRow.SwitchActionAutomation",
            DisplayName);

        public string UninstallActionAutomationName => Loc.Format(
            "LanguageDetail.ToolRow.UninstallActionAutomation",
            DisplayName);

        public string ManageActionAutomationName => $"{ManageActionText}：{DisplayName}";

        private static string BuildCapabilitySummary(LanguageToolCapabilities capabilities)
        {
            List<string> values = [];
            if (capabilities.Discover) values.Add(Loc.Get("LanguageDetail.Capability.Discover"));
            if (capabilities.Install) values.Add(Loc.Get("LanguageDetail.Capability.Install"));
            if (capabilities.VersionSwitch)
            {
                values.Add(Loc.Get("LanguageDetail.Capability.VersionSwitch"));
            }

            if (capabilities.ProjectPin) values.Add(Loc.Get("LanguageDetail.Capability.ProjectPin"));
            if (capabilities.PackageManagement)
            {
                values.Add(Loc.Get("LanguageDetail.Capability.PackageManagement"));
            }

            if (capabilities.VirtualEnvironment)
            {
                values.Add(Loc.Get("LanguageDetail.Capability.VirtualEnvironment"));
            }

            if (capabilities.MirrorConfiguration)
            {
                values.Add(Loc.Get("LanguageDetail.Capability.MirrorConfiguration"));
            }

            if (capabilities.Debug) values.Add(Loc.Get("LanguageDetail.Capability.Debug"));
            if (capabilities.Format) values.Add(Loc.Get("LanguageDetail.Capability.Format"));
            if (capabilities.Lint) values.Add(Loc.Get("LanguageDetail.Capability.Lint"));
            return values.Count == 0
                ? Loc.Get("LanguageDetail.ToolRow.CapabilityNone")
                : Loc.Get("LanguageDetail.ToolRow.CapabilitySummaryPrefix")
                    + string.Join(" · ", values);
        }

        private static string BuildProviderSummary(LanguageToolProviderProfile profile)
        {
            List<string> values = [];
            LanguageToolProviderCapabilities capabilities = profile.Capabilities;
            if (capabilities.ManagedInstall)
            {
                values.Add(Loc.Get("LanguageDetail.ProviderCapability.ManagedInstall"));
            }
            else if (capabilities.Discover)
            {
                values.Add(Loc.Get("LanguageDetail.ProviderCapability.DiscoverOnly"));
            }
            else
            {
                values.Add(Loc.Get("LanguageDetail.Tool.Badge.Metadata"));
            }

            if (capabilities.VersionSwitch)
            {
                values.Add(Loc.Get("LanguageDetail.ProviderCapability.VersionSwitch"));
            }

            if (capabilities.SourceConfiguration)
            {
                values.Add(Loc.Get("LanguageDetail.ProviderCapability.SourceConfiguration"));
            }

            if (capabilities.CacheManagement)
            {
                values.Add(Loc.Get("LanguageDetail.ProviderCapability.CacheManagement"));
            }

            if (capabilities.VirtualEnvironment)
            {
                values.Add(Loc.Get("LanguageDetail.Capability.VirtualEnvironment"));
            }

            return $"{profile.ProviderDisplayName} [{profile.Identity.ScopedId} · "
                + $"{ProviderAdapterName(profile.AdapterKind)} · {string.Join("/", values)}]";
        }

        private static string ProviderAdapterName(ToolProviderAdapterKind kind) => kind switch
        {
            ToolProviderAdapterKind.ManagedArchive =>
                Loc.Get("LanguageDetail.AdapterKind.ManagedArchive"),
            ToolProviderAdapterKind.WinGet => Loc.Get("LanguageDetail.AdapterKind.WinGet"),
            _ => Loc.Get("LanguageDetail.AdapterKind.Catalog"),
        };

        private static string WindowsSupportName(LanguageToolWindowsSupport support) => support switch
        {
            LanguageToolWindowsSupport.Native => Loc.Get("LanguageDetail.WindowsSupport.Native"),
            LanguageToolWindowsSupport.Wsl => "WSL",
            LanguageToolWindowsSupport.Conditional =>
                Loc.Get("LanguageDetail.WindowsSupport.Conditional"),
            LanguageToolWindowsSupport.Unsupported =>
                Loc.Get("LanguageDetail.WindowsSupport.Unsupported"),
            _ => support.ToString(),
        };

        private static string LanguageLabel(LanguageDefinition language) =>
            $"{language.DisplayName} [{language.Id}]";

        private static string RoleName(LanguageToolRole role) => role switch
        {
            LanguageToolRole.Compiler => Loc.Get("LanguageDetail.Role.Compiler"),
            LanguageToolRole.Interpreter => Loc.Get("LanguageDetail.Role.Interpreter"),
            LanguageToolRole.Runtime => Loc.Get("LanguageDetail.Role.Runtime"),
            LanguageToolRole.Sdk => "SDK",
            LanguageToolRole.PackageManager => Loc.Get("LanguageDetail.Role.PackageManager"),
            LanguageToolRole.Build => Loc.Get("LanguageDetail.Role.Build"),
            LanguageToolRole.Debugger => Loc.Get("LanguageDetail.Role.Debugger"),
            LanguageToolRole.Formatter => Loc.Get("LanguageDetail.Capability.Format"),
            LanguageToolRole.Linter => Loc.Get("LanguageDetail.Capability.Lint"),
            LanguageToolRole.VersionManager => Loc.Get("LanguageDetail.Role.VersionManager"),
            LanguageToolRole.VirtualEnvironment => Loc.Get("LanguageDetail.Capability.VirtualEnvironment"),
            LanguageToolRole.Repl => "REPL",
            LanguageToolRole.LanguageServer => Loc.Get("LanguageDetail.Role.LanguageServer"),
            LanguageToolRole.TestRunner => Loc.Get("LanguageDetail.Role.TestRunner"),
            _ => role.ToString(),
        };
    }

    private sealed class MirrorRow
    {
        public MirrorRow(
            LanguageDefinition language,
            LanguageToolDefinition tool,
            LanguageToolProviderDefinition provider,
            ResolvedProviderSource source,
            bool userOverridable)
        {
            Language = language;
            Tool = tool;
            Provider = provider;
            Source = source;
            UserOverridable = userOverridable;
        }

        public LanguageDefinition Language { get; }

        public LanguageToolDefinition Tool { get; }

        public LanguageToolProviderDefinition Provider { get; }

        public ResolvedProviderSource Source { get; }

        public bool UserOverridable { get; }

        public string ToolDisplayName => Tool.DisplayName;

        public string ProviderDisplayName => Provider.DisplayName;

        public string DisplayName => Source.DisplayName;

        public string Ownership => $"Language {Language.DisplayName} [{Language.Id}] → "
            + $"Language Tool {Tool.DisplayName} [{Tool.Id}] → "
            + $"Provider {Provider.DisplayName} [{Provider.Id}] → "
            + $"Provider Source {Source.DisplayName} [{Source.Owner.SlotId}] · "
            + Source.Origin switch
            {
                ProviderSourceOrigin.CatalogDefault =>
                    Loc.Get("LanguageDetail.SourceOrigin.Default"),
                ProviderSourceOrigin.UserOverride =>
                    Loc.Get("LanguageDetail.SourceOrigin.Override"),
                ProviderSourceOrigin.Custom => Source.IsEnabled
                    ? Loc.Get("LanguageDetail.SourceOrigin.CustomEnabled")
                    : Loc.Get("LanguageDetail.SourceOrigin.CustomDisabled"),
                _ => Source.Origin.ToString(),
            };

        public string Endpoint => Source.ConfiguredEndpoint.AbsoluteUri;

        public string Purpose => Source.Purpose;

        public bool CanEdit => Source.Origin == ProviderSourceOrigin.Custom || UserOverridable;

        public string AutomationName => $"{Ownership}。{Purpose}";

        public string PrimaryActionText => Source.Origin == ProviderSourceOrigin.Custom
            ? Source.IsEnabled
                ? Loc.Get("LanguageDetail.Actions.Disable")
                : Loc.Get("LanguageDetail.Actions.Enable")
            : Loc.Get("LanguageDetail.Actions.Edit");

        public string PrimaryActionAutomationName => $"{PrimaryActionText} Provider Source：{DisplayName}";

        public Visibility SecondaryActionVisibility => Source.Origin is ProviderSourceOrigin.UserOverride
            or ProviderSourceOrigin.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string SecondaryActionText => Source.Origin == ProviderSourceOrigin.Custom
            ? Loc.Get("LanguageDetail.Actions.DeleteCustom")
            : Loc.Get("LanguageDetail.Actions.ResetEndpoint");

        public string SecondaryGlyph => Source.Origin == ProviderSourceOrigin.Custom
            ? "\uE74D"
            : "\uE777";
    }

    private sealed record ProviderChoice(
        LanguageToolDefinition Tool,
        LanguageToolProviderDefinition Provider)
    {
        public string Label => $"{Tool.DisplayName} / {Provider.DisplayName}";
    }

    private sealed class ProviderPluginRow(
        LanguageDefinition language,
        RuntimeProviderPluginDescriptor descriptor)
    {
        public string Id { get; } = descriptor.Id;

        public string DisplayName { get; } = descriptor.Manifest.DisplayName;

        public string ToolId { get; } = descriptor.Manifest.LanguageToolId;

        public bool IsEnabled { get; } = descriptor.IsEnabled;

        public string ToggleText => IsEnabled
            ? Loc.Get("LanguageDetail.Actions.Disable")
            : Loc.Get("LanguageDetail.Actions.Enable");

        public string Detail { get; } = $"Language {language.DisplayName} [{language.Id}] → "
            + $"Language Tool {descriptor.Manifest.LanguageToolId} → Provider {descriptor.ProviderId} · "
            + Loc.Format("LanguageDetail.Plugins.AdapterLabel", descriptor.Manifest.Kind) + " · "
            + $"{descriptor.Manifest.Vendor} · "
            + Loc.Format("LanguageDetail.Count.Releases", descriptor.Manifest.Releases.Count) + " · "
            + (descriptor.IsEnabled
                ? Loc.Get("LanguageDetail.State.Enabled")
                : Loc.Get("LanguageDetail.State.Disabled"));

        public string AutomationName => $"{DisplayName}。{Detail}";

        public string ToggleAutomationName => Loc.Format(
            "LanguageDetail.Plugins.ToggleAutomationName",
            ToggleText,
            DisplayName);

        public string DeleteAutomationName => Loc.Format(
            "LanguageDetail.Plugins.DeleteAutomationName",
            DisplayName);
    }

    private sealed class ProjectEnvironmentRow(ProjectVirtualEnvironment environment)
    {
        public string Title { get; } = $"{environment.Manager} · {environment.Kind}";

        public string Detail { get; } = $"{environment.Health}"
            + (environment.Version is null ? string.Empty : $" · {environment.Version}")
            + (environment.Warnings.Count == 0
                ? string.Empty
                : Loc.Format(
                    "LanguageDetail.ProjectParse.WarningCountSuffix",
                    environment.Warnings.Count));

        public string Root { get; } = environment.Root;

        public ProjectVirtualEnvironmentHealth Health { get; } = environment.Health;
    }

    private sealed record RuntimeReleaseChoice(RuntimeRelease Release)
    {
        public string Label => $"{Release.Version} · {Release.Architecture} · "
            + string.Join(", ", Release.Channels);
    }

    private sealed record ToolInstallSource(
        string Label,
        string Detail,
        DeclarativeRuntimeCatalogProvider? PluginProvider);

    private sealed record ProviderEndpointChoice(
        ResolvedProviderSource Source,
        string Label);
}
