# AutoEnvPlus

<p align="center">
  <img src="assets/branding/autoenvplus-logo.png" alt="AutoEnvPlus logo" width="160">
</p>

<p align="center">
  <strong>AutoEnvPlus 是面向 Windows 10/11 的 Fluent 开发环境控制中心，用一个可审计工作台管理语言工具、版本、项目环境、下载、缓存与 Provider 来源。</strong>
</p>

<p align="center">
  <a href="https://github.com/yeyouyby/autoenvplus/actions/workflows/ci.yml"><img alt="Windows CI status" src="https://github.com/yeyouyby/autoenvplus/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: AGPL-3.0-only" src="https://img.shields.io/badge/license-AGPL--3.0--only-2f6f4e"></a>
  <a href="https://github.com/yeyouyby/autoenvplus/releases"><img alt="Latest GitHub prerelease" src="https://img.shields.io/github/v/release/yeyouyby/autoenvplus?include_prereleases&sort=semver&display_name=tag&label=prerelease"></a>
</p>

> [!IMPORTANT]
> 当前发布版本为 `v0.0.3`（Windows x64 测试版，面向验证而非生产部署）。每个 GitHub prerelease 必须同时提供三类经过发布工作流签名处理的主资产：`AutoEnvPlus-win-x64.exe`、`AutoEnvPlus-win-x64-portable.zip` 和 `AutoEnvPlus-win-x64.msi`，外加各自的 `.sha256` 校验文件与聚合 `SHA256SUMS.txt`。如果 [GitHub Releases](https://github.com/yeyouyby/autoenvplus/releases) 尚无这些文件，表示该版本预发布尚未完成；仓库中的工作流定义、本地构建或未签候选都不等于已经发布。签名使用自签名测试证书，Windows 会显示“未知发布者”，这是测试版的预期行为。

## 为什么是 AutoEnvPlus

AutoEnvPlus 不把开发环境简化成一组 PATH 编辑器。它使用固定的领域模型：

```text
语言 -> 语言工具 -> Provider -> Provider 来源
```

内置目录当前包含 **45 门语言、136 个真实工具条目、140 条工具作用域 Provider Profile 和 83 个 Provider 来源槽**。这些数字描述目录和能力元数据，并不代表存在 136 或 140 个安装器。

真正可执行的受管安装适配器恰好有 **9 个**：

| 安装方式 | 适配器 |
|---|---|
| 官方归档 | CPython、Node.js、Eclipse Temurin、.NET SDK |
| 固定 WinGet 白名单 | MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja |

其余工具只展示代码已经具备的发现、官方链接、来源配置或外部安装边界；界面不会把目录项冒充为已实现安装能力。

## 六组核心能力

| 能力组 | 当前能力 |
|---|---|
| **语言与工具目录** | 搜索和管理 45 门语言；按“语言 -> 工具 -> Provider -> 来源”查看真实能力；导入默认停用的 data-only 语言包。 |
| **版本与命令路由** | 管理 9 个适配器覆盖的运行时/工具链；以新终端会话、项目、全局、自动选择的顺序解析版本；用成对的 Runtime ID + Provider ID 避免同版本跨来源误选；18 个原生 Shim 命令只需一次 PATH 配置。 |
| **项目与 C/C++ 工作流** | 导入常见版本文件、生成 `autoenvplus.toml` / `autoenvplus.lock`、只读解析虚拟环境、打开经复检的已激活终端，并管理 MSVC Host/Target 与 CMake User Presets。 |
| **下载与网络** | HTTPS 单流或 1/2/4/8/16 路分段下载、本地包导入、可选 SHA-256/SHA-512 预期值、受管下载库、wheel 安装计划，以及精确归属到工具/Provider 的来源与独立代理设置。 |
| **存储、诊断与审计** | 发现和迁移常见包缓存；以隔离、恢复、永久清空三阶段处理纯缓存；显式运行六域环境诊断；记录脱敏且有界的活动日志。 |
| **Fluent 桌面与受限扩展** | WinUI 3 / Fluent 导航、主题、材质和密度（选择即时预览）；全局搜索直达语言详情与页面（Ctrl+F）；应用内新版本提示；页面状态跨导航保留；窗口位置跨会话持久化；单实例守卫；schema 2 声明式 Runtime Provider 仅为 9 个桥接工具增加受限 ZIP 来源，不加载插件 DLL 或脚本。 |

首页和语言页采用 **snapshot-on-load**：打开页面只读取已有快照，不扫描 PATH、不执行版本命令、不遍历缓存，也不联网。完整环境扫描、语言 PATH 重检、虚拟环境解析和实时连接检查都必须由用户手动触发。

完整矩阵见 [功能清单](docs/FEATURES.md)，操作流程见 [用户指南](docs/USER-GUIDE.md)。

## 快速开始

### 运行要求

- Windows 10 或 Windows 11，当前发布目标为 x64；
- 源码构建使用仓库 [`global.json`](global.json) 指定的 .NET SDK `10.0.200`；
- 构建 WinUI/原生 Shim 需要 Visual Studio 2022 C++ 桌面工具和 Windows SDK。

### 选择发布资产

| 主资产 | 用途与边界 |
|---|---|
| `AutoEnvPlus-win-x64.exe` | WinUI 主程序的 .NET/Windows App SDK self-contained single-file 分发。它不是 CLI；启动时可能把原生库、PRI/XBF 等 bundle 内容自解压到 .NET bundle extraction/临时目录，因此“单文件”不等于绿色版、完全无临时文件或脱离 Windows 系统依赖。 |
| `AutoEnvPlus-win-x64-portable.zip` | 普通 WinUI EXE + DLL 自包含目录，包含 `cli\autoenvplus.exe` 与原生 Shim。必须完整解压并保留目录结构；CLI 只是这个便携包和 MSI 内的辅助组件，不是第四类独立资产。 |
| `AutoEnvPlus-win-x64.msi` | per-user Windows Installer，提供开始菜单入口、应用和功能中的卸载及 major upgrade。卸载只移除程序文件，保留用户配置、受管数据根和已经安装的语言工具。 |

发布工作流要求单文件 EXE 和便携/MSI 内第一方 PE 通过签名复检（自签名证书 + RFC3161 时间戳，指纹精确匹配），并在已签 payload 上构建、再签 MSI 外层。ZIP 容器本身不能做 Authenticode 签名；其发布者身份来自内部第一方 PE 的有效签名，ZIP 字节完整性由 `.sha256` 和 `SHA256SUMS.txt` 校验。签名证书配置和一次 tag 工作流成功回读完成前，不能声称当前下载已经签名。

### 从源码运行

```powershell
git clone https://github.com/yeyouyby/autoenvplus.git
cd autoenvplus

dotnet restore AutoEnvPlus.sln
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj -c Release -m:1
dotnet run --project src\AutoEnvPlus.App -p:Platform=x64
```

先用 CLI 做只读检查：

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- doctor
dotnet run --project src\AutoEnvPlus.Cli -- list
dotnet run --project src\AutoEnvPlus.Cli -- provider list
```

具有持久影响的 CLI 操作通常先显示计划，只有带 `--yes` 才执行。安装或修改前请阅读 [快速开始](docs/QUICKSTART.md) 与 [用户指南](docs/USER-GUIDE.md)。

### 把 AutoEnvPlus 数据放到 D 盘

```powershell
$env:AUTOENVPLUS_HOME = 'D:\AutoEnvPlus\data'
dotnet run --project src\AutoEnvPlus.App -p:Platform=x64
```

受管根优先级为 CLI `--root`、`AUTOENVPLUS_HOME`、`%LOCALAPPDATA%\AutoEnvPlus`。更改设置后需重启才使用新根，且不会自动迁移或删除旧数据。D 盘设置只能约束 AutoEnvPlus 自己管理的目录，不能阻止系统组件或第三方安装脚本写入 C 盘、用户 Profile 或其他位置。

## 安全边界

- **预览不是执行。** 安装、卸载、迁移、清理、PATH 和 Shell/Profile 修改在执行前显示计划，并在写入前复检关键输入；外部 pip/WinGet 进程仍不具备事务回滚。
- **内容哈希不是发布者身份。** Python、Node.js 和 Temurin 使用各自的签名策略；.NET 当前依赖 release metadata 中的 SHA-512；插件哈希由插件作者声明，不能冒充官方签名。
- **插件是 data-only，但资产仍是代码。** 声明式插件不能携带 DLL、脚本、任意命令或安装钩子，下载得到的第三方 `.exe` 仍需要用户独立判断其可信度。
- **来源不等于代理。** Provider 来源属于 `languageToolId + providerId + slotId`；HTTP(S) 代理和 `NO_PROXY` 是独立传输设置，改变网络路径不会建立发布者信任。
- **精确身份贯穿三层选择。** 新终端会话、项目 `[tool-identities]` 和全局 profile 都可以保存成对的 Runtime ID 与 Provider ID；解析不会把同版本的另一个 Provider 当作等价替代。
- **当前仍是预览。** 自签名测试证书不受 Windows 信任，用户侧显示“未知发布者”属预期，后续可切换到受信任 CA。完整 Windows 10/11 安装升级 E2E、全量可访问性验收、插件签名/撤销通道和 ARM64 应用构建尚未完成。

完整威胁边界、验证链和已知残余风险见 [安全模型](docs/SECURITY.md)。发现安全问题时，请避免在公开 Issue 中披露可利用细节；先查看仓库的安全报告渠道或联系维护者。

## 文档

| 入口 | 内容 |
|---|---|
| [文档首页](docs/README.md) | 按用户、开发者、集成作者和发布维护者导航 |
| [快速开始](docs/QUICKSTART.md) | 环境准备、首次运行与 D 盘配置 |
| [功能清单](docs/FEATURES.md) | 完整能力矩阵、9 个适配器和当前限制 |
| [用户指南](docs/USER-GUIDE.md) | WinUI 与常用 CLI 工作流 |
| [故障排除](docs/TROUBLESHOOTING.md) | 扫描、下载、Provider、项目终端与构建问题 |
| [产品规格](docs/PRODUCT.md) | 权威产品模型与行为合同 |
| [技术架构](docs/ARCHITECTURE.md) | 组件、数据流、状态与事务边界 |
| [安全模型](docs/SECURITY.md) | 信任链、文件系统边界与残余风险 |
| [开发指南](docs/DEVELOPMENT.md) | 仓库结构、验证命令与贡献约束 |
| [路线图](docs/ROADMAP.md) | 已完成范围与后续目标 |
| [变更记录](docs/CHANGELOG.md) | 各版本测试版开发日志 |
| [发布指南](docs/RELEASING.md) | 不可跳过的发布检查清单 |

## 许可证

AutoEnvPlus 本体仅以 [`AGPL-3.0-only`](LICENSE) 发布。第三方组件不被重新许可，继续适用 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) 中列出的许可证。
