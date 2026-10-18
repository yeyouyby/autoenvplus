using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Text;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Languages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;

namespace AutoEnvPlus.App.Pages;

/// <summary>
/// Global editor for provider mirror sources across every language tool.
/// The per-language detail page edits one language at a time; this page
/// resolves the effective source for every catalog provider in one place so
/// overrides, custom sources, and their enable state can be audited and
/// edited without drilling into each language.
/// </summary>
public sealed partial class MirrorSourcesPage : Page
{
    private readonly string _managedRoot;
    private readonly ProviderSourcePreferenceStore _sourceStore;
    private readonly LanguagePackStore _packStore;
    private readonly CancellationTokenSource _pageCancellation = new();
    private IReadOnlyList<MirrorRow> _allRows = [];
    private bool _loaded;

    public MirrorSourcesPage()
    {
        _managedRoot = ManagedRootResolver.ResolveOrThrow();
        _sourceStore = new ProviderSourcePreferenceStore(_managedRoot);
        _packStore = new LanguagePackStore(_managedRoot);
        InitializeComponent();
        ApplyLocalizedStrings();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void ApplyLocalizedStrings()
    {
        PageTitleText.Text = Loc.Get("MirrorSources.Header.Title");
        PageSubtitleText.Text = Loc.Get("MirrorSources.Header.Subtitle");
        AutomationProperties.SetName(RefreshButton, Loc.Get("MirrorSources.Refresh.AccessibleName"));
        ToolTipService.SetToolTip(RefreshButton, Loc.Get("MirrorSources.Refresh.AccessibleName"));
        FilterBox.PlaceholderText = Loc.Get("MirrorSources.Filter.PlaceholderText");
        AutomationProperties.SetName(FilterBox, Loc.Get("MirrorSources.Filter.AccessibleName"));
        AddCustomButtonText.Text = Loc.Get("MirrorSources.Actions.AddCustom");
        AutomationProperties.SetName(AddCustomButton, Loc.Get("MirrorSources.Actions.AddCustom"));
        EmptyText.Text = Loc.Get("MirrorSources.Empty.Text");
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RenderAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _pageCancellation.Cancel();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RenderAsync();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs args)
    {
        ApplyFilter();
    }

    // x:Name inside a DataTemplate generates no code-behind field, so every
    // templated element assigns its localized text (and per-origin state)
    // through these Loaded handlers; the row travels via DataContext.

    private void OnOriginBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBlock badge && badge.DataContext is MirrorRow row)
        {
            badge.Text = row.Source.Origin switch
            {
                ProviderSourceOrigin.UserOverride => Loc.Get("MirrorSources.Item.OriginOverride"),
                ProviderSourceOrigin.Custom => Loc.Get("MirrorSources.Item.OriginCustom"),
                _ => Loc.Get("MirrorSources.Item.OriginDefault"),
            };
        }
    }

    private void OnDisabledBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border badge && badge.DataContext is MirrorRow row)
        {
            badge.Visibility = row.Source.Origin == ProviderSourceOrigin.Custom
                && !row.Source.IsEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (badge.Child is TextBlock text)
            {
                text.Text = Loc.Get("MirrorSources.Item.DisabledBadge");
            }
        }
    }

    private void OnEditButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button && button.DataContext is MirrorRow row)
        {
            button.Visibility = row.Source.Origin != ProviderSourceOrigin.Custom
                && row.UserOverridable
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (button.Content is StackPanel panel && panel.Children[1] is TextBlock text)
            {
                text.Text = Loc.Get("MirrorSources.Item.EditOverride");
            }

            AutomationProperties.SetName(
                button,
                Loc.Format("MirrorSources.Item.EditAction.AccessibleName", row.DisplayName));
        }
    }

    private void OnResetButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button && button.DataContext is MirrorRow row)
        {
            button.Visibility = row.Source.Origin == ProviderSourceOrigin.UserOverride
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (button.Content is StackPanel panel && panel.Children[1] is TextBlock text)
            {
                text.Text = Loc.Get("MirrorSources.Item.ResetOverride");
            }

            AutomationProperties.SetName(
                button,
                Loc.Format("MirrorSources.Item.ResetAction.AccessibleName", row.DisplayName));
        }
    }

    private void OnToggleButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button && button.DataContext is MirrorRow row)
        {
            button.Visibility = row.Source.Origin == ProviderSourceOrigin.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (button.Content is StackPanel panel && panel.Children[1] is TextBlock text)
            {
                text.Text = row.Source.IsEnabled
                    ? Loc.Get("MirrorSources.Item.Disable")
                    : Loc.Get("MirrorSources.Item.Enable");
            }

            AutomationProperties.SetName(
                button,
                Loc.Format("MirrorSources.Item.ToggleAction.AccessibleName", row.DisplayName));
        }
    }

    private void OnDeleteButtonLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Button button && button.DataContext is MirrorRow row)
        {
            button.Visibility = row.Source.Origin == ProviderSourceOrigin.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (button.Content is StackPanel panel && panel.Children[1] is TextBlock text)
            {
                text.Text = Loc.Get("MirrorSources.Item.Delete");
            }

            AutomationProperties.SetName(
                button,
                Loc.Format("MirrorSources.Item.DeleteAction.AccessibleName", row.DisplayName));
        }
    }

    private async Task RenderAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            LanguageCatalog catalog = await CreateAvailableCatalogAsync();
            List<MirrorRow> rows = [];
            foreach (LanguageToolDefinition tool in catalog.Tools)
            {
                foreach (LanguageToolProviderDefinition provider in tool.Providers)
                {
                    ProviderSourceListResolutionResult resolved = await _sourceStore
                        .ResolveProviderAsync(catalog, tool.Id, provider.Id, _pageCancellation.Token);
                    if (!resolved.Success)
                    {
                        continue;
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

            _allRows = rows;
            ApplyFilter();
            SetInfo(
                InfoBarSeverity.Informational,
                Loc.Get("MirrorSources.Messages.RefreshedTitle"),
                Loc.Format(
                    "MirrorSources.Summary",
                    rows.Count,
                    rows.Count(row => row.Source.Origin == ProviderSourceOrigin.UserOverride),
                    rows.Count(row => row.Source.Origin == ProviderSourceOrigin.Custom)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ProviderSourcePreferenceException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.LoadFailedTitle"),
                CoreErrorText.Localize(exception.Message));
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task<LanguageCatalog> CreateAvailableCatalogAsync()
    {
        LanguagePackListResult packs = await _packStore.ListAsync(_pageCancellation.Token);
        return new LanguageCatalog(
            BuiltInLanguageCatalog.Current.Languages.Concat(
                packs.Packs.SelectMany(pack => pack.Manifest.Languages)),
            BuiltInLanguageCatalog.Current.Tools.Concat(
                packs.Packs.SelectMany(pack => pack.Manifest.Tools)));
    }

    private void ApplyFilter()
    {
        string query = FilterBox.Text.Trim();
        IEnumerable<MirrorRow> selected = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(row => row.MatchesQuery(query));

        List<MirrorGroup> groups = selected
            .GroupBy(row => row.ToolDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new MirrorGroup(group.Key, group))
            .ToList();

        CollectionViewSource view = new()
        {
            IsSourceGrouped = true,
            Source = groups,
        };
        SourceList.ItemsSource = view.View;
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnEditClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row }
            || row.Source.Origin == ProviderSourceOrigin.Custom)
        {
            return;
        }

        try
        {
            TextBox endpoint = new()
            {
                Header = Loc.Get("MirrorSources.Dialog.EndpointHeader"),
                Text = row.Source.ConfiguredEndpoint.AbsoluteUri,
                PlaceholderText = "https://mirror.example/",
            };
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("MirrorSources.Dialog.EditTitle", row.DisplayName),
                Content = endpoint,
                PrimaryButtonText = Loc.Get("MirrorSources.Dialog.SaveOverride"),
                CloseButtonText = Loc.Get("MirrorSources.Dialog.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            LanguageCatalog catalog = await CreateAvailableCatalogAsync();
            await _sourceStore.SetBuiltInOverrideAsync(
                catalog,
                row.Source.Owner,
                endpoint.Text,
                _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "MirrorSources.Messages.LogOverrideSaved",
                    row.ToolDisplayName,
                    row.DisplayName),
                [row.Source.Owner.ToString()]);
            await RenderAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.SaveFailedTitle"),
                CoreErrorText.Localize(exception.Message));
        }
    }

    private async void OnResetClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row }
            || row.Source.Origin != ProviderSourceOrigin.UserOverride)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("MirrorSources.Dialog.ResetTitle", row.DisplayName),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "MirrorSources.Dialog.ResetContent",
                    row.EndpointText),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("MirrorSources.Dialog.ResetConfirm"),
            CloseButtonText = Loc.Get("MirrorSources.Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            LanguageCatalog catalog = await CreateAvailableCatalogAsync();
            await _sourceStore.RestoreBuiltInDefaultAsync(
                catalog,
                row.Source.Owner,
                _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "MirrorSources.Messages.LogOverrideReset",
                    row.ToolDisplayName,
                    row.DisplayName),
                [row.Source.Owner.ToString()]);
            await RenderAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.UpdateFailedTitle"),
                CoreErrorText.Localize(exception.Message));
        }
    }

    private async void OnToggleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row }
            || row.Source.Origin != ProviderSourceOrigin.Custom)
        {
            return;
        }

        try
        {
            LanguageCatalog catalog = await CreateAvailableCatalogAsync();
            bool enable = !row.Source.IsEnabled;
            await _sourceStore.SetCustomSourceEnabledAsync(
                catalog,
                row.Source.Owner,
                enable,
                _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    enable
                        ? "MirrorSources.Messages.LogCustomEnabled"
                        : "MirrorSources.Messages.LogCustomDisabled",
                    row.ToolDisplayName,
                    row.DisplayName),
                [row.Source.Owner.ToString()]);
            await RenderAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.UpdateFailedTitle"),
                CoreErrorText.Localize(exception.Message));
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MirrorRow row }
            || row.Source.Origin != ProviderSourceOrigin.Custom)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("MirrorSources.Dialog.DeleteTitle", row.DisplayName),
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = Loc.Format(
                    "MirrorSources.Dialog.DeleteContent",
                    row.EndpointText),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Loc.Get("MirrorSources.Dialog.DeleteConfirm"),
            CloseButtonText = Loc.Get("MirrorSources.Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            LanguageCatalog catalog = await CreateAvailableCatalogAsync();
            await _sourceStore.DeleteCustomSourceAsync(
                catalog,
                row.Source.Owner,
                _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "MirrorSources.Messages.LogCustomDeleted",
                    row.ToolDisplayName,
                    row.DisplayName),
                [row.Source.Owner.ToString()]);
            await RenderAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.UpdateFailedTitle"),
                CoreErrorText.Localize(exception.Message));
        }
    }

    private async void OnAddCustomSourceClicked(object sender, RoutedEventArgs args)
    {
        LanguageCatalog catalog;
        try
        {
            catalog = await CreateAvailableCatalogAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.LoadFailedTitle"),
                CoreErrorText.Localize(exception.Message));
            return;
        }

        ProviderChoice[] providers = catalog.Tools
            .SelectMany(tool => tool.Providers.Select(
                provider => new ProviderChoice(tool, provider)))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (providers.Length == 0)
        {
            SetInfo(
                InfoBarSeverity.Warning,
                Loc.Get("MirrorSources.Messages.AddFailedTitle"),
                Loc.Get("MirrorSources.Messages.NoProviders"));
            return;
        }

        ComboBox providerPicker = new()
        {
            Header = Loc.Get("MirrorSources.Dialog.ProviderHeader"),
            ItemsSource = providers,
            DisplayMemberPath = nameof(ProviderChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            providerPicker,
            Loc.Get("MirrorSources.Dialog.ProviderHeader"));
        TextBox slotId = new()
        {
            Header = Loc.Get("MirrorSources.Dialog.SlotIdHeader"),
            PlaceholderText = "company-mirror",
        };
        TextBox displayName = new()
        {
            Header = Loc.Get("MirrorSources.Dialog.NameHeader"),
            PlaceholderText = Loc.Get("MirrorSources.Dialog.NamePlaceholder"),
        };
        TextBox endpoint = new()
        {
            Header = Loc.Get("MirrorSources.Dialog.EndpointHeader"),
            PlaceholderText = "https://mirror.example/",
        };
        TextBox purpose = new()
        {
            Header = Loc.Get("MirrorSources.Dialog.PurposeHeader"),
            PlaceholderText = Loc.Get("MirrorSources.Dialog.PurposePlaceholder"),
        };
        ComboBox kindPicker = new()
        {
            Header = Loc.Get("MirrorSources.Dialog.KindHeader"),
            ItemsSource = Enum.GetValues<ProviderMirrorEndpointKind>(),
            SelectedItem = ProviderMirrorEndpointKind.GenericDownload,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            kindPicker,
            Loc.Get("MirrorSources.Dialog.KindHeader"));
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("MirrorSources.Dialog.AddTitle"),
            Content = new StackPanel
            {
                MaxWidth = 620,
                Spacing = 8,
                Children = { providerPicker, slotId, displayName, endpoint, purpose, kindPicker },
            },
            PrimaryButtonText = Loc.Get("MirrorSources.Dialog.AddConfirm"),
            CloseButtonText = Loc.Get("MirrorSources.Dialog.Cancel"),
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
                catalog,
                new ProviderSourceOwner(selected.Tool.Id, selected.Provider.Id, slotId.Text),
                displayName.Text,
                endpoint.Text,
                endpointKind,
                purpose.Text,
                enabled: true,
                _pageCancellation.Token);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.SettingsChange,
                ActivityStatus.Succeeded,
                Loc.Format(
                    "MirrorSources.Messages.LogCustomAdded",
                    selected.Tool.DisplayName,
                    displayName.Text),
                [selected.Tool.Id, selected.Provider.Id, slotId.Text]);
            await RenderAsync();
        }
        catch (Exception exception) when (exception is ProviderSourcePreferenceException
            or InvalidDataException
            or IOException)
        {
            SetInfo(
                InfoBarSeverity.Error,
                Loc.Get("MirrorSources.Messages.AddFailedTitle"),
                CoreErrorText.Localize(exception.Message));
        }
    }

    private void SetInfo(InfoBarSeverity severity, string title, string message)
    {
        MirrorInfo.Severity = severity;
        MirrorInfo.Title = title;
        MirrorInfo.Message = message;
        MirrorInfo.IsOpen = true;
    }

    private sealed class MirrorGroup : List<MirrorRow>
    {
        public MirrorGroup(string key, IEnumerable<MirrorRow> rows)
            : base(rows)
        {
            Key = key;
        }

        public string Key { get; }
    }

    private sealed record ProviderChoice(
        LanguageToolDefinition Tool,
        LanguageToolProviderDefinition Provider)
    {
        public string Label => $"{Tool.DisplayName} · {Provider.DisplayName}";
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
            DisplayName = source.DisplayName;
            ToolDisplayName = tool.DisplayName;
            OwnerText = $"{provider.DisplayName} · {tool.DisplayName}";
            EndpointText = source.ConfiguredEndpoint.AbsoluteUri;
            Purpose = source.Purpose;
            PurposeVisibility = string.IsNullOrWhiteSpace(source.Purpose)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        public LanguageToolDefinition Tool { get; }

        public LanguageToolProviderDefinition Provider { get; }

        public ResolvedProviderSource Source { get; }

        public bool UserOverridable { get; }

        public string DisplayName { get; }

        public string ToolDisplayName { get; }

        public string OwnerText { get; }

        public string EndpointText { get; }

        public string Purpose { get; }

        public Visibility PurposeVisibility { get; }

        public bool MatchesQuery(string query) =>
            DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ToolDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Provider.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || EndpointText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
