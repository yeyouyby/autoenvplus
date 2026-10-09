using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.App.Pages;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Settings;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace AutoEnvPlus.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowBackdropManager _backdropManager;
    private readonly AppWindowTitleBar _appWindowTitleBar;
    private AppDownloadManager _downloadManager = null!;
    private bool _allowClose;
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
        RootSurface.ActualThemeChanged += OnRootSurfaceActualThemeChanged;
        Closed += OnWindowClosed;
        AppWindow.Closing += OnAppWindowClosing;
        ConfigureSettingsNavigationItem();

        _backdropManager = new WindowBackdropManager(
            this,
            RootSurface,
            applicationSettings.Backdrop);
        ApplyApplicationSettings(applicationSettings);
        if (!TryRestoreWindowState())
        {
            ResizeWindowForDisplayScale();
        }
        NavigateTo(ApplicationSettingsPresentationPolicy.GetStartupNavigationTag(
            applicationSettings.StartupDestination));

        // Shell-level transfer visibility: while a download, import, or pip
        // install runs, badge the downloads nav item no matter which page is
        // currently shown (the pages themselves only see their own state).
        _downloadManager = ((App)Application.Current).DownloadManager;
        _downloadManager.StateChanged += OnDownloadStateChanged;
        UpdateTransferBadge();
    }

    private void OnDownloadStateChanged(object? sender, EventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(UpdateTransferBadge);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private sealed record WindowState(int X, int Y, int Width, int Height);

    private static string? TryGetWindowStatePath()
    {
        try
        {
            if (!ManagedRootResolver.TryResolve(null, out string? root, out _)
                || root is null)
            {
                return null;
            }

            return Path.Combine(Path.GetFullPath(root), "state", "window-state.json");
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private bool TryRestoreWindowState()
    {
        // Restore the last session's window placement; fall back to the
        // DPI-scaled design size when no valid state exists (first run, or the
        // saved monitor was disconnected).
        try
        {
            string? path = TryGetWindowStatePath();
            if (path is null || !File.Exists(path))
            {
                return false;
            }

            WindowState? state = JsonSerializer.Deserialize<WindowState>(
                File.ReadAllText(path));
            if (state is null
                || state.Width < 400
                || state.Height < 300
                || state.X < -32000
                || state.Y < -32000)
            {
                return false;
            }

            // Index the WinRT list directly: enumerating the projected
            // IReadOnlyList via LINQ throws InvalidCastException (the
            // IEnumerable cast is unsupported for this projection).
            bool intersectsAnyDisplay = false;
            IReadOnlyList<DisplayArea> areas = DisplayArea.FindAll();
            for (int i = 0; i < areas.Count; i++)
            {
                RectInt32 work = areas[i].WorkArea;
                if (state.X < work.X + work.Width
                    && state.X + state.Width > work.X
                    && state.Y < work.Y + work.Height
                    && state.Y + state.Height > work.Y)
                {
                    intersectsAnyDisplay = true;
                    break;
                }
            }

            if (!intersectsAnyDisplay)
            {
                return false;
            }

            AppWindow.MoveAndResize(new RectInt32(
                state.X,
                state.Y,
                state.Width,
                state.Height));
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or InvalidOperationException
            or InvalidCastException)
        {
            return false;
        }
    }

    private void TrySaveWindowState()
    {
        try
        {
            string? path = TryGetWindowStatePath();
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new WindowState(
                    AppWindow.Position.X,
                    AppWindow.Position.Y,
                    AppWindow.Size.Width,
                    AppWindow.Size.Height)));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // Window placement is a convenience; never block shutdown on it.
        }
    }

    private void ResizeWindowForDisplayScale()
    {
        // AppWindow.Resize works in physical pixels; a fixed 1180x760 looks
        // 33% smaller on a 150% display. Scale the design size by the window
        // DPI and clamp to the current monitor's work area.
        const int DesignWidth = 1180;
        const int DesignHeight = 760;
        double scale = 1;
        try
        {
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            scale = dpi > 0 ? dpi / 96d : 1;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 10 1607: fall back to the unscaled design size.
        }

        int width = (int)(DesignWidth * scale);
        int height = (int)(DesignHeight * scale);
        DisplayArea area = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);
        if (area.WorkArea.Width > 0)
        {
            width = Math.Min(width, area.WorkArea.Width);
            height = Math.Min(height, area.WorkArea.Height);
        }

        AppWindow.Resize(new SizeInt32(width, height));
    }

    private void UpdateTransferBadge()
    {
        AppDownloadManager manager = _downloadManager;
        bool busy = manager.Snapshot?.IsBusy == true || manager.IsPipInstallRunning;
        DownloadsTransferBadge.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool HasActiveBackgroundWork() => _downloadManager.Snapshot?.IsBusy == true
        || _downloadManager.IsPipInstallRunning;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !HasActiveBackgroundWork())
        {
            return;
        }

        // A transfer or pip install is still running; closing kills it with no
        // confirmation, so intercept once and ask.
        args.Cancel = true;
        _ = ConfirmCloseWithActiveWorkAsync();
    }

    private async Task ConfirmCloseWithActiveWorkAsync()
    {
        ContentDialog dialog = new()
        {
            XamlRoot = RootSurface.XamlRoot
                ?? throw new InvalidOperationException("窗口尚未完成初始化。"),
            Title = "有任务正在进行",
            Content = new TextBlock
            {
                Text = "下载或 pip 安装仍在进行；关闭窗口会中止任务，已下载的分段会尽力清理，pip 安装可能留下部分更改且不会回滚。仍要关闭吗？",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "关闭并中止任务",
            CloseButtonText = "继续运行",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _allowClose = true;
            Close();
        }
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
            // Ctrl+9 aliases Ctrl+0 so the navigation sequence Ctrl+1..9 has
            // no dead key between the last page and settings.
            VirtualKey.Number9 => "settings",
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

        UpdateTitleBarButtonColors();
    }

    private void OnRootSurfaceActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateTitleBarButtonColors();

    private void UpdateTitleBarButtonColors()
    {
        // When the app theme differs from the OS theme the system caption
        // buttons keep OS-theme colors and can become invisible (dark glyphs
        // on a dark window). Sync them to the window content's actual theme.
        try
        {
            bool dark = RootSurface.ActualTheme == ElementTheme.Dark;
            Windows.UI.Color foreground = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            Windows.UI.Color hoverBackground = dark
                ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            Windows.UI.Color pressedBackground = dark
                ? Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x28, 0x00, 0x00, 0x00);
            _appWindowTitleBar.ButtonForegroundColor = foreground;
            _appWindowTitleBar.ButtonHoverForegroundColor = foreground;
            _appWindowTitleBar.ButtonPressedForegroundColor = foreground;
            _appWindowTitleBar.ButtonHoverBackgroundColor = hoverBackground;
            _appWindowTitleBar.ButtonPressedBackgroundColor = pressedBackground;
        }
        catch (Exception)
        {
            // Title bar color APIs are unavailable in some hosting modes;
            // the system defaults remain in effect.
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
        TrySaveWindowState();
        Closed -= OnWindowClosed;
        AppWindow.Closing -= OnAppWindowClosing;
        if (_downloadManager is not null)
        {
            _downloadManager.StateChanged -= OnDownloadStateChanged;
        }

        RootSurface.SizeChanged -= OnRootSurfaceSizeChanged;
        RootSurface.Loaded -= OnRootSurfaceLoaded;
        RootSurface.ActualThemeChanged -= OnRootSurfaceActualThemeChanged;
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
