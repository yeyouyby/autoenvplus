using System.Collections.ObjectModel;
using System.Diagnostics;
using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.Downloads;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Packages;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Settings;
using AutoEnvPlus.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoEnvPlus.App.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly AppDownloadManager _manager;
    private readonly ObservableCollection<LibraryRow> _libraryRows = [];
    private bool _suppressFileNameTracking;
    private bool _fileNameEdited;
    private Guid? _lastCompletedTransfer;
    private CancellationTokenSource? _pipInstallCancellation;
    private bool _pipInstallRunning;
    private bool _defaultsLoaded;

    public DownloadsPage()
    {
        InitializeComponent();
        _manager = ((App)Application.Current).DownloadManager;
        ConnectionCountPicker.ItemsSource = ManagedSegmentedDownloader.SupportedConnectionCounts;
        ConnectionCountPicker.SelectedItem = 8;
        HashAlgorithmPicker.SelectedIndex = 0;
        IntegrityFields.Visibility = Visibility.Collapsed;
        LibraryList.ItemsSource = _libraryRows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _manager.StateChanged += OnManagerStateChanged;
        await LoadDownloadDefaultsAsync();
        RefreshLibrary();
        UpdateTransferState();
    }

    private async Task LoadDownloadDefaultsAsync()
    {
        if (_defaultsLoaded)
        {
            return;
        }

        _defaultsLoaded = true;
        try
        {
            string managedRoot = ManagedRootResolver.ResolveOrThrow();
            AutoEnvPlusApplicationSettings settings =
                await new AutoEnvPlusApplicationSettingsStore(managedRoot).LoadAsync();
            if (ManagedSegmentedDownloader.SupportedConnectionCounts.Contains(
                    settings.DefaultDownloadConnections))
            {
                ConnectionCountPicker.SelectedItem = settings.DefaultDownloadConnections;
            }

            MaximumSizeBox.Value = settings.DefaultDownloadMaximumBytes / (1024d * 1024d);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            DownloadInfo.Severity = InfoBarSeverity.Warning;
            DownloadInfo.Title = "下载默认值不可用";
            DownloadInfo.Message = $"已使用内置默认值；{exception.Message}";
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _manager.StateChanged -= OnManagerStateChanged;
        _pipInstallCancellation?.Cancel();
    }

    private async void OnStartDownloadClicked(object sender, RoutedEventArgs args)
    {
        if (_manager.Snapshot?.IsBusy == true || _pipInstallRunning)
        {
            return;
        }

        if (!Uri.TryCreate(SourceUrlTextBox.Text.Trim(), UriKind.Absolute, out Uri? sourceUri)
            || sourceUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(sourceUri.UserInfo))
        {
            ShowError("无法开始下载", "文件地址必须是不包含用户名或密码的绝对 HTTPS URL。");
            return;
        }

        string fileName = FileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            ShowError("无法开始下载", "请提供下载库内文件名。");
            return;
        }

        if (!TryGetMaximumBytes(out long maximumBytes, out string? sizeError))
        {
            ShowError("无法开始下载", sizeError ?? "输入无效。");
            return;
        }

        if (!TryCreateIntegrity(out PackageHashExpectation? integrity, out string? hashError))
        {
            ShowError("无法开始下载", hashError ?? "摘要无效。");
            return;
        }

        EffectiveNetworkSettings? network = await LoadDownloadNetworkSettingsAsync();
        if (network is null)
        {
            return;
        }

        int connectionCount = ConnectionCountPicker.SelectedItem is int selected ? selected : 8;
        try
        {
            SegmentedDownloadResult result = await _manager.DownloadAsync(
                new SegmentedDownloadRequest(
                    sourceUri,
                    fileName,
                    connectionCount,
                    maximumBytes,
                    integrity),
                network);
            string mode = result.TransferMode == DownloadTransferMode.Segmented
                ? $"{result.SegmentCount} 段"
                : result.FallbackReason is null
                    ? "单连接"
                    : $"单连接（自动降级：{FallbackText(result.FallbackReason.Value)}）";
            DownloadInfo.Severity = InfoBarSeverity.Success;
            DownloadInfo.Title = "文件已加入下载库";
            DownloadInfo.Message = $"{result.FilePath} · {mode}";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Succeeded,
                $"已完成受管包下载：{Path.GetFileName(result.FilePath)}；传输模式：{mode}。",
                [result.FilePath]);
            RefreshLibrary();
        }
        catch (OperationCanceledException)
        {
            DownloadInfo.Severity = InfoBarSeverity.Informational;
            DownloadInfo.Title = "下载已取消";
            DownloadInfo.Message = "未完成内容没有提交到下载库；程序已尝试清理受管暂存目录。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Cancelled,
                $"受管包下载已取消：{fileName}。",
                [_manager.LibraryRoot]);
        }
        catch (HttpRequestException exception)
        {
            ShowError("下载未完成", AppDownloadManager.DescribeNetworkFailure(exception));
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Failed,
                $"受管包下载失败：{fileName}；错误类型：{exception.GetType().Name}。",
                [_manager.LibraryRoot]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError("下载未完成", exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageDownload,
                ActivityStatus.Failed,
                $"受管包下载失败：{fileName}；错误类型：{exception.GetType().Name}。",
                [_manager.LibraryRoot]);
        }
    }

    private async void OnImportClicked(object sender, RoutedEventArgs args)
    {
        if ((_manager.Snapshot?.IsBusy == true || _pipInstallRunning)
            || ((App)Application.Current).MainWindowInstance is not Window window)
        {
            return;
        }

        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = "导入受管下载库",
        };
        foreach (string extension in ManagedDownloadLibrary.AllowedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (!TryGetMaximumBytes(out long maximumBytes, out string? sizeError))
        {
            ShowError("无法导入文件", sizeError ?? "输入无效。");
            return;
        }

        if (!TryCreateIntegrity(out PackageHashExpectation? integrity, out string? hashError))
        {
            ShowError("无法导入文件", hashError ?? "摘要无效。");
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"导入 {file.Name}",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"源文件\n{file.Path}\n\n下载库\n{_manager.LibraryRoot}\n\n文件将被复制，源文件不会移动或删除；导入后不会自动执行。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "复制并计算摘要",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            LocalPackageImportResult result = await _manager.ImportAsync(
                new LocalPackageImportRequest(file.Path, null, maximumBytes, integrity));
            DownloadInfo.Severity = InfoBarSeverity.Success;
            DownloadInfo.Title = "本地文件已导入";
            DownloadInfo.Message = result.HasVerifiedExpectedHash
                ? $"{result.FilePath} · 预期 {result.ExpectedHashAlgorithm?.DisplayName()} 已验证"
                : $"{result.FilePath} · 已计算内容 SHA-256，来源真实性未知";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageImport,
                ActivityStatus.Succeeded,
                $"已把本地包复制到受管下载库：{Path.GetFileName(result.FilePath)}。",
                [result.FilePath]);
            RefreshLibrary();
        }
        catch (OperationCanceledException)
        {
            DownloadInfo.Severity = InfoBarSeverity.Informational;
            DownloadInfo.Title = "导入已取消";
            DownloadInfo.Message = "源文件未修改，未完成副本没有提交；程序已尝试清理受管暂存目录。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageImport,
                ActivityStatus.Cancelled,
                $"本地包导入已取消：{file.Name}。",
                [_manager.LibraryRoot]);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError("导入未完成", exception.Message);
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.PackageImport,
                ActivityStatus.Failed,
                $"本地包导入失败：{file.Name}；错误类型：{exception.GetType().Name}。",
                [_manager.LibraryRoot]);
        }
    }

    private void OnCancelTransferClicked(object sender, RoutedEventArgs args)
    {
        _manager.Cancel();
        _pipInstallCancellation?.Cancel();
    }

    private void OnRefreshLibraryClicked(object sender, RoutedEventArgs args) =>
        RefreshLibrary();

    private void OnOpenLibraryClicked(object sender, RoutedEventArgs args) =>
        OpenFolder(_manager.LibraryRoot);

    private void OnOpenItemFolderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: LibraryRow row })
        {
            OpenFolder(Path.GetDirectoryName(row.Item.FilePath)!);
        }
    }

    private async void OnDeleteLibraryItemClicked(object sender, RoutedEventArgs args)
    {
        if ((_manager.Snapshot?.IsBusy == true || _pipInstallRunning)
            || sender is not Button { Tag: LibraryRow row })
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = $"删除 {row.FileName}",
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = $"将从受管下载库永久删除此文件和对应清单记录：\n{row.Item.FilePath}\n\n源下载地址或原始导入文件不会受到影响。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ManagedDownloadDeleteResult result = await _manager.Library.DeleteAsync(row.FileName);
            DownloadInfo.Severity = InfoBarSeverity.Success;
            DownloadInfo.Title = result.Deleted ? "下载库文件已删除" : "文件已不存在";
            DownloadInfo.Message = row.Item.FilePath;
            RefreshLibrary();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError("无法删除下载库文件", exception.Message);
        }
    }

    private async void OnInstallWheelClicked(object sender, RoutedEventArgs args)
    {
        if (_manager.Snapshot?.IsBusy == true
            || _pipInstallRunning
            || sender is not Button { Tag: LibraryRow { Item.Extension: ".whl" } row })
        {
            return;
        }

        string managedRoot = ManagedRootResolver.ResolveOrThrow();
        RegistryLoadResult registry;
        try
        {
            registry = await new ManagedRuntimeRegistry(managedRoot).LoadAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            ShowError("无法读取受管 Python", exception.Message);
            return;
        }

        if (registry.Errors.Count > 0)
        {
            ShowError("受管语言工具注册表无效", string.Join("；", registry.Errors.Take(3)));
            return;
        }

        RuntimeChoice[] runtimes = registry.Entries
            .Where(entry => entry.Kind == RuntimeKind.Python && File.Exists(entry.ExecutablePath))
            .OrderByDescending(entry => entry.Version)
            .Select(entry => new RuntimeChoice(entry))
            .ToArray();
        if (runtimes.Length == 0)
        {
            ShowError("没有可用的受管 Python", "请先在 Python 语言页安装 CPython，再创建隔离虚拟环境。");
            return;
        }

        ComboBox runtimePicker = new()
        {
            Header = "基础 Python",
            ItemsSource = runtimes,
            DisplayMemberPath = nameof(RuntimeChoice.DisplayName),
            SelectedIndex = 0,
        };
        TextBox environmentName = new()
        {
            Header = "虚拟环境名称",
            Text = CreateEnvironmentName(row.FileName),
        };
        ComboBox sourceModePicker = new()
        {
            Header = "依赖来源",
            ItemsSource = new[]
            {
                new SourceModeChoice(
                    PipPackageSourceMode.OfflineManagedLibrary,
                    "严格离线（仅此 wheel，不解析依赖）"),
                new SourceModeChoice(
                    PipPackageSourceMode.ConfiguredNetwork,
                    "当前 pip Provider 源与代理"),
            },
            DisplayMemberPath = nameof(SourceModeChoice.DisplayName),
            SelectedIndex = 0,
        };
        StackPanel choices = new() { Spacing = 12 };
        choices.Children.Add(runtimePicker);
        choices.Children.Add(environmentName);
        choices.Children.Add(sourceModePicker);
        choices.Children.Add(new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Severity = InfoBarSeverity.Warning,
            Title = "pip 安装不是事务操作",
            Message = "环境、缓存和临时文件固定在受管数据根，但包自身的构建脚本仍可能修改环境；失败时不会声称已自动回滚。",
        });
        ContentDialog optionsDialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"安装 {row.FileName}",
            Content = choices,
            PrimaryButtonText = "生成安装计划",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await optionsDialog.ShowAsync() != ContentDialogResult.Primary
            || runtimePicker.SelectedItem is not RuntimeChoice runtime
            || sourceModePicker.SelectedItem is not SourceModeChoice sourceMode)
        {
            return;
        }

        EffectiveNetworkSettings? pipNetwork = null;
        if (sourceMode.Mode == PipPackageSourceMode.ConfiguredNetwork)
        {
            pipNetwork = await LoadToolNetworkSettingsAsync(NetworkToolIds.Pip, "pip");
            if (pipNetwork is null)
            {
                return;
            }
        }

        PipLocalPackageInstallService service = new(managedRoot);
        PipLocalPackageInstallPlan plan;
        try
        {
            plan = await service.CreatePlanAsync(new PipLocalPackageInstallRequest(
                runtime.Entry,
                row.Item.FilePath,
                environmentName.Text.Trim(),
                sourceMode.Mode,
                pipNetwork));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError("无法生成 pip 安装计划", exception.Message);
            return;
        }

        string createStep = plan.RequiresEnvironmentCreation
            ? FormatCommand(plan.CreateEnvironmentCommand)
            : "使用已有且已复检的虚拟环境";
        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            Title = "确认本地 wheel 安装计划",
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = $"wheel\n{plan.WheelPath}\n\n目标环境\n{plan.EnvironmentRoot}\n\n创建环境\n{createStep}\n\n安装命令\n{FormatCommand(plan.InstallPackageCommand)}\n\nTEMP / TMP\n{service.TemporaryRoot}\n\npip 缓存\n{service.PipCacheRoot}\n\n回滚边界\n此操作不是事务安装。严格离线模式使用 --no-deps，只安装当前已审核 wheel，不解析或安装任何依赖；需要解析依赖时必须显式选择联网模式。",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "执行已审核计划",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _pipInstallRunning = true;
        _pipInstallCancellation?.Dispose();
        _pipInstallCancellation = new CancellationTokenSource();
        UpdateTransferState();
        try
        {
            Progress<PipLocalPackageInstallProgress> progress = new(value =>
            {
                TransferTitleText.Text = $"{PipStageText(value.Stage)} · {row.FileName}";
                TransferDetailText.Text = value.Message;
                TransferBytesText.Text = string.Empty;
                TransferProgressBar.IsIndeterminate =
                    value.Stage is not PipLocalPackageInstallStage.Completed
                    and not PipLocalPackageInstallStage.Cancelled;
            });
            PipLocalPackageInstallResult result = await service.ExecuteAsync(
                plan,
                progress,
                _pipInstallCancellation.Token);
            if (result.Success)
            {
                DownloadInfo.Severity = InfoBarSeverity.Success;
                DownloadInfo.Title = "wheel 已安装到受管虚拟环境";
                DownloadInfo.Message = AppendPipOutputCaptureNotice(
                    plan.EnvironmentRoot,
                    result);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PackageInstall,
                    ActivityStatus.Succeeded,
                    $"已把 {row.FileName} 安装到受管 Python 虚拟环境 {plan.EnvironmentName}；该操作不支持事务回滚。",
                    [row.Item.FilePath, plan.EnvironmentRoot]);
            }
            else if (result.WasCancelled)
            {
                DownloadInfo.Severity = InfoBarSeverity.Warning;
                DownloadInfo.Title = "pip 安装已取消";
                DownloadInfo.Message = AppendPipOutputCaptureNotice(
                    "子进程已停止；环境可能包含部分更改，不会声称已回滚。",
                    result);
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PackageInstall,
                    ActivityStatus.Cancelled,
                    $"安装 {row.FileName} 的 pip 进程已取消；虚拟环境可能包含部分更改。",
                    [plan.EnvironmentRoot]);
            }
            else
            {
                ShowError(
                    "pip 安装未完成",
                    AppendPipOutputCaptureNotice(
                        result.Error ?? "pip 返回失败状态。",
                        result));
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.PackageInstall,
                    ActivityStatus.Failed,
                    $"安装 {row.FileName} 失败；虚拟环境可能包含部分更改。",
                    [plan.EnvironmentRoot]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            ShowError("pip 安装计划已失效", exception.Message);
        }
        finally
        {
            _pipInstallRunning = false;
            _pipInstallCancellation.Dispose();
            _pipInstallCancellation = null;
            UpdateTransferState();
        }
    }

    private void OnIntegrityToggled(object sender, RoutedEventArgs args) =>
        IntegrityFields.Visibility = IntegrityToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnSourceUrlChanged(object sender, TextChangedEventArgs args)
    {
        if (_fileNameEdited
            || !Uri.TryCreate(SourceUrlTextBox.Text.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        string inferred = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(inferred))
        {
            return;
        }

        _suppressFileNameTracking = true;
        FileNameTextBox.Text = inferred;
        _suppressFileNameTracking = false;
    }

    private void OnFileNameChanged(object sender, TextChangedEventArgs args)
    {
        if (!_suppressFileNameTracking)
        {
            _fileNameEdited = !string.IsNullOrWhiteSpace(FileNameTextBox.Text);
        }
    }

    private async Task<EffectiveNetworkSettings?> LoadDownloadNetworkSettingsAsync()
    {
        NetworkSettingsLoadResult loaded = await new NetworkSettingsStore(
            ManagedRootResolver.ResolveOrThrow()).LoadAsync();
        if (!loaded.Success || loaded.Settings is null)
        {
            ShowError(
                "无法读取下载网络设置",
                string.Join("；", loaded.Errors.Take(3).Select(error => error.Message)));
            return null;
        }

        NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(
            loaded.Settings,
            NetworkToolIds.Downloads);
        if (!resolved.Success || resolved.EffectiveSettings is null)
        {
            ShowError(
                "下载中心网络设置无效",
                string.Join("；", resolved.Errors.Take(3).Select(error => error.Message)));
            return null;
        }

        return resolved.EffectiveSettings;
    }

    private async Task<EffectiveNetworkSettings?> LoadToolNetworkSettingsAsync(
        string toolId,
        string displayName)
    {
        ProviderSourceNetworkSettingsLoadResult loaded =
            await new ProviderSourceNetworkSettingsLoader(
                ManagedRootResolver.ResolveOrThrow()).LoadForToolsAsync([toolId]);
        if (!loaded.Success || loaded.Settings is null)
        {
            ShowError(
                "无法读取下载网络设置",
                string.Join("；", loaded.Errors.Take(3)));
            return null;
        }

        NetworkSettingsResolutionResult resolved = NetworkSettingsResolver.Resolve(
            loaded.Settings,
            toolId);
        if (!resolved.Success || resolved.EffectiveSettings is null)
        {
            ShowError(
                $"{displayName} 网络设置无效",
                string.Join("；", resolved.Errors.Take(3).Select(error => error.Message)));
            return null;
        }

        return resolved.EffectiveSettings;
    }

    private static string CreateEnvironmentName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        char[] characters = stem
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-'
                    ? character
                    : '-')
            .ToArray();
        string normalized = new string(characters)
            .Trim('.', '_', '-')
            .Replace("..", ".", StringComparison.Ordinal);
        if (normalized.Length > 48)
        {
            normalized = normalized[..48].TrimEnd('.', '_', '-');
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? "wheel-env"
            : $"{normalized}-env";
    }

    private static string FormatCommand(PipLocalPackageInstallCommand command) =>
        command.ExecutablePath + " " + string.Join(' ', command.ArgumentList.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument) =>
        argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : argument;

    private static string PipStageText(PipLocalPackageInstallStage stage) => stage switch
    {
        PipLocalPackageInstallStage.Validating => "正在复检安装计划",
        PipLocalPackageInstallStage.CreatingEnvironment => "正在创建虚拟环境",
        PipLocalPackageInstallStage.InstallingPackage => "正在运行 pip",
        PipLocalPackageInstallStage.Completed => "pip 安装完成",
        PipLocalPackageInstallStage.Cancelled => "pip 安装已取消",
        _ => stage.ToString(),
    };

    private static string AppendPipOutputCaptureNotice(
        string message,
        PipLocalPackageInstallResult result)
    {
        List<string> truncatedStreams = [];
        if (result.StandardOutputTruncated)
        {
            truncatedStreams.Add("stdout");
        }

        if (result.StandardErrorTruncated)
        {
            truncatedStreams.Add("stderr");
        }

        return truncatedStreams.Count == 0
            ? message
            : $"{message}\n\n{string.Join('/', truncatedStreams)} 已截断；结果仅保留末尾 {PipLocalPackageProcessRunner.MaximumCapturedOutputCharacters:N0} 个字符。";
    }

    private bool TryCreateIntegrity(
        out PackageHashExpectation? integrity,
        out string? error)
    {
        integrity = null;
        error = null;
        if (!IntegrityToggle.IsOn)
        {
            return true;
        }

        PackageHashAlgorithm algorithm =
            (HashAlgorithmPicker.SelectedItem as ComboBoxItem)?.Tag as string == "sha512"
                ? PackageHashAlgorithm.Sha512
                : PackageHashAlgorithm.Sha256;
        string expected = ExpectedHashTextBox.Text.Trim();
        if (!algorithm.IsValidHash(expected))
        {
            error = $"预期摘要必须是有效的 {algorithm.DisplayName()} 十六进制值。";
            return false;
        }

        integrity = new PackageHashExpectation(algorithm, expected);
        return true;
    }

    private bool TryGetMaximumBytes(out long maximumBytes, out string? error)
    {
        maximumBytes = 0;
        error = null;
        double megabytes = MaximumSizeBox.Value;
        double maximumMegabytes = AutoEnvPlusApplicationSettings.MaximumDownloadMaximumBytes
            / (1024d * 1024d);
        if (double.IsNaN(megabytes) || megabytes < 1 || megabytes > maximumMegabytes)
        {
            error = $"最大大小必须介于 1 MB 与 {maximumMegabytes:F0} MB 之间。";
            return false;
        }

        maximumBytes = checked((long)(megabytes * 1024d * 1024d));
        return true;
    }

    private void OnManagerStateChanged(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateTransferState();
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(UpdateTransferState);
        }
    }

    private void UpdateTransferState()
    {
        AppTransferSnapshot? snapshot = _manager.Snapshot;
        bool busy = snapshot?.IsBusy == true || _pipInstallRunning;
        StartDownloadButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        CancelTransferButton.IsEnabled = busy;
        if (snapshot is null)
        {
            TransferTitleText.Text = "当前没有任务";
            TransferBytesText.Text = string.Empty;
            TransferDetailText.Text = _manager.LibraryRoot;
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Value = 0;
            return;
        }

        TransferTitleText.Text = $"{PhaseText(snapshot.Phase, snapshot.Error)} · {snapshot.FileName}";
        TransferBytesText.Text = snapshot.TotalBytes is long total
            ? $"{FormatBytes(snapshot.CompletedBytes)} / {FormatBytes(total)}"
            : FormatBytes(snapshot.CompletedBytes);
        TransferProgressBar.IsIndeterminate = busy && snapshot.TotalBytes is null;
        TransferProgressBar.Maximum = Math.Max(1, snapshot.TotalBytes ?? 1);
        TransferProgressBar.Value = Math.Min(
            TransferProgressBar.Maximum,
            snapshot.CompletedBytes);
        string segments = snapshot.TotalSegments > 0
            ? $" · 分段 {snapshot.CompletedSegments}/{snapshot.TotalSegments}"
            : string.Empty;
        TransferDetailText.Text = $"{snapshot.Error ?? snapshot.OutputPath ?? snapshot.SourceDisplay}{segments}";

        if (!busy
            && snapshot.Phase == ManagedTransferPhase.Completed
            && _lastCompletedTransfer != snapshot.Id)
        {
            _lastCompletedTransfer = snapshot.Id;
            RefreshLibrary();
        }
    }

    private async void RefreshLibrary()
    {
        try
        {
            ManagedDownloadLibraryItem[] items = (await _manager.Library.ListFilesAsync())
                .ToArray();
            _libraryRows.Clear();
            foreach (ManagedDownloadLibraryItem item in items)
            {
                _libraryRows.Add(new LibraryRow(item));
            }

            LibrarySummaryText.Text =
                $"{items.Length:N0} 个文件 · {FormatBytes(items.Sum(item => item.SizeBytes))} · {_manager.LibraryRoot}";
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            ShowError("无法读取下载库", exception.Message);
            LibrarySummaryText.Text = "下载库清单未通过安全检查";
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            ProcessStartInfo startInfo = new()
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            ShowError("无法打开下载库", exception.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        DownloadInfo.Severity = InfoBarSeverity.Error;
        DownloadInfo.Title = title;
        DownloadInfo.Message = message;
    }

    private static string PhaseText(ManagedTransferPhase phase, string? error) => error is not null
        ? "失败"
        : phase switch
        {
            ManagedTransferPhase.Probing => "正在探测服务器",
            ManagedTransferPhase.Downloading => "正在下载",
            ManagedTransferPhase.Copying => "正在复制",
            ManagedTransferPhase.Verifying => "正在计算并验证摘要",
            ManagedTransferPhase.Committing => "正在提交到下载库",
            ManagedTransferPhase.Completed => "已就绪",
            ManagedTransferPhase.Cancelled => "已取消",
            _ => phase.ToString(),
        };

    private static string FallbackText(DownloadFallbackReason reason) => reason switch
    {
        DownloadFallbackReason.RangeNotSupported => "服务器不支持 Range",
        DownloadFallbackReason.StableEntityUnavailable => "缺少稳定实体标识",
        DownloadFallbackReason.EntityChanged => "远端实体发生变化",
        _ => reason.ToString(),
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} B" : $"{size:F1} {units[unit]}";
    }

    private sealed record LibraryRow(ManagedDownloadLibraryItem Item)
    {
        public string FileName => Item.FileName;

        public Visibility PipInstallVisibility => Item.Extension.Equals(
            ".whl",
            StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string Summary
        {
            get
            {
                string origin = Item.Origin switch
                {
                    ManagedDownloadOrigin.Network => "网络下载",
                    ManagedDownloadOrigin.LocalImport => "本地导入",
                    _ => "现有文件",
                };
                string mode = Item.TransferMode switch
                {
                    DownloadTransferMode.Segmented => "多连接分段",
                    DownloadTransferMode.SingleStream => "单连接",
                    _ => "本地复制",
                };
                return $"{FormatBytes(Item.SizeBytes)} · {origin} · {mode} · {Item.Source ?? "来源未记录"}";
            }
        }

        public string IntegritySummary => Item.HasVerifiedExpectedHash
            ? $"预期 {Item.ExpectedHashAlgorithm?.DisplayName()} 已重新验证 · {Item.VerifiedHash}"
            : Item.HasRecordedExpectedHashEvidence && Item.ContentIdentityChanged
                ? "文件内容已变化；导入/下载时记录的预期哈希证据不再适用"
                : Item.HasRecordedExpectedHashEvidence
                    ? "预期哈希仅在导入/下载提交时验证，当前内容尚未重新验证"
                    : Item.ContentSha256 is string sha256
                        ? $"提交时记录内容 SHA-256，来源真实性未知 · {sha256}"
                        : "摘要尚未记录";
    }

    private sealed record RuntimeChoice(ManagedRuntimeEntry Entry)
    {
        public string DisplayName =>
            $"Python {Entry.Version} · {Entry.Architecture} · {Entry.InstallRoot}";
    }

    private sealed record SourceModeChoice(
        PipPackageSourceMode Mode,
        string DisplayName);
}
