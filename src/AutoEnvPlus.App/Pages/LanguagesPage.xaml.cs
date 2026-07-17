using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Languages;
using AutoEnvPlus.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class LanguagesPage : Page
{
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly string _managedRoot;
    private readonly LanguagePackStore _packStore;
    private readonly LanguageVisibilityStore _visibilityStore;
    private readonly LanguageToolInventoryStore _inventoryStore;
    private LanguageCatalog _activeCatalog = BuiltInLanguageCatalog.Current;
    private LanguageCatalog _availableCatalog = BuiltInLanguageCatalog.Current;
    private LanguageVisibilityState _visibilityState = LanguageVisibilityState.Empty;
    private IReadOnlySet<string> _detectedCommands = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase);
    private LanguageRow[] _rows = [];
    private LanguageVisibilityPolicy _settingsVisibilityPolicy =
        LanguageVisibilityPolicy.TopTenAndDetected;
    private bool _busy;
    private bool _loaded;
    private bool _settingsPolicyApplied;
    private DateTimeOffset? _inventoryCapturedAtUtc;
    private bool _inventoryCatalogChanged;
    private string? _inventoryWarning;

    public LanguagesPage()
    {
        InitializeComponent();
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _packStore = new LanguagePackStore(_managedRoot);
        _visibilityStore = new LanguageVisibilityStore(_managedRoot);
        _inventoryStore = new LanguageToolInventoryStore(_managedRoot);
        FilterCombo.ItemsSource = new[]
        {
            new FilterChoice("all", "所有状态"),
            new FilterChoice("detected", "已检测"),
            new FilterChoice("default", "默认 Top 10"),
            new FilterChoice("hidden", "已隐藏"),
        };
        FilterCombo.SelectedIndex = 0;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => _pageCancellation.Cancel();

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RefreshAsync(scanPath: false);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) =>
        await RefreshAsync(scanPath: true);

    private async Task RefreshAsync(bool scanPath)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            LanguagePackListResult packs = await _packStore.ListAsync(_pageCancellation.Token);
            _availableCatalog = CreateCatalog(packs.Packs, enabledOnly: false);
            _activeCatalog = CreateCatalog(packs.Packs, enabledOnly: true);
            AutoEnvPlusApplicationSettings settings =
                await new AutoEnvPlusApplicationSettingsStore(_managedRoot).LoadAsync(
                    _pageCancellation.Token);
            _settingsVisibilityPolicy = settings.LanguageVisibilityPolicy;
            if (!_settingsPolicyApplied)
            {
                _settingsPolicyApplied = true;
                ShowAllToggle.IsOn = _settingsVisibilityPolicy ==
                    LanguageVisibilityPolicy.AllBuiltIn;
            }

            _visibilityState = await _visibilityStore.LoadAsync(
                _availableCatalog,
                _pageCancellation.Token);

            await LoadInventoryAsync(scanPath);

            HashSet<string> detectedLanguageIds = _activeCatalog.Tools
                .Where(tool => tool.DiscoveryCommands.Any(_detectedCommands.Contains))
                .SelectMany(tool => tool.LanguageIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            LanguageVisibilitySnapshot visibility = LanguageVisibilityEvaluator.Evaluate(
                _activeCatalog,
                _visibilityState,
                detectedLanguageIds);
            _rows = visibility.Entries
                .Select(entry => new LanguageRow(
                    entry,
                    _activeCatalog.GetToolsForLanguage(entry.Language.Id),
                    _detectedCommands))
                .OrderByDescending(row => row.Entry.IsVisible)
                .ThenByDescending(row => row.Entry.IsDetected)
                .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            RenderLanguagePacks(packs);
            ApplyFilter();

            StatusInfo.IsOpen = true;
            StatusInfo.Severity = packs.Errors.Count == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            StatusInfo.Title = "语言目录已就绪";
            string inventoryStatus = _inventoryCapturedAtUtc is DateTimeOffset capturedAt
                ? $"库存 {capturedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                    + (_inventoryCatalogChanged ? "（目录已变化，等待重新检测）" : string.Empty)
                : "尚无库存快照（未自动扫描）";
            StatusInfo.Message = $"{_activeCatalog.Languages.Count} 门可用语言 · "
                + $"{_activeCatalog.Tools.Count} 个语言工具 · "
                + $"检测到 {detectedLanguageIds.Count} 门语言 · {inventoryStatus}"
                + (_inventoryWarning is null ? string.Empty : $" · {_inventoryWarning}")
                + (packs.Errors.Count > 0
                    ? $" · {packs.Errors.Count} 个语言包未通过验证"
                    : string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is LanguagePackException
            or LanguageVisibilityException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "无法读取语言目录";
            StatusInfo.Message = exception switch
            {
                LanguageVisibilityException visibility =>
                    $"语言显示状态未通过校验（{visibility.Code}）；状态内容未回显。",
                LanguagePackException pack =>
                    $"语言包未通过校验（{pack.Code}）；清单内容未回显。",
                _ => "语言目录或状态路径当前不可用；路径外内容和敏感值未显示。",
            };
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadInventoryAsync(bool scanPath)
    {
        _inventoryWarning = null;
        LanguageToolInventorySnapshot? snapshot;
        if (scanPath)
        {
            snapshot = await new LanguageToolInventoryScanner().ScanPathAsync(
                _activeCatalog,
                cancellationToken: _pageCancellation.Token);
            await _inventoryStore.SaveAsync(snapshot, _pageCancellation.Token);
        }
        else
        {
            try
            {
                snapshot = await _inventoryStore.LoadAsync(_pageCancellation.Token);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException)
            {
                snapshot = null;
                _inventoryWarning = "库存快照未通过校验";
            }
        }

        _inventoryCapturedAtUtc = snapshot?.CapturedAtUtc;
        _inventoryCatalogChanged = snapshot is not null && !snapshot.IsCompatibleWith(_activeCatalog);
        _detectedCommands = snapshot?.GetDetectedCommands(_activeCatalog)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        string filterId = (FilterCombo.SelectedItem as FilterChoice)?.Id ?? "all";
        bool showAll = ShowAllToggle.IsOn;
        LanguageRow[] filtered = _rows
            .Where(row => showAll || _settingsVisibilityPolicy switch
            {
                LanguageVisibilityPolicy.EnabledOnly =>
                    row.Entry.IsExplicitlyEnabled && !row.Entry.IsExplicitlyHidden,
                _ => row.Entry.IsVisible,
            })
            .Where(row => filterId switch
            {
                "detected" => row.Entry.IsDetected,
                "default" => row.Entry.Reasons.HasFlag(LanguageVisibilityReason.Default),
                "hidden" => row.Entry.IsExplicitlyHidden,
                _ => true,
            })
            .Where(row => query.Length == 0 || row.SearchText.Contains(
                query,
                StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        LanguageList.ItemsSource = filtered;
        LanguageList.Visibility = filtered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyLanguageText.Visibility = filtered.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        int effectiveCount = _rows.Count(row => row.Entry.IsVisible);
        int detectedCount = _rows.Count(row => row.Entry.IsDetected);
        LanguageSummaryText.Text = $"当前显示 {filtered.Length} 门 · 有效集合 {effectiveCount} 门 · "
            + $"库存已检测 {detectedCount} 门 · 目录总计 {_rows.Length} 门 · "
            + $"设置策略 {VisibilityPolicyName(_settingsVisibilityPolicy)}";
    }

    private void OnSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ApplyFilter();
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if ((FilterCombo.SelectedItem as FilterChoice)?.Id == "hidden")
        {
            ShowAllToggle.IsOn = true;
        }

        ApplyFilter();
    }

    private void OnShowAllToggled(object sender, RoutedEventArgs args) => ApplyFilter();

    private async void OnToggleLanguageVisibilityClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: LanguageRow row })
        {
            return;
        }

        SetBusy(true);
        try
        {
            _visibilityState = row.Entry.IsVisible && !row.Entry.IsExplicitlyHidden
                ? await _visibilityStore.SetHiddenAsync(
                    _availableCatalog,
                    row.LanguageId,
                    true,
                    _pageCancellation.Token)
                : await _visibilityStore.SetEnabledAsync(
                    _availableCatalog,
                    row.LanguageId,
                    true,
                    _pageCancellation.Token);
        }
        catch (LanguageVisibilityException exception)
        {
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "无法保存语言显示状态";
            StatusInfo.Message = $"状态未写入（{exception.Code}）。";
            SetBusy(false);
            return;
        }

        SetBusy(false);
        await RefreshAsync(scanPath: false);
    }

    private async void OnResetVisibilityClicked(object sender, RoutedEventArgs args)
    {
        if (_busy)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "重置语言显示偏好",
            Content = "清除所有显式启用和隐藏状态。之后仍会显示默认 Top 10 和 PATH 已检测到的语言。",
            PrimaryButtonText = "重置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            _visibilityState = await _visibilityStore.ResetAsync(
                _availableCatalog,
                cancellationToken: _pageCancellation.Token);
        }
        catch (LanguageVisibilityException exception)
        {
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "无法重置语言显示状态";
            StatusInfo.Message = $"状态未更新（{exception.Code}）。";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync(scanPath: false);
    }

    private void OnOpenLanguageClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: LanguageRow row })
        {
            return;
        }

        LanguageDetailPage detail = new(
            row.LanguageId,
            _activeCatalog,
            _availableCatalog,
            _detectedCommands);
        detail.BackRequested += OnDetailBackRequested;
        detail.InventoryUpdated += OnDetailInventoryUpdated;
        DetailSurface.Content = detail;
        DetailSurface.Visibility = Visibility.Visible;
        ListSurface.Visibility = Visibility.Collapsed;
    }

    private void OnDetailBackRequested(object? sender, EventArgs args)
    {
        if (DetailSurface.Content is LanguageDetailPage detail)
        {
            detail.BackRequested -= OnDetailBackRequested;
            detail.InventoryUpdated -= OnDetailInventoryUpdated;
        }

        DetailSurface.Content = null;
        DetailSurface.Visibility = Visibility.Collapsed;
        ListSurface.Visibility = Visibility.Visible;
    }

    private async void OnDetailInventoryUpdated(object? sender, EventArgs args) =>
        await RefreshAsync(scanPath: false);

    private async void OnImportLanguagePackClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || ((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = "预检语言包",
        };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            LanguagePackImportPreview preview = await _packStore.PreviewImportAsync(
                file.Path,
                _pageCancellation.Token);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = $"导入 {preview.Manifest.DisplayName}",
                Content = CreatePackPreview(preview, file.Name),
                PrimaryButtonText = "导入（保持停用）",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _packStore.ImportAsync(preview, _pageCancellation.Token);
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Success;
            StatusInfo.Title = "语言包已导入并保持停用";
            StatusInfo.Message = "显式启用后，新语言和工具才会进入有效目录。";
        }
        catch (LanguagePackException exception)
        {
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "无法导入语言包";
            StatusInfo.Message = $"清单未通过安全校验（{exception.Code}）；内容未回显。";
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync(scanPath: false);
    }

    private async void OnToggleLanguagePackClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: LanguagePackRow row })
        {
            return;
        }

        if (!row.IsEnabled)
        {
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = $"启用 {row.DisplayName}",
                Content = "该 data-only 语言包的语言和工具元数据将加入目录；它不会获得脚本或 DLL 执行能力。",
                PrimaryButtonText = "显式启用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        SetBusy(true);
        try
        {
            if (row.IsEnabled)
            {
                await _packStore.DisableAsync(row.Id, _pageCancellation.Token);
            }
            else
            {
                await _packStore.EnableAsync(row.Id, _pageCancellation.Token);
            }
        }
        catch (LanguagePackException exception)
        {
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "无法更改语言包状态";
            StatusInfo.Message = $"操作未提交（{exception.Code}）。";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync(scanPath: false);
    }

    private async void OnDeleteLanguagePackClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || sender is not Button { Tag: LanguagePackRow row })
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"删除 {row.DisplayName}",
            Content = "删除受管语言包清单；不会删除任何外部语言工具或项目文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _packStore.DeleteAsync(row.Id, _pageCancellation.Token);
        }
        catch (LanguagePackException exception)
        {
            StatusInfo.IsOpen = true;
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = "无法删除语言包";
            StatusInfo.Message = $"操作未提交（{exception.Code}）。";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshAsync(scanPath: false);
    }

    private void RenderLanguagePacks(LanguagePackListResult result)
    {
        LanguagePackRow[] rows = result.Packs.Select(pack => new LanguagePackRow(pack)).ToArray();
        LanguagePackList.ItemsSource = rows;
        LanguagePackList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyPackText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LanguagePackSummaryText.Text = $"已导入 {rows.Length} 个 · 已启用 {rows.Count(row => row.IsEnabled)} 个"
            + (result.Errors.Count > 0 ? $" · 错误 {result.Errors.Count} 个" : string.Empty);
    }

    private static LanguageCatalog CreateCatalog(
        IReadOnlyList<LanguagePackDescriptor> packs,
        bool enabledOnly)
    {
        IEnumerable<LanguagePackDescriptor> selected = enabledOnly
            ? packs.Where(pack => pack.IsEnabled)
            : packs;
        return new LanguageCatalog(
            BuiltInLanguageCatalog.Current.Languages.Concat(
                selected.SelectMany(pack => pack.Manifest.Languages)),
            BuiltInLanguageCatalog.Current.Tools.Concat(
                selected.SelectMany(pack => pack.Manifest.Tools)));
    }

    private static string VisibilityPolicyName(LanguageVisibilityPolicy policy) => policy switch
    {
        LanguageVisibilityPolicy.TopTenAndDetected => "Top 10 与已检测",
        LanguageVisibilityPolicy.EnabledOnly => "只显示手动启用",
        LanguageVisibilityPolicy.AllBuiltIn => "显示全部内置语言",
        _ => policy.ToString(),
    };

    private static StackPanel CreatePackPreview(
        LanguagePackImportPreview preview,
        string fileName) => new()
        {
            MaxWidth = 620,
            Spacing = 8,
            Children =
        {
            Detail("文件", fileName),
            Detail("语言包 ID", preview.Manifest.Id),
            Detail("发布者", preview.Manifest.Publisher),
            Detail("内容", $"{preview.LanguageCount} 门语言 · {preview.ToolCount} 个工具"),
            Detail("许可证", preview.Manifest.License),
            new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "默认保持停用",
                Message = "导入只复制规范化 JSON；不会运行清单内容。",
            },
        },
        };

    private static StackPanel Detail(string label, string value) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
        },
    };

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshProgress.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
        ImportPackButton.IsEnabled = !busy;
    }

    private sealed record FilterChoice(string Id, string Label);

    private sealed class LanguageRow
    {
        public LanguageRow(
            LanguageVisibilityEntry entry,
            IReadOnlyList<LanguageToolDefinition> tools,
            IReadOnlySet<string> detectedCommands)
        {
            Entry = entry;
            LanguageId = entry.Language.Id;
            DisplayName = entry.Language.DisplayName;
            Monogram = DisplayName.Length <= 2
                ? DisplayName.ToUpperInvariant()
                : DisplayName[..1].ToUpperInvariant();
            int detectedTools = tools.Count(tool =>
                tool.DiscoveryCommands.Any(detectedCommands.Contains));
            int managedTools = tools.Count(tool => tool.Capabilities.Install);
            ToolSummary = $"{tools.Count} 个语言工具 · PATH 已检测 {detectedTools} 个 · "
                + $"AutoEnvPlus 可管理安装 {managedTools} 个";
            string aliases = entry.Language.Aliases.Count == 0
                ? entry.Language.Id
                : string.Join("、", entry.Language.Aliases.Take(4));
            string extensions = entry.Language.FileExtensions.Count == 0
                ? "无固定扩展名"
                : string.Join(" ", entry.Language.FileExtensions.Take(6));
            IdentitySummary = $"{aliases} · {extensions}";
            SearchText = string.Join(
                " ",
                new[] { LanguageId, DisplayName }
                    .Concat(entry.Language.Aliases)
                    .Concat(entry.Language.FileExtensions)
                    .Concat(tools.Select(tool => tool.DisplayName)));
        }

        public LanguageVisibilityEntry Entry { get; }

        public string LanguageId { get; }

        public string DisplayName { get; }

        public string Monogram { get; }

        public string ToolSummary { get; }

        public string IdentitySummary { get; }

        public string SearchText { get; }

        public Visibility DetectedVisibility => Entry.IsDetected
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility DefaultVisibility => Entry.Reasons.HasFlag(
            LanguageVisibilityReason.Default)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility HiddenVisibility => Entry.IsExplicitlyHidden
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string VisibilityActionText => Entry.IsVisible && !Entry.IsExplicitlyHidden
            ? $"隐藏 {DisplayName}"
            : $"启用 {DisplayName}";

        public string VisibilityGlyph => Entry.IsVisible && !Entry.IsExplicitlyHidden
            ? "\uE8F5"
            : "\uE890";
    }

    private sealed class LanguagePackRow(LanguagePackDescriptor descriptor)
    {
        public string Id { get; } = descriptor.Id;

        public string DisplayName { get; } = descriptor.Manifest.DisplayName;

        public bool IsEnabled { get; } = descriptor.IsEnabled;

        public string ToggleText => IsEnabled ? "停用" : "启用";

        public string Detail { get; } = $"{descriptor.Id} · {descriptor.Manifest.Publisher} · "
            + $"{descriptor.Manifest.Languages.Count} 门语言 · {descriptor.Manifest.Tools.Count} 个工具 · "
            + (descriptor.IsEnabled ? "已启用" : "已停用");
    }
}
