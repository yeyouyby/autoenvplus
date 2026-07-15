using AutoEnvPlus.App.Activity;
using AutoEnvPlus.App.RuntimeCatalogs;
using AutoEnvPlus.Core.Activity;
using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Environment;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.DotNet;
using AutoEnvPlus.Core.Providers.Java;
using AutoEnvPlus.Core.Providers.NodeJs;
using AutoEnvPlus.Core.Providers.Python;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace AutoEnvPlus.App.Pages;

public sealed partial class RuntimesPage : Page
{
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private CancellationTokenSource? _operationCancellation;
    private bool _operationRunning;

    public RuntimesPage()
    {
        InitializeComponent();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AutoEnvPlus/0.1");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _ = BeginOperation();
        try
        {
            await RefreshStatusesAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            ActionInfo.Severity = InfoBarSeverity.Error;
            ActionInfo.Title = "无法读取运行时状态";
            ActionInfo.Message = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _operationCancellation?.Cancel();
        _pageCancellation.Cancel();
        _httpClient.Dispose();
    }

    private async Task RefreshStatusesAsync()
    {
        CancellationToken cancellationToken = _operationCancellation?.Token
            ?? _pageCancellation.Token;
        IReadOnlyList<DiscoveredRuntime> runtimes = await new RuntimeDiscoveryService().DiscoverCurrentAsync(
            cancellationToken);
        RegistryLoadResult registry = await new ManagedRuntimeRegistry(GetManagedRoot()).LoadAsync(
            cancellationToken);
        if (registry.Errors.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", registry.Errors));
        }

        RuntimeProfile global = await new GlobalRuntimeProfileStore(GetManagedRoot()).LoadAsync(
            cancellationToken);
        IReadOnlySet<string> globalDefaults = ResolveGlobalDefaults(global, registry.Entries);
        ManagedRuntimeList.ItemsSource = registry.Entries
            .OrderBy(entry => entry.Kind)
            .ThenByDescending(entry => entry.Version)
            .Select(entry => new ManagedRuntimeRow(
                entry,
                globalDefaults.Contains(entry.Id)))
            .ToArray();
        SetStatus(PythonStatus, RuntimeKind.Python, runtimes, registry.Entries);
        SetStatus(NodeStatus, RuntimeKind.NodeJs, runtimes, registry.Entries);
        SetStatus(JavaStatus, RuntimeKind.Java, runtimes, registry.Entries);
        SetStatus(DotNetStatus, RuntimeKind.DotNet, runtimes, registry.Entries);
    }

    private static void SetStatus(
        TextBlock target,
        RuntimeKind kind,
        IReadOnlyList<DiscoveredRuntime> runtimes,
        IReadOnlyList<ManagedRuntimeEntry> managedEntries)
    {
        ManagedRuntimeEntry[] managed = managedEntries
            .Where(entry => entry.Kind == kind)
            .OrderByDescending(entry => entry.Version)
            .ToArray();
        DiscoveredRuntime[] matches = runtimes.Where(runtime => runtime.Kind == kind).ToArray();
        DiscoveredRuntime? winner = matches.FirstOrDefault(runtime => runtime.IsHealthy);
        string managedText = managed.Length > 0
            ? $"AutoEnvPlus 已托管 {managed.Length} 个版本，最高 {managed[0].Version}"
            : "AutoEnvPlus 尚未托管版本";
        if (winner is null)
        {
            string discoveredText = matches.Length == 0
                ? "PATH 中未检测到"
                : $"检测失败：{matches[0].Error}";
            target.Text = $"{managedText} · {discoveredText}";
            return;
        }

        string suffix = matches.Length > 1 ? $" · PATH 中共 {matches.Length} 个候选" : string.Empty;
        target.Text = $"{managedText} · PATH 当前 {winner.Version} · {winner.ExecutablePath}{suffix}";
    }

    private async void OnInstallClicked(object sender, RoutedEventArgs args)
    {
        if (_operationRunning
            || sender is not Button button
            || button.Tag is not string kindValue
            || !Enum.TryParse(kindValue, out RuntimeKind kind))
        {
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        ArchiveInstallPlan? activityPlan = null;
        RuntimeRelease? activityRelease = null;
        bool installConfirmed = false;
        try
        {
            RuntimeArchitecture architecture = CurrentArchitecture();
            int? javaFeatureVersion = kind == RuntimeKind.Java
                ? await SelectJavaFeatureVersionAsync(cancellationToken)
                : null;
            if (kind == RuntimeKind.Java && javaFeatureVersion is null)
            {
                ResetActionInfo();
                return;
            }

            IArchiveRuntimeProvider provider = CreateProvider(
                kind,
                architecture,
                javaFeatureVersion);
            ActionInfo.Severity = InfoBarSeverity.Informational;
            ActionInfo.Title = "正在加载官方目录";
            ActionInfo.Message = provider.Id;

            IReadOnlyList<RuntimeRelease> catalog = await provider.GetReleasesAsync(cancellationToken);
            RuntimeRelease[] releases = catalog
                .Where(release => release.Architecture == architecture)
                .Take(20)
                .ToArray();
            if (releases.Length == 0)
            {
                throw new InvalidDataException("官方目录没有当前架构可用的稳定版本。");
            }

            RuntimeRelease? selectedRelease = await SelectReleaseAsync(kind, releases);
            if (selectedRelease is null)
            {
                ResetActionInfo();
                return;
            }
            activityRelease = selectedRelease;

            ActionInfo.Title = "正在解析安装资产";
            ActionInfo.Message = selectedRelease.ProviderVersion;
            RuntimePackageAsset asset = await provider.GetAssetAsync(
                selectedRelease,
                cancellationToken);
            ArchiveInstallPlan plan = provider.CreateInstallPlan(asset, GetManagedRoot());
            activityPlan = plan;
            bool confirmed = await ConfirmInstallPlanAsync(plan);
            if (!confirmed)
            {
                ResetActionInfo();
                return;
            }
            installConfirmed = true;

            Progress<InstallProgress> progress = new(value =>
            {
                ActionInfo.Title = StageTitle(value.Stage, asset.HashAlgorithm);
                ActionInfo.Message = value.TotalBytes > 0 && value.CompletedBytes is long completed
                    ? $"{completed / 1_048_576d:F1} / {value.TotalBytes / 1_048_576d:F1} MB"
                    : asset.FileName;
            });
            ManagedRuntimeEntry entry = new(
                $"{selectedRelease.Kind.ToString().ToLowerInvariant()}-{selectedRelease.Version}-{selectedRelease.Architecture.ToString().ToLowerInvariant()}",
                selectedRelease.ProviderId,
                selectedRelease.Kind,
                selectedRelease.Version,
                selectedRelease.Architecture,
                plan.DestinationRoot,
                plan.ExpectedExecutableRelativePath,
                asset.PackageHash,
                DateTimeOffset.UtcNow,
                selectedRelease.Channels,
                asset.HashAlgorithm);
            ManagedRuntimeInstallTransactionResult result = await new ManagedRuntimeInstallCoordinator(
                GetManagedRoot(),
                _httpClient).InstallAsync(
                    new ManagedRuntimeInstallRequest(plan, entry, SetGlobalDefault: true),
                    progress,
                    cancellationToken);
            if (!result.Success)
            {
                string cleanup = result.PendingCleanup
                    ? $" 可能需要人工检查：{result.InstallRoot}"
                    : string.Empty;
                throw new InvalidOperationException(
                    (result.Error ?? "安装失败。") + cleanup);
            }

            ActionInfo.Severity = InfoBarSeverity.Success;
            ActionInfo.Title = result.InstallOutcome == InstallOutcome.AlreadyInstalled
                ? "运行时已登记"
                : "安装完成";
            ActionInfo.Message = $"{selectedRelease.Kind} {selectedRelease.Version} · {result.InstallRoot}";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.RuntimeInstall,
                ActivityStatus.Succeeded,
                result.InstallOutcome == InstallOutcome.AlreadyInstalled
                    ? $"已重新确认托管运行时：{selectedRelease.Kind} {selectedRelease.Version} ({selectedRelease.Architecture})。"
                    : $"已安装托管运行时：{selectedRelease.Kind} {selectedRelease.Version} ({selectedRelease.Architecture})。",
                [result.InstallRoot ?? plan.DestinationRoot]);
            await RefreshStatusesAsync();
        }
        catch (OperationCanceledException)
        {
            ActionInfo.Severity = InfoBarSeverity.Informational;
            ActionInfo.Title = "操作已取消";
            ActionInfo.Message = "没有提交未完成的安装。";
            if (installConfirmed)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.RuntimeInstall,
                    ActivityStatus.Cancelled,
                    activityRelease is null
                        ? $"{kind} 运行时安装已取消。"
                        : $"{activityRelease.Kind} {activityRelease.Version} 安装已取消。",
                    activityPlan is null ? [] : [activityPlan.DestinationRoot]);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            ActionInfo.Severity = InfoBarSeverity.Error;
            ActionInfo.Title = "安装未完成";
            ActionInfo.Message = exception.Message;
            if (installConfirmed)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.RuntimeInstall,
                    ActivityStatus.Failed,
                    activityRelease is null
                        ? $"{kind} 运行时安装失败。错误类型：{exception.GetType().Name}。"
                        : $"{activityRelease.Kind} {activityRelease.Version} 安装失败。错误类型：{exception.GetType().Name}。",
                    activityPlan is null ? [] : [activityPlan.DestinationRoot]);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnSetGlobalClicked(object sender, RoutedEventArgs args)
    {
        if (_operationRunning
            || sender is not Button { Tag: ManagedRuntimeRow row }
            || row.IsGlobalDefault)
        {
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        bool changeConfirmed = false;
        try
        {
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = $"设为 {DisplayName(row.Entry.Kind)} 全局默认",
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = $"版本\n{row.Entry.Version} ({row.Entry.Architecture})\n\n可执行文件\n{row.Entry.ExecutablePath}\n\n项目配置和当前会话选择仍具有更高优先级。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "设为默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            changeConfirmed = true;

            if (!File.Exists(row.Entry.ExecutablePath))
            {
                throw new FileNotFoundException(
                    "托管运行时的可执行文件不存在。",
                    row.Entry.ExecutablePath);
            }

            await new GlobalRuntimeProfileStore(GetManagedRoot()).SetAsync(
                row.Entry.Kind,
                new VersionSelector(VersionSelectorKind.Exact, row.Entry.Version),
                cancellationToken);
            ActionInfo.Severity = InfoBarSeverity.Success;
            ActionInfo.Title = "全局默认版本已更新";
            ActionInfo.Message = $"{DisplayName(row.Entry.Kind)} {row.Entry.Version}。项目和会话选择未修改。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.RuntimeSwitch,
                ActivityStatus.Succeeded,
                $"已把 {DisplayName(row.Entry.Kind)} 全局默认切换到 {row.Entry.Version}；项目与会话选择未修改。",
                [row.Entry.ExecutablePath]);
            await RefreshStatusesAsync();
        }
        catch (OperationCanceledException)
        {
            ActionInfo.Severity = InfoBarSeverity.Informational;
            ActionInfo.Title = "操作已取消";
            ActionInfo.Message = string.Empty;
            if (changeConfirmed)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.RuntimeSwitch,
                    ActivityStatus.Cancelled,
                    $"{DisplayName(row.Entry.Kind)} 全局默认切换到 {row.Entry.Version} 的操作已取消。",
                    [row.Entry.ExecutablePath]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            ActionInfo.Severity = InfoBarSeverity.Error;
            ActionInfo.Title = "无法更新全局默认版本";
            ActionInfo.Message = exception.Message;
            if (changeConfirmed)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.RuntimeSwitch,
                    ActivityStatus.Failed,
                    $"{DisplayName(row.Entry.Kind)} 全局默认切换失败。错误类型：{exception.GetType().Name}。",
                    [row.Entry.ExecutablePath]);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnUninstallClicked(object sender, RoutedEventArgs args)
    {
        if (_operationRunning || sender is not Button { Tag: ManagedRuntimeRow row })
        {
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        bool uninstallConfirmed = false;
        try
        {
            ManagedRuntimeUninstaller uninstaller = new(GetManagedRoot());
            ManagedRuntimeUninstallPlan plan = await uninstaller.CreatePlanAsync(
                row.Entry.Id,
                cancellationToken);
            if (plan.IsReferenced)
            {
                ContentDialog blocked = new()
                {
                    XamlRoot = XamlRoot,
                    Title = "运行时仍被引用",
                    Content = new TextBlock
                    {
                        IsTextSelectionEnabled = true,
                        Text = string.Join("\n", plan.References.Select(reference =>
                            $"{reference.Kind} · {reference.Owner}\n{reference.Detail}")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CloseButtonText = "关闭",
                };
                await blocked.ShowAsync();
                return;
            }

            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = $"卸载 {row.DisplayName}",
                Content = new TextBlock
                {
                    IsTextSelectionEnabled = true,
                    Text = $"将从托管注册表和磁盘移除：\n{row.InstallRoot}\n\n目录会先移动到 AutoEnvPlus .trash；注册表更新失败时自动移回。共享下载缓存不会删除。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "确认卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            uninstallConfirmed = true;

            ManagedRuntimeUninstallResult result = await uninstaller.ExecuteAsync(
                plan,
                cancellationToken: cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "卸载失败。");
            }

            ActionInfo.Severity = result.PendingTrashCleanup
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            ActionInfo.Title = "运行时已卸载";
            ActionInfo.Message = result.PendingTrashCleanup
                ? "注册表已更新；被占用的文件留在 .trash，稍后可重试清理。"
                : "运行时目录已删除，共享下载缓存已保留。";
            await AppActivityLog.TryWriteAsync(
                ActivityOperationType.RuntimeUninstall,
                ActivityStatus.Succeeded,
                result.PendingTrashCleanup
                    ? $"已卸载 {row.DisplayName}；部分被占用文件保留在受管 .trash。"
                    : $"已卸载 {row.DisplayName}；共享下载缓存保持不变。",
                [row.InstallRoot]);
            await RefreshStatusesAsync();
        }
        catch (OperationCanceledException)
        {
            ActionInfo.Severity = InfoBarSeverity.Informational;
            ActionInfo.Title = "卸载已取消";
            ActionInfo.Message = string.Empty;
            if (uninstallConfirmed)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.RuntimeUninstall,
                    ActivityStatus.Cancelled,
                    $"卸载 {row.DisplayName} 的操作已取消。",
                    [row.InstallRoot]);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            ActionInfo.Severity = InfoBarSeverity.Error;
            ActionInfo.Title = "无法卸载运行时";
            ActionInfo.Message = exception.Message;
            if (uninstallConfirmed)
            {
                await AppActivityLog.TryWriteAsync(
                    ActivityOperationType.RuntimeUninstall,
                    ActivityStatus.Failed,
                    $"卸载 {row.DisplayName} 失败。错误类型：{exception.GetType().Name}。",
                    [row.InstallRoot]);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        if (_operationRunning)
        {
            return;
        }

        _ = BeginOperation();
        try
        {
            ActionInfo.Severity = InfoBarSeverity.Informational;
            ActionInfo.Title = "正在刷新运行时状态";
            ActionInfo.Message = "重新读取 PATH、托管注册表和全局选择。";
            await RefreshStatusesAsync();
            ResetActionInfo();
        }
        catch (OperationCanceledException)
        {
            ActionInfo.Title = "刷新已取消";
            ActionInfo.Message = string.Empty;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            ActionInfo.Severity = InfoBarSeverity.Error;
            ActionInfo.Title = "无法刷新运行时状态";
            ActionInfo.Message = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnCancelOperationClicked(object sender, RoutedEventArgs args) =>
        _operationCancellation?.Cancel();

    private async Task<int?> SelectJavaFeatureVersionAsync(CancellationToken cancellationToken)
    {
        ActionInfo.Severity = InfoBarSeverity.Informational;
        ActionInfo.Title = "正在加载 Java 版本线";
        ActionInfo.Message = "Eclipse Adoptium 官方可用版本目录";
        JavaFeatureReleaseCatalogSnapshot catalog = await new AdoptiumFeatureReleaseCatalog(
            _httpClient).GetAsync(cancellationToken);
        JavaFeatureChoice[] choices = catalog.AvailableReleases
            .Select(version => new JavaFeatureChoice(
                version,
                catalog.LtsReleases.Contains(version),
                version == catalog.LatestFeatureRelease,
                version == catalog.RecommendedFeatureRelease))
            .OrderByDescending(choice => choice.IsRecommended)
            .ThenByDescending(choice => choice.IsLatestFeature)
            .ThenByDescending(choice => choice.FeatureVersion)
            .ToArray();

        ComboBox selector = new()
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(JavaFeatureChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360,
        };
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "版本线实时来自 Eclipse Adoptium 官方目录。优先推荐最新 LTS；下一步可选择该版本线的精确补丁版本。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(selector);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "选择 Eclipse Temurin JDK 版本线",
            Content = content,
            PrimaryButtonText = "加载补丁版本",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            && selector.SelectedItem is JavaFeatureChoice choice
                ? choice.FeatureVersion
                : null;
    }

    private async Task<RuntimeRelease?> SelectReleaseAsync(
        RuntimeKind kind,
        IReadOnlyList<RuntimeRelease> releases)
    {
        ComboBox selector = new()
        {
            ItemsSource = releases.Select(release => new RuntimeChoice(release)).ToArray(),
            DisplayMemberPath = nameof(RuntimeChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360,
        };
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = kind == RuntimeKind.Java
                ? "请选择该 JDK 版本线中的稳定补丁版本和架构。"
                : "请选择要安装的稳定版本和架构。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(selector);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = $"安装 {DisplayName(kind)}",
            Content = content,
            PrimaryButtonText = "查看安装计划",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && selector.SelectedItem is RuntimeChoice choice
            ? choice.Release
            : null;
    }

    private async Task<bool> ConfirmInstallPlanAsync(ArchiveInstallPlan plan)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "确认受管理安装",
            Content = CreateInstallPlanPreview(plan),
            PrimaryButtonText = "下载并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ScrollViewer CreateInstallPlanPreview(ArchiveInstallPlan plan)
    {
        string hashName = plan.Asset.HashAlgorithm.DisplayName();
        StackPanel content = new()
        {
            MinWidth = 460,
            MaxWidth = 620,
            Spacing = 12,
        };
        content.Children.Add(new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Severity = InfoBarSeverity.Success,
            Title = $"下载后强制校验 {hashName}",
            Message = "只有实际下载字节与 Provider 给出的包哈希完全一致时，安装才会继续。",
        });
        content.Children.Add(CreatePackageDetailsExpander(plan));

        content.Children.Add(new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = $"校验来源证据（{plan.Asset.Verifications.Count}）",
        });
        foreach (PackageVerification verification in plan.Asset.Verifications)
        {
            content.Children.Add(CreateVerificationExpander(
                verification,
                plan.Asset.PackageHash));
        }

        if (plan.Asset.SignatureVerifications.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = $"数字签名证据（{plan.Asset.SignatureVerifications.Count}）",
            });
            foreach (PackageSignatureVerification signature in plan.Asset.SignatureVerifications)
            {
                content.Children.Add(CreateSignatureExpander(signature));
            }

            content.Children.Add(new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Success,
                Title = "发布清单数字签名已验证",
                Message = plan.Asset.SignatureVerifications.Any(signature =>
                    signature.Kind == PackageSignatureVerificationKind.SigstoreBundle)
                    ? $"Sigstore 已验证 Fulcio 证书链、python.org 固定发布身份、Rekor SET/包含证明/检查点和清单签名；下载包仍会再按签名清单中的 {hashName} 逐字节校验。"
                    : $"OpenPGP 签名证明校验清单由固定信任的发布密钥签署；下载包仍会再按清单中的 {hashName} 逐字节校验。",
            });
        }
        else if (plan.Asset.SignatureRequirement is PackageSignatureRequirement requirement)
        {
            content.Children.Add(new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = "安装时数字签名验证",
            });
            content.Children.Add(CreateSignatureRequirementExpander(requirement));
            content.Children.Add(new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "下载后强制验证包签名",
                Message = $"安装器会先核对 {hashName}，再用固定主指纹的发布密钥流式验证包本体；签名无效时删除缓存包且不会解压。",
            });
        }
        else
        {
            content.Children.Add(new InfoBar
            {
                IsClosable = false,
                IsOpen = true,
                Severity = InfoBarSeverity.Warning,
                Title = "数字签名尚未验证",
                Message = $"这些证据说明 {hashName} 来自哪里，但不能证明 PGP、Authenticode 或 Sigstore 签名有效。",
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = "安装成功后将设为该运行时的全局默认版本；项目配置和当前会话仍具有更高优先级。",
            TextWrapping = TextWrapping.Wrap,
        });

        return new ScrollViewer
        {
            Content = content,
            MaxHeight = 580,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
        };
    }

    private static Expander CreatePackageDetailsExpander(ArchiveInstallPlan plan)
    {
        StackPanel details = new() { Spacing = 10 };
        details.Children.Add(CreateDetail("文件", plan.Asset.FileName));
        details.Children.Add(CreateLinkDetail("下载地址", plan.Asset.DownloadUri));
        details.Children.Add(CreateDetail(
            plan.Asset.HashAlgorithm.DisplayName(),
            plan.Asset.PackageHash));
        details.Children.Add(CreateDetail("安装目录", plan.DestinationRoot));
        return new Expander
        {
            Header = $"{DisplayName(plan.Asset.Release.Kind)} {plan.Asset.Release.Version} · {plan.Asset.Release.Architecture}",
            Content = details,
            IsExpanded = true,
        };
    }

    private static Expander CreateVerificationExpander(
        PackageVerification verification,
        string assetHash)
    {
        bool coversPackage = verification.Value.Equals(
            assetHash,
            StringComparison.OrdinalIgnoreCase);
        StackPanel details = new() { Spacing = 10 };
        details.Children.Add(CreateDetail("对象", verification.Subject));
        details.Children.Add(CreateDetail("算法", verification.Algorithm));
        details.Children.Add(CreateDetail("值", verification.Value));
        details.Children.Add(CreateLinkDetail("证据来源", verification.SourceUri));
        details.Children.Add(new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Severity = coversPackage
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Informational,
            Title = coversPackage ? "覆盖当前安装包" : "验证上游清单",
            Message = VerificationDescription(
                verification.Kind,
                coversPackage,
                verification.Algorithm),
        });
        return new Expander
        {
            Header = $"{VerificationKindName(verification.Kind)} · {verification.Algorithm}",
            Content = details,
            IsExpanded = true,
        };
    }

    private static Expander CreateSignatureExpander(PackageSignatureVerification signature)
    {
        StackPanel details = new() { Spacing = 10 };
        details.Children.Add(CreateDetail("签名对象", signature.SignedSubject));
        details.Children.Add(CreateDetail("签名类型", SignatureKindName(signature.Kind)));
        details.Children.Add(CreateDetail("摘要算法", signature.HashAlgorithm));
        if (signature.Kind == PackageSignatureVerificationKind.SigstoreBundle)
        {
            details.Children.Add(CreateDetail("证书身份", signature.CertificateIdentity ?? "未知"));
            details.Children.Add(CreateDetail("OIDC Issuer", signature.CertificateOidcIssuer ?? "未知"));
            details.Children.Add(CreateDetail("叶证书 SHA-256", signature.PrimaryKeyFingerprint));
            details.Children.Add(CreateDetail("证书 Subject Key ID", signature.SigningKeyId));
            details.Children.Add(CreateDetail(
                "Rekor 位置",
                $"index {signature.TransparencyLogIndex} · tree {signature.TransparencyLogTreeSize}"));
            details.Children.Add(CreateDetail("Rekor Log ID", signature.TransparencyLogId ?? "未知"));
            details.Children.Add(CreateDetail("Trusted root SHA-256", signature.TrustRootSha256 ?? "未知"));
        }
        else
        {
            details.Children.Add(CreateDetail("主密钥指纹", signature.PrimaryKeyFingerprint));
            details.Children.Add(CreateDetail("实际签名 Key ID", signature.SigningKeyId));
        }

        details.Children.Add(CreateDetail(
            "签名时间（UTC）",
            signature.CreatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")));
        if (signature.SignedContentUri is Uri signedContentUri)
        {
            details.Children.Add(CreateLinkDetail("已签名内容", signedContentUri));
        }

        details.Children.Add(CreateLinkDetail(
            signature.Kind == PackageSignatureVerificationKind.SigstoreBundle
                ? "Sigstore bundle"
                : "签名清单",
            signature.SignatureUri));
        details.Children.Add(CreateLinkDetail(
            signature.Kind == PackageSignatureVerificationKind.SigstoreBundle
                ? "固定 trusted root 来源"
                : "固定公钥来源",
            signature.KeySourceUri));
        if (signature.IdentityPolicyUri is Uri identityPolicyUri)
        {
            details.Children.Add(CreateLinkDetail("发布身份策略", identityPolicyUri));
        }

        details.Children.Add(new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Severity = signature.SignerTrust == PackageSignerTrust.ActiveAtTrustSnapshot
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Informational,
            Title = signature.Kind == PackageSignatureVerificationKind.SigstoreBundle
                ? "Sigstore 发布身份与透明日志已验证"
                : signature.SignerTrust == PackageSignerTrust.ActiveAtTrustSnapshot
                    ? "活跃发布密钥"
                    : "历史发布密钥",
            Message = signature.Kind == PackageSignatureVerificationKind.SigstoreBundle
                ? "证书邮件身份和 OIDC Issuer 精确匹配 python.org 的版本系列策略；Fulcio 链、SCT 日志、Rekor SET、Merkle 包含证明与签名检查点均锚定到内置 trusted-root 快照。"
                : signature.SignerTrust == PackageSignerTrust.ActiveAtTrustSnapshot
                    ? "完整主指纹和实际签名密钥均匹配 AutoEnvPlus 固定的发布密钥信任快照。"
                    : "该密钥只允许验证信任快照日期之前的历史版本。",
        });
        return new Expander
        {
            Header = signature.Kind == PackageSignatureVerificationKind.SigstoreBundle
                ? $"{SignatureKindName(signature.Kind)} · {signature.CertificateIdentity}"
                : $"{SignatureKindName(signature.Kind)} · {signature.SigningKeyId}",
            Content = details,
            IsExpanded = true,
        };
    }

    private static Expander CreateSignatureRequirementExpander(
        PackageSignatureRequirement requirement)
    {
        StackPanel details = new() { Spacing = 10 };
        details.Children.Add(CreateDetail("签名对象", requirement.SignedSubject));
        details.Children.Add(CreateDetail("签名类型", SignatureKindName(requirement.Kind)));
        details.Children.Add(CreateDetail(
            "固定主密钥指纹",
            requirement.ExpectedPrimaryKeyFingerprint));
        details.Children.Add(CreateLinkDetail("Detached 签名", requirement.SignatureUri));
        details.Children.Add(CreateLinkDetail("固定公钥来源", requirement.KeySourceUri));
        return new Expander
        {
            Header = $"{SignatureKindName(requirement.Kind)} · 安装时验证",
            Content = details,
            IsExpanded = true,
        };
    }

    private static StackPanel CreateDetail(string label, string value)
    {
        StackPanel detail = new() { Spacing = 2 };
        detail.Children.Add(new TextBlock
        {
            Opacity = 0.7,
            Text = label,
        });
        detail.Children.Add(new TextBlock
        {
            IsTextSelectionEnabled = true,
            Text = value,
            TextWrapping = TextWrapping.Wrap,
        });
        return detail;
    }

    private static StackPanel CreateLinkDetail(string label, Uri uri)
    {
        StackPanel detail = new() { Spacing = 2 };
        detail.Children.Add(new TextBlock
        {
            Opacity = 0.7,
            Text = label,
        });
        detail.Children.Add(new HyperlinkButton
        {
            Content = new TextBlock
            {
                IsTextSelectionEnabled = true,
                Text = uri.AbsoluteUri,
                TextWrapping = TextWrapping.Wrap,
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            NavigateUri = uri,
            Padding = new Thickness(0),
        });
        return detail;
    }

    private static string VerificationKindName(PackageVerificationKind kind) => kind switch
    {
        PackageVerificationKind.ProviderChecksum => "Provider 包校验和",
        PackageVerificationKind.VerifiedManifest => "已验证的发行清单",
        _ => kind.ToString(),
    };

    private static string SignatureKindName(PackageSignatureVerificationKind kind) => kind switch
    {
        PackageSignatureVerificationKind.OpenPgpCleartext => "OpenPGP Cleartext Signature",
        PackageSignatureVerificationKind.OpenPgpDetached => "OpenPGP Detached Signature",
        PackageSignatureVerificationKind.SigstoreBundle => "Sigstore Bundle",
        _ => kind.ToString(),
    };

    private static string VerificationDescription(
        PackageVerificationKind kind,
        bool coversPackage,
        string algorithm) => kind switch
        {
            PackageVerificationKind.VerifiedManifest =>
                $"发行文件 API 中的 {algorithm} 已用于验证这份 Windows 包清单。",
            PackageVerificationKind.ProviderChecksum when coversPackage =>
                $"该 HTTPS 来源声明了当前安装包的 {algorithm}。",
            _ => "该证据参与解释安装包校验链。",
        };

    private IArchiveRuntimeProvider CreateProvider(
        RuntimeKind kind,
        RuntimeArchitecture architecture,
        int? javaFeatureVersion) =>
        kind switch
        {
            RuntimeKind.Python => new PythonOrgCatalogProvider(_httpClient, architecture),
            RuntimeKind.NodeJs => new NodeJsCatalogProvider(_httpClient),
            RuntimeKind.DotNet => new DotNetSdkCatalogProvider(_httpClient, architecture),
            RuntimeKind.Java => new AdoptiumCatalogProvider(
                _httpClient,
                javaFeatureVersion
                    ?? throw new InvalidOperationException("安装 Java 前必须选择 JDK 版本线。"),
                architecture),
            _ => throw new NotSupportedException($"尚未实现 {kind} 安装 Provider。"),
        };

    private void SetInstallButtonsEnabled(bool enabled)
    {
        PythonInstallButton.IsEnabled = enabled;
        NodeInstallButton.IsEnabled = enabled;
        JavaInstallButton.IsEnabled = enabled;
        DotNetInstallButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        ManagedRuntimeList.IsEnabled = enabled;
    }

    private CancellationToken BeginOperation()
    {
        _operationRunning = true;
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCancellation.Token);
        SetInstallButtonsEnabled(false);
        CancelOperationButton.IsEnabled = true;
        OperationProgress.IsActive = true;
        return _operationCancellation.Token;
    }

    private void EndOperation()
    {
        _operationRunning = false;
        CancelOperationButton.IsEnabled = false;
        OperationProgress.IsActive = false;
        SetInstallButtonsEnabled(true);
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private static IReadOnlySet<string> ResolveGlobalDefaults(
        RuntimeProfile global,
        IReadOnlyList<ManagedRuntimeEntry> entries)
    {
        HashSet<string> defaults = new(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeKind kind in entries.Select(entry => entry.Kind).Distinct())
        {
            RuntimeResolutionResult resolution = new RuntimeResolver().Resolve(
                kind,
                new RuntimeResolutionContext(Global: global),
                entries.Select(candidate => candidate.ToRuntimeInstallation()),
                CurrentArchitecture());
            if (resolution.Success)
            {
                defaults.Add(resolution.Installation!.Id);
            }
        }

        return defaults;
    }

    private void ResetActionInfo()
    {
        ActionInfo.Severity = InfoBarSeverity.Informational;
        ActionInfo.Title = "安全安装模式";
        ActionInfo.Message = "Node.js 验证签名清单；Java 安装时验证包本体的 detached OpenPGP 签名；Python 验证官方发布证据；.NET SDK 按 Microsoft 官方元数据校验 SHA-512。";
    }

    private static string StageTitle(
        string stage,
        PackageHashAlgorithm hashAlgorithm) => stage switch
        {
            "download" => "正在下载",
            "verify" => $"正在校验 {hashAlgorithm.DisplayName()}",
            "verify-signature" => "正在验证 OpenPGP 包签名",
            "extract" => "正在安全解压",
            "commit" => "正在提交安装",
            "complete" => "安装完成",
            _ => stage,
        };

    private static string DisplayName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.NodeJs => "Node.js",
        RuntimeKind.Java => "Java",
        RuntimeKind.DotNet => ".NET SDK",
        _ => kind.ToString(),
    };

    private static RuntimeArchitecture CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => RuntimeArchitecture.X86,
        Architecture.Arm64 => RuntimeArchitecture.Arm64,
        _ => RuntimeArchitecture.X64,
    };

    private static string GetManagedRoot() => ManagedRootResolver.ResolveOrThrow();

    private sealed record RuntimeChoice(RuntimeRelease Release)
    {
        public string Label => $"{Release.Version} · {Release.Architecture} · {string.Join(", ", Release.Channels)}";
    }

    private sealed record JavaFeatureChoice(
        int FeatureVersion,
        bool IsLts,
        bool IsLatestFeature,
        bool IsRecommended)
    {
        public string Label
        {
            get
            {
                List<string> badges = [];
                if (IsRecommended)
                {
                    badges.Add("推荐");
                }

                if (IsLts)
                {
                    badges.Add("LTS");
                }

                if (IsLatestFeature)
                {
                    badges.Add("最新特性版");
                }

                return badges.Count == 0
                    ? $"JDK {FeatureVersion}"
                    : $"JDK {FeatureVersion} · {string.Join(" · ", badges)}";
            }
        }
    }

    private sealed record ManagedRuntimeRow(
        ManagedRuntimeEntry Entry,
        bool IsGlobalDefault)
    {
        public string DisplayName => $"{Entry.Kind} {Entry.Version} ({Entry.Architecture})";

        public string Id => Entry.Id;

        public string InstallRoot => Entry.InstallRoot;

        public Visibility DefaultBadgeVisibility => IsGlobalDefault
            ? Visibility.Visible
            : Visibility.Collapsed;

        public bool CanSetGlobal => !IsGlobalDefault;

        public string DefaultActionText => IsGlobalDefault ? "当前默认" : "设为默认";
    }
}
