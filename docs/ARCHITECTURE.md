# AutoEnvPlus 技术架构

## 组件边界

```text
WinUI App ─┐
CLI ───────┼─> Core Engine ─> Language / Tool / Provider / Resolver / Doctor
Shell Hook ┘             │           │
                         │           ├─> Built-in Catalog / Language Pack Store
                         │           ├─> Provider Source Preferences / Proxy Settings
                         │           ├─> Declarative Provider Plugin Store / Registry
                         │           ├─> Managed Install and Download Libraries
                         │           ├─> PATH / Shim / Project / Cache State
                         │           └─> Elevated Broker（按需）
                         └─> fixed child-process environment only
```

- `AutoEnvPlus.App` 只负责 Fluent 界面、交互和可视化；
- `AutoEnvPlus.Cli` 为自动化、终端和 Shell Hook 提供稳定入口；
- `AutoEnvPlus.Core` 不依赖 WinUI，保存领域模型和操作计划；
- Provider 只生成安装/卸载计划，不直接任意修改主机；
- 第三方 Provider 只从严格 JSON 数据构造，不通过反射或原生加载器执行第三方 DLL；
- Broker 只接受经过验证的有限操作，不接受任意 Shell 字符串。

`BuiltInLanguageCatalog` 从内嵌 schema 1 JSON 构造 45 门内置语言、136 个真实工具目录、140 条工具作用域 Provider Profile 和 83 个 Provider 来源槽；这些 Profile 复用 5 个 Provider ID，是目录与能力元数据，不是 140 个可执行插件。`LanguagePackStore` 把已启用 data-only 语言包合并为有效目录，并拒绝内置或跨包 ID 冲突。`LanguageVisibilityStore` 只保存用户显式启用/隐藏集合，最终可见性由 `Top 10 + 快照中的 PATH 发现 + 用户启用 - 用户隐藏` 计算。目录能力是展示与路由事实，不是任意执行许可。

`LanguageToolRuntimeBridge` 把 9 个真实受管安装适配器映射到语言工具：CPython、Node.js、Eclipse Temurin、.NET SDK、MSVC Build Tools、Clang、GCC、CMake 和 Ninja。前四个复用官方归档 Provider 与安装协调器，后五个复用固定 WinGet 白名单；已启用的声明式插件可成为同一工具的精确替代来源。其余 127 个目录工具不虚构安装适配器，只在存在真实能力时开放对应操作。

`ProviderSourcePreferenceStore` 以 `languageToolId + providerId + slotId` 保存目录来源槽覆盖和自定义源。镜像归属于精确 `toolId/providerId`：内置归档安装会把用户显式选择的 `GenericDownload` 来源按 Provider 协议传入对应构造器，声明式插件的固定 asset URI 不被镜像重写。包工具执行通过受限投影读取 pip/npm 等明确映射的 Provider 来源；通用代理是独立设置，仍由 `NetworkSettingsStore` 解析。

诊断服务是只读聚合层。调用者显式选择 `PathAndCommands`、`ManagedTools`、`ProjectEnvironment`、`ProviderConfiguration`、`StoragePressure` 和 `ProviderConnectivity` 六个域；默认不遍历项目/缓存，也不访问网络。服务可报告 PATH 与 Shim 绕过、重复工具与版本漂移、全局选择偏差、项目锁和虚拟环境健康、Provider 来源配置、缓存与磁盘压力，并把实时连接限制为最多 32 个端点和单端点超时。WinUI 进入页面不会自动扫描，只在用户确认域后执行，并可导出结构化 JSON。

WinUI 概览页是应用启动后的 Fluent 功能首页，但不再等同于一次全局扫描。`OverviewSnapshotStore` 在 `<managed-root>\state\overview-snapshot.json` 保存最后一次摘要和完整扫描时间；默认启动策略 `CachedOnly` 只读取该文件。快速刷新只并行读取受管注册表、全局选择、项目、活动、代理、下载库和插件状态；只有显式完整扫描才检查 PATH、执行版本探测并测量缓存。部分区域失败只进入快照错误集合，不阻断其他摘要。

语言页遵循相同的只读打开原则。`LanguageToolInventoryStore` 在 `<managed-root>\state\language-tool-inventory.json` 保存 PATH 发现快照；页面导航和普通状态修改只读取该快照，不运行命令发现。只有用户点击重检时，`LanguageToolInventoryScanner` 才显式扫描 PATH 并原子保存新快照；单门语言详情的重检只合并该语言工具的结果，不把打开页面变成隐式扫描。

一级导航固定为概览、语言、项目环境、下载中心、PATH 与命令、缓存与存储、环境诊断、活动记录和设置。原“运行时”“工具链”“Provider 插件”“网络”入口不再是一级页面：运行时、C/C++ 组件、Provider 插件和 Provider 来源都归入语言详情，通用代理只在设置中管理。

## 文件布局

```text
<managed-root>\runtimes
<managed-root>\runtimes\dotnet\sdk\<version>\<architecture>
<managed-root>\runtimes\<kind>\plugins\<plugin-id>\<version>\<architecture>
<managed-root>\plugins\runtime-providers\<plugin-id>.json
<managed-root>\plugins\language-packs\<pack-id>.json
<managed-root>\shims
<managed-root>\downloads\<hash-algorithm>\<package-hash>
<managed-root>\downloads\library
<managed-root>\downloads\library\.autoenvplus-staging
<managed-root>\environments\python\<environment-name>
<managed-root>\temporary\pip
<managed-root>\caches\pip
<managed-root>\state
<managed-root>\state\application-settings.json
<managed-root>\state\language-packs.json
<managed-root>\state\language-visibility.json
<managed-root>\state\language-tool-inventory.json
<managed-root>\state\provider-source-preferences.json
<managed-root>\state\overview-snapshot.json
<managed-root>\state\network-settings.json
<managed-root>\state\runtime-provider-plugins.json
<managed-root>\shell\powershell\AutoEnvPlus.PowerShell.psm1
<managed-root>\state\activity.jsonl
```

受管根按 CLI 显式 `--root`、`AUTOENVPLUS_HOME`、`%LOCALAPPDATA%\AutoEnvPlus` 的顺序解析，并统一拒绝相对路径、驱动器根与 UNC 共享根。设置页写入用户级环境变量后要求重启，不修改当前进程环境，也不隐式搬迁旧数据；这样一个进程生命周期内的 WinUI、CLI 调用、Provider 插件 Registry 和生成的 Shell 模块始终绑定同一根。把 `AUTOENVPLUS_HOME` 指向 `D:\codex\autoenvplus-data` 时，插件清单、activation state、插件安装和下载缓存也位于 D 盘；这不等于拦截运行时以后启动的第三方程序写入系统盘。

程序更新、运行时安装和用户数据彼此分离。卸载 AutoEnvPlus 时，不会在未确认的情况下删除运行时与项目锁文件。

## Shim 设计

AutoEnvPlus 只需把 `<managed-root>\shims` 加入用户 PATH 一次，之后切换工具身份不会反复改写 PATH。18 个无 CLR Win32 Shim 从当前目录向上查找 `autoenvplus.toml`，再按新终端会话、项目、全局、自动选择顺序解析；前三层都可同时约束 selector、Runtime ID 和 Provider ID。

用户 PATH 修改前的快照只能位于受管 `state\path-snapshots` 的直接子目录。核心层枚举和回滚都会重新限制文件大小、拒绝 reparse point、校验 GUID 与文件名、规范 Shim 绝对路径，并严格重建 `Before -> Shim-first After` 变换。只有当前 PATH 仍与快照 `After` 完全一致时才允许回滚；WinUI 仅消费核心层返回的状态，不自行读取 JSON 或注册表。

Shim 需要满足：

- 冷启动开销目标低于 20 ms；
- 不加载 WinUI；
- 保持参数、标准输入输出、退出码和 Ctrl+C 行为；
- 检测递归调用；
- 对解析结果进行短期缓存，并以配置文件时间戳失效；
- 产生可供 `autoenvplus which` 解释的解析轨迹。

当前原生 Shim 覆盖 18 个命令：`python`、`python3`、`pip`、`pip3`、`node`、`npm`、`npx`、`java`、`javac`、`jar`、`dotnet`、`cl`、`clang`、`clang++`、`gcc`、`g++`、`cmake`、`ninja`。它使用 `Windows.Data.Json` 结构化解析状态，校验路径始终位于受管根内，执行与 Core 相同的新终端会话、项目、全局、自动选择顺序，使用标准 Windows 参数引用，继承控制台/标准流并返回子进程退出码；递归深度上限为 4。启动受管 Java 时为子进程设置 `JAVA_HOME`；启动受管 .NET SDK 时为子进程设置精确安装根对应的 `DOTNET_ROOT` 和 `DOTNET_MULTILEVEL_LOOKUP=0`。`AUTOENVPLUS_SHIM_TRACE=1` 可输出解析轨迹。若构建输出缺少原生二进制，安装器保留发布版 `autoenvplus.exe` 或开发版 `dotnet autoenvplus.dll` 的 CMD 回退。

性能门禁采用相同受管状态与相同立即退出子进程，交替测量直接启动和经 Shim 启动，以差值排除 PowerShell 与子进程本身开销。Windows 10 22H2 x64 本机 60 次 Release 测量：直接中位 11.73 ms，Shim 中位 27.28 ms，中位额外开销 15.55 ms，达到低于 20 ms 的目标。

## PowerShell 集成

PowerShell 模块只修改当前进程的版本、`*_RUNTIME_ID` 与 `*_RUNTIME_PROVIDER_ID` 变量，由现有解析器继续执行 `新终端会话 > 项目 > 全局 > 自动选择`。模块覆盖 9 个受管工具身份，且不会修改持久全局或项目选择。模块导出：

- `Use-AutoEnvPlusRuntime <python|node|java|dotnet|msvc|llvm|mingw|cmake|ninja> <selector> [-RuntimeId <id> -ProviderId <id>]`：先设置新终端会话选择与可选精确身份，再调用 `autoenvplus which` 验证；验证失败会恢复旧值；
- `Clear-AutoEnvPlusRuntime [runtime]`：清除一个或全部会话选择；
- 导入时只在当前 PowerShell 进程中把受管 Shim 目录置于 PATH 前部，不修改用户或系统 PATH。

Profile 使用固定的开始/结束标记管理单个块。安装计划会保留块外全部内容、清理重复受管块，并在写入前检查 Profile 与模块是否仍等于预览版本。实际写入顺序为模块原子写入、快照原子写入、Profile 原子替换。回滚只接受 `<managed-root>\state\powershell-profile-snapshots` 内、ID 与文件名一致的快照；Profile 出现较新修改时拒绝覆盖。

项目已激活终端是独立流程：服务读取最近的 `autoenvplus.toml`，同时解析 `[tools]` selector 与 `[tool-identities]` 中成对的 Runtime ID/Provider ID，从托管注册表预解析为精确条目，验证注册可执行文件和对应 Shim，再生成只读启动计划。WinUI 可选择 Windows Terminal 或 Windows PowerShell；选择 Windows Terminal 时只生成固定的 `wt.exe new-tab --startingDirectory <project-root> <powershell.exe> -NoLogo -NoExit` 参数，`wt.exe` 不可用则计划回退到 Windows PowerShell。CLI 默认请求 Windows PowerShell。WinUI/CLI 展示计划后，启动前重新计算 manifest SHA-256、重新加载注册表并比较宿主、参数和环境覆盖，任何变化都会拒绝旧计划。实际启动使用 `CreateProcessW` 和独立 Unicode 环境块；直接 PowerShell 使用 `CREATE_NEW_CONSOLE`，Windows Terminal 则继承同一个已审核环境块并在固定项目根打开 PowerShell 子 Shell。精确身份只写入新子进程环境，父进程、项目文件、全局 profile 与持久环境变量均不改变。

`ProjectVirtualEnvironmentDiscoveryService` 是独立于版本导入和终端启动的只读发现层。它只探测一组固定的项目相对候选路径，不做递归目录遍历，不调用任何外部命令，并拒绝项目根祖先或候选路径中的重解析点。默认最多检查 96 个不同路径、读取单个配置文件最多 256 KiB、返回最多 64 项；配置以有界 UTF-8 读取，JSON 设置最大解析深度，证据列表也有固定上限。路径/结果达到上限、配置损坏或过大、入口缺失和安全拒绝都会作为结构化健康状态或扫描警告返回，而不是猜测环境可用。

发现器识别 Python `pyvenv.cfg` 的 `home`、`version`、`include-system-site-packages` 与 `Scripts/python.exe`，并结合 Poetry/Pipenv 项目标记或 Conda `conda-meta/history`；Node.js 结合 `node_modules/.bin`、包管理器锁和 `package.json` Corepack 证据；其他固定探测器覆盖 `.config/dotnet-tools.json`、Maven/Gradle Wrapper、`rust-toolchain(.toml)`/`target` 与 `go.work`。结果按语言、类型、根和管理器稳定排序。WinUI 仅在用户选中项目并点击“解析虚拟环境”后把扫描工作放到后台线程；切换项目或离开页面会取消旧扫描，首页不会触发该服务。

项目同时选中 Python 或 Node.js 时，终端计划读取并校验同一受管根中的代理与 Provider 来源状态，把 pip/npm 的有效来源分别映射为 `PIP_INDEX_URL` / `NPM_CONFIG_REGISTRY`。终端只能有一组共享 `HTTP_PROXY`、`HTTPS_PROXY` 和 `NO_PROXY`：只有 Python 时使用 pip 作用域，只有 Node.js 时使用 npm 作用域；两者代理相同则使用该共同值，两者不同时明确警告并使用 `downloads` 作用域作为共享代理，精确工具/Provider 的来源仍分别保留。被有效配置禁用的变量会列入环境移除集合，避免无意继承父进程旧值。计划携带代理与 Provider 来源文件路径及 SHA-256；启动前会重新加载并比较摘要、覆盖和移除集合，任一设置变化都会使已审核计划失效。没有 Python/Node.js 选择时不为项目终端注入包工具网络设置。

## Provider 来源与代理执行接入

`ProviderSourcePreferenceStore` 是镜像与包源的权威状态，以 `languageToolId + providerId + slotId` 绑定具体工具、Provider 和来源槽。`NetworkSettingsStore` 则只在 `<managed-root>\state\network-settings.json` 读写 schema 1 代理兼容配置，并以同路径门闩串行化。两个 Store 都使用同目录临时文件、`WriteThrough` 和原子替换；未知 schema、未知工具或 Provider、重复大小写键、无效 override 或畸形 URI 都安全失败，不把部分文档当成默认配置继续运行。

通用 HTTP/HTTPS 代理与 `NO_PROXY` 只在设置中管理；`NetworkSettingsResolver` 仍为既有执行入口解析全局代理和兼容的工具作用域覆盖，但不再充当跨 Provider 镜像模型。代理只接受绝对 HTTP/HTTPS URI，Provider 来源只接受绝对 HTTPS URI；两者都拒绝 user-info、query 和 fragment，因此配置文件不能成为凭据或签名 query 的明文存储。模型和 `ToString()` 只报告“configured/disabled”和数量，不输出端点。WinUI/CLI 的显示层仍再次去除 user-info、query 和 fragment，活动记录则执行通用敏感字段脱敏。

`NetworkHttpClientFactory` 为显式 HTTP 操作创建独立 `HttpClientHandler`，按 scheme 选择代理并应用 host、端口、wildcard 或 CIDR `NO_PROXY`。代理策略不从 URI 提取凭据，也不拥有用户名/密码存储；它把 `CredentialCache.DefaultCredentials` 交给 Windows 网络栈，以便支持代理的集成身份认证。`ToolNetworkEnvironment` 只修改新建子进程的环境字典，先清理大小写两套 HTTP/HTTPS/NO_PROXY 变量并移除未建模的 `ALL_PROXY`，再写入有效值；pip 使用 `PIP_INDEX_URL`，npm/pnpm 使用 `NPM_CONFIG_REGISTRY`，Yarn 同时使用 `YARN_NPM_REGISTRY_SERVER` 与 npm registry 兼容变量。它不写用户级工具配置，子进程的代理认证能力由工具自身决定。

当前调用链显式选择代理作用域：WinUI 语言详情与 CLI `catalog`/`install` 对 Python、Node.js、Java、.NET 使用 `runtime-python`、`runtime-node`、`runtime-java` 或 `runtime-dotnet` 兼容代理，声明式 MSVC/LLVM/MinGW/CMake/Ninja 使用 `runtime-cpp`；下载中心使用 `downloads`。CLI `tool`、wheel 联网模式和项目终端另从精确 `toolId/providerId` 读取 Provider 来源，并投影给 pip/npm 等明确支持的工具。其他已建模来源在没有专用执行器前不会自动影响普通终端程序。CLI `network show` 仍是代理兼容层的只读观察面，不是镜像编辑页面。

Provider 来源参数的含义不是统一“替换域名”：Python 接收 API base，Node.js 接收 distribution base，Adoptium 接收 API base，.NET 接收完整 release-index URI。下载中心的 URL 是用户显式资产地址，不受 Provider 来源重写。声明式插件的 asset URL 同样保持权威，只复用所属类别的代理与 `NO_PROXY`。因此模型没有跨 Provider 的全局镜像；83 个来源槽各自保留所属 `toolId/providerId` 的协议与路径语义。签名要求不会因来源变化而关闭；但 .NET 自定义 index 仍只有该 index 提供的 SHA-512 checksum evidence，没有独立发布者签名，不能描述为经过 Microsoft 身份验证。

## 受管下载与本地 wheel

运行时安装缓存和用户下载库是两个独立子系统。`ResumableHttpDownloader` 为 Provider 已知哈希的运行时资产提供 `<managed-root>\downloads\sha256|sha512\<hash>` 断点续传缓存；`ManagedSegmentedDownloader` 则把用户显式 URL 的完成文件提交到 `<managed-root>\downloads\library`，不会把未知内容放进 Provider 哈希缓存，也不会透明接管 pip 的网络栈。

分段下载的状态机为：

```text
HTTPS URL
  -> HEAD（允许 405/501）
  -> GET bytes=0-0 探测 Range、总长度、强 ETag 或 Last-Modified
  -> [实体可稳定绑定] N 个互斥 byte range + If-Range -> 逐段复检 -> 顺序组装
  -> [Range 不支持 / 无稳定实体 / 实体变化] 单流重新获取
  -> 大小上限 -> 内容 SHA-256 -> 可选 SHA-256/SHA-512 预期值 -> 原子提交 -> 清单
```

请求固定 `Accept-Encoding: identity`，每段要求 HTTP 206、精确 `Content-Range`、预期长度与相同实体标识。强 ETag 优先；没有强 ETag 时使用 Last-Modified 与总长度。探测或分段中的 200 响应、实体标识变化会转为记录原因的单流重新获取；畸形 206、错误长度和其他协议违例直接失败，避免把不同实体拼接。单流仍在响应头和读取循环中执行大小上限。连接数只允许 1/2/4/8/16，并限制到不超过总字节数；这是一项传输策略，不是速度承诺。

每个操作只在库内 `.autoenvplus-staging\<operation-id>` 创建分段、组装或复制文件。取消传播到所有分段并进入本次暂存清理路径；IO/权限导致的清理失败是尽力而为，残留不会逃出受管 staging。提交前再次检查目标、清单和取消状态。`LocalPackageImportService` 对来源普通文件做有界复制并在复制后验证长度/哈希，把副本提交到库而不是保留任意外部路径引用。库只接受白名单包/归档扩展、Windows 安全文件名和根目录直接子普通文件，拒绝 reparse point 与路径逃逸。清单保存来源的脱敏 scheme/host/path 或本地源文件名，不保存 URL query、凭据或任意本地源绝对路径；HTTP 失败显示也只保留状态或 `transport failure`，不透传可能含签名 query 的异常文本。网络传输阶段只持有目标级锁；最终“目标检查 → 旧文件隔离 → 新文件移动 → 原子清单 → 清理”以及删除操作按 `target lock -> <library>\.autoenvplus-library.lock` 的统一顺序持有跨进程锁。清单写入失败会移走新文件并恢复旧文件；恢复也失败时保留 staging recovery evidence。

`PackageHashExpectationValidator` 总是计算内容 SHA-256 作为稳定内容 ID；只有提供预期 `PackageHashAlgorithm` 与合法长度哈希时才额外计算/比较并记录 `ExpectedHash` 和 `VerifiedHash`。因此“有 ContentSha256”不等同于“已通过受信哈希验证”。下载或导入不会触发自动执行。列举库时，仅对有预期哈希证据的条目重新计算当前内容身份；即使替换文件长度相同，也会保留“提交时曾记录”的历史事实但把当前证据标为 stale，不再显示为已验证。pip 计划再次捕获 wheel SHA-256，并要求它与下载库的当前记录身份一致。

`PipLocalPackageInstallService` 只接受下载库顶层 `.whl` 和托管注册表中的 Python。计划固定 `<managed-root>\environments\python\<name>`、`temporary\pip` 与 `caches\pip`，以参数列表生成 `python -m venv` 和环境 Python 的 `-m pip install`，不接受任意命令字符串。严格离线模式增加 `--no-index --find-links <library> --no-deps`，只安装当前 wheel，不解析或安装依赖；联网模式才应用 `pip` 有效代理和 `PIP_INDEX_URL` 并解析依赖。两种模式都用 `PIP_CONFIG_FILE=NUL` 禁止继承用户 pip 配置，并清除未审核的额外索引、代理和 `ALL_PROXY`。WinUI 先展示完整计划，再由服务对计划完整性 SHA-256、运行时/wheel/已有环境可执行文件的路径、长度、时间与内容 SHA-256 复检；环境新建后、pip 安装前再复检关键输入。

pip 子进程收到受管 `TEMP`、`TMP`、`PIP_CACHE_DIR`、禁用版本检查和非交互标志，取消会终止进程树。stdout 与 stderr 被持续并行 drain，防止输出管道阻塞；每路内存只保留末尾 65,536 字符，并把独立截断标志传播到结果和 WinUI 提示。但是 venv 创建与 pip 安装不是事务协议：失败或取消后环境可能包含部分修改，不自动删除或伪装成回滚成功；联网依赖与第三方构建/安装逻辑仍可能无视这些目录变量并写到 C 盘或其他位置。

## 修改事务

可事务化的 AutoEnvPlus 持久配置修改遵循同一个协议：

```text
Plan -> Preview -> Snapshot -> Apply -> Validate -> Commit
                                  └──── failure ───> Rollback
```

操作计划使用白名单步骤，例如下载、验证哈希、解压到临时目录、原子重命名、写用户环境变量、广播 `WM_SETTINGCHANGE`。不把拼接后的命令行当作通用事务步骤。pip、WinGet 和终端等外部进程仍要求固定参数计划、预览、确认和执行前复检，但不会因经过计划就声称具备文件级事务回滚。

运行时下载缓存按 Provider 声明的算法和包哈希稳定寻址，当前支持 `<managed-root>\downloads\sha256\<hash>` 与 `sha512\<hash>`。未完成下载保留 `.partial` 与 URI、ETag、Last-Modified、总长度元数据；重试只在服务器确认同一实体时使用 Range/If-Range，服务器忽略范围或返回无效 Content-Range 时从零重新下载。完成文件仍需通过 Provider 给出的 SHA-256 或 SHA-512 才能进入解压阶段。安装计划还必须携带至少一条 HTTPS 来源、算法和值均匹配的 checksum 证据，且证据值必须覆盖待安装资产哈希。签名清单模式必须证明“实际被签名的内容 URI”等于提供包哈希的证据 URI：OpenPGP cleartext 清单的签名 URI 本身就是该内容，Sigstore 则分别记录 manifest URI 与 `.sigstore` bundle URI。detached 包签名模式要求签名对象与包文件名一致，并在内容哈希通过后直接验证同一个缓存包字节。要求签名的两种模式都不能降级到 checksum-only；显式声明 `ChecksumEvidence` 的 .NET Provider 不冒充签名验证。

WinUI 安装确认页按证据链展示包文件、下载 URI、目标目录、实际哈希算法、每条校验对象和值及其 HTTPS 来源。Python 展示精确 release-manager 邮件、OIDC Issuer、叶证书 SHA-256/SKI、Rekor index/tree/log ID、trusted-root SHA-256、身份策略、manifest 与 bundle URI；Node.js 展示已验证的 OpenPGP 清单签名、完整主指纹、实际签名 Key ID、活跃/历史状态、签名 URI 和固定公钥来源。Temurin 展示安装时强制执行的 detached 包签名、固定主指纹、签名 URI 和公钥来源。.NET SDK 展示 Microsoft channel metadata URI、Windows ZIP 和 SHA-512，并明确显示当前没有独立数字签名证据。

卸载先扫描全局选择、已知项目 `autoenvplus.toml` 和 `autoenvplus.lock`。创建计划时的扫描只用于预览；执行会取得运行时事务锁、重新加载注册表并重新扫描引用，无引用时才将目录同卷移动到 `.trash`，再更新安装注册表。注册表失败或移动后的取消会移回原目录；取消回滚失败时报告明确恢复错误，不删除证据。单个运行时卸载不会删除可能共享的包下载缓存。

运行时安装由协调器串联“归档安装、托管注册表登记、可选全局默认写入”。协调器先验证计划与注册表条目的 Provider、版本、架构、目标目录、可执行文件相对路径、包哈希算法和值完全一致。安装根中的 `.autoenvplus-install.json` 收据还绑定这些身份、包文件名、预期入口相对路径与入口 `.exe` SHA-256；只有收据完全匹配且入口内容重新哈希通过时才返回 `AlreadyInstalled`。缺收据、摘要变化、入口篡改或安装根/缓存/staging/祖先出现 reparse point 时安全失败。登记或全局配置失败时恢复原注册表条目和原全局 Profile；只有本次新建且状态已恢复的安装目录才会自动清理，既有安装不会因重新登记失败被删除。

托管注册表、schema 2 全局 profile、安装、卸载、运行时双文件解析快照和“设为全局默认”都遵循 `managed-runtime-install-state.lock -> registry/profile lock` 的固定跨进程顺序，同一路径的不同 Store 实例还共享进程内 gate。`ManagedGlobalRuntimeSelectionService` 在同一事务内重载注册表、解析 selector、复核用户确认时看到的精确 entry、检查入口是受管根内普通文件，再把 selector、Runtime ID 和 Provider ID 一并写入全局 profile；因此卸载不能夹在检查与保存之间制造悬空全局选择。项目层用 `[tool-identities]` 保存同样的成对身份，新终端会话层用 `*_RUNTIME_ID` 与 `*_RUNTIME_PROVIDER_ID` 环境变量临时覆盖，三个层级都不会把同版本的另一个 Provider 当作等价替代。

托管安装注册表当前写入 schema 2，以 `packageHashAlgorithm` 和 `packageHash` 保存 SHA-256/SHA-512 身份。Core 与原生 Shim 仍接受 schema 1，并把旧 `packageSha256` 明确解释为 SHA-256；下一次正常写入会按 schema 2 序列化。注册表限制为 4 MiB/4,096 条，全局 profile 限制为 256 KiB/64 个选择；Core 写入和原生 Shim 读取执行相同边界，Shim 的项目 manifest 另有 256 KiB 上限。未知算法、错误长度、未来 schema、重复 RuntimeId、同 Provider/类别/架构的等价版本 precedence 和受管根之外的安装路径均安全失败。`autoenvplus.lock` 同样写入 schema 2 的算法和值；读取旧 schema 1 锁时会把 `packageSha256` 迁移为内存中的 SHA-256 身份。锁文件缺字段、算法/长度不匹配或 schema 不受支持时，引用扫描会安全阻止普通卸载，直到用户重新生成锁或显式强制处理。

存储迁移共用逐文件 SHA-256 复制验证和原目录保留策略。pip、npm、Yarn Classic、NuGet、Gradle、vcpkg 二进制缓存和 Conan 2 Home 在提交阶段通过受限的用户环境变量存储切换。NuGet 的全局包、HTTP 下载缓存和插件缓存分别使用 `NUGET_PACKAGES`、`NUGET_HTTP_CACHE_PATH` 和 `NUGET_PLUGINS_CACHE_PATH`，避免只迁移全局包后下载缓存仍写入 `%LOCALAPPDATA%\NuGet`。vcpkg 使用 `VCPKG_DEFAULT_BINARY_CACHE`，默认 `%LOCALAPPDATA%\vcpkg\archives`，Conan 使用 `CONAN_HOME`，默认 `%USERPROFILE%\.conan2`。Maven 使用禁用 DTD 和外部实体解析的结构化 XML 读写器更新用户 `.m2\settings.xml` 中唯一的 `localRepository`，并保留其他节点。pnpm 使用 Windows 官方全局配置 `%LOCALAPPDATA%\pnpm\config\rc` 中唯一的 `store-dir`，保留注释与其他键。配置切换前会检查其仍等于预览版本并保存受管快照；回滚只恢复配置，不自动删除迁移副本。快照绑定缓存 ID、白名单变量或授权的 Maven/pnpm 配置路径，防止篡改后修改其他配置。

纯缓存清理采用单独的两阶段协议。计划与执行都拒绝根目录、任意祖先重解析点、受管根重叠和包含保护系统目录的目标；变更期间通过不共享删除权限的 Win32 目录句柄锁定从卷根到源/隔离目录的每个路径组件，防止检查后把祖先替换为 junction。第一阶段把经过指纹复检的顶层项同卷移动到源目录旁的 `.autoenvplus-cache-trash\<transaction-id>\content`，并以原子 manifest 记录状态。恢复要求隔离清单和源目录仍可无覆盖合并；永久清空在锁定全部已枚举目录后逐文件删除并逐层非递归移除空目录，不调用递归删除。Gradle 与 Conan 根包含非缓存用户状态，不进入清理白名单。

活动记录使用 `<managed-root>\state\activity.jsonl`。每条记录带固定 schema、UTC 时间、操作类型、终态、脱敏摘要、受影响路径和可选快照/回滚路径。写入以同路径进程内门闩和 `FileShare.None` 锁文件串行化，重新读取有界记录后原子替换；损坏行只被跳过，超大文件安全失败。WinUI 活动页只展示和复制摘要，绝不把日志中的路径当作可执行命令或自动回滚入口。

## Provider 契约

Provider 描述版本搜索、安装计划和真实性要求。每个 Release 必须记录 Provider ID、类别、版本、架构、通道、来源、哈希算法与值、校验来源证据、命令入口和许可证信息；需要发布者签名的内置 Provider 还必须携带签名证据或待执行签名要求。Python Provider 对 Windows manifest 执行固定身份与固定 trusted-root 的 Sigstore 验证，并把实际签名内容 URI 与 bundle URI 分开记录；Node Provider 使用 Bouncy Castle 验证 cleartext OpenPGP 签名，密钥文件固定到 `nodejs/release-keys` 提交，完整主指纹与签名子钥 Key ID 映射固定在源码；信任快照之后只接受活跃钥，历史钥只能验证更早版本。Temurin Provider 固定 Adoptium 完整主指纹，安装器在下载后流式验证 API 指定的 detached 签名。.NET Provider 读取 Microsoft `releases-index.json` 和每个通道的 `releases.json`，只选择 active/maintenance 支持阶段的稳定 SDK Windows ZIP，并使用元数据给出的 SHA-512 checksum evidence。详细策略见 `docs/SECURITY.md`。

首批 Provider：

- Python：优先受管理的独立目录安装，同时识别 Python Launcher 与官方安装器；
- Node.js：官方 ZIP 适合无管理员、多版本并存；
- Java：先从 Adoptium HTTPS 官方目录选择 GA/LTS 版本线，再选择该版本线的精确 Temurin ZIP，并记录 vendor；
- .NET SDK：从 Microsoft 官方 release metadata 读取最多四条 active/maintenance 通道，选择稳定 x64/x86/ARM64 Windows ZIP，并安装到版本与架构隔离目录；
- MSVC：调用 Visual Studio Installer，AutoEnvPlus 自身不重新分发 MSVC。

### 声明式插件 Provider

`RuntimeProviderPluginManifestParser` 把 schema 2 JSON 规范化为内存模型。公开 schema 用 `languageToolId` 精确支持 `cpython`、`nodejs`、`eclipse-temurin`、`dotnet-sdk`、`msvc-build-tools`、`clang`、`gcc`、`cmake` 和 `ninja`；这些是具有已注册受管 ZIP 桥接的工具 ID。解析器仍兼容导入旧 schema 1：它把 `runtimeKind` 映射到精确工具 ID，随后只以 schema 2 保存；schema 2 清单若携带旧 `runtimeKind` 会被拒绝。根对象只包含 ID、显示元数据、`languageToolId` 和静态 releases；每个 x86/x64/ARM64 asset 只包含 HTTPS ZIP、必填 checksum 来源、恰好一个 SHA-256/SHA-512、可选 archive root 和预期 `.exe` 相对路径。未知字段、重复属性、超过 512 KiB/256 releases/每 release 8 assets 的清单、非 HTTPS 或带凭据/query/fragment 的 URI、路径逃逸和非 `.exe` 入口均被拒绝。

schema 没有 DLL、脚本、命令、注册表/环境写入、安装参数、安装后钩子或目标路径字段。`DeclarativeRuntimeCatalogProvider` 只能把已解析数据转换为 `ChecksumEvidence` 安装资产，并把目标固定为 `<managed-root>\runtimes\<kind>\plugins\<plugin-id>\<version>\<architecture>`。它复用通用下载、哈希、ZIP 上限、Zip Slip/reparse point 防护与原子提交，但不会把插件提供的 checksum 描述为官方签名。

`RuntimeProviderPluginStore` 把规范化清单原子写入 `<managed-root>\plugins\runtime-providers\<id>.json`，把启用集合独立写入 `state\runtime-provider-plugins.json`，并用进程内门闩与受管 lock 文件串行化。导入默认停用；只有显式启用的合法插件能进入 `RuntimeProviderPluginRegistry`。损坏的已启用插件或 activation state 会让 Registry fail closed。删除先把清单移入插件根内 quarantine，再原子更新启用状态；状态更新失败时恢复清单，最终清理失败则报告隔离副本待清理。

schema 2（以及兼容导入的 schema 1）的 `checksumSourceUri` 是插件作者声明的人工核对引用；运行时安装器不会抓取或解析该 URL。安装器只强制比较实际下载字节与插件 JSON 中的 SHA-256/SHA-512，因此 UI/CLI 对插件只能显示“声明的 checksum 引用”，不能显示成已经从该 HTTPS 来源验证了清单或发布者身份。

内置官方 Provider 始终是默认推荐。WinUI 和 CLI 的目录/安装调用携带精确 Provider ID；`plugin:<id>` 未启用、类别不匹配或版本/架构不存在时直接失败，不回退到同类别的内置 Provider 或另一个插件。Provider ID 进入托管注册表和项目锁，插件运行时 ID 使用 `plugin-<kind>-<plugin-id>-<version>-<arch>`，因此插件删除后以同一 ID 重导入另一类别不会覆盖旧记录。相同类别、版本、架构由多个来源提供时目标目录不冲突，WinUI 不自动把新安装改成全局默认。停用或删除只影响后续目录加载和新安装，不删除已经登记的运行时或共享包缓存。

C/C++ 的声明式插件只扩展便携 ZIP 来源；现有固定 WinGet ID、Visual Studio/MSVC 发现、Windows SDK 组合和 Host/Target 激活流程继续存在。插件不能声明 `vcvarsall.bat` 或任意激活脚本，因此一个 MSVC 插件入口只证明 ZIP 内预期 `cl.exe` 存在，不自动构成完整开发者命令行环境。详细作者契约、CLI 和信任边界见 [Provider 插件指南](PROVIDER-PLUGINS.md)，机器可读契约见 [JSON Schema](../schemas/runtime-provider-plugin.schema.json)，占位模板见 [示例清单](../examples/runtime-provider-plugin.template.json)。

MSVC 发现读取每个实例的默认工具版本，并检查 `VC\Tools\MSVC\<version>\bin\Host<arch>\<target>\cl.exe`，只暴露实际存在的 Host/Target 组合。终端激活计划使用 `cmd.exe /d /k call "<vcvarsall.bat>" <pair>`，GUI 与 CLI 均先展示实例、Host、Target 和完整参数；只有显式确认后才启动外部终端。

C/C++ 外部组件安装只允许固定 WinGet ID，并始终先展示计划：MSVC Build Tools、LLVM、CMake、Ninja，以及 `BrechtSanders.WinLibs.POSIX.UCRT` 提供的 MinGW-w64/GCC。该 WinLibs 变体使用 POSIX 线程模型和 Windows 10 可用的 UCRT；官方 WinGet portable 清单暴露 `gcc`、`g++`、`gdb` 等命令别名，安装后由同一 PATH 发现器重新检测。AutoEnvPlus 不接收用户提供的包 ID、安装器 URL 或任意 override 参数。WinUI 用页面级非阻塞锁保证确认、安装和复检期间只有一个 WinGet 操作；取消或页面卸载会触发现有安装器终止 WinGet 进程树。

C/C++ 语言详情重检时使用只读的“当前进程 PATH + 最新用户 PATH + 最新系统 PATH”合并视图，保留当前进程的命令优先级并去重，再补入 WinGet 刚写入但 GUI 尚未继承的目录。通用 PATH 诊断仍只报告当前进程的真实解析结果；该刷新不会修改当前进程或主机环境变量。

项目级 CMake 集成只写 `CMakeUserPresets.json`，不改团队共享的 `CMakePresets.json`。AutoEnvPlus 管理名字稳定的 Configure/Build Preset，写入 Visual Studio generator、目标架构、Host toolset、生成目录和 `CMAKE_GENERATOR_INSTANCE`；其他根属性和用户 presets 原样保留。预览后会检查文件未变化，写入前保存受管快照并原子替换；快照绑定项目根与固定文件名，回滚遇到较新改动时拒绝覆盖。

## Win10/Win11 兼容

应用使用 WinUI 3 和 Windows App SDK，目标平台为 Windows 10 1809 以上。窗口背景不按操作系统版本号硬编码，而是读取高对比度与“透明效果”设置，并调用 Windows App SDK 的材料能力检测：高对比度优先纯色，关闭透明效果时使用纯色，否则依次尝试 Mica、Desktop Acrylic，材料不可用或初始化失败时使用主题纯色。启用材料时根视觉层透明，纯色回退时恢复 `ApplicationPageBackgroundThemeBrush`。

系统设置变化优先使用 WinRT 事件；无包身份的 Windows 10 可能拒绝 `AccessibilitySettings.HighContrastChanged` 订阅，此时由 WinUI `ActualThemeChanged` 和两秒低频设置轮询补位。设置页只读显示当前实际选择及回退原因。Windows 10 22H2 build 19045 已通过隐藏启动与 UI Automation 验证，透明效果开启时实际选择 Desktop Acrylic；Windows 11 的 Mica 路径仍需在真实 Windows 11 主机上完成端到端验证。功能层不得依赖仅 Windows 11 存在的 API，使用新 API 前必须做能力检测。

便携发布脚本生成 x64 unpackaged 自包含布局：GUI 携带 .NET 与 Windows App SDK，`cli` 子目录携带单文件自包含 CLI 和原生 Shim，并生成逐文件及 ZIP SHA-256。WinUI GUI 取自 RID 自包含 Build 布局，并强制检查 PRI 与 XBF；当前 Windows App SDK 的 `dotnet publish -o` 会漏掉这些 XAML 资源，不能直接作为可运行布局。

MSIX 发布层复用该布局，在隔离 staging 中加入 Windows 10 build 17763 完整信任桌面清单、Fluent 资产、开始菜单注册和 CLI 执行别名。包名、Publisher、四段版本、发布 URI 和 AppInstaller URI 都是受验证参数。生产模式要求证书 Subject 精确匹配 Publisher，使用 SHA-256/RFC 3161 可选时间戳签名，并在输出前执行 CMS、Authenticode、解包身份和更新元数据交叉验证；开发模式使用 30 天临时证书且不修改信任库，只生成供本机开发测试的测试签名 MSIX。AppInstaller XML 由同一参数生成，更新安装最终由目标 MSIX 的相同 Publisher 代码签名授权。正式渠道仍需真实发布者证书、Windows 10/11 安装升级 E2E 与 WinGet 清单。
