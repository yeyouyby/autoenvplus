using System.Diagnostics;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoEnvPlus.App.Pages;

public sealed partial class ToolchainsPage : Page
{
    private readonly CancellationTokenSource _pageCancellation = new();

    public ToolchainsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _pageCancellation.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            await DiscoverAndRenderAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DiscoverAndRenderAsync()
    {
        CppToolchainDiscoveryResult result = await new CppToolchainDiscoveryService().DiscoverAsync(
            _pageCancellation.Token);
        RenderVisualStudio(result.VisualStudioInstallations);
        RenderSdks(result.WindowsSdks);
        RenderTools(result.BuildTools);
        ToolchainInfo.Severity = result.Errors.Count == 0
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        ToolchainInfo.Title = "工具链检测完成";
        ToolchainInfo.Message = $"Visual C++ {result.VisualStudioInstallations.Count} 套 · Windows SDK {result.WindowsSdks.Count} 套 · 独立工具 {result.BuildTools.Count} 个"
            + (result.Errors.Count > 0 ? $" · {string.Join("；", result.Errors)}" : string.Empty);
    }

    private async void OnInstallComponentClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button
            || button.Tag is not string value
            || !Enum.TryParse(value, out ToolchainComponent component))
        {
            return;
        }

        WingetToolchainInstaller installer = new();
        string? winget = installer.FindWinget();
        if (winget is null)
        {
            ToolchainInfo.Severity = InfoBarSeverity.Error;
            ToolchainInfo.Title = "找不到 WinGet";
            ToolchainInfo.Message = "请先安装或修复 Microsoft App Installer。";
            return;
        }

        ExternalToolInstallPlan plan = installer.CreatePlan(component, winget);
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"安装 {plan.DisplayName}",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"将通过 WinGet 精确安装以下包：\n{plan.PackageId}\n\n{(plan.MayRequireElevation ? "包安装器可能请求管理员权限。" : "通常不需要管理员权限。")}\n\nAutoEnvPlus 不会执行用户提供的任意命令参数。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "调用 WinGet 安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        button.IsEnabled = false;
        ToolchainInfo.Severity = InfoBarSeverity.Informational;
        ToolchainInfo.Title = "正在安装工具链组件";
        ToolchainInfo.Message = plan.PackageId;
        try
        {
            ExternalToolInstallResult result = await installer.InstallAsync(
                plan,
                _pageCancellation.Token);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"WinGet 退出码 {result.ExitCode}：{result.StandardError}");
            }

            ToolchainInfo.Severity = InfoBarSeverity.Success;
            ToolchainInfo.Title = "组件安装完成";
            ToolchainInfo.Message = plan.DisplayName;
            await DiscoverAndRenderAsync();
        }
        catch (OperationCanceledException)
        {
            ToolchainInfo.Severity = InfoBarSeverity.Informational;
            ToolchainInfo.Title = "安装已取消";
            ToolchainInfo.Message = plan.DisplayName;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or System.ComponentModel.Win32Exception)
        {
            ToolchainInfo.Severity = InfoBarSeverity.Error;
            ToolchainInfo.Title = "组件安装失败";
            ToolchainInfo.Message = exception.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void RenderVisualStudio(IReadOnlyList<VisualCppInstallation> installations)
    {
        VisualStudioPanel.Children.Clear();
        if (installations.Count == 0)
        {
            VisualStudioPanel.Children.Add(EmptyText("未检测到包含 C++ 工具的 Visual Studio/Build Tools。"));
            return;
        }

        foreach (VisualCppInstallation installation in installations)
        {
            Grid grid = CardGrid();
            StackPanel text = new() { Spacing = 4 };
            text.Children.Add(new TextBlock
            {
                Text = installation.DisplayName,
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            text.Children.Add(SecondaryText(
                $"MSVC {installation.MsvcToolsVersion ?? "未识别"} · {installation.InstallationPath}"));
            grid.Children.Add(text);
            Button terminal = CreateTerminalButton(installation);
            Grid.SetColumn(terminal, 1);
            grid.Children.Add(terminal);
            VisualStudioPanel.Children.Add(Card(grid));
        }
    }

    private void RenderSdks(IReadOnlyList<WindowsSdkInstallation> sdks)
    {
        SdkPanel.Children.Clear();
        if (sdks.Count == 0)
        {
            SdkPanel.Children.Add(EmptyText("未检测到完整 Windows SDK。"));
            return;
        }

        foreach (WindowsSdkInstallation sdk in sdks)
        {
            SdkPanel.Children.Add(Card(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Windows SDK {sdk.Version}",
                        FontSize = 16,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    SecondaryText($"架构：{string.Join(", ", sdk.Architectures)} · {sdk.RootPath}"),
                },
            }));
        }
    }

    private void RenderTools(IReadOnlyList<DiscoveredRuntime> tools)
    {
        BuildToolsPanel.Children.Clear();
        if (tools.Count == 0)
        {
            BuildToolsPanel.Children.Add(EmptyText("clang、gcc、CMake、Ninja 当前不在 PATH。"));
            return;
        }

        foreach (DiscoveredRuntime tool in tools)
        {
            BuildToolsPanel.Children.Add(Card(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{ToolDisplayName(tool.Kind)} {tool.Version?.ToString() ?? "未知版本"}",
                        FontSize = 16,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    },
                    SecondaryText(tool.ExecutablePath),
                },
            }));
        }
    }

    private Button CreateTerminalButton(VisualCppInstallation installation)
    {
        Button terminal = new()
        {
            Content = "打开开发终端",
            IsEnabled = installation.ActivationScript is not null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        MenuFlyout menu = new();
        IReadOnlyList<CppArchitecturePair> pairs = installation.AvailableArchitecturePairs?.Count > 0
            ? installation.AvailableArchitecturePairs
            : [new CppArchitecturePair(
                RuntimeArchitecture.X64,
                RuntimeArchitecture.X64,
                "x64")];
        foreach (CppArchitecturePair pair in pairs
            .OrderBy(pair => pair.HostArchitecture == RuntimeArchitecture.X64 ? 0 : 1)
            .ThenBy(pair => pair.TargetArchitecture))
        {
            MenuFlyoutItem item = new()
            {
                Text = $"{pair.TargetArchitecture} 目标 · {pair.HostArchitecture} Host",
                Tag = new TerminalActivationRequest(installation, pair),
            };
            item.Click += OnOpenTerminalClicked;
            menu.Items.Add(item);
        }

        terminal.Flyout = menu;
        return terminal;
    }

    private void OnOpenTerminalClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: TerminalActivationRequest request })
        {
            return;
        }

        try
        {
            CppActivationPlan plan = new CppToolchainDiscoveryService().CreateActivationPlan(
                request.Installation,
                request.Pair.TargetArchitecture,
                request.Pair.HostArchitecture);
            ProcessStartInfo startInfo = new()
            {
                FileName = plan.Executable,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = true,
            };
            foreach (string argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ToolchainInfo.Severity = InfoBarSeverity.Error;
            ToolchainInfo.Title = "无法打开开发终端";
            ToolchainInfo.Message = exception.Message;
        }
    }

    private static Grid CardGrid()
    {
        Grid grid = new() { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static Border Card(UIElement child) => new()
    {
        Padding = new Thickness(18),
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Child = child,
    };

    private static TextBlock SecondaryText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static string ToolDisplayName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Llvm => "LLVM / Clang",
        RuntimeKind.Mingw => "MinGW / GCC",
        _ => kind.ToString(),
    };

    private sealed record TerminalActivationRequest(
        VisualCppInstallation Installation,
        CppArchitecturePair Pair);
}
