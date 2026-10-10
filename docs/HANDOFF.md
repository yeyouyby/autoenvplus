# AutoEnvPlus 阶段交接

更新时间：2026-10-10

目标版本：`v0.0.3` 已发布；下一版本未定

本文只保留继续工作的最小上下文。权威行为分别见 [产品规格](PRODUCT.md)、[技术架构](ARCHITECTURE.md)、[安全模型](SECURITY.md) 和 [功能清单](FEATURES.md)。

## 当前状态

- `v0.0.1`（2026-10-05）、`v0.0.2`（2026-10-06，GUI 启动缺陷修复）、`v0.0.3`（2026-10-10，产品打磨）均已作为 GitHub prerelease 发布并完成签名与资产回读；
- `v0.0.3` 包含五页产品审计的全部 UI/UX 修复与四项功能级改进（页面状态保留、应用内更新检查、全局搜索、行级 a11y 名称），详见 [变更记录](CHANGELOG.md)；
- 产品版本权威源是 `0.0.3`，Windows 包版本为 `0.0.3.0`，发布 tag 为 `v0.0.3`；
- 三类权威主资产是 `AutoEnvPlus-win-x64.exe`（WinUI single-file）、`AutoEnvPlus-win-x64-portable.zip`（含 CLI/Shim）和 `AutoEnvPlus-win-x64.msi`（per-user）；CLI 不单独作为第四类资产；
- tag 工作流要求三类资产通过自签名门禁，缺少配置或签名失败即停止；
- 不应沿用此前阶段记录的固定测试通过数量，最终结果必须在候选 commit 上重新运行并记录；
- 不得声称 PR、tag 或 GitHub prerelease 已存在，除非从 GitHub 权威状态回读确认。

## 下一步候选工作

- 界面本地化框架：把 500+ 硬编码中文字符串抽到 `.resw` 资源文件（XAML `x:Uid` + 代码 `ResourceLoader`），支持多语言；当前只有 `CoreErrorText` 在 UI 边界翻译核心错误消息；
- Windows 10 真机与完整可访问性/缩放矩阵验收（Windows 11 本机已验证启动/主题/键盘/UIA）；
- Windows 10/11 完整安装升级 E2E（MSI 全新安装、同版本拒绝、升级、降级拒绝、卸载数据保留）；
- ARM64 应用构建与受信任 CA 发布证书切换。

## 不可破坏的合同

1. 领域模型是“语言 -> 语言工具 -> Provider -> Provider 来源”。
2. 45 门语言、136 个工具、140 条 Provider Profile 是元数据目录；真实受管适配器恰好 9 个。
3. 概览和语言页只加载快照；完整扫描、PATH 重检、虚拟环境解析和实时连接检查由用户显式触发。
4. 选择顺序是新终端会话、项目、全局、自动；前三层都可绑定 selector + Runtime ID + Provider ID。
5. Provider 来源按 `languageToolId + providerId + slotId` 所有，不能与通用代理或跨 Provider “全局镜像”混合。
6. 语言包和 Runtime Provider 插件均为 data-only；schema 2 插件只绑定 9 个桥接工具。
7. 永久写入、迁移、清理、卸载、PATH 和 Shell/Profile 修改必须预览、确认并在执行前复检。
8. AutoEnvPlus 本体许可证仅为 `AGPL-3.0-only`。
9. single-file 可能自解压 bundle 内容，不得描述为绿色版、完全无临时文件或无 Windows 系统依赖。

## 当前真实适配器

```text
官方归档：CPython、Node.js、Eclipse Temurin、.NET SDK
固定 WinGet：MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja
```

Windows SDK 可以发现，但没有独立受管安装适配器。MSVC 插件 ZIP 中出现 `cl.exe` 也不能替代 Visual Studio workload、Windows SDK 或完整 `vcvars` 激活。

## 新机器开发环境

建议仓库位于 `D:\codex\autoenvplus`，并设置：

```powershell
$env:NUGET_PACKAGES = 'D:\codex\.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = 'D:\codex\.nuget\v3-cache'
$env:NUGET_PLUGINS_CACHE_PATH = 'D:\codex\.nuget\plugins-cache'
$env:DOTNET_CLI_HOME = 'D:\codex\.dotnet'
$env:TEMP = 'D:\codex\tmp'
$env:TMP = $env:TEMP
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AUTOENVPLUS_BUILD_CACHE_ROOT = 'D:\codex'
$env:AUTOENVPLUS_HOME = 'D:\codex\autoenvplus-data'
```

D 盘是开发建议，不得硬编码为产品必需路径；受管根也不是第三方子进程沙箱。

## 发布新版本时的收口顺序

1. 对当前工作树做功能/安全 review，修复阻断项；
2. 执行 [开发指南](DEVELOPMENT.md#构建与验证) 的完整本地门禁；
3. 审核最终 diff 和许可证/第三方声明；
4. 按 [发布指南](RELEASING.md) 创建 PR、等待 CI、合并、打 tag 并回读 GitHub prerelease；
5. 发布后再把 [变更记录](CHANGELOG.md) 的对应版本从“待发布”改为实际日期。

## 最小验证命令

```powershell
dotnet restore AutoEnvPlus.sln --locked-mode -p:Platform=x64 -r win-x64
dotnet format AutoEnvPlus.sln --verify-no-changes --no-restore
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore -m:1
dotnet build src\AutoEnvPlus.App\AutoEnvPlus.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true --no-restore -m:1
dotnet msbuild src\AutoEnvPlus.Shim\AutoEnvPlus.Shim.proj /t:Rebuild /p:Configuration=Release /p:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\test-packaging.ps1
git diff --check
```

任何未运行、失败或只在旧 commit 上通过的检查都必须明确记录，不能在 PR/Release 中写成当前候选已验证。
