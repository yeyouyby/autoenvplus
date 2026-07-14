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
    private readonly SemaphoreSlim _installOperationLock = new(1, 1);
    private CancellationTokenSource? _installCancellation;

    public ToolchainsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _installCancellation?.Cancel();
        _pageCancellation.Cancel();
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
        if (sender is not Button { Tag: string value }
            || !Enum.TryParse(value, out ToolchainComponent component))
        {
            return;
        }

        if (!_installOperationLock.Wait(0))
        {
            return;
        }

        SetInstallButtonsEnabled(false);
        ExternalToolInstallPlan? plan = null;
        try
        {
            WingetToolchainInstaller installer = new();
            string? winget = installer.FindWinget();
            if (winget is null)
            {
                ToolchainInfo.Severity = InfoBarSeverity.Error;
                ToolchainInfo.Title = "找不到 WinGet";
                ToolchainInfo.Message = "请先安装或修复 Microsoft App Installer。";
                return;
            }

            plan = installer.CreatePlan(component, winget);
            ContentDialog confirmation = CreateInstallConfirmation(plan);
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            CancellationToken cancellationToken = BeginInstallOperation(plan);
            cancellationToken.ThrowIfCancellationRequested();
            ExternalToolInstallResult result = await installer.InstallAsync(
                plan,
                cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"WinGet 退出码 {result.ExitCode}：{result.StandardError}");
            }

            ToolchainInfo.Severity = InfoBarSeverity.Success;
            ToolchainInfo.Title = "组件安装完成";
            ToolchainInfo.Message = plan.DisplayName;
            CancelInstallButton.IsEnabled = false;
            await DiscoverAndRenderAsync();
        }
        catch (OperationCanceledException)
        {
            ToolchainInfo.Severity = InfoBarSeverity.Informational;
            ToolchainInfo.Title = "安装已取消";
            ToolchainInfo.Message = plan?.DisplayName ?? "工具链组件";
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
            EndInstallOperation();
            SetInstallButtonsEnabled(!_pageCancellation.IsCancellationRequested);
            _installOperationLock.Release();
        }
    }

    private ContentDialog CreateInstallConfirmation(ExternalToolInstallPlan plan) => new()
    {
        XamlRoot = XamlRoot,
        Title = $"安装 {plan.DisplayName}",
        Content = new TextBlock
        {
            IsTextSelectionEnabled = true,
            Text = $"将通过 WinGet 精确安装以下包：\n{plan.PackageId}\n\n{(plan.MayRequireElevation ? "AutoEnvPlus 保持普通用户权限；包安装器可能单独请求管理员权限。" : "AutoEnvPlus 和包安装器通常都不需要管理员权限。")}\n\nAutoEnvPlus 仅使用内置白名单包标识，不会执行用户提供的任意命令参数。",
            TextWrapping = TextWrapping.Wrap,
        },
        PrimaryButtonText = "调用 WinGet 安装",
        CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Close,
    };

    private CancellationToken BeginInstallOperation(ExternalToolInstallPlan plan)
    {
        _installCancellation?.Dispose();
        _installCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        InstallProgress.IsActive = true;
        CancelInstallButton.IsEnabled = true;
        ToolchainInfo.Severity = InfoBarSeverity.Informational;
        ToolchainInfo.Title = "正在安装工具链组件";
        ToolchainInfo.Message = plan.PackageId;
        return _installCancellation.Token;
    }

    private void EndInstallOperation()
    {
        CancelInstallButton.IsEnabled = false;
        InstallProgress.IsActive = false;
        _installCancellation?.Dispose();
        _installCancellation = null;
    }

    private void SetInstallButtonsEnabled(bool enabled)
    {
        foreach (UIElement child in InstallActionsPanel.Children)
        {
            if (child is Button button)
            {
                button.IsEnabled = enabled;
            }
        }
    }

    private void OnCancelInstallClicked(object sender, RoutedEventArgs args)
    {
        if (_installCancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        cancellation.Cancel();
        CancelInstallButton.IsEnabled = false;
        ToolchainInfo.Severity = InfoBarSeverity.Informational;
        ToolchainInfo.Title = "正在取消安装";
        ToolchainInfo.Message = "正在停止 WinGet 及其启动的安装进程…";
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
