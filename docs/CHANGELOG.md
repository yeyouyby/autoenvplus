# AutoEnvPlus 变更记录

本文件记录用户可见的版本变化。格式参考 Keep a Changelog，版本遵循 SemVer。只有 Git tag 与 GitHub Release 回读成功后，才能把一个版本标记为已发布。

## 0.0.5 - 2026-10-21

功能增强与本地化完成版：镜像源全局管理、用户 PATH 编辑器与智能修复、诊断一键修复、后台持续统计，以及覆盖全部界面（含 Core 诊断消息）的双语本地化。核心引擎、CLI 与 Shim 行为不变。

### Added

- 全局镜像源管理页（导航"镜像源"）：按语言工具分组查看、编辑、重置、启停全部 Provider 镜像并支持新增自定义镜像，替代散落在各语言详情页的镜像配置。
- 用户 PATH 编辑器（PATH 页新标签）：直接管理注册表用户 PATH——逐条目展示失效/重复状态并支持单条移除；"智能修复"一键清除失效目录并去重（保留首次出现）。快照新增类型标记（EnsureFirst/RemoveDirectory/Repair），回滚列表按类型显示，全部修改先写快照可回滚。
- 诊断一键修复：诊断问题项按类型提供修复动作——用户 PATH 智能修复、Shim 重装、Shim 移到用户 PATH 最前；确认后走快照管线、写入活动日志并自动重扫。仅用户拥有的 PATH 条目提供修复建议，机器级条目不自动处理。
- 概览统计后台持续：统计采集移入常驻后台服务（OverviewMonitorService），切换页面不再中断。

### Changed

- 本地化全量抽取：全部页面、外壳导航与后台服务的中文字符串迁入 MRT Core 资源表（zh-CN/en-US，约 1600 键），统一经代码后置 `Loc` 解析；设置页语言切换对全部界面生效。未打包 WinUI 3 不能用 `x:Uid`（既不匹配点号键 PRI 布局也不跟随应用内语言覆盖），代码后置赋值是既定模式。
- Core 层诊断文案本地化：诊断问题改为"稳定 Id + 有序参数"结构（`DiagnosticIssue.Args`），诊断页按 `Diagnostics.Issue.*` 资源键渲染标题与详情，英文界面不再出现中文诊断文案；CLI 导出与 Core 测试仍使用 Core 层中文原文，行为不变。
- 产品标识与 Adoptium 目录本地化：窗口标题/版本徽标经 `Identity.*` 资源键格式化（结构化数据与显示文本分离，测试不再钉死中文字面量）；Adoptium Java 版本目录的守卫异常消息改为英文（UI 层本就走通用错误兜底，不展示原文）。

### Fixed

- 新下载的单文件 EXE 首次启动可能因安全软件扫描新文件而瞬态崩溃（XamlParseException）：MainWindow 创建现自动重试最多三次（递增延迟），重试过程写入 `%TEMP%\autoenvplus-startup-retry.log`；诊断日志写入改为 best-effort，不再因日志被阻塞而中断重试。
- 全局搜索建议与语言详情页自动化名称中残留的硬编码全角分隔符（"、"。"："）改为资源键解析，英文界面不再出现中文标点。
- 修复 NativeShim 契约测试的共享状态泄漏：等价版本用例会污染共享注册表，使后续 pip 解析用例间歇性读到错误注册表而失败（CI 与本地首跑均出现过）；每个测试环境现在使用独立临时根目录。

## 0.0.4 - 2026-10-11

单文件启动修复版：v0.0.3 的单文件 EXE 在部分机器上首次 WinUI 激活即崩溃（COMException 0x80040111，无窗口无日志），portable 与 MSI 不受影响。核心引擎、CLI 与 Shim 行为不变。

### Added

- 本地化框架：应用字符串迁入 MRT Core 资源表（`Strings/zh-CN` 与 `Strings/en-US`），代码后置经 `Loc` 解析；设置页新增"语言"区块（跟随系统/简体中文/English），选择持久化到应用设置并在下次启动经 `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride` 生效。未打包 WinUI 3 的 `x:Uid` 既不匹配点号键的 PRI 布局、也不跟随应用内语言覆盖，因此全部字符串走代码后置赋值——这是后续全量字符串抽取的既定模式。

### Fixed

- 单文件 EXE 启动崩溃（三个独立成因，均已修复并在 8.0.25 捆绑包上实测窗口正常出现）：
  - Windows App SDK 的 undocked reg-free WinRT 目录以裸文件名加载 WinUI 原生 DLL，而 .NET 8.0.27+ 的单文件宿主不再预加载解压目录中的原生 DLL，裸名搜索找不到目标导致首次激活失败（COMException 0x80040111，无窗口无日志）。现由自定义入口点在 `Application.Start` 前同时调用 `SetDllDirectoryW` 与 `AddDllDirectory`（分别覆盖传统搜索序与 `LOAD_LIBRARY_SEARCH_USER_DIRS` 调用方），把解压目录加入 DLL 搜索路径；`global.json` 改为 `rollForward: disable` 钉死 SDK 10.0.200（运行时包 8.0.25），CI 不再随镜像补丁浮动到 8.0.27；
  - csproj 设置 `DefaultLanguage`（本地化引入）会让单文件 bundle 在首次 WinUI 激活即失败，移除该属性后语言切换不受影响（实测英文偏好下 UI 正确切换）。语言回退改由 PRI 默认承担：系统语言不匹配 zh-CN/en-US 时回退英文；
  - **CI 产线单文件 EXE 必崩的真正元凶**：Windows App SDK 的 `CreateWinRTRegistration` target 把 reg-free WinRT 清单嵌进 apphost，且仅在 target 实际执行时 `PublishSingleFile` 已置位才会生成单文件形态（`loadFrom="%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%..."`，47 个文件条目）；该 target 带增量检查（Inputs/Outputs）。CI 先跑 portable 发布（无 `PublishSingleFile`）生成框架形态清单（25 个条目、无 `loadFrom`），随后的单文件发布命中增量检查直接复用坏清单——bundle 内容与本地构建逐字节一致，唯独内嵌清单缺 `loadFrom`，目录无法从解压目录解析任何 WinRT 激活，首次 `Microsoft.UI.Xaml` 激活即 0x80040111。本地从未复现是因为 obj 里残留着早前单文件构建的好清单。两个发布脚本现在都会在发布前删除生成的 `Manifests` 目录，强制按各自布局重新生成；已按 CI 顺序（portable→单文件）从干净状态复现坏清单与崩溃、并验证修复后同序列产出好清单且窗口正常。
- 早期启动失败静默退出：激活前的异常现在会写入 `%TEMP%\autoenvplus-startup-failure.log` 并弹出提示，不再无声消失。

### Changed

- 发布可追溯性：单文件发布脚本从 bundle 输入清单提取并打印捆绑的 .NET 运行时包版本；
- 启动环境诊断：`Application.Start` 前把进程路径、`AppContext.BaseDirectory`、关键 DLL/PRI 存在性、`SetDllDirectoryW`/`AddDllDirectory` 结果与 `LoadLibraryExW` 三种搜索模式实测写入 `%TEMP%\autoenvplus-startup-env.log`（冒烟测试失败时随 `autoenvplus-*.log` 一并收集）——正是这组探针把 CI 独有故障定位到"文件在盘、搜索路径已注册、加载仍 126"从而锁定内嵌清单；
- 发布门禁：GitHub Release 发布前在 CI 上实际启动签名后的单文件 EXE 与 portable App.exe 冒烟验证——v0.0.3 带病发布的直接原因就是此前没有任何门禁真正启动过产物；冒烟失败时输出窗口枚举快照（hwnd/可见性/标题）与应用日志（`%TEMP%` 与 `RUNNER_TEMP` 下的 `autoenvplus-*.log`），并接受 `MainWindowHandle` 或直接枚举到的可见窗口作为通过条件。

## 0.0.3 - 2026-10-10

产品打磨版：五页产品审计发现的全部 UI/UX 缺口与四项功能级改进。核心引擎、CLI 与 Shim 行为不变。已作为 GitHub prerelease `v0.0.3` 发布并完成签名与资产回读。

### Added

- 全局搜索：导航面板搜索框（Ctrl+F 聚焦），按名称/ID/别名/扩展名匹配 45 门语言并直达语言详情页，按标题匹配页面；
- 应用内更新检查：概览页每会话一次静默查询 GitHub 最新发布（遵循全局代理），有新版本时显示可关闭提示条与发布页链接，离线静默；
- 设置页"关于"区块：版本、许可证（AGPL-3.0）、仓库/发布/问题链接与当前受管根；
- 设置页"恢复默认目录"：清除用户级 `AUTOENVPLUS_HOME` 覆盖并提示重启（仅在覆盖存在时显示）；
- 首次运行引导：概览页无快照时提供"立即读取"按钮；
- 下载中心传输速度与剩余时间（6 秒滑动窗口测速）；
- 下载/导入重复文件名预检确认框；
- 窗口位置与尺寸跨会话持久化（断开的显示器防御性丢弃，回退 DPI 感知默认尺寸）。

### Changed

- 页面状态保留：外壳按标签缓存页面实例，表单值、筛选与滚动位置在导航往返后保留；项目页随上下文变化重建；
- 主题/材质/密度选择即时预览，保存才持久化；
- 代理输入内联校验：格式提示随输入切换，无效输入禁用保存按钮；
- 核心错误消息在 UI 边界本地化为中文（`CoreErrorText`，约 30 条规则），核心层与测试断言保持英文原文；
- 语言详情页展示安装失败的具体原因、安装后自动刷新、删除来源前确认、插件活动记录与哈希证据；
- PATH 页新增第 4 层级与后台探测；存储页取消/回滚/提示修复；CMake User Preset 回滚跨会话可用；
- 列表行按钮的屏幕阅读器名称带行上下文（如"打开 Python 语言详情"），不再每行同名。

### Fixed

- 单实例守卫：重复启动唤起已有窗口后静默退出，不再弹出阻塞对话框；
- 窗口标题栏按钮在深色主题下不可见的问题；
- 有传输任务时关闭窗口增加确认，不再无提示中止下载或 pip 安装；
- `DisplayArea.FindAll()` 的 WinRT 投影 LINQ 崩溃（启动失败日志捕获的真实缺陷）；
- 下载中心导航徽标在有任务进行时正确显示；
- pip 安装不再绑定页面生命周期，离开页面不中断安装。

## 0.0.2 - 2026-10-06

GUI 启动缺陷修复版。`v0.0.1` 的 GUI 存在启动即失败的缺陷（见下），本版本修复后重新发布。已作为 GitHub prerelease `v0.0.2` 发布并完成签名与资产回读。

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
