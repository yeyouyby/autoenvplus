# AutoEnvPlus 阶段交接

更新时间：2026-07-17

这份文档用于在另一台 Windows 机器上继续开发，也用于说明本阶段 PR 的目标、已经完成的能力、不可破坏的产品决策和下一阶段任务。产品规格、技术边界和安全模型仍分别以 `PRODUCT.md`、`ARCHITECTURE.md` 和 `SECURITY.md` 为准。

## 产品目标

AutoEnvPlus 是面向 Windows 10 22H2 与 Windows 11 的 WinUI 3 Fluent 应用，用一个可审计工作台管理现代编程语言所需的编译器、解释器、运行时、构建工具、包工具、版本选择、项目环境、下载、缓存目录、Provider 来源和代理。

核心体验不是反复修改 PATH，而是只把 AutoEnvPlus Shim 目录加入用户 PATH 一次。之后全局默认、项目固定版本和新终端会话选择都解析到精确的 Runtime ID 与 Provider ID；安装、切换和卸载不会反复重写用户 PATH。

## 固定领域模型

```text
语言 -> 语言工具 -> Provider -> Provider 来源
```

- 一级导航使用“语言”，不恢复独立“运行时”“工具链”“Provider 插件”或“网络与镜像”页面。
- 语言是固定容器；语言包可以增加语言或工具定义；Provider 插件定义某个语言工具的安装与管理能力。
- C/C++ 是语言页面，MSVC、GCC、Clang、CMake、Ninja 和 Windows SDK 都是该领域内的语言工具或相关组件。
- Provider 来源严格属于 `languageToolId + providerId + slotId`。不存在跨 Provider 的全局镜像；用户自定义镜像也保存在对应 Provider 槽内。
- 通用 HTTP/HTTPS 代理和 `NO_PROXY` 只在设置中管理。代理决定如何连接，Provider 来源决定连接哪个发行目录或包仓库。
- 目录元数据不能冒充已实现插件。140 条 Provider Profile 表示目录和能力声明，不等于 140 个可执行安装适配器。

## 已完成能力

- Fluent 首页、语言列表和语言详情；语言详情包含概览、工具与版本、包与镜像、项目环境和高级设置。
- 45 门内置语言、136 个语言工具目录、140 条工具作用域 Provider Profile、83 个 Provider 来源槽。
- 默认显示 Top 10 常用语言以及显式扫描发现的本机语言；其余语言可搜索、启用或通过语言包扩展。
- 9 个真实受管适配器：CPython、Node.js、Eclipse Temurin、.NET SDK、MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja。
- Provider 插件 schema 2 使用 `languageToolId`；schema 1 `runtimeKind` 可导入并规范化，导入后默认停用。
- Provider 来源可恢复内置值，也可添加、启停和删除具名 HTTPS 自定义来源；所有引用和冲突检查保持 Provider 精确作用域。
- 手动文件导入、1/2/4/8/16 连接分段下载、断点元数据、逐字节 SHA-256/SHA-512 校验、下载库和 pip wheel 离线/联网安装预览。
- 全局、项目、新终端会话三层版本选择；项目 `autoenvplus.toml` 支持 `[tools]` 和 `[tool-identities]`，写入前显示差异并用原子替换防止覆盖并发编辑。
- 项目虚拟环境解析覆盖 Python venv/Poetry/Pipenv/Conda、Node 包管理器与 Corepack、.NET 本地工具、Maven/Gradle Wrapper、Rust 和 Go；解析只读且不执行项目工具。
- 18 个 Shim 命令：`python`、`python3`、`pip`、`pip3`、`node`、`npm`、`npx`、`java`、`javac`、`jar`、`dotnet`、`cl`、`clang`、`clang++`、`gcc`、`g++`、`cmake`、`ninja`。
- 环境诊断按 PATH/命令、托管工具、项目、Provider、存储和实时连接分域显式启动；可发现快照漂移、精确身份失效、插件能力漂移、暂存残留、下载 partial、系统盘缓存和 Shim 完整性问题。
- 缓存与存储支持 pip、npm、pnpm、Yarn、NuGet、Maven、Gradle、vcpkg 和 Conan 的发现、测量、迁移预览、安全清理和回滚。
- 活动记录具有跨进程锁、原子写、1000 条、2 MiB 和 1 到 365 天保留上限。
- 便携 ZIP、开发签名 MSIX/AppInstaller、CLI、原生 x64 Shim 和 AGPL-3.0-only 分发文件。

## 快照与扫描合同

- 首页打开只读取 `overview-snapshot.json`，语言页打开只读取 `language-tool-inventory.json`。
- 页面加载不得扫描 PATH、运行版本命令、遍历缓存或访问网络。
- 首页“快速刷新”只读取受管状态；完整环境扫描必须由用户显式启动。
- 语言 PATH 重检、虚拟环境解析、环境诊断和 Provider 实时连接检查都必须由用户显式启动。
- 设置中不提供会制造虚假状态的“自动扫描”“关闭 Shim”或“关闭破坏性确认”开关。

## 安全与存储合同

- 仓库、NuGet 包、NuGet HTTP/插件缓存、dotnet CLI home、TEMP/TMP、发布暂存、构建产物和 AutoEnvPlus 受管数据均放在 `D:\codex`。
- 受管写入使用固定根、普通目录/文件检查、重解析点与设备路径拒绝、跨进程锁、预览哈希复核和同目录原子替换。
- 下载只允许受审核 HTTPS，重定向后重新验证协议；进度、日志和清单不保留 URL query、fragment 或凭据。
- 缓存诊断不跟随根或祖先重解析点，并按顺序、最多 50,000 个条目和 32 层测量单个缓存；达到上限时报告统计不完整。
- 卸载前重新扫描全局、项目和项目锁引用；精确项目身份只阻止卸载实际固定的 Provider。
- 永久写入、迁移、清理、卸载和 Shell/Profile 修改必须预览并确认，安全确认不可关闭。

## 当前验证基线

本阶段最终收口已经通过：

```text
Core tests:                 697 passed, 0 failed, 0 skipped
WinUI Release x64:          0 warnings, 0 errors
Solution Release x64:       0 warnings, 0 errors
Native Shim Release x64:    0 warnings, 0 errors
Source XAML XML parse:      12 files passed
Packaging assertions:       41 passed
Portable publish:           543 files, about 289 MiB
Development MSIX:           create, sign, unpack and hash verification passed
```

开发证书 MSIX 仅用于结构和签名流程验证，不是生产可信安装包。正式发布必须提供真实代码签名 PFX、精确 Publisher 和可信 RFC 3161 时间戳。

## 新机器继续开发

仓库目标目录：

```text
D:\codex\autoenvplus
```

每个构建或测试 PowerShell 会话先设置：

```powershell
$env:NUGET_PACKAGES='D:\codex\.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH='D:\codex\.nuget\v3-cache'
$env:NUGET_PLUGINS_CACHE_PATH='D:\codex\.nuget\plugins-cache'
$env:DOTNET_CLI_HOME='D:\codex\.dotnet'
$env:TEMP='D:\codex\tmp'
$env:TMP=$env:TEMP
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:AUTOENVPLUS_BUILD_CACHE_ROOT='D:\codex'
$env:AUTOENVPLUS_HOME='D:\codex\autoenvplus-data'
```

最小复核命令：

```powershell
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj -c Release --no-restore
dotnet build AutoEnvPlus.sln -c Release -p:Platform=x64 --no-restore -m:1
dotnet format AutoEnvPlus.sln --verify-no-changes --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\test-packaging.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1 -Configuration Release -BuildCacheRoot D:\codex
git diff --check
```

解决方案构建使用 `-m:1`，避免 WinUI App 与 CLI 并发写共享输出时触发 XAML 编译器争用。

## 下一阶段任务

1. 在 Windows 10 22H2 和 Windows 11 真机完成键盘、屏幕阅读器、125%/150% 缩放、浅色/深色、高对比度和紧凑密度手工验收。
2. 为更多语言工具增加经过审核的真实安装适配器；每个适配器必须有可信目录、固定资产协议、哈希或签名证据、入口文件合同和卸载测试，不能仅增加 Profile 数量。
3. 增加 Provider Profile 与语言包的签名发行、撤销和可信发布者模型，同时保持 data-only 导入边界。
4. 为项目提供可选的受审核目录切换 hook；只有 hook 真正存在后，才考虑展示自动激活开关。
5. 增加下载任务队列的带宽限制、计划执行、失败重试策略和跨重启队列恢复，不降低现有 URL 脱敏与哈希复核要求。
6. 扩展项目解析器到容器、WSL、Dev Container、Nix/Guix 清单和更多 lockfile，但 Windows 主机与外部环境之间必须明确边界。
7. 建立 GitHub Actions 的 Windows 构建、测试、格式、原生 Shim 和打包门禁；正式发布再接入可信代码签名服务。
8. 继续细化 MSVC 边界：AutoEnvPlus 可管理可执行工具和开发者会话，不能声称完全替代 Visual Studio C++ workload、组件安装器或所有 `vcvars` 语义。

## 已知交付边界

- 用户此前要求不操控 GUI，因此本阶段没有自动启动应用、Browser 或 Computer Use；所有 UI 验证均为编译、XAML 解析和代码合同验证，真机视觉与可访问性验收列在下一阶段。
- GitHub PR 创建和合并需要本机 `gh` 登录；普通 Git 推送与 `gh` 认证是两个独立凭据通道。
- AutoEnvPlus 本体许可证固定为 `AGPL-3.0-only`；第三方组件继续采用各自许可证，详见根目录 `THIRD-PARTY-NOTICES.md`。
