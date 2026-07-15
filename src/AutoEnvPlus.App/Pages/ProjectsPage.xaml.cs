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
    private string? _projectRoot;
    private string? _manifestPath;
    private ProjectEnvironmentImportResult? _import;
    private string? _lastCMakePresetSnapshot;

    public ProjectsPage()
    {
        InitializeComponent();
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

        await LoadProjectAsync(folder.Path);
    }

    private async Task LoadProjectAsync(string startPath)
    {
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
        await new KnownProjectStore(GetManagedRoot()).AddAsync(_projectRoot!);
        ProjectInfo.Severity = InfoBarSeverity.Success;
        ProjectInfo.Title = "项目环境已读取";
        ProjectInfo.Message = _manifestPath is not null
            ? "可以使用已安装运行时生成精确锁文件。"
            : "确认导入来源后可生成 autoenvplus.toml。";
    }

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
            ProjectTerminalPlan plan = await service.CreatePlanAsync(_projectRoot);
            StringBuilder preview = new();
            preview.AppendLine($"项目\n{plan.ProjectRoot}");
            preview.AppendLine($"\n清单\n{plan.ManifestPath}");
            preview.AppendLine($"\nShell\n{plan.ShellExecutable} {string.Join(' ', plan.ShellArguments)}");
            preview.AppendLine($"\nShim\n{plan.ShimDirectory}");
            preview.AppendLine("\n精确运行时");
            if (plan.Selections.Count == 0)
            {
                preview.AppendLine("（没有可由 AutoEnvPlus Shim 激活的运行时）");
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

            foreach (string warning in plan.Warnings)
            {
                preview.AppendLine($"\n警告：{warning}");
            }

            foreach (string error in plan.Errors)
            {
                preview.AppendLine($"\n错误：{error}");
            }

            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = "打开项目已激活终端",
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        IsTextSelectionEnabled = true,
                        MinWidth = 540,
                        Text = preview.ToString().TrimEnd(),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
                PrimaryButtonText = "打开新 PowerShell",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = plan.CanLaunch,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            int processId = await service.LaunchAsync(plan);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "项目终端已启动";
            ProjectInfo.Message = $"PID {processId} · 会话级精确版本，不修改用户 PATH 或全局默认。";
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
                    "项目精确锁文件未生成；运行时解析存在未满足项。",
                    [_manifestPath]);
                return;
            }

            string lockPath = result.LockPath ?? Path.Combine(
                Path.GetDirectoryName(_manifestPath)!,
                ProjectLockFileService.LockFileName);
            ProjectInfo.Severity = InfoBarSeverity.Success;
            ProjectInfo.Title = "项目锁文件已生成";
            ProjectInfo.Message = $"{lockPath} · {result.Document!.Runtimes.Count} 个精确运行时";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.ProjectImport,
                ActivityStatus.Succeeded,
                $"已生成包含 {result.Document.Runtimes.Count} 个精确运行时的项目锁文件。",
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

    private sealed record CMakePresetChoice(
        VisualCppInstallation Installation,
        CppArchitecturePair Pair)
    {
        public string Label => $"{Installation.DisplayName} · {Pair.TargetArchitecture} 目标 · {Pair.HostArchitecture} Host";
    }
}
