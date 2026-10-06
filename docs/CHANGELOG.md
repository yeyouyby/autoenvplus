# AutoEnvPlus 变更记录

本文件记录用户可见的版本变化。格式参考 Keep a Changelog，版本遵循 SemVer。只有 Git tag 与 GitHub Release 回读成功后，才能把一个版本标记为已发布。

## Unreleased

## 0.0.2 - 待发布

GUI 启动缺陷修复版。`v0.0.1` 的 GUI 存在启动即失败的缺陷（见下），本版本修复后重新发布。

### Fixed

- 修复 GUI 启动时 `MainWindow` 构造函数过早访问 `AppTitleBar.XamlRoot` 导致的 `NullReferenceException`：XamlRoot 在窗口内容接入 XAML 树之前为 null，异常从 `OnLaunched` 逃逸后被 WinUI 吞掉，留下一个无窗口的后台进程（任务管理器可见进程但界面永不出现）。现在所有 XamlRoot 相关工作推迟到 `RootSurface.Loaded` 事件执行；
- 启动失败不再静默：任何启动异常都会写入 `%TEMP%\autoenvplus-startup-failure.log`、弹出原生错误对话框并退出进程，不再产生无窗口的幽灵进程。

## 0.0.1 - 2026-10-05

首个 Windows x64 测试版本，已作为 GitHub prerelease `v0.0.1` 发布并完成签名与资产回读。

> **已知缺陷**：`v0.0.1` 的 GUI（单文件 EXE、便携包和 MSI 中的 `AutoEnvPlus.App.exe`）因 `MainWindow` 构造函数中的 `NullReferenceException` 无法显示窗口——进程存活但界面永不出现。CLI 与原生 Shim 不受影响。请改用 `v0.0.2` 或更高版本。

### Added

- `autoenvplus mcp`：stdio MCP（Model Context Protocol）服务器，把 CLI 暴露为可调用工具。六个工具：`doctor`、`list_runtimes`、`catalog`、`provider_list`、`which` 和通用 `cli`（可转发任意 CLI 命令，覆盖 install/uninstall/use/exec/tool/network/download/provider/plugin/shim/shell/storage/toolchain/project/resolve 全部功能）。每次工具调用都会启动真实 CLI 进程并返回退出码、stdout 与 stderr；协议支持 initialize/ping/tools 列表与调用、批处理与标准 JSON-RPC 错误码，并拒绝嵌套 MCP 递归。

- WinUI 3 / Fluent 工作台，包含概览、语言、项目环境、下载中心、PATH 与命令、缓存与存储、环境诊断、活动记录和设置；
- 固定“语言 -> 语言工具 -> Provider -> Provider 来源”模型；45 门内置语言、136 个工具条目、140 条工具作用域 Provider Profile 和 83 个 Provider 来源槽；
- 9 个真实受管安装适配器：CPython、Node.js、Eclipse Temurin、.NET SDK、MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja；
- 18 个无 CLR 原生 x64 Shim 命令与一次 PATH 配置；
- 新终端会话、项目、全局、自动选择的版本解析，以及成对 Runtime ID/Provider ID 精确绑定；
- 项目版本文件导入、`autoenvplus.toml`、schema 2 `autoenvplus.lock`、虚拟环境只读解析和已激活终端；
- HTTPS 单流/分段下载、本地导入、受管下载库、SHA-256/SHA-512 预期值和 WinUI wheel 安装计划；
- Provider 来源覆盖/恢复与自定义 HTTPS 源，以及独立的 HTTP(S) 代理和 `NO_PROXY`；
- pip/npm/pnpm/Yarn/NuGet/Maven/Gradle/vcpkg/Conan 等缓存发现、迁移、隔离、恢复和永久清理边界；
- 六域显式环境诊断、结构化 JSON 导出和有界脱敏活动记录；
- data-only 语言包 schema 1，以及绑定 9 个桥接工具的 Runtime Provider schema 2；旧插件 schema 1 可兼容导入并规范化升级；
- 三类 Windows x64 分发候选：WinUI self-contained single-file `AutoEnvPlus-win-x64.exe`、包含 CLI/Shim 的 `AutoEnvPlus-win-x64-portable.zip`，以及 per-user `AutoEnvPlus-win-x64.msi`；
- tag 驱动的自签名 fail-closed 预发布流程，以及每个主资产的 SHA-256 sidecar 和聚合 `SHA256SUMS.txt`；遗留 MSIX/AppInstaller 脚本保留为开发/实验工具，不属于 `v0.0.1` 三类公开资产。

### Security

- Python Windows manifest 的 Sigstore 身份、Fulcio/SCT/Rekor 和固定 trusted-root 验证；
- Node.js OpenPGP 签名 checksum 清单与固定发布密钥策略；
- Eclipse Temurin ZIP detached OpenPGP 签名与固定主指纹；
- .NET SDK release metadata SHA-512 checksum evidence，并明确不声称独立发布者签名；
- HTTPS 重定向复检、哈希算法感知缓存、安全 ZIP 解压、Zip Slip/reparse point/受管根逃逸防护与安装收据；
- 预览后输入复检、跨进程锁、原子/补偿写入、安全卸载引用扫描和精确 Provider 身份；
- 代理、Provider 来源、活动日志和下载 URL 的凭据/query 脱敏；
- 插件默认停用、严格 data-only 清单、精确 Provider 选择和第三方 checksum 信任提示；
- 发布签名材料（自签名证书 PFX 与密码）仅限 `release-signing` environment；PR/普通 CI 不签名，缺少配置、任一 PE/MSI 验签失败或三类资产不齐时禁止未签名回退发布。自签名测试证书不受 Windows 信任，用户侧显示“未知发布者”。

### Changed

- 概览与语言页固定为 snapshot-on-load；完整环境扫描和语言 PATH 重检改为用户手动触发；
- 工具链、Provider 插件和镜像配置归入对应语言详情，通用代理保留在设置页；
- 产品版本统一为 `0.0.1`，Windows 包版本为 `0.0.1.0`，发布 tag 为 `v0.0.1`。

### Known limitations

- 测试版以 Windows x64 为主；发布证书配置与首次签名 tag 回读、完整 Windows 10/11 安装升级 E2E、可访问性/缩放矩阵和 ARM64 应用尚未完成；
- WinUI single-file 可能把原生库和 PRI/XBF 自解压到 .NET bundle extraction/临时目录，不承诺绿色运行、完全无临时文件或完全无系统依赖；
- 136 个工具目录中只有 9 个真实适配器；
- 插件没有签名包、自动更新、撤销列表或组织允许策略；
- 下载中心不透明接管包管理器 resolver，wheel/WinGet 外部执行失败时不保证事务回滚；
- D 盘受管根不能沙箱化任意第三方子进程。

### 开发里程碑

| 日期 | 仓库里程碑 |
|---|---|
| 2026-07-14 | 建立 Core/CLI/WinUI/Shim 基线，加入 Python Sigstore 验证与 MSIX 打包链 |
| 2026-07-15 | 加入统一受管根、安全缓存清理、活动记录、工作台与 .NET SDK 管理 |
| 2026-07-17 | 完成语言/工具/Provider 目录、data-only 插件和项目工作台主流程，合并首个功能 PR |
| 2026-07-18 | 在工作树中准备 `v0.0.1` 版本统一、Fluent UI 收口、安全修复、文档与 GitHub Actions；仍待最终提交、CI 和发布回读 |
| 2026-07-20 | 收口 single-file、portable ZIP、per-user MSI 三类资产合同和强制签名工作流；真实签名与 GitHub prerelease 仍待回读 |
| 2026-07-21 | 发布签名从 SignPath OSS 切换为自签名证书 + signtool + RFC3161 时间戳；CLI 新增 stdio MCP 服务器 |
