using AutoEnvPlus.App.Text;
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
    private CancellationTokenSource _pageCancellation = new();
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
    private string? _pendingOpenLanguageId;
    private bool _settingsPolicyApplied;
    private DateTimeOffset? _inventoryCapturedAtUtc;
    private bool _inventoryCatalogChanged;
    private string? _inventoryWarning;

    public LanguagesPage()
    {
        InitializeComponent();
        ApplyLocalizedStrings();
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _packStore = new LanguagePackStore(_managedRoot);
        _visibilityStore = new LanguageVisibilityStore(_managedRoot);
        _inventoryStore = new LanguageToolInventoryStore(_managedRoot);
        FilterCombo.SelectedIndex = 0;
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
        PageTitleText.Text = Loc.Get("Languages.Header.Title");
        PageSubtitleText.Text = Loc.Get("Languages.Header.Subtitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshProgress,
            Loc.Get("Languages.Refresh.ProgressAccessibleName"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RefreshButton,
            Loc.Get("Languages.Refresh.AccessibleName"));
        ToolTipService.SetToolTip(
            RefreshButton,
            Loc.Get("Languages.Refresh.ToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ImportPackButton,
            Loc.Get("Languages.ImportPack.Label"));
        ImportPackButtonText.Text = Loc.Get("Languages.ImportPack.Label");
        StatusInfo.Title = Loc.Get("Languages.Status.ReadingTitle");
        CatalogNoteInfo.Title = Loc.Get("Languages.Catalog.NoteTitle");
        CatalogNoteInfo.Message = Loc.Get("Languages.Catalog.NoteMessage");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SearchBox,
            Loc.Get("Languages.Search.AccessibleName"));
        SearchBox.PlaceholderText = Loc.Get("Languages.Search.PlaceholderText");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            FilterCombo,
            Loc.Get("Languages.Filter.AccessibleName"));
        FilterCombo.ItemsSource = new[]
        {
            new FilterChoice("all", Loc.Get("Languages.Filters.All")),
            new FilterChoice("detected", Loc.Get("Languages.Filters.Detected")),
            new FilterChoice("default", Loc.Get("Languages.Filters.Default")),
            new FilterChoice("hidden", Loc.Get("Languages.Filters.Hidden")),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ShowAllToggle,
            Loc.Get("Languages.ShowAll.Header"));
        ShowAllToggle.Header = Loc.Get("Languages.ShowAll.Header");
        ShowAllToggle.OffContent = Loc.Get("Languages.ShowAll.OffContent");
        ShowAllToggle.OnContent = Loc.Get("Languages.ShowAll.OnContent");
        LanguageMappingTitleText.Text = Loc.Get("Languages.Summary.SectionTitle");
        LanguageSummaryText.Text = Loc.Get("Languages.Summary.InitialText");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ResetVisibilityButton,
            Loc.Get("Languages.ResetVisibility.Title"));
        ToolTipService.SetToolTip(
            ResetVisibilityButton,
            Loc.Get("Languages.ResetVisibility.ToolTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LanguageList,
            Loc.Get("Languages.Header.Title"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            EmptyLanguageState,
            Loc.Get("Languages.Empty.Title"));
        EmptyLanguageTitleText.Text = Loc.Get("Languages.Empty.Title");
        EmptyLanguageHintText.Text = Loc.Get("Languages.Empty.Hint");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            LanguagePackExpander,
            Loc.Get("Languages.Packs.AccessibleName"));
        LanguagePackTitleText.Text = Loc.Get("Languages.Packs.SectionTitle");
        LanguagePackSummaryText.Text = Loc.Get("Languages.Packs.SummaryInitial");
        LanguagePackSchemaInfo.Title = Loc.Get("Languages.Packs.SchemaNoteTitle");
        LanguagePackSchemaInfo.Message = Loc.Get("Languages.Packs.SchemaNoteMessage");
        EmptyPackText.Text = Loc.Get("Languages.Packs.Empty");
    }

    private void OnDetectedBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge)
        {
            badge.Text = Loc.Get("Languages.Filters.Detected");
        }
    }

    private void OnDefaultBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge)
        {
            badge.Text = Loc.Get("Languages.Item.DefaultBadge");
        }
    }

    private void OnHiddenBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge)
        {
            badge.Text = Loc.Get("Languages.Filters.Hidden");
        }
    }

    private void OnOpenLanguageButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(button, Loc.Get("Languages.Item.Open.ToolTip"));
        }
    }

    private void OnDeletePackButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            ToolTipService.SetToolTip(button, Loc.Get("Languages.Packs.Delete.ToolTip"));
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => _pageCancellation.Cancel();

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        // The shell caches this page across navigations; the previous visit's
        // Unloaded cancelled the token, so re-arm it on every load.
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
        _pageCancellation = new CancellationTokenSource();
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RefreshAsync(scanPath: false);
        ConsumePendingOpenLanguage();
    }

    private void ConsumePendingOpenLanguage()
    {
        if (_pendingOpenLanguageId is null)
        {
            return;
        }

        string languageId = _pendingOpenLanguageId;
        _pendingOpenLanguageId = null;
        LanguageRow? row = FindRow(languageId);
        if (row is not null)
        {
            OpenLanguageDetail(row);
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) =>
        await RefreshAsync(scanPath: true);

    private async Task RefreshAsync(bool scanPath)
    {
        if (_busy)
        {
            return;
        }

        StatusInfo.IsOpen = true;
        StatusInfo.Severity = InfoBarSeverity.Informational;
        StatusInfo.Title = scanPath
            ? Loc.Get("Languages.Status.ScanningTitle")
            : Loc.Get("Languages.Status.ReadingTitle");
        StatusInfo.Message = scanPath
            ? Loc.Get("Languages.Status.ScanningMessage")
            : Loc.Get("Languages.Status.ReadingMessage");
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
            StatusInfo.Title = Loc.Get("Languages.Status.ReadyTitle");
            string inventoryStatus = _inventoryCapturedAtUtc is DateTimeOffset capturedAt
                ? Loc.Format(
                    "Languages.Status.InventoryAt",
                    capturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                    + (_inventoryCatalogChanged
                        ? Loc.Get("Languages.Status.InventoryStale")
                        : string.Empty)
                : Loc.Get("Languages.Status.InventoryNone");
            int operationAdapterCount = _activeCatalog.Tools.Count(tool =>
                LanguageToolUiPolicy.GetManagementKind(tool) != ToolManagementKind.None);
            List<string> summarySegments =
            [
                Loc.Format("Languages.Status.SummaryLanguages", _activeCatalog.Languages.Count),
                Loc.Format("Languages.Status.SummaryTools", _activeCatalog.Tools.Count),
                Loc.Format("Languages.Status.SummaryAdapters", operationAdapterCount),
                Loc.Format("Languages.Status.SummaryDetected", detectedLanguageIds.Count),
                inventoryStatus,
            ];
            if (_inventoryWarning is not null)
            {
                summarySegments.Add(_inventoryWarning);
            }

            if (packs.Errors.Count > 0)
            {
                summarySegments.Add(Loc.Format(
                    "Languages.Status.PackErrors",
                    packs.Errors.Count));
            }

            StatusInfo.Message = string.Join(
                Loc.Get("Languages.Text.Separator"),
                summarySegments);
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
            StatusInfo.Title = Loc.Get("Languages.Messages.ReadFailedTitle");
            StatusInfo.Message = exception switch
            {
                LanguageVisibilityException visibility =>
                    Loc.Format("Languages.Messages.VisibilityInvalid", visibility.Code),
                LanguagePackException pack =>
                    Loc.Format("Languages.Messages.PackInvalid", pack.Code),
                _ => Loc.Get("Languages.Messages.CatalogUnavailable"),
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
                _inventoryWarning = Loc.Get("Languages.Messages.InventoryInvalid");
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
        EmptyLanguageState.Visibility = filtered.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        int effectiveCount = _rows.Count(row => row.Entry.IsVisible);
        int detectedCount = _rows.Count(row => row.Entry.IsDetected);
        LanguageSummaryText.Text = string.Join(
            Loc.Get("Languages.Text.Separator"),
            Loc.Format("Languages.Summary.VisibleCount", filtered.Length),
            Loc.Format("Languages.Summary.EffectiveCount", effectiveCount),
            Loc.Format("Languages.Summary.DetectedCount", detectedCount),
            Loc.Format("Languages.Summary.TotalCount", _rows.Length),
            Loc.Format(
                "Languages.Summary.Policy",
                VisibilityPolicyName(_settingsVisibilityPolicy)));
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
            StatusInfo.Title = Loc.Get("Languages.Messages.VisibilitySaveFailedTitle");
            StatusInfo.Message = Loc.Format(
                "Languages.Messages.VisibilitySaveFailed",
                exception.Code);
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
            Title = Loc.Get("Languages.ResetVisibility.Title"),
            Content = Loc.Get("Languages.ResetVisibility.DialogContent"),
            PrimaryButtonText = Loc.Get("Languages.ResetVisibility.ResetButton"),
            CloseButtonText = Loc.Get("Languages.Dialog.Cancel"),
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
            StatusInfo.Title = Loc.Get("Languages.Messages.ResetFailedTitle");
            StatusInfo.Message = Loc.Format("Languages.Messages.ResetFailed", exception.Code);
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

        OpenLanguageDetail(row);
    }

    internal void RequestOpenLanguage(string languageId)
    {
        // Entry point for global search: open the language's detail view. On
        // the first visit the rows are not loaded yet, so remember the
        // request and consume it once the initial refresh completes.
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        LanguageRow? row = FindRow(languageId);
        if (row is not null)
        {
            OpenLanguageDetail(row);
            return;
        }

        _pendingOpenLanguageId = languageId;
    }

    private LanguageRow? FindRow(string languageId) => _rows.FirstOrDefault(candidate =>
        candidate.LanguageId.Equals(languageId, StringComparison.OrdinalIgnoreCase));

    private void OpenLanguageDetail(LanguageRow row)
    {
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
            CommitButtonText = Loc.Get("Languages.ImportPack.PickerCommit"),
        };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        SetBusy(true);
        string? importErrorMessage = null;
        try
        {
            LanguagePackImportPreview preview = await _packStore.PreviewImportAsync(
                file.Path,
                _pageCancellation.Token);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format(
                    "Languages.ImportPack.ConfirmTitle",
                    preview.Manifest.DisplayName),
                Content = CreatePackPreview(preview, file.Name),
                PrimaryButtonText = Loc.Get("Languages.ImportPack.ConfirmButton"),
                CloseButtonText = Loc.Get("Languages.Dialog.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _packStore.ImportAsync(preview, _pageCancellation.Token);
        }
        catch (LanguagePackException exception)
        {
            importErrorMessage = Loc.Format(
                "Languages.Messages.ManifestInvalid",
                exception.Code);
        }
        finally
        {
            SetBusy(false);
        }

        // Refresh the list first so the final status below is not overwritten
        // by RefreshAsync's own progress/success messages.
        await RefreshAsync(scanPath: false);

        StatusInfo.IsOpen = true;
        if (importErrorMessage is not null)
        {
            StatusInfo.Severity = InfoBarSeverity.Error;
            StatusInfo.Title = Loc.Get("Languages.Messages.ImportFailedTitle");
            StatusInfo.Message = importErrorMessage;
        }
        else
        {
            StatusInfo.Severity = InfoBarSeverity.Success;
            StatusInfo.Title = Loc.Get("Languages.Messages.ImportedTitle");
            StatusInfo.Message = Loc.Get("Languages.Messages.ImportedMessage");
        }
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
                Title = Loc.Format("Languages.Packs.EnableTitle", row.DisplayName),
                Content = Loc.Get("Languages.Packs.EnableDialogContent"),
                PrimaryButtonText = Loc.Get("Languages.Packs.EnableConfirm"),
                CloseButtonText = Loc.Get("Languages.Dialog.Cancel"),
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
            StatusInfo.Title = Loc.Get("Languages.Messages.PackStateFailedTitle");
            StatusInfo.Message = Loc.Format(
                "Languages.Messages.OperationNotCommitted",
                exception.Code);
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
            Title = Loc.Format("Languages.Packs.DeleteTitle", row.DisplayName),
            Content = Loc.Get("Languages.Packs.DeleteDialogContent"),
            PrimaryButtonText = Loc.Get("Languages.Packs.DeleteConfirm"),
            CloseButtonText = Loc.Get("Languages.Dialog.Cancel"),
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
            StatusInfo.Title = Loc.Get("Languages.Messages.DeleteFailedTitle");
            StatusInfo.Message = Loc.Format(
                "Languages.Messages.OperationNotCommitted",
                exception.Code);
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
        List<string> packSummarySegments =
        [
            Loc.Format("Languages.Packs.SummaryImported", rows.Length),
            Loc.Format("Languages.Packs.SummaryEnabled", rows.Count(row => row.IsEnabled)),
        ];
        if (result.Errors.Count > 0)
        {
            packSummarySegments.Add(Loc.Format(
                "Languages.Packs.SummaryErrors",
                result.Errors.Count));
        }

        LanguagePackSummaryText.Text = string.Join(
            Loc.Get("Languages.Text.Separator"),
            packSummarySegments);
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
        LanguageVisibilityPolicy.TopTenAndDetected => Loc.Get("Languages.Policy.TopTenAndDetected"),
        LanguageVisibilityPolicy.EnabledOnly => Loc.Get("Languages.Policy.EnabledOnly"),
        LanguageVisibilityPolicy.AllBuiltIn => Loc.Get("Languages.Policy.AllBuiltIn"),
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
            Detail(Loc.Get("Languages.Preview.FileLabel"), fileName),
            Detail(Loc.Get("Languages.Preview.PackIdLabel"), preview.Manifest.Id),
            Detail(Loc.Get("Languages.Preview.PublisherLabel"), preview.Manifest.Publisher),
            Detail(
                Loc.Get("Languages.Preview.ContentLabel"),
                Loc.Format(
                    "Languages.Preview.ContentValue",
                    preview.LanguageCount,
                    preview.ToolCount)),
            Detail(Loc.Get("Languages.Preview.LicenseLabel"), preview.Manifest.License),
            new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = Loc.Get("Languages.Preview.InactiveTitle"),
                Message = Loc.Get("Languages.Preview.InactiveMessage"),
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
            int managedTools = tools.Count(tool =>
                LanguageToolUiPolicy.GetManagementKind(tool) != ToolManagementKind.None);
            int metadataOnlyTools = tools.Count - managedTools;
            ToolSummary = Loc.Format(
                "Languages.Item.ToolSummary",
                tools.Count,
                detectedTools,
                managedTools,
                metadataOnlyTools);
            string aliases = entry.Language.Aliases.Count == 0
                ? entry.Language.Id
                : string.Join(
                    Loc.Get("Languages.Item.AliasSeparator"),
                    entry.Language.Aliases.Take(4));
            string extensions = entry.Language.FileExtensions.Count == 0
                ? Loc.Get("Languages.Item.NoExtensions")
                : string.Join(" ", entry.Language.FileExtensions.Take(6));
            IdentitySummary = aliases + Loc.Get("Languages.Text.Separator") + extensions;
            SearchText = string.Join(
                " ",
                new[] { LanguageId, DisplayName }
                    .Concat(entry.Language.Aliases)
                    .Concat(entry.Language.FileExtensions)
                    .Concat(tools.Select(tool => tool.DisplayName)));
        }

        public LanguageVisibilityEntry Entry { get; }

        public string LanguageId { get; }

        public string OpenActionAutomationName => Loc.Format(
            "Languages.Item.OpenActionAccessibleName",
            DisplayName);

        public string DisplayName { get; }

        public string Monogram { get; }

        public string ToolSummary { get; }

        public string IdentitySummary { get; }

        public string SearchText { get; }

        public string AutomationName => Loc.Format(
            "Languages.Item.AutomationName",
            DisplayName,
            ToolSummary);

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
            ? Loc.Format("Languages.Item.HideAction", DisplayName)
            : Loc.Format("Languages.Item.EnableAction", DisplayName);

        public string VisibilityGlyph => Entry.IsVisible && !Entry.IsExplicitlyHidden
            ? "\uE8F5"
            : "\uE890";
    }

    private sealed class LanguagePackRow(LanguagePackDescriptor descriptor)
    {
        public string Id { get; } = descriptor.Id;

        public string DisplayName { get; } = descriptor.Manifest.DisplayName;

        public bool IsEnabled { get; } = descriptor.IsEnabled;

        public string ToggleText => IsEnabled
            ? Loc.Get("Languages.Packs.Disable")
            : Loc.Get("Languages.Packs.Enable");

        public string ToggleActionAutomationName => IsEnabled
            ? Loc.Format("Languages.Packs.DisableActionAccessibleName", DisplayName)
            : Loc.Format("Languages.Packs.EnableActionAccessibleName", DisplayName);

        public string DeleteActionAutomationName => Loc.Format(
            "Languages.Packs.DeleteActionAccessibleName",
            DisplayName);

        public string Detail { get; } = string.Join(
            Loc.Get("Languages.Text.Separator"),
            descriptor.Id,
            descriptor.Manifest.Publisher,
            Loc.Format(
                "Languages.Packs.DetailLanguages",
                descriptor.Manifest.Languages.Count),
            Loc.Format("Languages.Packs.DetailTools", descriptor.Manifest.Tools.Count),
            descriptor.IsEnabled
                ? Loc.Get("Languages.Packs.Enabled")
                : Loc.Get("Languages.Packs.Disabled"));
    }
}
