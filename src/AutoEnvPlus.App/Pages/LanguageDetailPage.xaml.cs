using System.Runtime.InteropServices;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.RuntimeCatalogs;
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? BackRequested;

    public event EventHandler? InventoryUpdated;

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
                "正在扫描当前语言",
                "只检查此语言声明的 PATH 命令；不会运行版本命令或访问网络。");
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
            DetailStatusInfo.Title = "当前语言扫描已完成";
            DetailStatusInfo.Message = $"已更新 {_language.DisplayName} 的库存快照；"
                + $"检测到 {_toolRows.Count(row => row.IsDetected)} 个工具。";
        }
        catch (OperationCanceledException)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = "当前语言扫描已取消";
            DetailStatusInfo.Message = "未完成结果没有写入库存快照。";
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError("无法扫描当前语言", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private void RenderLanguageHeader()
    {
        LanguageTitle.Text = _language.DisplayName;
        LanguageSubtitle.Text = $"{_language.Id} · 编译器工具与运行时（语言工具）";
        HomepageButton.NavigateUri = _language.Homepage;
        LanguageIdText.Text = $"稳定 ID：{_language.Id}";
        LanguageAliasesText.Text = "别名：" + (_language.Aliases.Count == 0
            ? "无"
            : string.Join("、", _language.Aliases));
        LanguageExtensionsText.Text = "文件扩展名：" + (_language.FileExtensions.Count == 0
            ? "无固定扩展名"
            : string.Join(" ", _language.FileExtensions));
        LanguageVisibilityText.Text = _language.DefaultEnabled
            ? "显示策略：Top 10 默认语言；仍可由用户显式隐藏。"
            : "显示策略：PATH 检测或用户显式启用后进入有效集合。";
    }

    private async Task LoadDetailAsync()
    {
        SetOperationState(true, "正在读取语言工具", "加载库存快照、托管版本和 Provider 状态…");
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
                ? "此语言目录没有声明包管理工具。"
                : string.Join(" · ", packageManagers);

            await RenderMirrorSourcesAsync();
            RenderProviderPlugins(await pluginsTask);
            SetOperationState(
                false,
                "语言详情已就绪",
                $"{_toolRows.Length} 个工具 · {_toolRows.Count(row => row.IsDetected)} 个已检测 · "
                + $"{_toolRows.Count(row => row.IsManagedInstall)} 个可管理安装",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError("无法加载语言详情", exception);
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
                ? "未发现完整 Windows SDK"
                : string.Join(
                    "；",
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
                discoverySummary = "已发现：" + string.Join(
                    "；",
                    installations.Select(installation =>
                        $"{installation.DisplayName} · MSVC "
                        + (installation.MsvcToolsVersion ?? "版本未知")));
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
                discoverySummary = "已发现：" + string.Join(
                    "；",
                    discoveredTools.Select(discovered =>
                        $"{discovered.Version?.ToString() ?? "版本未知"} · "
                        + discovered.ExecutablePath));
            }
        }

        string versionSummary = discoverySummary
            ?? (managed.Length > 0
                ? $"托管版本：{string.Join(", ", managed.Select(entry => entry.Version).Take(5))}"
                : pathDetected
                    ? $"PATH：{string.Join(", ", tool.DiscoveryCommands.Where(_detectedCommands.Contains))}"
                    : "未发现托管版本或 PATH 入口");
        if (bridge is not null
            && globalProfile.Selections.TryGetValue(
                bridge.RuntimeKind,
                out VersionSelector? globalSelector))
        {
            versionSummary += $" · 全局选择：{globalSelector}";
            if (globalProfile.ExactSelections.TryGetValue(
                    bridge.RuntimeKind,
                    out RuntimeSelectionIdentity? identity))
            {
                versionSummary += $" · {identity.RuntimeId} / {identity.ProviderId}";
            }
        }

        ToolManagementKind management = bridge?.RuntimeKind is RuntimeKind.Python
            or RuntimeKind.NodeJs
            or RuntimeKind.Java
            or RuntimeKind.DotNet
                ? ToolManagementKind.OfficialArchive
                : component is not null
                    ? ToolManagementKind.WinGet
                    : ToolManagementKind.None;
        return new ToolRow(
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
                    : throw new InvalidOperationException("语言工具缺少 Provider RuntimeKind 映射。"));
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
            DetailStatusInfo.Title = "操作已取消";
            DetailStatusInfo.Message = "未提交的安装已停止。";
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError("安装未完成", exception);
        }
        finally
        {
            EndOperation();
        }
    }

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
                "正在读取已安装版本",
                $"正在校验 {row.DisplayName} 的受管注册项。");
            RegistryLoadResult registry = await new ManagedRuntimeRegistry(_managedRoot).LoadAsync(
                cancellationToken);
            if (registry.Errors.Count > 0)
            {
                throw new InvalidDataException("受管语言工具注册表未通过校验。");
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
                DetailStatusInfo.Title = "没有可切换的已安装版本";
                DetailStatusInfo.Message = "受管注册表已刷新；请先安装此语言工具的版本。";
                return;
            }

            GlobalRuntimeVersionChoice[] choices = entries
                .Select(entry => new GlobalRuntimeVersionChoice(
                    entry,
                    File.Exists(entry.ExecutablePath)))
                .ToArray();
            ComboBox versionPicker = new()
            {
                Header = "已安装版本",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 420,
            };
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
                    "全局默认",
                    "保存精确 RuntimeId / ProviderId；项目和会话仍可覆盖。"),
                new(
                    VersionSelectionScope.Project,
                    "当前项目",
                    "预览并更新项目根目录的 autoenvplus.toml。"),
                new(
                    VersionSelectionScope.NewTerminalSession,
                    "新终端会话",
                    "打开带精确身份环境变量的新终端，不影响已打开终端。"),
            ];
            ComboBox scopePicker = new()
            {
                Header = "生效范围",
                ItemsSource = scopeChoices,
                DisplayMemberPath = nameof(VersionSelectionScopeChoice.Label),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            TextBlock scopeDescription = new()
            {
                Text = scopeChoices[0].Description,
                TextWrapping = TextWrapping.Wrap,
            };
            bool hasUnavailableChoices = choices.Any(choice => !choice.CanSelect);
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = $"选择 {row.DisplayName} 版本",
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
                            Title = "选择优先级",
                            Message = "会话 > 项目 > 全局。三种方式都复用一次配置的 AutoEnvPlus Shim，不会重写用户 PATH。",
                        },
                        new InfoBar
                        {
                            IsClosable = false,
                            IsOpen = hasUnavailableChoices,
                            Severity = InfoBarSeverity.Warning,
                            Title = "部分候选不可用",
                            Message = "入口文件缺失的注册项不能选择。同版本来自多个 Provider 时会保存精确 RuntimeId 和 ProviderId。",
                        },
                    },
                },
                PrimaryButtonText = "设为全局默认",
                CloseButtonText = "取消",
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
                        VersionSelectionScope.Global => "设为全局默认",
                        VersionSelectionScope.Project => "预览项目配置",
                        VersionSelectionScope.NewTerminalSession => "预览新终端",
                        _ => "继续",
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
                DetailStatusInfo.Title = "版本选择未更改";
                DetailStatusInfo.Message = "项目、当前会话和 PATH 均未修改。";
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
                "正在切换全局版本",
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
                DetailStatusInfo.Title = "无法切换全局版本";
                DetailStatusInfo.Message = "所选注册项已变化、入口不可用或选择不再唯一；请重新打开选择器后再试。";
                return;
            }

            await RefreshToolRowsAsync(cancellationToken);
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Success;
            DetailStatusInfo.Title = "全局版本已切换";
            DetailStatusInfo.Message = $"{row.DisplayName} {entry.Version} · "
                + $"{ArchitectureLabel(entry.Architecture)} · {entry.ProviderId}。"
                + "项目与当前会话未修改，PATH 未修改。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.RuntimeSwitch,
                ActivityStatus.Succeeded,
                $"已把 {row.DisplayName} 全局版本切换到 {entry.Version} "
                + $"({ArchitectureLabel(entry.Architecture)} · {entry.ProviderId})；"
                + "项目、当前会话和 PATH 均未修改。",
                [entry.ExecutablePath]);
        }
        catch (OperationCanceledException)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = "版本选择已取消";
            DetailStatusInfo.Message = "项目、当前会话和 PATH 均未修改。";
        }
        catch (Exception exception) when (IsExpectedException(exception))
        {
            ShowSafeError("无法应用版本选择", exception);
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
            DetailStatusInfo.Title = "项目版本未更改";
            DetailStatusInfo.Message = "没有选择项目目录；全局、会话和 PATH 均未修改。";
            return;
        }

        ProjectPathBox.Text = projectRoot;
        SetOperationState(
            true,
            "正在生成项目配置预览",
            $"正在为 {row.DisplayName} {entry.Version} 生成修改前/修改后内容。");
        VersionSelector selector = new(VersionSelectorKind.Exact, entry.Version);
        ProjectToolSelectionService service = new(_managedRoot, projectRoot);
        ProjectToolSelectionPlan plan = await service.CreatePlanAsync(
            entry,
            selector,
            cancellationToken);
        TextBox beforePreview = CreateManifestPreviewBox(
            plan.ManifestExisted ? plan.Before : "（autoenvplus.toml 尚不存在）");
        TextBox afterPreview = CreateManifestPreviewBox(plan.After);
        StackPanel preview = new()
        {
            MaxWidth = 760,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "修改前",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                beforePreview,
                new TextBlock
                {
                    Text = "修改后",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                afterPreview,
                new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Informational,
                    Title = "安全写入",
                    Message = "只更新 [tools] 与 [tool-identities] 中当前工具的键；保留未知段落。应用前复核 SHA-256，并用同目录临时文件原子替换。",
                },
            },
        };
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = plan.ManifestExisted
                ? $"更新项目中的 {row.DisplayName}"
                : $"为项目添加 {row.DisplayName}",
            Content = new ScrollViewer
            {
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = preview,
            },
            PrimaryButtonText = plan.Changed ? "应用项目配置" : "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = "项目版本未更改";
            DetailStatusInfo.Message = "预览已关闭；项目文件、全局、会话和 PATH 均未修改。";
            return;
        }

        ProjectToolSelectionResult result = await service.ApplyAsync(plan, cancellationToken);
        if (!result.Success)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Error;
            DetailStatusInfo.Title = "项目版本未写入";
            DetailStatusInfo.Message = result.Error
                ?? "项目文件在确认后发生变化；请刷新预览再试。";
            return;
        }

        ProjectInfo.IsOpen = true;
        ProjectInfo.Severity = InfoBarSeverity.Success;
        ProjectInfo.Title = "项目精确版本已保存";
        ProjectInfo.Message = $"{row.DisplayName} {entry.Version} · {entry.Id} / {entry.ProviderId}";
        DetailStatusInfo.IsOpen = true;
        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = result.Changed ? "项目版本已更新" : "项目版本已经是所选项";
        DetailStatusInfo.Message = $"{row.DisplayName} {entry.Version} 仅对 {projectRoot} 及其子目录生效；全局、当前会话和 PATH 未修改。";
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeSwitch,
            ActivityStatus.Succeeded,
            $"已把项目 {row.DisplayName} 固定到 {entry.Version} "
            + $"({entry.Id} / {entry.ProviderId})；全局、会话和 PATH 未修改。",
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
            Header = "终端主机",
            ItemsSource = hostChoices,
            DisplayMemberPath = nameof(SessionTerminalHostChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
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
                    Title = "仅影响新终端",
                    Message = "精确版本通过新进程环境传递；不会修改全局默认、项目文件、用户 PATH 或已打开终端。",
                },
                previewText,
            },
        };
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"打开 {row.DisplayName} {entry.Version} 新终端",
            Content = new ScrollViewer
            {
                MaxHeight = 580,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            },
            CloseButtonText = "取消",
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
                    ? "在 Windows Terminal 中打开"
                    : "打开新 PowerShell";
            confirmation.IsPrimaryButtonEnabled = selectedPlan.CanLaunch;
        }

        hostPicker.SelectionChanged += (_, _) => UpdateTerminalPreview();
        UpdateTerminalPreview();
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            DetailStatusInfo.IsOpen = true;
            DetailStatusInfo.Severity = InfoBarSeverity.Informational;
            DetailStatusInfo.Title = "新终端未打开";
            DetailStatusInfo.Message = "全局、项目、当前会话和 PATH 均未修改。";
            return;
        }

        int processId = await service.LaunchRuntimeSessionAsync(
            selectedPlan,
            cancellationToken);
        DetailStatusInfo.IsOpen = true;
        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = "精确版本终端已启动";
        DetailStatusInfo.Message = $"PID {processId} · {row.DisplayName} {entry.Version} · "
            + $"{entry.Id} / {entry.ProviderId}。全局、项目和 PATH 未修改。";
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeSwitch,
            ActivityStatus.Succeeded,
            $"已为 {row.DisplayName} {entry.Version} 打开精确版本新终端 "
            + $"({entry.Id} / {entry.ProviderId})；全局、项目和 PATH 未修改。",
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
        preview.AppendLine($"工作目录\n{plan.WorkingDirectory}");
        preview.AppendLine($"\n终端主机\n{TerminalHostLabel(plan.EffectiveHost)}");
        if (plan.Selection is ProjectTerminalSelection selection)
        {
            preview.AppendLine(
                $"\n精确选择\n{selection.Kind} {selection.ResolvedVersion} · "
                + $"{selection.RuntimeId} / {selection.ProviderId}");
        }

        preview.AppendLine($"\nShim\n{plan.ShimDirectory}");
        preview.AppendLine(
            $"\n网络投影\n{(plan.NetworkSummary.Applied ? "已应用 Provider 来源与代理" : "无语言工具网络覆盖")}");
        if (plan.Warnings.Count > 0)
        {
            preview.AppendLine("\n警告");
            foreach (string warning in plan.Warnings)
            {
                preview.AppendLine("• " + warning);
            }
        }

        if (plan.Errors.Count > 0)
        {
            preview.AppendLine("\n无法启动");
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
            throw new InvalidDataException("受管语言工具注册表未通过校验。");
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
                "内置 WinGet（推荐）",
                "固定包 ID 白名单；保留厂商安装和 workload 流程。",
                null)
            : new ToolInstallSource(
                "内置官方 Provider（推荐）",
                OfficialTrustDescription(kind),
                null);
        ToolInstallSource[] choices =
        [
            builtIn,
            .. plugins.Select(provider => new ToolInstallSource(
                $"{provider.Manifest.DisplayName} · {provider.Id}",
                $"第三方声明式 ZIP · {provider.Manifest.Vendor} · "
                    + $"{provider.Manifest.Releases.Count} 个版本",
                provider)),
        ];
        ComboBox selector = new()
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(ToolInstallSource.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
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
            Title = "第三方 Provider",
            Message = "插件 checksum 是第三方声明，不等同于内置 Provider 的官方签名。不会在失败后静默回退到内置来源。",
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
                Text = "当前语言没有此工具对应且已启用的声明式 Provider 插件。",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"选择 {row.DisplayName} 安装来源",
            Content = content,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
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
            throw new InvalidOperationException("未找到 WinGet；无法执行固定白名单安装计划。");
        }

        ExternalToolInstallPlan plan = installer.CreatePlan(component, winget);
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"安装 {row.DisplayName}",
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 8,
                Children =
                {
                    Detail("固定包 ID", plan.PackageId),
                    Detail("来源", "WinGet allow-list"),
                    Detail("权限", plan.MayRequireElevation ? "可能请求管理员权限" : "通常不需要提升权限"),
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = true,
                        Severity = InfoBarSeverity.Informational,
                        Title = "留在语言详情",
                        Message = "安装将由现有白名单服务执行；操作保留在当前语言页。",
                    },
                },
            },
            PrimaryButtonText = "开始安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetOperationState(true, $"正在安装 {row.DisplayName}", plan.PackageId);
        ExternalToolInstallResult result = await installer.InstallAsync(plan, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"WinGet 返回退出码 {result.ExitCode}。");
        }

        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = $"{row.DisplayName} 安装完成";
        DetailStatusInfo.Message = $"WinGet 包 {plan.PackageId} 已完成；重新检测可刷新 PATH 证据。";
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.ToolchainInstall,
            ActivityStatus.Succeeded,
            $"已从语言详情安装 {row.DisplayName}（{plan.PackageId}）。",
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
            isThirdParty ? "正在加载第三方声明式版本目录" : "正在加载官方版本目录",
            provider.Id);
        RuntimeRelease[] releases = (await provider.GetReleasesAsync(cancellationToken))
            .Where(release => release.Architecture == architecture)
            .Take(30)
            .ToArray();
        if (releases.Length == 0)
        {
            throw new InvalidDataException("所选 Provider 没有当前架构可用的稳定版本。");
        }

        ComboBox releasePicker = new()
        {
            ItemsSource = releases.Select(release => new RuntimeReleaseChoice(release)).ToArray(),
            DisplayMemberPath = nameof(RuntimeReleaseChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ContentDialog selectDialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"选择 {row.DisplayName} 版本",
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
                            ? $"已选择官方推荐 JDK {javaFeature} 版本线。"
                            : $"架构：{architecture} · Provider：{provider.Id}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "预览安装",
            CloseButtonText = "取消",
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
            throw new InvalidDataException("托管语言工具注册表未通过校验。");
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
            Content = "安装后设为全局默认版本",
            IsChecked = supportsGlobalSelection && !providerCollision && !isThirdParty,
            IsEnabled = supportsGlobalSelection && !providerCollision,
        };
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"安装 {row.DisplayName} {selected.Release.Version}",
            Content = new StackPanel
            {
                MaxWidth = 680,
                Spacing = 8,
                Children =
                {
                    Detail("Provider", selected.Release.ProviderId),
                    Detail("文件", asset.FileName),
                    Detail(asset.HashAlgorithm.DisplayName(), asset.PackageHash),
                    Detail("安装目录", plan.DestinationRoot),
                    Detail("入口", plan.ExpectedExecutableRelativePath),
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = isThirdParty,
                        Severity = InfoBarSeverity.Warning,
                        Title = "第三方 checksum 声明",
                        Message = "将严格校验插件声明的摘要和 ZIP 入口，但该声明不等同于官方发布签名；安装失败不会改用内置来源。",
                    },
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = providerCollision,
                        Severity = InfoBarSeverity.Warning,
                        Title = "同版本存在多个 Provider",
                        Message = "安装会保留 Provider 身份，但不会自动改写全局默认。",
                    },
                    setGlobalDefaultChoice,
                    new TextBlock
                    {
                        Text = !supportsGlobalSelection
                            ? "此工具不使用全局版本选择器。"
                            : isThirdParty
                                ? "第三方 Provider 默认不改写全局版本；需要时请显式勾选。"
                                : "内置官方 Provider 默认设为全局版本；可在安装前取消。",
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "开始受管安装",
            CloseButtonText = "取消",
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
            throw new InvalidOperationException(result.Error ?? "受管安装失败。");
        }

        DetailStatusInfo.Severity = InfoBarSeverity.Success;
        DetailStatusInfo.Title = result.InstallOutcome == InstallOutcome.AlreadyInstalled
            ? "版本已确认"
            : "安装完成";
        DetailStatusInfo.Message = $"{row.DisplayName} {selected.Release.Version} · "
            + $"{selected.Release.ProviderId} · {result.InstallRoot}";
        await AppActivityLog.TryWriteAsync(
            ActivityOperationType.RuntimeInstall,
            ActivityStatus.Succeeded,
            $"已从 {_language.DisplayName} 详情安装 {row.DisplayName} {selected.Release.Version}。",
            [result.InstallRoot ?? plan.DestinationRoot]);
    }

    private async Task<Uri> SelectManagedProviderEndpointAsync(
        ToolRow row,
        CancellationToken cancellationToken)
    {
        LanguageToolProviderDefinition managedProvider = row.Tool.Providers.FirstOrDefault(
            provider => provider.ManagedInstallSupported)
            ?? throw new InvalidOperationException(
                "此语言工具没有可用于内置归档安装的 Provider。");
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
                "内置 Provider 没有启用的兼容下载/元数据端点。");
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
            Header = "Provider 下载/元数据端点",
            ItemsSource = choices,
            DisplayMemberPath = nameof(ProviderEndpointChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"选择 {row.DisplayName} Provider 端点",
            Content = new StackPanel
            {
                MaxWidth = 680,
                Spacing = 8,
                Children =
                {
                    selector,
                    new TextBlock
                    {
                        Text = "只列出此工具受管 Provider 下已启用的 GenericDownload 源；所选端点必须实现该 Provider 的兼容目录契约。",
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "使用此端点",
            CloseButtonText = "取消",
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
            Title = "使用非默认 Provider 端点",
            Content = new StackPanel
            {
                MaxWidth = 640,
                Spacing = 8,
                Children =
                {
                    Detail("端点", source.ConfiguredEndpoint.AbsoluteUri),
                    new InfoBar
                    {
                        IsClosable = false,
                        IsOpen = true,
                        Severity = dotNet ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                        Title = dotNet ? ".NET 元数据与摘要同源" : "固定签名策略保持启用",
                        Message = dotNet
                            ? "此非默认 release index 同时决定资产 URL 与 SHA-512 元数据，且没有独立发布者签名；不能视为 Microsoft 已验证来源。"
                            : "Python、Node.js 或 Temurin 的现有固定签名/证明验证策略仍会执行；自定义端点必须提供与对应 Provider 兼容的目录和资产。",
                    },
                },
            },
            PrimaryButtonText = "继续使用",
            CloseButtonText = "取消",
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
                    rows.Add(new MirrorRow(tool, provider, source, slot?.UserOverridable ?? true));
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
        EmptyMirrorText.Visibility = ordered.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MirrorInfo.Message = ordered.Length == 0
            ? "此语言的 Provider 暂未声明来源槽；通用代理仍按设置中的传输策略生效。"
            : $"{ordered.Length} 个 Provider 源 · "
                + $"{ordered.Count(row => row.Source.Origin == ProviderSourceOrigin.UserOverride)} 个覆盖 · "
                + $"{ordered.Count(row => row.Source.Origin == ProviderSourceOrigin.Custom)} 个自定义";
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
                    Header = "HTTPS 端点",
                    Text = row.Source.ConfiguredEndpoint.AbsoluteUri,
                    PlaceholderText = "https://mirror.example/",
                };
                ContentDialog dialog = new()
                {
                    XamlRoot = XamlRoot,
                    Title = $"编辑 {row.DisplayName}",
                    Content = endpoint,
                    PrimaryButtonText = "保存覆盖",
                    CloseButtonText = "取消",
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
            ShowSafeError("无法保存 Provider 源", exception);
        }
    }

    private async void OnResetMirrorClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row })
        {
            return;
        }

        try
        {
            if (row.Source.Origin == ProviderSourceOrigin.Custom)
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

            await RenderMirrorSourcesAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            ShowSafeError("无法更新 Provider 源", exception);
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
            Header = "归属 Provider",
            ItemsSource = providers,
            DisplayMemberPath = nameof(ProviderChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        TextBox slotId = new() { Header = "源 ID", PlaceholderText = "company-mirror" };
        TextBox displayName = new() { Header = "显示名称", PlaceholderText = "公司来源" };
        TextBox endpoint = new() { Header = "HTTPS 端点", PlaceholderText = "https://mirror.example/" };
        TextBox purpose = new() { Header = "用途", PlaceholderText = "内部包和元数据" };
        ComboBox kindPicker = new()
        {
            Header = "端点类型",
            ItemsSource = Enum.GetValues<ProviderMirrorEndpointKind>(),
            SelectedItem = ProviderMirrorEndpointKind.GenericDownload,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "添加 Provider 自定义源",
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 8,
                Children = { providerPicker, slotId, displayName, endpoint, purpose, kindPicker },
            },
            PrimaryButtonText = "添加并启用",
            CloseButtonText = "取消",
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
            ShowSafeError("无法添加自定义源", exception);
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
            ProjectInfo.Title = "请选择项目目录";
            ProjectInfo.Message = string.Empty;
            return;
        }

        try
        {
            ProjectInfo.Severity = InfoBarSeverity.Informational;
            ProjectInfo.Title = "正在解析项目环境";
            ProjectInfo.Message = "只读取固定候选路径。";
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
                ? $"未发现 {_language.DisplayName} 项目环境"
                : $"发现 {rows.Length} 项 {_language.DisplayName} 环境证据";
            ProjectInfo.Message = $"检查 {result.InspectedPathCount} 个固定路径"
                + (result.ScanLimitReached ? " · 已达到扫描上限" : string.Empty)
                + (result.Warnings.Count > 0 ? $" · 警告 {result.Warnings.Count} 项" : string.Empty);
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
            ProjectInfo.Title = "无法解析项目环境";
            ProjectInfo.Message = "项目路径或固定候选元数据未通过安全检查。";
        }
    }

    private void RenderProviderPlugins(RuntimeProviderPluginListResult result)
    {
        IReadOnlySet<string> toolIds = _catalog.GetToolsForLanguage(_language.Id)
            .Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProviderPluginRow[] rows = result.Plugins
            .Where(plugin => toolIds.Contains(plugin.Manifest.LanguageToolId))
            .Select(plugin => new ProviderPluginRow(plugin))
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
            CommitButtonText = "预检此语言的 Provider",
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
                Title = $"导入 {preview.Manifest.DisplayName}",
                Content = new StackPanel
                {
                    MaxWidth = 620,
                    Spacing = 8,
                    Children =
                    {
                        Detail("语言", _language.DisplayName),
                        Detail("语言工具", preview.Manifest.LanguageToolId),
                        Detail("内置适配器", preview.Manifest.Kind.ToString()),
                        Detail("Provider", preview.Manifest.ProviderId),
                        Detail("内容", $"{preview.ReleaseCount} 个版本 · {preview.AssetCount} 个资产"),
                        new InfoBar
                        {
                            IsClosable = false,
                            IsOpen = true,
                            Severity = InfoBarSeverity.Warning,
                            Title = "第三方声明",
                            Message = "导入后保持停用；checksum 不等同于官方签名。",
                        },
                    },
                },
                PrimaryButtonText = "导入（保持停用）",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _pluginStore.ImportAsync(preview, _pageCancellation.Token);
            RenderProviderPlugins(await _pluginStore.ListAsync(_pageCancellation.Token));
        }
        catch (RuntimeProviderPluginException exception)
        {
            ShowSafeError("无法导入此语言的 Provider", exception);
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
            }
            else
            {
                ContentDialog confirmation = new()
                {
                    XamlRoot = XamlRoot,
                    Title = $"启用 {row.DisplayName}",
                    Content = "启用后它会成为此语言工具的第三方安装来源；插件仍不能执行脚本或 DLL。",
                    PrimaryButtonText = "显式启用",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                await _pluginStore.EnableAsync(row.Id, _pageCancellation.Token);
            }

            RenderProviderPlugins(await _pluginStore.ListAsync(_pageCancellation.Token));
        }
        catch (RuntimeProviderPluginException exception)
        {
            ShowSafeError("无法更改 Provider 插件状态", exception);
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
            Title = $"删除 {row.DisplayName}",
            Content = "删除插件清单和启用状态；不会删除已经安装的托管工具。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _pluginStore.DeleteAsync(row.Id, _pageCancellation.Token);
            RenderProviderPlugins(await _pluginStore.ListAsync(_pageCancellation.Token));
        }
        catch (RuntimeProviderPluginException exception)
        {
            ShowSafeError("无法删除 Provider 插件", exception);
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
            throw new InvalidDataException("网络传输设置未通过校验。");
        }

        if (!NetworkToolIds.TryGetRuntimeScope(kind, out string toolId))
        {
            throw new NotSupportedException("此语言工具没有语言工具网络作用域。");
        }

        NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(
            loaded.Settings,
            toolId);
        return resolved.Success && resolved.EffectiveSettings is not null
            ? resolved.EffectiveSettings
            : throw new InvalidDataException("网络传输策略无法解析。");
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
                javaFeature ?? throw new InvalidOperationException("缺少 JDK 版本线。"),
                architecture,
                baseUri: providerEndpoint),
            RuntimeKind.DotNet => new DotNetSdkCatalogProvider(
                httpClient,
                architecture,
                indexUri: providerEndpoint),
            _ => throw new NotSupportedException("此工具没有内置官方归档 Provider。"),
        };

    private static string OfficialTrustDescription(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Python =>
            "python.org 官方发布；验证 Sigstore/Fulcio、Rekor 透明日志和包摘要。",
        RuntimeKind.NodeJs =>
            "Node.js 官方发布；验证固定发布密钥签署的 OpenPGP checksum 清单。",
        RuntimeKind.Java =>
            "Eclipse Temurin；校验 API SHA-256 和包本体 detached OpenPGP 签名。",
        RuntimeKind.DotNet =>
            "Microsoft .NET 官方发行元数据；强制校验 SHA-512。",
        _ => "使用 AutoEnvPlus 固定白名单的内置来源。",
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
                $"官方端点返回 HTTP {(int)http.StatusCode.Value}；查询参数未显示。",
            HttpRequestException => "无法建立 HTTPS 连接；查询参数和凭据未显示。",
            RuntimeProviderPluginException plugin =>
                $"Provider 插件未通过校验（{plugin.Code}）。",
            ProviderSourcePreferenceException source =>
                $"Provider 源设置未通过校验（{source.Error.Code}）。",
            LanguagePackException pack => $"语言包未通过校验（{pack.Code}）。",
            InvalidOperationException when exception.Message.StartsWith(
                "WinGet 返回退出码 ",
                StringComparison.Ordinal) => exception.Message,
            _ => "操作未完成；路径、端点查询参数和敏感值未回显。",
        };
    }

    private static bool IsExpectedException(Exception exception) => exception is HttpRequestException
        or InvalidDataException
        or InvalidOperationException
        or NotSupportedException
        or IOException
        or UnauthorizedAccessException
        or RuntimeProviderPluginException
        or ProviderSourcePreferenceException
        or LanguagePackException
        or System.ComponentModel.Win32Exception;

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
        "download" => "正在下载",
        "verify" => $"正在校验 {algorithm.DisplayName()}",
        "extract" => "正在安全解压",
        "commit" => "正在提交安装",
        "complete" => "安装完成",
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
        ProviderSourceOrigin.CatalogDefault => "Provider 默认",
        ProviderSourceOrigin.UserOverride => "用户覆盖",
        ProviderSourceOrigin.Custom => "自定义",
        _ => origin.ToString(),
    };

    private enum ToolManagementKind
    {
        None,
        OfficialArchive,
        WinGet,
    }

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
            + (CanSelect ? string.Empty : " · 不可用");

        public string HelpText => !ExecutableExists
                ? "受管注册项对应的入口文件不存在。"
                : $"精确切换到 {Entry.Id} / {Entry.ProviderId}，不修改 PATH。";
    }

    private sealed class ToolRow
    {
        public ToolRow(
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
            RoleSummary = string.Join(" · ", tool.Roles.Select(RoleName));
            CapabilitySummary = BuildCapabilitySummary(tool.Capabilities);
            ProviderSummary = "Provider：" + string.Join(
                " · ",
                tool.Providers.Select(provider => BuildProviderSummary(
                    LanguageToolProviderProfile.Create(tool, provider))));
        }

        public LanguageToolDefinition Tool { get; }

        public string DisplayName => Tool.DisplayName;

        public bool IsDetected { get; }

        public bool IsManagedInstall => ManagementKind != ToolManagementKind.None
            && Tool.Capabilities.Install;

        public string RoleSummary { get; }

        public string VersionSummary { get; }

        public string CapabilitySummary { get; }

        public string ProviderSummary { get; }

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

        public Visibility ManageActionVisibility => IsManagedInstall
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility SwitchActionVisibility =>
            SupportsVersionSwitch(RuntimeKind) && ManagedEntries.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string ManageActionText => ManagementKind == ToolManagementKind.WinGet
            ? "安装 / 修复"
            : "选择版本";

        private static string BuildCapabilitySummary(LanguageToolCapabilities capabilities)
        {
            List<string> values = [];
            if (capabilities.Discover) values.Add("发现");
            if (capabilities.Install) values.Add("安装");
            if (capabilities.VersionSwitch) values.Add("版本切换");
            if (capabilities.ProjectPin) values.Add("项目固定");
            if (capabilities.PackageManagement) values.Add("包管理");
            if (capabilities.VirtualEnvironment) values.Add("虚拟环境");
            if (capabilities.MirrorConfiguration) values.Add("镜像配置");
            if (capabilities.Debug) values.Add("调试");
            if (capabilities.Format) values.Add("格式化");
            if (capabilities.Lint) values.Add("静态检查");
            return values.Count == 0 ? "能力：目录信息" : "能力：" + string.Join(" · ", values);
        }

        private static string BuildProviderSummary(LanguageToolProviderProfile profile)
        {
            List<string> values = [];
            LanguageToolProviderCapabilities capabilities = profile.Capabilities;
            if (capabilities.ManagedInstall) values.Add("可安装");
            else if (capabilities.Discover) values.Add("仅发现");
            else values.Add("目录元数据");
            if (capabilities.VersionSwitch) values.Add("可切换");
            if (capabilities.SourceConfiguration) values.Add("可配置源");
            if (capabilities.CacheManagement) values.Add("缓存");
            if (capabilities.VirtualEnvironment) values.Add("虚拟环境");
            return $"{profile.ProviderDisplayName} [{profile.Identity.ScopedId}："
                + string.Join("/", values) + "]";
        }

        private static string RoleName(LanguageToolRole role) => role switch
        {
            LanguageToolRole.Compiler => "编译器",
            LanguageToolRole.Interpreter => "解释器",
            LanguageToolRole.Runtime => "运行时",
            LanguageToolRole.Sdk => "SDK",
            LanguageToolRole.PackageManager => "包管理器",
            LanguageToolRole.Build => "构建",
            LanguageToolRole.Debugger => "调试器",
            LanguageToolRole.Formatter => "格式化",
            LanguageToolRole.Linter => "静态检查",
            LanguageToolRole.VersionManager => "版本管理",
            LanguageToolRole.VirtualEnvironment => "虚拟环境",
            LanguageToolRole.Repl => "REPL",
            LanguageToolRole.LanguageServer => "语言服务器",
            LanguageToolRole.TestRunner => "测试运行器",
            _ => role.ToString(),
        };
    }

    private sealed class MirrorRow
    {
        public MirrorRow(
            LanguageToolDefinition tool,
            LanguageToolProviderDefinition provider,
            ResolvedProviderSource source,
            bool userOverridable)
        {
            Tool = tool;
            Provider = provider;
            Source = source;
            UserOverridable = userOverridable;
        }

        public LanguageToolDefinition Tool { get; }

        public LanguageToolProviderDefinition Provider { get; }

        public ResolvedProviderSource Source { get; }

        public bool UserOverridable { get; }

        public string ToolDisplayName => Tool.DisplayName;

        public string ProviderDisplayName => Provider.DisplayName;

        public string DisplayName => Source.DisplayName;

        public string Ownership => $"{Tool.DisplayName} / {Provider.DisplayName} / {Source.Owner.SlotId} · "
            + Source.Origin switch
            {
                ProviderSourceOrigin.CatalogDefault => "Provider 默认",
                ProviderSourceOrigin.UserOverride => "用户覆盖",
                ProviderSourceOrigin.Custom => Source.IsEnabled ? "自定义 · 已启用" : "自定义 · 已停用",
                _ => Source.Origin.ToString(),
            };

        public string Endpoint => Source.ConfiguredEndpoint.AbsoluteUri;

        public string Purpose => Source.Purpose;

        public bool CanEdit => Source.Origin == ProviderSourceOrigin.Custom || UserOverridable;

        public string PrimaryActionText => Source.Origin == ProviderSourceOrigin.Custom
            ? Source.IsEnabled ? "停用" : "启用"
            : "编辑";

        public Visibility SecondaryActionVisibility => Source.Origin is ProviderSourceOrigin.UserOverride
            or ProviderSourceOrigin.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string SecondaryActionText => Source.Origin == ProviderSourceOrigin.Custom
            ? "删除自定义源"
            : "恢复 Provider 默认端点";

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

    private sealed class ProviderPluginRow(RuntimeProviderPluginDescriptor descriptor)
    {
        public string Id { get; } = descriptor.Id;

        public string DisplayName { get; } = descriptor.Manifest.DisplayName;

        public bool IsEnabled { get; } = descriptor.IsEnabled;

        public string ToggleText => IsEnabled ? "停用" : "启用";

        public string Detail { get; } = $"{descriptor.Manifest.LanguageToolId}/{descriptor.ProviderId} · "
            + $"适配器 {descriptor.Manifest.Kind} · "
            + $"{descriptor.Manifest.Vendor} · {descriptor.Manifest.Releases.Count} 个版本 · "
            + (descriptor.IsEnabled ? "已启用" : "已停用");
    }

    private sealed class ProjectEnvironmentRow(ProjectVirtualEnvironment environment)
    {
        public string Title { get; } = $"{environment.Manager} · {environment.Kind}";

        public string Detail { get; } = $"{environment.Health}"
            + (environment.Version is null ? string.Empty : $" · {environment.Version}")
            + (environment.Warnings.Count == 0
                ? string.Empty
                : $" · 警告 {environment.Warnings.Count} 项");

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
