# AutoEnvPlus 功能清单

本文描述 `v0.0.1` 的真实功能边界。状态含义：

- **受管**：AutoEnvPlus 有可执行的内置适配器并管理安装状态；
- **发现/配置**：能够检查、展示或投影配置，但不代表可以安装；
- **外部**：由 WinGet、pip 或其他外部进程执行，AutoEnvPlus 只能计划、约束和记录其边界；
- **计划中**：当前版本没有实现。

## 目录与执行能力

| 目录项 | 数量 | 含义 |
|---|---:|---|
| 内置语言 | 45 | 稳定的导航与配置容器；默认启用 Top 10 |
| 语言工具 | 136 | 编译器、解释器、运行时、SDK、包管理器、构建、调试、格式化和 lint 等真实条目 |
| Provider Profile | 140 | 工具作用域的分发方式、来源和能力元数据；复用 5 个 Provider ID |
| Provider 来源槽 | 83 | 归属于 `languageToolId + providerId + slotId` 的端点槽 |
| 受管安装适配器 | 9 | 唯一可以宣称存在安装桥接的工具集合 |

140 条 Provider Profile **不是 140 个插件或安装器**。136 个工具条目中只有下表 9 项具有真实受管安装适配器，其余 127 项只提供其已经实现的发现、链接、来源配置或外部工具边界。

## 九个真实受管适配器

| 工具 ID | 用户名称 | 内置方式 | 主要完整性/身份证据 | 插件 ZIP 桥接 |
|---|---|---|---|---|
| `cpython` | CPython | 官方归档 | Sigstore 签名 Windows manifest + SHA-256 | 是 |
| `nodejs` | Node.js | 官方归档 | OpenPGP 签名 checksum 清单 + SHA-256 | 是 |
| `eclipse-temurin` | Eclipse Temurin | 官方归档 | SHA-256 + 包本体 OpenPGP detached signature | 是 |
| `dotnet-sdk` | .NET SDK | 官方归档 | Microsoft release metadata 中的 SHA-512；无独立包签名结论 | 是 |
| `msvc-build-tools` | MSVC Build Tools | 固定 WinGet 白名单 | 固定 Package ID；执行边界属于 WinGet | 仅受限 ZIP/入口，不等于完整 workload 激活 |
| `clang` | Clang/LLVM | 固定 WinGet 白名单 | 固定 Package ID；执行边界属于 WinGet | 是 |
| `gcc` | GCC/WinLibs | 固定 WinGet 白名单 | 固定 Package ID；执行边界属于 WinGet | 是 |
| `cmake` | CMake | 固定 WinGet 白名单 | 固定 Package ID；执行边界属于 WinGet | 是 |
| `ninja` | Ninja | 固定 WinGet 白名单 | 固定 Package ID；执行边界属于 WinGet | 是 |

声明式 Runtime Provider 插件只为上述 9 个工具增加 data-only HTTPS ZIP 目录，不会创造第 10 个执行适配器，也不能声明脚本、DLL、任意命令、安装钩子、注册表写入或自定义目标目录。

## 1. 语言与目录

| 功能 | 状态 | 说明 |
|---|---|---|
| 45 门内置语言 | 已实现 | Top 10：Python、JavaScript、TypeScript、Java、C、C++、C#、Go、Rust、PHP |
| 搜索、筛选、启用和隐藏 | 已实现 | 用户显式状态持久化；不会因搜索自动改写 |
| 快照加载 | 已实现 | 语言页只读 `language-tool-inventory.json`；PATH 重检需手动触发 |
| 五区语言详情 | 已实现 | 概览、工具与版本、Provider 来源、项目环境、高级设置 |
| 语言包 schema 1 | 已实现 | 严格 data-only；导入默认停用；不能覆盖现有 ID |
| 工具能力矩阵 | 已实现 | 区分发现、安装、切换、项目固定、包管理、虚拟环境、缓存、镜像等能力 |
| 签名语言目录更新 | 计划中 | 当前内置目录随应用发布，不从远端自动更新 |

## 2. 运行时、工具链与命令

| 功能 | 状态 | 说明 |
|---|---|---|
| Python/Node.js/Temurin/.NET 归档安装 | 受管 | 下载、验证、安全解压、收据、注册和隔离目录 |
| MSVC/Clang/GCC/CMake/Ninja 安装 | 外部受控 | 仅允许固定 WinGet Package ID，显示计划并记录边界 |
| PATH 运行时发现 | 已实现 | Python、Node.js、Java、.NET 与 C/C++ 工具链 |
| 原生 x64 Shim | 已实现 | 不加载 CLR；覆盖 18 个命令；构建缺失时才使用 CMD 回退 |
| 选择优先级 | 已实现 | 新终端会话 -> 项目 -> 全局 -> 自动选择 |
| 精确工具身份 | 已实现 | 会话、项目和全局均可保存 selector + Runtime ID + Provider ID |
| 全局选择 | 已实现 | 与托管注册表在统一锁序中复检并原子写入 |
| 安全卸载 | 已实现 | 扫描全局、项目和锁文件引用，隔离目录并补偿注册表更新 |
| Windows SDK 独立安装 | 未实现 | 可以发现；不宣称存在单独的受管安装适配器 |

18 个 Shim 命令是 `python`、`python3`、`pip`、`pip3`、`node`、`npm`、`npx`、`java`、`javac`、`jar`、`dotnet`、`cl`、`clang`、`clang++`、`gcc`、`g++`、`cmake` 和 `ninja`。

## 3. 项目环境

| 功能 | 状态 | 说明 |
|---|---|---|
| 版本文件导入 | 已实现 | `.python-version`、`.nvmrc`、`.node-version`、`.java-version`、`package.json`、`global.json` |
| 项目清单 | 已实现 | 读取/更新 `autoenvplus.toml`，保留未知分区和注释，写入前复核 |
| 项目锁 | 已实现 | schema 2 保存精确版本、架构、Provider、哈希算法和值 |
| 虚拟环境解析 | 已实现 | Python、Node、.NET、Maven/Gradle、Rust、Go；只读、不递归执行项目命令 |
| 已激活终端 | 已实现 | Windows Terminal/PowerShell 计划、精确身份、独立环境块和启动前复检 |
| CMake User Presets | 已实现 | 预览、合并、快照、并发修改拒绝和回滚 |
| 工作区自动激活 | 计划中 | 当前不会在进入目录时自动执行项目 hook |

## 4. 下载、Provider 来源与网络

| 功能 | 状态 | 说明 |
|---|---|---|
| HTTPS 下载 | 已实现 | 初始 URL 和重定向均复检 HTTPS；日志与清单移除凭据/query/fragment |
| 分段下载 | 已实现 | 1/2/4/8/16 连接；要求长度、Range 和稳定实体标识，否则有原因地单流降级 |
| 本地文件导入 | 已实现 | 复制到受管 staging，复检普通文件、大小和内容身份 |
| 下载库 | 已实现 | 原子清单、跨进程锁、安全覆盖/删除、当前内容重新哈希 |
| 预期哈希 | 已实现 | SHA-256 或 SHA-512；未提供预期值时只记录内容 SHA-256，不声称可信 |
| wheel 安装计划 | WinUI 已实现 | 受管 Python/venv/TEMP/cache；严格离线或显式联网；失败不事务回滚 |
| 通用代理 | 已实现 | HTTP、HTTPS、`NO_PROXY`；不保存独立用户名/密码 |
| Provider 来源 | 已实现 | 内置槽覆盖/恢复、具名自定义 HTTPS 源启停/删除，精确归属工具和 Provider |
| CLI 来源确认 | 已实现 | 内置归档安装使用精确来源槽；非默认来源要求 `--accept-non-default-source` |
| 透明接管 pip resolver | 未实现 | 下载中心处理显式 URL/本地文件，不编排 pip 的内部依赖下载 |

## 5. 存储、诊断与审计

| 功能 | 状态 | 说明 |
|---|---|---|
| 缓存发现与统计 | 已实现 | pip、npm、pnpm、Yarn、NuGet、Maven、Gradle、vcpkg、Conan 等 |
| 事务迁移 | 已实现 | 预览、配置快照、并发修改复检、回滚 |
| 纯缓存清理 | 已实现 | 同卷隔离 -> 恢复或二次确认永久清空 |
| 概览快照 | 已实现 | 快速刷新只读取受管状态；完整扫描由用户手动触发 |
| 六域环境诊断 | 已实现 | PATH/命令、托管工具、项目、Provider、存储/磁盘、实时连接 |
| JSON 报告 | 已实现 | WinUI/CLI 共用结构化诊断模型；敏感 URI 脱敏 |
| 活动记录 | 已实现 | 跨进程锁、大小/条目/保留期上限、筛选和复制摘要 |

## 6. Fluent UI 与扩展

| 功能 | 状态 | 说明 |
|---|---|---|
| WinUI 3 导航 | 已实现 | 概览、语言、项目、下载、PATH、存储、诊断、活动、设置 |
| Fluent 材质 | 已实现 | Windows 11 优先 Mica，Windows 10 Desktop Acrylic；高对比度/关闭透明时纯色回退 |
| 主题、密度与启动页 | 已实现 | 设置持久化；不会提供可关闭安全确认的伪开关 |
| Runtime Provider schema 2 | 已实现 | `languageToolId` 精确绑定 9 个桥接工具；兼容导入 schema 1 `runtimeKind` |
| 插件生命周期 | 已实现 | 严格解析、预览、默认停用、显式启用、精确选择、停用/删除保留现有运行时 |
| 插件代码加载 | 不支持 | data-only 设计明确拒绝 DLL、脚本、命令和安装钩子 |

## 7. Windows x64 分发

| 功能 | 状态 | 说明 |
|---|---|---|
| WinUI single-file | 构建已实现 | `AutoEnvPlus-win-x64.exe` 是 GUI 主程序，不是 CLI；self-contained bundle 可能在启动时自解压原生库与 PRI/XBF。 |
| 完整便携包 | 构建已实现 | `AutoEnvPlus-win-x64-portable.zip` 保留普通 WinUI EXE + DLL 布局，并包含 CLI 与原生 Shim。 |
| per-user MSI | 构建已实现 | `AutoEnvPlus-win-x64.msi` 安装到当前用户，支持开始菜单、卸载和 major upgrade；卸载不删除用户数据或受管工具。 |
| SignPath 发布门禁 | 工作流已定义 | tag 工作流要求三类资产全部通过 SignPath/Authenticode 验证；缺少外部配置或签名失败时不发布。实际签名和 GitHub prerelease 仍需外部服务及 tag 回读证明。 |

## 当前限制

- `v0.0.1` 是 x64 测试版本，不是生产就绪版本；SignPath OSS 外部审批/配置、首次签名 tag 回读、Windows 10/11 完整安装升级 E2E、可访问性与视觉矩阵尚未完成。
- 目录包含 136 个工具，但只有 9 个受管适配器；不能根据目录条目推断可安装性。
- .NET 官方 Provider 和自定义 .NET index 当前只有元数据 checksum evidence，没有独立发布者签名结论。
- 声明式插件没有签名包、自动更新、撤销列表或组织允许策略；启用意味着用户信任清单作者和其选择的资产。
- 下载中心不会透明接管包管理器 resolver；wheel 联网安装与 WinGet 都属于外部进程边界，失败或取消可能留下部分状态。
- 多连接是协议允许时的传输策略，不保证比单流更快。
- D 盘受管根不是文件系统沙箱，不能约束任意第三方子进程的写盘行为。
- C/C++ 的插件 ZIP 入口不能替代 Visual Studio workload、Windows SDK 和完整 `vcvars` 环境。
- ARM64 应用构建、签名的目录/插件分发和团队策略仍在路线图中。

更细的产品合同见 [产品规格](PRODUCT.md)，安全残余风险见 [安全模型](SECURITY.md)，后续目标见 [路线图](ROADMAP.md)。
