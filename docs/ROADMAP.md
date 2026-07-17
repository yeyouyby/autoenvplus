# AutoEnvPlus 路线图

## 原型阶段（当前）

- [x] “语言 → 语言工具 → Provider → Provider 来源”领域模型，以及名为“语言”的一级导航；
- [x] 45 门内置语言、136 个真实工具目录、140 条工具作用域 Provider Profile、83 个 Provider 来源槽和显式能力矩阵；140 条 Profile 不是 140 个可执行插件；
- [x] Top 10 + 本机发现默认可见、用户启用/隐藏持久化，以及数据-only 语言包导入/启停/删除；
- [x] 每语言五分区详情，以及 4 个官方归档与 5 个固定 WinGet 组成的 9 个真实受管安装适配器；已启用第三方 Provider 必须精确选择；
- [x] 以 `languageToolId + providerId + slotId` 所有的默认来源覆盖/恢复和自定义 HTTPS 源生命周期；镜像属于具体工具/Provider，通用代理只在设置；
- [x] 概览与语言工具清单持久快照；两个页面打开时均只读快照，概览完整扫描与语言 PATH 重检均须显式触发；
- [x] Python/Node/.NET/Java/Rust/Go 项目虚拟环境按需只读解析；
- [x] 六域环境诊断，以及 PATH 一次配置 Shim 后的精确新终端会话/项目/全局命令路由页与 18 个 Shim 命令；
- [x] 启动、扫描、语言、下载、代理、Shell、安全、日志、主题、材质和密度设置；
- [x] 产品边界与版本解析优先级；
- [x] WinUI 3、Core、CLI、Tests 解决方案结构；
- [x] 版本选择与 PATH 检查的第一组领域实现；
- [x] PATH 运行时发现器与版本输出解析；
- [x] `autoenvplus.toml` 向上定位、`[tools]` selector 与 `[tool-identities]` 成对 Runtime ID/Provider ID 的读取、更新和校验；
- [x] `.python-version`、`.nvmrc`、`.node-version`、`.java-version`、`package.json`、`global.json` 导入；
- [x] 精确版本、架构、Provider、包哈希算法与值的 schema 2 `autoenvplus.lock`；
- [x] PATH 快照/回滚与缓存迁移事务；
- [x] Node.js 官方发行目录、SHA-256 资产解析与安全 ZIP 安装器；
- [x] Adoptium 官方 Java GA/LTS 版本线动态选择、Temurin 资产解析与 ZIP 安装计划；
- [x] python.org Sigstore-signed Windows manifest 与 PythonCore ZIP 安装计划；
- [x] Fluent 概览首页，以及“概览 / 语言 / 项目环境 / 下载中心 / PATH 与命令 / 缓存与存储 / 环境诊断 / 活动记录 / 设置”一级导航；运行时、工具链、Provider 插件和网络能力归入语言详情或设置；
- [x] 数据-only Runtime Provider 插件 schema 2 契约、JSON Schema/模板、受管插件仓库和默认停用生命周期；schema 2 使用 `languageToolId`，导入器兼容 schema 1 `runtimeKind` 并规范化升级；

## MVP 0.1

- [x] Python、Node.js、Java 发现与用户级托管安装；
- [x] Microsoft 官方 release metadata、Windows x64/x86/ARM64 ZIP、SHA-512 校验和隔离目录的 .NET SDK 托管安装；
- [x] selector + Runtime ID + Provider ID 的精确全局选择、项目 `[tool-identities]` 和新终端会话环境选择，以及 `exec`；
- [x] 预览、并发重检与子进程隔离的项目已激活终端，WinUI 支持 Windows Terminal/PowerShell 选择并在 `wt.exe` 不可用时回退；
- [x] .NET 项目精确激活、`dotnet` Shim、`DOTNET_ROOT` 与多级查找隔离；
- [x] CMD Shim 原型；
- [x] PowerShell 会话模块、受管 Profile 块、预览、快照与回滚；
- [x] 覆盖 18 个命令的无 CLR Win32 原生 Shim、派生命令、递归保护与 CMD 回退；
- [x] PATH 编辑预览、受信快照枚举、WinUI 状态展示和执行时复检回滚；
- [x] pip/npm 与 NuGet 全局包、HTTP、插件缓存统计和迁移；
- [x] Maven `settings.xml` 与 Gradle User Home 事务迁移、快照和回滚；
- [x] pnpm `store-dir` 与 Yarn Classic 缓存事务迁移；
- [x] PATH、命令冲突和运行时健康诊断；
- [x] WinUI/CLI 聚合诊断页、结构化报告与 WinUI JSON 导出；
- [x] 统一 WinUI 工作台：概览只读快照、语言中心、PATH、缓存/磁盘、最近项目与最近活动摘要；
- [x] `--root` / `AUTOENVPLUS_HOME` / 默认目录统一解析与非系统盘设置；
- [x] 纯缓存同卷隔离、可恢复清理和二次确认永久清空；
- [x] 有界脱敏活动记录、跨进程写入锁与 WinUI 筛选/复制页面；
- [x] 下载与归档 SHA-256/SHA-512 算法感知校验与分区缓存；
- [x] HTTP Range、ETag/Last-Modified、If-Range 断点续传；
- [x] 引用扫描、隔离目录和注册表回滚的安全卸载；
- [x] 安装目录、托管注册表和全局选择的补偿式事务，以及跨进程统一锁序、执行时引用重扫和原子全局选择；
- [x] 绑定 Provider/包身份/入口 SHA-256 的安装收据与重复安装篡改复检；
- [x] WinUI 安装预览中的逐条哈希算法/来源证据与签名状态提示；
- [x] 托管注册表 schema 2 的哈希算法/值持久化，以及 Core/原生 Shim 对 schema 1 `packageSha256` 的读取兼容；
- [x] Node.js clear-signed checksum 清单的 OpenPGP 验证与固定发布密钥策略；
- [x] Eclipse Temurin 包本体 detached OpenPGP 验证与固定 Adoptium 主指纹；
- [x] Python Windows manifest 的 Sigstore v0.3、固定 release-manager 身份、Fulcio/SCT/Rekor 与内置 trusted-root 验证；
- [x] 设置中的通用代理与 `NO_PROXY`、精确 `languageToolId + providerId + slotId` Provider 来源模型、受管状态文件、URI 凭据/query 拒绝与脱敏展示；
- [x] WinUI/CLI 语言详情 Provider、CLI 受管 pip/npm 命令、下载中心和项目终端的代理/Provider 来源接入与执行前复检；
- [x] HTTPS URL 的 1/2/4/8/16 连接分段探测、实体一致性、单流降级、进度、取消与大小上限；
- [x] 受管下载库、本地包导入、内容 SHA-256 标识、可选 SHA-256/SHA-512 预期值验证、跨进程库事务锁、当前内容复检和安全删除；
- [x] 下载库 wheel 到受管 Python venv 的 WinUI 计划、预览、输入复检、受管 TEMP/cache、严格离线 `--no-deps`、有界输出和非事务结果提示；
- [x] `cpython`、`nodejs`、`eclipse-temurin`、`dotnet-sdk`、`msvc-build-tools`、`clang`、`gcc`、`cmake`、`ninja` 九个 `languageToolId` 的声明式 ZIP Provider，以及 WinUI/CLI 导入、启停、精确 Provider 目录/安装选择；
- [x] 插件 Provider ID/安装路径隔离、第三方 checksum 信任提示、停用/删除保留既有运行时和损坏启用状态 fail-closed；
- [x] 插件 RuntimeId/ProviderId 纳入精确工具身份，删除后重导入不覆盖既有注册项；完整 exact build metadata 匹配与等价 precedence 注册冲突 fail-closed；

## 0.2

- [ ] Yarn Berry；
- [ ] 可导入/导出的“环境方案”：语言启用集、Provider pin、精确工具版本、源策略、缓存策略与项目锁；
- [ ] 项目模板与工作区自动激活规则；
- [ ] 签名的语言目录更新、EOL/漏洞/撤销元数据与可解释升级建议；
- [ ] 扩展 Tool Provider 插件能力：在现有 schema 2 `languageToolId` 精确绑定上增加受限的发现、来源、缓存、环境与诊断声明，并逐步减少内部 9 项 `RuntimeKind` 适配桥接；
- [ ] Language Pack v2：包版本、应用兼容范围、依赖、更新通道、发布者摘要与语言详情来源追踪；
- [ ] 团队环境基线和漂移时间线：只读比较、修复预览、可回滚变更集；
- [ ] 离线环境包：经过哈希证据验证的工具归档、包文件、锁文件和源元数据导出/导入；
- [ ] 沙箱化的外部 Provider Host，用受限 RPC 扩展更多安装协议而不把第三方 DLL 加载进主进程；
- [ ] 团队网络/镜像策略与锁文件；
- [ ] 签名插件包、可审计 Provider 更新源、撤销列表与组织级允许列表；当前 schema 2 是用户手动导入的静态 JSON，schema 1 仅保留兼容导入；
- [ ] 精确全局、项目与新终端会话选择的团队策略、组织允许列表和跨设备迁移；本地 selector + Runtime ID + Provider ID pin 已完成；
- [ ] 可审计的 pip/npm/Maven resolver 下载任务编排、依赖图和批量分段传输；当前下载中心是显式 URL/导入工作流，不透明接管包管理器下载。

## 0.3

- [x] Visual Studio/Build Tools、Windows SDK、MSVC 发现与 x64/x86 Host/Target 激活终端；
- [x] LLVM/Clang、CMake、Ninja PATH 发现；
- [x] 项目级 MSVC `CMakeUserPresets.json` 预览、快照与回滚；
- [x] MinGW-w64/GCC PATH 发现、WinLibs POSIX/UCRT 白名单安装与 WinGet 单任务取消；
- [x] MSVC、LLVM、MinGW、CMake、Ninja 的便携 ZIP 插件来源与固定 WinGet 安装路径并存；插件不获得任意安装或激活脚本能力；
- [x] vcpkg 二进制缓存与 Conan 2 Home 的发现、事务迁移和回滚；
- [ ] vcpkg/Conan 项目依赖与锁文件集成；
- [ ] x64/x86/ARM64 完整目标环境；当前应用发布与 E2E 仍以 x64 为主。

## 1.0 质量门槛

- [x] x64 WinUI + 单文件 CLI + 原生 Shim 自包含便携发布与 SHA-256 清单；
- [x] 参数化 MSIX/AppInstaller、仅供测试的开发签名、生产证书 fail-closed 与包身份/更新 URI 验证；
- [x] Mica / Desktop Acrylic / 高对比度与透明效果纯色回退的能力检测和 Windows 10 22H2 隐藏 UI 验证；
- [ ] Windows 10 22H2 与受支持 Windows 11 构建的端到端测试；Windows 11 真实主机 E2E 尚未完成；
- [ ] 中断、磁盘不足、权限失败与网络失败的恢复测试；
- [ ] 真实 CDN、认证代理、限流、实体切换和大文件场景下的分段/降级端到端矩阵；
- [ ] 可访问性、高对比度和完整键盘导航；
- [ ] 真实发布者证书与可信时间戳、MSIX 安装/升级/回滚 E2E 和 WinGet 发布；
- [ ] ARM64 应用构建与运行时管理。
