using System.Text;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Projects;
using AutoEnvPlus.Core.State;
using AutoEnvPlus.Core.Toolchains;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class ProjectsPage : Page
{
    private readonly string? _initialProjectRoot;
    private string? _projectRoot;
    private string? _manifestPath;
    private ProjectEnvironmentImportResult? _import;
    private string? _lastCMakePresetSnapshot;
    private CancellationTokenSource? _virtualEnvironmentScanCancellation;

    public ProjectsPage()
        : this(null)
    {
    }

    internal ProjectsPage(string? initialProjectRoot)
    {
        _initialProjectRoot = string.IsNullOrWhiteSpace(initialProjectRoot)
            ? null
            : Path.GetFullPath(initialProjectRoot);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        if (_initialProjectRoot is not null)
        {
            await TryLoadProjectAsync(_initialProjectRoot);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _virtualEnvironmentScanCancellation?.Cancel();
        _virtualEnvironmentScanCancellation?.Dispose();
        _virtualEnvironmentScanCancellation = null;
    }

    private async void OnChooseProjectClicked(object sender, RoutedEventArgs args)
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            CommitButtonText = "选择项目",
        };
        picker.FileTypeFilter.Add("*");
        if (((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await TryLoadProjectAsync(folder.Path);
    }

    private async Task TryLoadProjectAsync(string startPath)
    {
        try
        {
            if (!Directory.Exists(startPath))
            {
                throw new DirectoryNotFoundException($"项目目录不存在：{startPath}");
            }

            await LoadProjectAsync(startPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = "无法读取项目环境";
            ProjectInfo.Message = exception.Message;
        }
    }

    private async Task LoadProjectAsync(string startPath)
    {
        _virtualEnvironmentScanCancellation?.Cancel();
        _virtualEnvironmentScanCancellation?.Dispose();
        _virtualEnvironmentScanCancellation = null;
        _manifestPath = new ProjectManifestService().FindManifest(startPath);
        _import = new ProjectEnvironmentImportService().Discover(startPath);
        _projectRoot = _manifestPath is not null
            ? Path.GetDirectoryName(_manifestPath)
            : _import.ProjectRoot ?? Path.GetFullPath(startPath);
        ProjectPathText.Text = _projectRoot;
        StringBuilder details = new();

        if (_manifestPath is not null)
        {
            ProjectManifestLoadResult manifest = new ProjectManifestService().Load(_manifestPath);
            details.AppendLine($"Manifest: {_manifestPath}");
            foreach ((AutoEnvPlus.Core.Runtimes.RuntimeKind kind, AutoEnvPlus.Core.Runtimes.VersionSelector selector) in manifest.Manifest.Tools)
            {
                details.AppendLine($"{kind}: {selector}");
            }

            foreach (ProjectManifestError error in manifest.Errors)
            {
                details.AppendLine($"line {error.LineNumber}: {error.Message}");
            }
        }
        else if (_import.Found)
        {
            details.AppendLine("发现可导入的版本声明：");
            foreach (ImportedRuntimeSelection source in _import.Sources)
            {
                details.AppendLine($"{source.Kind}: {source.Selector} ← {source.SourcePath} ({source.RawValue})");
            }

            foreach (string warning in _import.Warnings)
            {
                details.AppendLine($"警告：{warning}");
            }
        }
        else
        {
            details.AppendLine("没有发现 AutoEnvPlus 支持的版本声明文件。可以手动创建 autoenvplus.toml。");
        }

        ProjectDetailsText.Text = details.ToString().TrimEnd();
        OpenProjectTerminalButton.IsEnabled = _manifestPath is not null;
        CreateManifestButton.IsEnabled = _manifestPath is null && _import.Found;
        CreateLockButton.IsEnabled = _manifestPath is not null;
        CreateCMakePresetButton.IsEnabled = File.Exists(
            Path.Combine(_projectRoot!, "CMakeLists.txt"));
        ResetVirtualEnvironmentDiscovery();
        await new KnownProjectStore(GetManagedRoot()).AddAsync(_projectRoot!);
        ProjectInfo.Severity = InfoBarSeverity.Success;
        ProjectInfo.Title = "项目环境已读取";
        ProjectInfo.Message = _manifestPath is not null
            ? "可以使用已安装语言工具生成精确锁文件。"
            : "确认导入来源后可生成 autoenvplus.toml。";
    }

    private async void OnDiscoverVirtualEnvironmentsClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null)
        {
            return;
        }

        string scannedRoot = _projectRoot;
        _virtualEnvironmentScanCancellation?.Cancel();
        _virtualEnvironmentScanCancellation?.Dispose();
        CancellationTokenSource scanCancellation = new();
        _virtualEnvironmentScanCancellation = scanCancellation;
        CancellationToken cancellationToken = scanCancellation.Token;
        SetVirtualEnvironmentBusy(true);
        VirtualEnvironmentList.ItemsSource = null;
        VirtualEnvironmentList.Visibility = Visibility.Collapsed;
        VirtualEnvironmentEmptyText.Visibility = Visibility.Collapsed;
        VirtualEnvironmentInfo.IsOpen = true;
        VirtualEnvironmentInfo.Severity = InfoBarSeverity.Informational;
        VirtualEnvironmentInfo.Title = "正在解析项目虚拟环境";
        VirtualEnvironmentInfo.Message = "只读取固定候选路径和受大小限制的配置文件。";
        try
        {
            ProjectVirtualEnvironmentDiscoveryResult result = await Task.Run(
                () => new ProjectVirtualEnvironmentDiscoveryService().Discover(
                    scannedRoot,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (!IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                return;
            }

            ShowVirtualEnvironmentDiscovery(result);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                VirtualEnvironmentInfo.IsOpen = true;
                VirtualEnvironmentInfo.Severity = InfoBarSeverity.Informational;
                VirtualEnvironmentInfo.Title = "解析已取消";
                VirtualEnvironmentInfo.Message = "没有修改项目文件或环境设置。";
                VirtualEnvironmentEmptyText.Text = "可再次点击“解析虚拟环境”。";
                VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            if (IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                VirtualEnvironmentInfo.IsOpen = true;
                VirtualEnvironmentInfo.Severity = InfoBarSeverity.Error;
                VirtualEnvironmentInfo.Title = "无法解析虚拟环境";
                VirtualEnvironmentInfo.Message = exception.Message;
                VirtualEnvironmentEmptyText.Text = "扫描失败；项目内容未被修改。";
                VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (IsCurrentVirtualEnvironmentScan(scanCancellation, scannedRoot))
            {
                SetVirtualEnvironmentBusy(false);
                scanCancellation.Dispose();
                _virtualEnvironmentScanCancellation = null;
            }
        }
    }

    private void ResetVirtualEnvironmentDiscovery()
    {
        VirtualEnvironmentList.ItemsSource = null;
        VirtualEnvironmentList.Visibility = Visibility.Collapsed;
        VirtualEnvironmentInfo.IsOpen = false;
        VirtualEnvironmentEmptyText.Text = "点击“解析虚拟环境”读取当前项目的本地证据。";
        VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
        VirtualEnvironmentProgress.Visibility = Visibility.Collapsed;
        DiscoverVirtualEnvironmentsButton.IsEnabled = _projectRoot is not null;
    }

    private void ShowVirtualEnvironmentDiscovery(ProjectVirtualEnvironmentDiscoveryResult result)
    {
        VirtualEnvironmentRow[] rows = result.Environments
            .Select(environment => new VirtualEnvironmentRow(environment))
            .ToArray();
        int invalidCount = result.Environments.Count(environment =>
            environment.Health == ProjectVirtualEnvironmentHealth.Invalid);
        int attentionCount = result.Environments.Count(environment =>
            environment.Health == ProjectVirtualEnvironmentHealth.NeedsAttention);

        VirtualEnvironmentInfo.IsOpen = true;
        VirtualEnvironmentInfo.Severity = invalidCount > 0
            || attentionCount > 0
            || result.Warnings.Count > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        VirtualEnvironmentInfo.Title = rows.Length == 0
            ? "未发现项目虚拟环境"
            : $"发现 {rows.Length} 项本地环境证据";
        VirtualEnvironmentInfo.Message = rows.Length == 0
            ? result.Warnings.Count == 0
                ? $"已检查 {result.InspectedPathCount} 个固定候选路径；没有执行外部命令。"
                : string.Join("；", result.Warnings.Take(3))
            : $"需注意 {attentionCount} 项 · 无效 {invalidCount} 项 · "
                + $"检查路径 {result.InspectedPathCount} 个"
                + (result.ScanLimitReached ? " · 已达到扫描上限" : string.Empty);

        if (rows.Length == 0)
        {
            VirtualEnvironmentList.ItemsSource = null;
            VirtualEnvironmentList.Visibility = Visibility.Collapsed;
            VirtualEnvironmentEmptyText.Text = "没有发现 .venv/Conda、Node 依赖环境、.NET 工具清单、Java Wrapper、Rust 或 Go 工作区。";
            VirtualEnvironmentEmptyText.Visibility = Visibility.Visible;
            return;
        }

        VirtualEnvironmentList.ItemsSource = rows;
        VirtualEnvironmentList.Visibility = Visibility.Visible;
        VirtualEnvironmentEmptyText.Visibility = Visibility.Collapsed;
    }

    private void SetVirtualEnvironmentBusy(bool busy)
    {
        DiscoverVirtualEnvironmentsButton.IsEnabled = !busy && _projectRoot is not null;
        VirtualEnvironmentProgress.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool PathsEqual(string left, string? right) => right is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentVirtualEnvironmentScan(
        CancellationTokenSource scanCancellation,
        string scannedRoot) => ReferenceEquals(_virtualEnvironmentScanCancellation, scanCancellation)
        && PathsEqual(scannedRoot, _projectRoot);

    private async void OnCreateManifestClicked(object sender, RoutedEventArgs args)
    {
        if (_import is null || !_import.Found)
        {
            return;
        }

        try
        {
            _manifestPath = await new ProjectEnvironmentImportService().WriteManifestAsync(_import);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "项目清单已创建";
            ProjectInfo.Message = _manifestPath;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Succeeded,
                "已从现有版本声明生成 autoenvplus.toml。",
                [_projectRoot!, _manifestPath]);
            await LoadProjectAsync(_projectRoot!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = "无法创建项目清单";
            ProjectInfo.Message = exception.Message;
            if (_projectRoot is not null)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.ProjectImport,
                    ActivityStatus.Failed,
                    $"生成 autoenvplus.toml 失败。错误类型：{exception.GetType().Name}。",
                    [_projectRoot]);
            }
        }
    }

    private async void OnOpenProjectTerminalClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null || _manifestPath is null)
        {
            return;
        }

        OpenProjectTerminalButton.IsEnabled = false;
        try
        {
            ProjectTerminalService service = new(GetManagedRoot());
            ProjectTerminalHost[] availableHosts = service.IsHostAvailable(
                ProjectTerminalHost.WindowsTerminal)
                    ? [ProjectTerminalHost.WindowsTerminal, ProjectTerminalHost.WindowsPowerShell]
                    : [ProjectTerminalHost.WindowsPowerShell];
            TerminalHostChoice[] hostChoices = availableHosts
                .Select(host => new TerminalHostChoice(host, TerminalHostDisplayName(host)))
                .ToArray();
            Dictionary<ProjectTerminalHost, ProjectTerminalPlan> plans = [];
            foreach (ProjectTerminalHost host in availableHosts)
            {
                plans[host] = await service.CreatePlanAsync(_projectRoot, host);
            }

            ComboBox hostSelector = new()
            {
                ItemsSource = hostChoices,
                DisplayMemberPath = nameof(TerminalHostChoice.Label),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            TextBlock previewText = new()
            {
                IsTextSelectionEnabled = true,
                MinWidth = 500,
                TextWrapping = TextWrapping.Wrap,
            };
            StackPanel dialogContent = new() { Spacing = 10 };
            dialogContent.Children.Add(new TextBlock
            {
                Text = "终端主机",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            dialogContent.Children.Add(hostSelector);
            if (availableHosts.Length == 1)
            {
                dialogContent.Children.Add(new InfoBar
                {
                    IsClosable = false,
                    IsOpen = true,
                    Severity = InfoBarSeverity.Informational,
                    Title = "Windows Terminal 不可用",
                    Message = "当前未检测到 wt.exe，本次将打开 Windows PowerShell。",
                });
            }

            dialogContent.Children.Add(previewText);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = "打开项目已激活终端",
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = dialogContent,
                },
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            ProjectTerminalPlan selectedPlan = plans[hostChoices[0].Host];
            void UpdateTerminalPreview()
            {
                if (hostSelector.SelectedItem is not TerminalHostChoice choice)
                {
                    return;
                }

                selectedPlan = plans[choice.Host];
                previewText.Text = CreateTerminalPreview(selectedPlan);
                confirmation.PrimaryButtonText = choice.Host == ProjectTerminalHost.WindowsTerminal
                    ? "在 Windows Terminal 中打开"
                    : "打开新 PowerShell";
                confirmation.IsPrimaryButtonEnabled = selectedPlan.CanLaunch;
            }

            hostSelector.SelectionChanged += (_, _) => UpdateTerminalPreview();
            UpdateTerminalPreview();
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            int processId = await service.LaunchAsync(selectedPlan);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "项目终端已启动";
            ProjectInfo.Message = $"PID {processId} · {TerminalHostDisplayName(selectedPlan.EffectiveHost)} · 会话级精确版本，不修改用户 PATH 或全局默认。";
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = "无法打开项目终端";
            ProjectInfo.Message = exception.Message;
        }
        finally
        {
            OpenProjectTerminalButton.IsEnabled = _manifestPath is not null;
        }
    }

    private static string CreateTerminalPreview(ProjectTerminalPlan plan)
    {
        StringBuilder preview = new();
        preview.AppendLine($"项目\n{plan.ProjectRoot}");
        preview.AppendLine($"\n清单\n{plan.ManifestPath}");
        preview.AppendLine($"\n终端主机\n{TerminalHostDisplayName(plan.EffectiveHost)}");
        preview.AppendLine($"\nShell\n{plan.ShellExecutable} {string.Join(' ', plan.ShellArguments)}");
        preview.AppendLine($"\nShim\n{plan.ShimDirectory}");
        preview.AppendLine("\n精确语言工具");
        if (plan.Selections.Count == 0)
        {
            preview.AppendLine("（没有可由 AutoEnvPlus Shim 激活的语言工具）");
        }
        else
        {
            foreach (ProjectTerminalSelection selection in plan.Selections)
            {
                preview.AppendLine(
                    $"{selection.Kind}: {selection.RequestedSelector} → {selection.ResolvedVersion} ({selection.RuntimeId})");
                preview.AppendLine($"  {selection.EnvironmentVariable}={selection.ResolvedVersion}");
            }
        }

        ProjectTerminalNetworkSummary network = plan.NetworkSummary;
        preview.AppendLine("\n网络环境");
        if (!network.Applied)
        {
            preview.AppendLine("项目没有 Python 或 Node.js 网络工具，未注入包源环境。");
        }
        else
        {
            preview.AppendLine($"代理来源：{ProxySourceText(network.ProxySource)}");
            preview.AppendLine(
                $"HTTP：{ConfiguredText(network.HttpProxyConfigured)} · HTTPS：{ConfiguredText(network.HttpsProxyConfigured)} · 绕过项：{network.NoProxyEntryCount}");
            if (network.PipEnvironmentApplied)
            {
                preview.AppendLine($"pip 镜像：{MirrorText(network.PipMirrorConfigured)}");
            }

            if (network.NpmEnvironmentApplied)
            {
                preview.AppendLine($"npm registry：{MirrorText(network.NpmMirrorConfigured)}");
            }

            if (plan.EnvironmentRemovals.Count > 0)
            {
                preview.AppendLine($"将清除 {plan.EnvironmentRemovals.Count} 个不应继承的网络环境变量。");
            }
        }

        foreach (string warning in plan.Warnings)
        {
            preview.AppendLine($"\n警告：{warning}");
        }

        foreach (string error in plan.Errors)
        {
            preview.AppendLine($"\n错误：{error}");
        }

        return preview.ToString().TrimEnd();
    }

    private static string TerminalHostDisplayName(ProjectTerminalHost host) => host switch
    {
        ProjectTerminalHost.WindowsTerminal => "Windows Terminal",
        ProjectTerminalHost.WindowsPowerShell => "Windows PowerShell",
        _ => host.ToString(),
    };

    private static string ProxySourceText(ProjectTerminalProxySource source) => source switch
    {
        ProjectTerminalProxySource.Pip => "pip 工具覆盖",
        ProjectTerminalProxySource.Npm => "npm 工具覆盖",
        ProjectTerminalProxySource.MatchingPackageTools => "pip 与 npm 一致配置",
        ProjectTerminalProxySource.Downloads => "下载中心回退配置",
        _ => "直连",
    };

    private static string ConfiguredText(bool configured) => configured ? "已配置代理" : "直连";

    private static string MirrorText(bool configured) => configured ? "已配置" : "官方源";

    private async void OnCreateLockClicked(object sender, RoutedEventArgs args)
    {
        if (_manifestPath is null)
        {
            return;
        }

        try
        {
            RegistryLoadResult registry = await new ManagedRuntimeRegistry(GetManagedRoot()).LoadAsync();
            ProjectLockResult result = await new ProjectLockFileService().CreateAsync(
                _manifestPath,
                registry.Entries);
            if (!result.Success)
            {
                ProjectInfo.Severity = InfoBarSeverity.Warning;
                ProjectInfo.Title = "锁文件尚未生成";
                ProjectInfo.Message = string.Join("；", result.Errors);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.ProjectImport,
                    ActivityStatus.Failed,
                    "项目精确锁文件未生成；语言工具解析存在未满足项。",
                    [_manifestPath]);
                return;
            }

            string lockPath = result.LockPath ?? Path.Combine(
                Path.GetDirectoryName(_manifestPath)!,
                ProjectLockFileService.LockFileName);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "项目锁文件已生成";
            ProjectInfo.Message = $"{lockPath} · {result.Document!.Runtimes.Count} 个精确语言工具";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Succeeded,
                $"已生成包含 {result.Document.Runtimes.Count} 个精确语言工具的项目锁文件。",
                [_manifestPath, lockPath]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = "无法生成项目锁文件";
            ProjectInfo.Message = exception.Message;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Failed,
                $"项目锁文件生成失败。错误类型：{exception.GetType().Name}。",
                [_manifestPath]);
        }
    }

    private async void OnCreateCMakePresetClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null
            || !File.Exists(Path.Combine(_projectRoot, "CMakeLists.txt")))
        {
            return;
        }

        CreateCMakePresetButton.IsEnabled = false;
        try
        {
            CppToolchainDiscoveryResult discovered = await new CppToolchainDiscoveryService()
                .DiscoverAsync();
            CMakePresetChoice[] choices = discovered.VisualStudioInstallations
                .SelectMany(installation => (installation.AvailableArchitecturePairs ?? [])
                    .Select(pair => new CMakePresetChoice(installation, pair)))
                .OrderBy(choice => choice.Installation.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(choice => choice.Pair.HostArchitecture == AutoEnvPlus.Core.Runtimes.RuntimeArchitecture.X64 ? 0 : 1)
                .ThenBy(choice => choice.Pair.TargetArchitecture)
                .ToArray();
            if (choices.Length == 0)
            {
                throw new InvalidOperationException(
                    "没有发现可用于 CMake Preset 的 MSVC Host/Target 组合。");
            }

            ComboBox selector = new()
            {
                ItemsSource = choices,
                DisplayMemberPath = nameof(CMakePresetChoice.Label),
                SelectedIndex = 0,
                MinWidth = 500,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ContentDialog selection = new()
            {
                XamlRoot = XamlRoot,
                Title = "选择 CMake MSVC 配置",
                Content = selector,
                PrimaryButtonText = "预览 JSON",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await selection.ShowAsync() != ContentDialogResult.Primary
                || selector.SelectedItem is not CMakePresetChoice choice)
            {
                return;
            }

            CMakeUserPresetsService service = new(GetManagedRoot(), _projectRoot);
            CMakeUserPresetsPlan plan = service.CreatePlan(
                choice.Installation,
                choice.Pair);
            TextBox preview = new()
            {
                AcceptsReturn = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                IsReadOnly = true,
                MinWidth = 620,
                MinHeight = 360,
                MaxHeight = 520,
                Text = plan.After,
                TextWrapping = TextWrapping.NoWrap,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = $"写入 {CMakeUserPresetsService.PresetsFileName}",
                Content = preview,
                PrimaryButtonText = "写入并保存快照",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            CMakeUserPresetsResult result = await service.ApplyAsync(plan);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "CMake Preset 写入失败。");
            }

            _lastCMakePresetSnapshot = result.SnapshotPath;
            RollbackCMakePresetButton.IsEnabled = _lastCMakePresetSnapshot is not null;
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "CMake User Preset 已写入";
            ProjectInfo.Message = $"{result.PresetsPath} · {plan.ConfigurePresetName} · 快照 {result.SnapshotPath}";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CMakePreset,
                ActivityStatus.Succeeded,
                $"已写入项目级 {CMakeUserPresetsService.PresetsFileName}：{plan.ConfigurePresetName}。",
                result.SnapshotPath is null
                    ? [result.PresetsPath]
                    : [result.PresetsPath, result.SnapshotPath],
                result.SnapshotPath,
                result.SnapshotPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = "无法生成 CMake Preset";
            ProjectInfo.Message = exception.Message;
            if (_projectRoot is not null)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CMakePreset,
                    ActivityStatus.Failed,
                    $"项目级 CMake User Preset 写入失败。错误类型：{exception.GetType().Name}。",
                    [_projectRoot]);
            }
        }
        finally
        {
            CreateCMakePresetButton.IsEnabled = _projectRoot is not null
                && File.Exists(Path.Combine(_projectRoot, "CMakeLists.txt"));
        }
    }

    private async void OnRollbackCMakePresetClicked(object sender, RoutedEventArgs args)
    {
        if (_projectRoot is null || _lastCMakePresetSnapshot is null)
        {
            return;
        }

        string snapshot = _lastCMakePresetSnapshot;
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = "回滚 CMake User Presets",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"项目\n{_projectRoot}\n\n快照\n{snapshot}\n\n如果文件后来被修改，AutoEnvPlus 会拒绝覆盖。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "回滚",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            CMakeUserPresetsResult result = await new CMakeUserPresetsService(
                GetManagedRoot(),
                _projectRoot).RollbackAsync(snapshot);
            if (!result.Success)
            {
                ProjectInfo.Severity = InfoBarSeverity.Error;
                ProjectInfo.Title = "CMake Preset 回滚失败";
                ProjectInfo.Message = result.Error;
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.CMakePreset,
                    ActivityStatus.Failed,
                    "CMake User Preset 回滚在执行时复检阶段被拒绝。",
                    [_projectRoot, snapshot],
                    snapshot,
                    snapshot);
                return;
            }

            _lastCMakePresetSnapshot = null;
            RollbackCMakePresetButton.IsEnabled = false;
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "CMake User Presets 已回滚";
            ProjectInfo.Message = result.PresetsPath;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CMakePreset,
                ActivityStatus.Succeeded,
                "已回滚项目级 CMake User Preset。",
                [_projectRoot, result.PresetsPath, snapshot],
                snapshot,
                snapshot);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProjectInfo.Severity = InfoBarSeverity.Error;
            ProjectInfo.Title = "CMake Preset 回滚失败";
            ProjectInfo.Message = exception.Message;
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.CMakePreset,
                ActivityStatus.Failed,
                $"CMake User Preset 回滚失败。错误类型：{exception.GetType().Name}。",
                [_projectRoot, snapshot],
                snapshot,
                snapshot);
        }
    }

    private static string GetManagedRoot() => ManagedRootResolver.ResolveOrThrow();

    private sealed record VirtualEnvironmentRow(ProjectVirtualEnvironment Environment)
    {
        public string Title => $"{LanguageName(Environment.LanguageId)} · "
            + $"{Environment.Manager} · {KindName(Environment.Kind)}";

        public string HealthText => Environment.Health switch
        {
            ProjectVirtualEnvironmentHealth.Healthy => "正常",
            ProjectVirtualEnvironmentHealth.NeedsAttention => "需要检查",
            _ => "不可用",
        };

        public string VersionText => Environment.Version is null
            ? string.Empty
            : $"版本：{Environment.Version}";

        public Visibility VersionVisibility => Environment.Version is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string Root => Environment.Root;

        public string ExecutableText => Environment.Executable is null
            ? string.Empty
            : $"入口：{Environment.Executable}";

        public Visibility ExecutableVisibility => Environment.Executable is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        public string EvidenceText => "证据：" + string.Join(" · ", Environment.Evidence);

        public string WarningText => Environment.Warnings.Count == 0
            ? string.Empty
            : "注意：" + string.Join("；", Environment.Warnings);

        public Visibility WarningVisibility => Environment.Warnings.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        private static string LanguageName(string languageId) => languageId switch
        {
            "python" => "Python",
            "nodejs" => "Node.js",
            "dotnet" => ".NET",
            "java" => "Java",
            "rust" => "Rust",
            "go" => "Go",
            _ => languageId,
        };

        private static string KindName(string kind) => kind switch
        {
            "virtual-environment" => "虚拟环境",
            "conda-environment" => "Conda 环境",
            "environment-definition" => "环境管理声明",
            "dependency-environment" => "依赖环境",
            "local-tool-manifest" => "本地工具清单",
            "build-wrapper" => "构建 Wrapper",
            "toolchain-selection" => "编译工具选择",
            "build-output" => "构建输出",
            "workspace" => "工作区",
            _ => kind,
        };
    }

    private sealed record TerminalHostChoice(ProjectTerminalHost Host, string Label);

    private sealed record CMakePresetChoice(
        VisualCppInstallation Installation,
        CppArchitecturePair Pair)
    {
        public string Label => $"{Installation.DisplayName} · {Pair.TargetArchitecture} 目标 · {Pair.HostArchitecture} Host";
    }
}
