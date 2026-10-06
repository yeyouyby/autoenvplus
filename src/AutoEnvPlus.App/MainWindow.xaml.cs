using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.App.Pages;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Settings;
using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace AutoEnvPlus.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowBackdropManager _backdropManager;
    private readonly AppWindowTitleBar _appWindowTitleBar;
    private bool _suppressSelectionChanged;
    private string? _currentNavigationTag;

    public MainWindow()
        : this(AutoEnvPlusApplicationSettings.Default)
    {
    }

    public MainWindow(AutoEnvPlusApplicationSettings applicationSettings)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        applicationSettings.Validate();
        InitializeComponent();
        ApplyProductIdentity();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindowTitleBar = AppWindow.TitleBar;
        RootSurface.SizeChanged += OnRootSurfaceSizeChanged;
        RootSurface.Loaded += OnRootSurfaceLoaded;
        Closed += OnWindowClosed;
        ConfigureSettingsNavigationItem();

        _backdropManager = new WindowBackdropManager(
            this,
            RootSurface,
            applicationSettings.Backdrop);
        ApplyApplicationSettings(applicationSettings);
        AppWindow.Resize(new SizeInt32(1180, 760));
        NavigateTo(ApplicationSettingsPresentationPolicy.GetStartupNavigationTag(
            applicationSettings.StartupDestination));
    }

    internal AutoEnvPlusApplicationSettings CurrentApplicationSettings { get; private set; } =
        AutoEnvPlusApplicationSettings.Default;

    internal void ApplyApplicationSettings(AutoEnvPlusApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        PagePaddingMetrics padding = ApplicationSettingsPresentationPolicy.GetPagePadding(
            settings.Density);
        Application.Current.Resources["PagePadding"] = new Thickness(
            padding.Left,
            padding.Top,
            padding.Right,
            padding.Bottom);
        RootSurface.RequestedTheme = ApplicationSettingsPresentationPolicy.GetRequestedTheme(
            settings.Theme) switch
        {
            RequestedElementTheme.Default => ElementTheme.Default,
            RequestedElementTheme.Light => ElementTheme.Light,
            RequestedElementTheme.Dark => ElementTheme.Dark,
            _ => throw new InvalidOperationException("Unsupported application theme selection."),
        };
        _backdropManager.SetPreference(settings.Backdrop);
        CurrentApplicationSettings = settings;
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateCore("settings");
            return;
        }

        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateCore(tag);
        }
    }

    internal void NavigateTo(string tag, string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        bool moveFocus = ContentFrame.Content is not null;
        ShellPageMetadata metadata = GetPageMetadata(tag);
        Control? navigationItem = SelectNavigationItem(metadata.Tag);

        if (context is null
            && metadata.Tag.Equals(_currentNavigationTag, StringComparison.Ordinal))
        {
            QueueNavigationFocus(navigationItem);
            return;
        }

        NavigateCore(metadata.Tag, context);
        if (moveFocus)
        {
            QueueNavigationFocus(SelectNavigationItem(
                _currentNavigationTag ?? metadata.Tag));
        }
    }

    private void NavigateCore(string tag, string? context = null)
    {
        ShellPageMetadata metadata = GetPageMetadata(tag);
        Page page;
        try
        {
            page = CreatePage(metadata.Tag, context);
        }
        catch (InvalidOperationException) when (!ManagedRootResolver.TryResolve(
            null,
            out _,
            out _))
        {
            metadata = GetPageMetadata("settings");
            _ = SelectNavigationItem(metadata.Tag);
            page = new SettingsPage(_backdropManager);
        }

        ContentFrame.Content = page;
        _currentNavigationTag = metadata.Tag;
        UpdatePageHeader(metadata);
    }

    private Page CreatePage(string tag, string? context) => tag switch
    {
        "dashboard" => new DashboardPage(),
        "languages" => new LanguagesPage(),
        "path" => new PathPage(),
        "storage" => new StoragePage(),
        "projects" => new ProjectsPage(context),
        "downloads" => new DownloadsPage(),
        "doctor" => new DiagnosticsPage(),
        "activity" => new ActivityPage(),
        "settings" => new SettingsPage(_backdropManager),
        _ => new DashboardPage(),
    };

    private Control? SelectNavigationItem(string tag)
    {
        object? item = tag.Equals("settings", StringComparison.Ordinal)
            ? RootNavigation.SettingsItem
            : RootNavigation.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(candidate => candidate.Tag is string candidateTag
                    && candidateTag.Equals(tag, StringComparison.Ordinal));
        if (item is null || ReferenceEquals(RootNavigation.SelectedItem, item))
        {
            return item as Control;
        }

        _suppressSelectionChanged = true;
        try
        {
            RootNavigation.SelectedItem = item;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        return item as Control;
    }

    private void UpdatePageHeader(ShellPageMetadata metadata)
    {
        PageHeaderTitle.Text = metadata.Title;
        PageHeaderSubtitle.Text = metadata.Subtitle;
        AutomationProperties.SetName(
            ShellPageHeader,
            $"{metadata.Title}。{metadata.Subtitle}");
        AutomationProperties.SetName(ContentFrame, $"{metadata.Title}内容");
    }

    private void ConfigureSettingsNavigationItem()
    {
        if (RootNavigation.SettingsItem is not NavigationViewItem settingsItem)
        {
            return;
        }

        settingsItem.Content = "设置";
        AutomationProperties.SetName(settingsItem, "设置");
        ToolTipService.SetToolTip(settingsItem, "设置（Ctrl+0）");
    }

    private void QueueNavigationFocus(Control? navigationItem)
    {
        if (navigationItem is null || ContentFrame.Content is null)
        {
            return;
        }

        _ = navigationItem.DispatcherQueue.TryEnqueue(() =>
            navigationItem.Focus(FocusState.Keyboard));
    }

    private void OnNavigationAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        string? tag = sender.Key switch
        {
            VirtualKey.Number1 => "dashboard",
            VirtualKey.Number2 => "languages",
            VirtualKey.Number3 => "projects",
            VirtualKey.Number4 => "downloads",
            VirtualKey.Number5 => "path",
            VirtualKey.Number6 => "storage",
            VirtualKey.Number7 => "doctor",
            VirtualKey.Number8 => "activity",
            VirtualKey.Number0 => "settings",
            _ => null,
        };
        if (tag is null)
        {
            return;
        }

        NavigateTo(tag);
        args.Handled = true;
    }

    private void OnRootSurfaceSizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateTitleBarInsets(_appWindowTitleBar);

    private void OnRootSurfaceLoaded(object sender, RoutedEventArgs args)
    {
        // XamlRoot is only available once the window content is attached to the
        // XAML tree; touching it from the constructor throws
        // NullReferenceException before the window is ever shown.
        if (AppTitleBar.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed += OnTitleBarXamlRootChanged;
            UpdateTitleBarInsets(_appWindowTitleBar);
        }
    }

    private void OnTitleBarXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        UpdateTitleBarInsets(_appWindowTitleBar);

    private void UpdateTitleBarInsets(AppWindowTitleBar titleBar)
    {
        if (AppTitleBar.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        double scale = xamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        TitleBarLeftInsetColumn.Width = new GridLength(titleBar.LeftInset / scale);
        TitleBarRightInsetColumn.Width = new GridLength(titleBar.RightInset / scale);
    }

    private void ApplyProductIdentity()
    {
        ProductIdentityPresentation identity =
            ProductIdentityPresentationPolicy.FromAssembly(typeof(MainWindow).Assembly);
        Title = identity.WindowTitle;
        VersionBadgeText.Text = identity.DisplayVersion;
        AutomationProperties.SetName(AppTitleBar, identity.AutomationName);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;
        RootSurface.SizeChanged -= OnRootSurfaceSizeChanged;
        RootSurface.Loaded -= OnRootSurfaceLoaded;
        if (AppTitleBar.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed -= OnTitleBarXamlRootChanged;
        }
    }

    private static ShellPageMetadata GetPageMetadata(string tag) => tag switch
    {
        "dashboard" => new("dashboard", "概览", "上次环境快照与常用入口"),
        "languages" => new("languages", "语言工具", "版本、Provider 与全局选择"),
        "projects" => new("projects", "项目环境", "隔离配置与项目工具链"),
        "downloads" => new("downloads", "下载中心", "受管包与传输任务"),
        "path" => new("path", "PATH 与命令", "命令路由、冲突与回滚"),
        "storage" => new("storage", "缓存与存储", "受管数据、迁移与清理"),
        "doctor" => new("doctor", "环境诊断", "只读检查与报告导出"),
        "activity" => new("activity", "活动记录", "关键变更与操作结果"),
        "settings" => new("settings", "设置", "外观、启动、网络与日志"),
        _ => new("dashboard", "概览", "上次环境快照与常用入口"),
    };

    private sealed record ShellPageMetadata(
        string Tag,
        string Title,
        string Subtitle);
}
