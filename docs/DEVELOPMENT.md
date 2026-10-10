# AutoEnvPlus 开发指南

本文面向继续开发 AutoEnvPlus（当前 `v0.0.3`）及后续版本的贡献者。开始修改前先阅读 [产品规格](PRODUCT.md)、[技术架构](ARCHITECTURE.md) 和 [安全模型](SECURITY.md)；功能看似可用不等于满足产品合同和安全边界。

## 开发环境

- Windows 10/11 x64；
- `.NET SDK 10.0.200`，由仓库 `global.json` 固定；
- Visual Studio 2022 C++ Desktop Build Tools；
- Windows SDK；
- Git 与 PowerShell。

推荐把依赖、临时文件和构建输出放到独立 D 盘根：

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

脚本不得假定每台机器都有 `D:\codex`；CI 使用仓库内可配置的回退根。本地路径也不得写进持久产品默认值。

## 仓库结构

```text
src/AutoEnvPlus.App/          WinUI 3 / Fluent 桌面界面
src/AutoEnvPlus.Core/         领域模型、存储、网络、安装和安全边界
src/AutoEnvPlus.Cli/          复用 Core 的命令行入口
src/AutoEnvPlus.Shim/         不加载 CLR 的原生 x64 命令路由器
tests/AutoEnvPlus.Core.Tests/ Core、CLI 合同和原生 Shim 测试
schemas/                      公开 data-only JSON Schema
examples/                     需要替换占位值的清单模板
eng/                          构建、single-file、便携、MSI 与遗留 MSIX 验证脚本
packaging/                    WiX MSI 项目及遗留 AppxManifest/AppInstaller 模板
docs/                         产品、用户、安全、开发和发布文档
```

## 关键产品合同

### 目录不等于执行能力

内置目录的权威计数是 45 门语言、136 个工具、140 条 Provider Profile 和 83 个来源槽。只有 `LanguageToolRuntimeBridge` 定义的 9 个工具拥有真实受管适配器。新增 Profile 时必须明确 `capabilities`，不能只因出现 UI 条目就宣称支持安装。

### 快照按加载读取

概览和语言页初始化只读取持久快照。任何新增的 PATH 探测、版本命令、缓存遍历或网络访问都必须放在用户显式触发的扫描流程，并记录采集时间、取消和错误状态。

### 精确身份不能降级

解析顺序是新终端会话、项目、全局、自动选择。前三层都可固定 selector、Runtime ID 和 Provider ID。存在精确身份时，代码不得仅按版本选择同版本的另一个 Provider。

### 计划与执行分离

安装、卸载、PATH、Shell/Profile、缓存迁移/清理、下载覆盖、项目清单和 CMake Presets 都要在预览后复检输入。Core 服务必须把预览摘要、输入身份和执行边界建模，而不是只在 UI 放确认框。

### Provider 来源不等于代理

Provider 来源以 `languageToolId + providerId + slotId` 拥有；通用代理位于独立设置。不要加入“全局镜像”或用字符串拼接把一个 Provider 的 base URI 套给另一种协议。

### data-only 扩展

语言包和 Runtime Provider 插件解析器必须拒绝未知字段、重复属性、超限数据、危险 URI 和路径。公开 schema 2 插件以 `languageToolId` 绑定 9 个桥接工具；schema 1 只作兼容导入并规范化升级。不得加入 DLL、脚本、任意命令或安装钩子而仍称其为 data-only。

## 构建与验证

首次恢复：

```powershell
dotnet restore AutoEnvPlus.sln --locked-mode -p:Platform=x64 -r win-x64
```

日常质量门禁：

```powershell
dotnet format AutoEnvPlus.sln --verify-no-changes --no-restore
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj `
  -c Release -p:Platform=x64 -r win-x64 --no-restore -m:1
dotnet build src\AutoEnvPlus.App\AutoEnvPlus.App.csproj `
  -c Release -p:Platform=x64 -r win-x64 --self-contained true --no-restore -m:1
dotnet msbuild src\AutoEnvPlus.Shim\AutoEnvPlus.Shim.proj `
  /t:Rebuild /p:Configuration=Release /p:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\test-packaging.ps1
git diff --check
```

解决方案或 App 构建保持 `-m:1`，避免多个项目同时写 CLI/Shim 共享输出。依赖恢复后检查 App、CLI、Core 与测试项目的所有 `packages.lock.json` 都没有意外漂移。

三类发布候选的本地构建：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-single-file.ps1 `
  -Configuration Release `
  -BuildCacheRoot D:\codex
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1 `
  -Configuration Release `
  -BuildCacheRoot D:\codex
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-msi.ps1 `
  -Configuration Release `
  -BuildCacheRoot D:\codex
```

本地脚本默认只生成候选，不能建立发布者身份。公开 tag 流程会用自签名证书签第一方 PE、用已签 payload 重建 portable/MSI、再签 MSI 外层；PR 和普通 CI 不接触签名材料。完整签名与身份验证细节见 [分发说明](DISTRIBUTION.md)。

## 测试原则

- 文件系统操作覆盖普通文件、目录、重解析点、路径逃逸、并发修改、大小/条目上限和恢复失败；
- 网络解析覆盖重定向、响应上限、超时/取消、实体变化、脱敏和自定义来源；
- 安装测试同时验证 Provider、版本、架构、哈希算法和值、入口收据与补偿事务；
- 精确选择覆盖同版本多 Provider、插件删除/重导入、项目/全局/会话优先级；
- UI 行为至少覆盖 ViewModel/策略测试、XAML 编译、Windows 10/11 真机视觉与可访问性检查；
- 修复安全问题时先加入能证明原行为的失败测试，再修实现并验证相邻边界。

## 文档与变更记录

用户可见修改至少同步：

- [功能清单](FEATURES.md) 的状态与限制；
- [用户指南](USER-GUIDE.md) 的工作流；
- [安全模型](SECURITY.md) 的新增/变化边界；
- [变更记录](CHANGELOG.md) 的 `Unreleased` 或目标版本条目。

不要在 README 放完整开发日志或长功能矩阵；根文档保持可扫描，具体证据留在 `docs/`。

## 提交与 PR

1. 从最新主分支创建主题分支；
2. 保持提交范围可解释，不混入生成产物或本机配置；
3. 在 PR 中说明用户影响、安全边界、验证命令和未验证项；
4. CI 全部通过后再准备 tag；
5. PR 存在、合并、tag 推送和 GitHub Release 发布都是外部状态，只有回读 GitHub 后才能写成完成。

历史里程碑见 [变更记录](CHANGELOG.md)，下一步目标见 [路线图](ROADMAP.md)，发布门禁见 [发布指南](RELEASING.md)。
