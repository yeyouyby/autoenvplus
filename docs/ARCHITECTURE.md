# AutoEnvPlus 技术架构

## 组件边界

```text
WinUI App ─┐
CLI ───────┼─> Core Engine ─> Provider / Resolver / PATH / Cache / Doctor
Shell Hook ┘                         │
                                    ├─> State Store
                                    ├─> Download Store
                                    └─> Elevated Broker（按需）
```

- `AutoEnvPlus.App` 只负责 Fluent 界面、交互和可视化；
- `AutoEnvPlus.Cli` 为自动化、终端和 Shell Hook 提供稳定入口；
- `AutoEnvPlus.Core` 不依赖 WinUI，保存领域模型和操作计划；
- Provider 只生成安装/卸载计划，不直接任意修改主机；
- Broker 只接受经过验证的有限操作，不接受任意 Shell 字符串。

诊断服务是只读聚合层：一次扫描复用 PATH 分析、命令候选解析、运行时版本探测、托管注册表和全局 Profile 解析，生成 GUI 与 CLI 共用的结构化问题、命令赢家、运行时状态和全局选择结果。探测有五秒超时且支持取消，不通过诊断路径写入任何主机状态。WinUI 只在用户通过系统文件选择器确认目标后，才把当前内存报告连同 schema version 导出为 JSON。

WinUI 概览页是另一个只读聚合层。它并行读取托管注册表、全局 Profile、PATH 运行时、PATH 健康报告、已知项目、缓存测量和活动记录，分别呈现部分错误而不阻断其他摘要。运行时摘要覆盖 Python、Node.js、Java 和 .NET SDK；最近项目通过 `MainWindow.NavigateTo(tag, context)` 把项目根交给项目页，程序化导航同时同步 `NavigationView` 选中项。

## 文件布局

```text
<managed-root>\runtimes
<managed-root>\runtimes\dotnet\sdk\<version>\<architecture>
<managed-root>\shims
<managed-root>\downloads\<hash-algorithm>\<package-hash>
<managed-root>\state
<managed-root>\shell\powershell\AutoEnvPlus.PowerShell.psm1
<managed-root>\state\activity.jsonl
```

受管根按 CLI 显式 `--root`、`AUTOENVPLUS_HOME`、`%LOCALAPPDATA%\AutoEnvPlus` 的顺序解析，并统一拒绝相对路径、驱动器根与 UNC 共享根。设置页写入用户级环境变量后要求重启，不修改当前进程环境，也不隐式搬迁旧数据；这样一个进程生命周期内的 WinUI、CLI 调用和生成的 Shell 模块始终绑定同一根。

程序更新、运行时安装和用户数据彼此分离。卸载 AutoEnvPlus 时，不会在未确认的情况下删除运行时与项目锁文件。

## Shim 设计

AutoEnvPlus 只需把 `<managed-root>\shims` 加入用户 PATH 一次。`python.exe`、`node.exe`、`java.exe`、`dotnet.exe` 等无 CLR Win32 Shim 从当前目录向上查找 `autoenvplus.toml`，再按会话、项目、全局顺序解析实际运行时。

用户 PATH 修改前的快照只能位于受管 `state\path-snapshots` 的直接子目录。核心层枚举和回滚都会重新限制文件大小、拒绝 reparse point、校验 GUID 与文件名、规范 Shim 绝对路径，并严格重建 `Before -> Shim-first After` 变换。只有当前 PATH 仍与快照 `After` 完全一致时才允许回滚；WinUI 仅消费核心层返回的状态，不自行读取 JSON 或注册表。

Shim 需要满足：

- 冷启动开销目标低于 20 ms；
- 不加载 WinUI；
- 保持参数、标准输入输出、退出码和 Ctrl+C 行为；
- 检测递归调用；
- 对解析结果进行短期缓存，并以配置文件时间戳失效；
- 产生可供 `autoenvplus which` 解释的解析轨迹。

当前原生 Shim 覆盖 `python`/`python3`、`pip`/`pip3`、`node`、`npm`/`npx`、`java`、`javac`、`jar` 和 `dotnet`。它使用 `Windows.Data.Json` 结构化解析状态，校验路径始终位于受管根内，执行与 Core 相同的会话、项目、全局、自动选择顺序，使用标准 Windows 参数引用，继承控制台/标准流并返回子进程退出码；递归深度上限为 4。启动受管 Java 时为子进程设置 `JAVA_HOME`；启动受管 .NET SDK 时为子进程设置精确安装根对应的 `DOTNET_ROOT` 和 `DOTNET_MULTILEVEL_LOOKUP=0`。`AUTOENVPLUS_SHIM_TRACE=1` 可输出解析轨迹。若构建输出缺少原生二进制，安装器保留发布版 `autoenvplus.exe` 或开发版 `dotnet autoenvplus.dll` 的 CMD 回退。

性能门禁采用相同受管状态与相同立即退出子进程，交替测量直接启动和经 Shim 启动，以差值排除 PowerShell 与子进程本身开销。Windows 10 22H2 x64 本机 60 次 Release 测量：直接中位 11.73 ms，Shim 中位 27.28 ms，中位额外开销 15.55 ms，达到低于 20 ms 的目标。

## PowerShell 集成

PowerShell 模块只修改当前进程的 `AUTOENVPLUS_PYTHON_VERSION`、`AUTOENVPLUS_NODE_VERSION`、`AUTOENVPLUS_JAVA_VERSION` 和 `AUTOENVPLUS_DOTNET_VERSION`，由现有解析器继续执行 `会话 > 项目 > 全局 > 自动选择`。模块导出：

- `Use-AutoEnvPlusRuntime <python|node|java|dotnet> <selector>`：先设置会话选择，再调用 `autoenvplus which` 验证；验证失败会恢复旧值；
- `Clear-AutoEnvPlusRuntime [runtime]`：清除一个或全部会话选择；
- 导入时只在当前 PowerShell 进程中把受管 Shim 目录置于 PATH 前部，不修改用户或系统 PATH。

Profile 使用固定的开始/结束标记管理单个块。安装计划会保留块外全部内容、清理重复受管块，并在写入前检查 Profile 与模块是否仍等于预览版本。实际写入顺序为模块原子写入、快照原子写入、Profile 原子替换。回滚只接受 `<managed-root>\state\powershell-profile-snapshots` 内、ID 与文件名一致的快照；Profile 出现较新修改时拒绝覆盖。

项目已激活终端是独立流程：服务读取最近的 `autoenvplus.toml`，从托管注册表把 Python/Node.js/Java/.NET 选择器预解析为精确版本，验证注册可执行文件和对应 Shim，再生成只读启动计划。WinUI 可选择 Windows Terminal 或 Windows PowerShell；选择 Windows Terminal 时只生成固定的 `wt.exe new-tab --startingDirectory <project-root> <powershell.exe> -NoLogo -NoExit` 参数，`wt.exe` 不可用则计划回退到 Windows PowerShell。CLI 默认请求 Windows PowerShell。WinUI/CLI 展示计划后，启动前重新计算 manifest SHA-256、重新加载注册表并比较宿主、参数和环境覆盖，任何变化都会拒绝旧计划。实际启动使用 `CreateProcessW` 和独立 Unicode 环境块；直接 PowerShell 使用 `CREATE_NEW_CONSOLE`，Windows Terminal 则继承同一个已审核环境块并在固定项目根打开 PowerShell 子 Shell。父进程与持久环境变量均不改变。

## 修改事务

所有主机修改由同一个事务引擎执行：

```text
Plan -> Preview -> Snapshot -> Apply -> Validate -> Commit
                                  └──── failure ───> Rollback
```

操作计划使用白名单步骤，例如下载、验证哈希、解压到临时目录、原子重命名、写用户环境变量、广播 `WM_SETTINGCHANGE`。不把拼接后的命令行当作通用事务步骤。

下载缓存按 Provider 声明的算法和包哈希稳定寻址，当前支持 `<managed-root>\downloads\sha256\<hash>` 与 `sha512\<hash>`。未完成下载保留 `.partial` 与 URI、ETag、Last-Modified、总长度元数据；重试只在服务器确认同一实体时使用 Range/If-Range，服务器忽略范围或返回无效 Content-Range 时从零重新下载。完成文件仍需通过 Provider 给出的 SHA-256 或 SHA-512 才能进入解压阶段。安装计划还必须携带至少一条 HTTPS 来源、算法和值均匹配的 checksum 证据，且证据值必须覆盖待安装资产哈希。签名清单模式必须证明“实际被签名的内容 URI”等于提供包哈希的证据 URI：OpenPGP cleartext 清单的签名 URI 本身就是该内容，Sigstore 则分别记录 manifest URI 与 `.sigstore` bundle URI。detached 包签名模式要求签名对象与包文件名一致，并在内容哈希通过后直接验证同一个缓存包字节。要求签名的两种模式都不能降级到 checksum-only；显式声明 `ChecksumEvidence` 的 .NET Provider 不冒充签名验证。

WinUI 安装确认页按证据链展示包文件、下载 URI、目标目录、实际哈希算法、每条校验对象和值及其 HTTPS 来源。Python 展示精确 release-manager 邮件、OIDC Issuer、叶证书 SHA-256/SKI、Rekor index/tree/log ID、trusted-root SHA-256、身份策略、manifest 与 bundle URI；Node.js 展示已验证的 OpenPGP 清单签名、完整主指纹、实际签名 Key ID、活跃/历史状态、签名 URI 和固定公钥来源。Temurin 展示安装时强制执行的 detached 包签名、固定主指纹、签名 URI 和公钥来源。.NET SDK 展示 Microsoft channel metadata URI、Windows ZIP 和 SHA-512，并明确显示当前没有独立数字签名证据。

卸载先扫描全局选择、已知项目 `autoenvplus.toml` 和 `autoenvplus.lock`。无引用时将目录同卷移动到 `.trash`，再更新安装注册表；注册表失败则移回原目录。单个运行时卸载不会删除可能共享的包下载缓存。

运行时安装由协调器串联“归档安装、托管注册表登记、可选全局默认写入”。协调器先验证计划与注册表条目的 Provider、版本、架构、目标目录、可执行文件相对路径、包哈希算法和值完全一致。登记或全局配置失败时恢复原注册表条目和原全局 Profile；只有本次新建且状态已恢复的安装目录才会自动清理，既有安装不会因重新登记失败被删除。

托管安装注册表当前写入 schema 2，以 `packageHashAlgorithm` 和 `packageHash` 保存 SHA-256/SHA-512 身份。Core 与原生 Shim 仍接受 schema 1，并把旧 `packageSha256` 明确解释为 SHA-256；下一次正常写入会按 schema 2 序列化。未知算法、错误长度、未来 schema 和受管根之外的安装路径均安全失败。`autoenvplus.lock` 同样写入 schema 2 的算法和值；读取旧 schema 1 锁时会把 `packageSha256` 迁移为内存中的 SHA-256 身份。锁文件缺字段、算法/长度不匹配或 schema 不受支持时，引用扫描会安全阻止普通卸载，直到用户重新生成锁或显式强制处理。

存储迁移共用逐文件 SHA-256 复制验证和原目录保留策略。pip、npm、Yarn Classic、NuGet、Gradle、vcpkg 二进制缓存和 Conan 2 Home 在提交阶段通过受限的用户环境变量存储切换。NuGet 的全局包、HTTP 下载缓存和插件缓存分别使用 `NUGET_PACKAGES`、`NUGET_HTTP_CACHE_PATH` 和 `NUGET_PLUGINS_CACHE_PATH`，避免只迁移全局包后下载缓存仍写入 `%LOCALAPPDATA%\NuGet`。vcpkg 使用 `VCPKG_DEFAULT_BINARY_CACHE`，默认 `%LOCALAPPDATA%\vcpkg\archives`，Conan 使用 `CONAN_HOME`，默认 `%USERPROFILE%\.conan2`。Maven 使用禁用 DTD 和外部实体解析的结构化 XML 读写器更新用户 `.m2\settings.xml` 中唯一的 `localRepository`，并保留其他节点。pnpm 使用 Windows 官方全局配置 `%LOCALAPPDATA%\pnpm\config\rc` 中唯一的 `store-dir`，保留注释与其他键。配置切换前会检查其仍等于预览版本并保存受管快照；回滚只恢复配置，不自动删除迁移副本。快照绑定缓存 ID、白名单变量或授权的 Maven/pnpm 配置路径，防止篡改后修改其他配置。

纯缓存清理采用单独的两阶段协议。计划与执行都拒绝根目录、任意祖先重解析点、受管根重叠和包含保护系统目录的目标；变更期间通过不共享删除权限的 Win32 目录句柄锁定从卷根到源/隔离目录的每个路径组件，防止检查后把祖先替换为 junction。第一阶段把经过指纹复检的顶层项同卷移动到源目录旁的 `.autoenvplus-cache-trash\<transaction-id>\content`，并以原子 manifest 记录状态。恢复要求隔离清单和源目录仍可无覆盖合并；永久清空在锁定全部已枚举目录后逐文件删除并逐层非递归移除空目录，不调用递归删除。Gradle 与 Conan 根包含非缓存用户状态，不进入清理白名单。

活动记录使用 `<managed-root>\state\activity.jsonl`。每条记录带固定 schema、UTC 时间、操作类型、终态、脱敏摘要、受影响路径和可选快照/回滚路径。写入以同路径进程内门闩和 `FileShare.None` 锁文件串行化，重新读取有界记录后原子替换；损坏行只被跳过，超大文件安全失败。WinUI 活动页只展示和复制摘要，绝不把日志中的路径当作可执行命令或自动回滚入口。

## Provider 契约

Provider 描述版本搜索、已有安装检测、安装计划和健康检查。每个 Release 必须记录版本、架构、通道、官方来源、哈希算法与值、校验来源证据、签名证据或待执行签名要求、真实性要求、命令入口和许可证信息。Python Provider 对 Windows manifest 执行固定身份与固定 trusted-root 的 Sigstore 验证，并把实际签名内容 URI 与 bundle URI 分开记录；Node Provider 使用 Bouncy Castle 验证 cleartext OpenPGP 签名，密钥文件固定到 `nodejs/release-keys` 提交，完整主指纹与签名子钥 Key ID 映射固定在源码；信任快照之后只接受活跃钥，历史钥只能验证更早版本。Temurin Provider 固定 Adoptium 完整主指纹，安装器在下载后流式验证 API 指定的 detached 签名。.NET Provider 读取 Microsoft `releases-index.json` 和每个通道的 `releases.json`，只选择 active/maintenance 支持阶段的稳定 SDK Windows ZIP，并使用元数据给出的 SHA-512 checksum evidence。详细策略见 `docs/SECURITY.md`。

首批 Provider：

- Python：优先受管理的独立目录安装，同时识别 Python Launcher 与官方安装器；
- Node.js：官方 ZIP 适合无管理员、多版本并存；
- Java：先从 Adoptium HTTPS 官方目录选择 GA/LTS 版本线，再选择该版本线的精确 Temurin ZIP，并记录 vendor；
- .NET SDK：从 Microsoft 官方 release metadata 读取最多四条 active/maintenance 通道，选择稳定 x64/x86/ARM64 Windows ZIP，并安装到版本与架构隔离目录；
- MSVC：调用 Visual Studio Installer，AutoEnvPlus 自身不重新分发 MSVC。

MSVC 发现读取每个实例的默认工具版本，并检查 `VC\Tools\MSVC\<version>\bin\Host<arch>\<target>\cl.exe`，只暴露实际存在的 Host/Target 组合。终端激活计划使用 `cmd.exe /d /k call "<vcvarsall.bat>" <pair>`，GUI 与 CLI 均先展示实例、Host、Target 和完整参数；只有显式确认后才启动外部终端。

C/C++ 外部组件安装只允许固定 WinGet ID，并始终先展示计划：MSVC Build Tools、LLVM、CMake、Ninja，以及 `BrechtSanders.WinLibs.POSIX.UCRT` 提供的 MinGW-w64/GCC。该 WinLibs 变体使用 POSIX 线程模型和 Windows 10 可用的 UCRT；官方 WinGet portable 清单暴露 `gcc`、`g++`、`gdb` 等命令别名，安装后由同一 PATH 发现器重新检测。AutoEnvPlus 不接收用户提供的包 ID、安装器 URL 或任意 override 参数。WinUI 用页面级非阻塞锁保证确认、安装和复检期间只有一个 WinGet 操作；取消或页面卸载会触发现有安装器终止 WinGet 进程树。

工具链页重检时使用只读的“当前进程 PATH + 最新用户 PATH + 最新系统 PATH”合并视图，保留当前进程的命令优先级并去重，再补入 WinGet 刚写入但 GUI 尚未继承的目录。通用 PATH 诊断仍只报告当前进程的真实解析结果；该刷新不会修改当前进程或主机环境变量。

项目级 CMake 集成只写 `CMakeUserPresets.json`，不改团队共享的 `CMakePresets.json`。AutoEnvPlus 管理名字稳定的 Configure/Build Preset，写入 Visual Studio generator、目标架构、Host toolset、生成目录和 `CMAKE_GENERATOR_INSTANCE`；其他根属性和用户 presets 原样保留。预览后会检查文件未变化，写入前保存受管快照并原子替换；快照绑定项目根与固定文件名，回滚遇到较新改动时拒绝覆盖。

## Win10/Win11 兼容

应用使用 WinUI 3 和 Windows App SDK，目标平台为 Windows 10 1809 以上。窗口背景不按操作系统版本号硬编码，而是读取高对比度与“透明效果”设置，并调用 Windows App SDK 的材料能力检测：高对比度优先纯色，关闭透明效果时使用纯色，否则依次尝试 Mica、Desktop Acrylic，材料不可用或初始化失败时使用主题纯色。启用材料时根视觉层透明，纯色回退时恢复 `ApplicationPageBackgroundThemeBrush`。

系统设置变化优先使用 WinRT 事件；无包身份的 Windows 10 可能拒绝 `AccessibilitySettings.HighContrastChanged` 订阅，此时由 WinUI `ActualThemeChanged` 和两秒低频设置轮询补位。设置页只读显示当前实际选择及回退原因。Windows 10 22H2 build 19045 已通过隐藏启动与 UI Automation 验证，透明效果开启时实际选择 Desktop Acrylic；Windows 11 的 Mica 路径仍需在真实 Windows 11 主机上完成端到端验证。功能层不得依赖仅 Windows 11 存在的 API，使用新 API 前必须做能力检测。

便携发布脚本生成 x64 unpackaged 自包含布局：GUI 携带 .NET 与 Windows App SDK，`cli` 子目录携带单文件自包含 CLI 和原生 Shim，并生成逐文件及 ZIP SHA-256。WinUI GUI 取自 RID 自包含 Build 布局，并强制检查 PRI 与 XBF；当前 Windows App SDK 的 `dotnet publish -o` 会漏掉这些 XAML 资源，不能直接作为可运行布局。

MSIX 发布层复用该布局，在隔离 staging 中加入 Windows 10 build 17763 完整信任桌面清单、Fluent 资产、开始菜单注册和 CLI 执行别名。包名、Publisher、四段版本、发布 URI 和 AppInstaller URI 都是受验证参数。生产模式要求证书 Subject 精确匹配 Publisher，使用 SHA-256/RFC 3161 可选时间戳签名，并在输出前执行 CMS、Authenticode、解包身份和更新元数据交叉验证；开发模式使用 30 天临时证书且不修改信任库，只生成供本机开发测试的测试签名 MSIX。AppInstaller XML 由同一参数生成，更新安装最终由目标 MSIX 的相同 Publisher 代码签名授权。正式渠道仍需真实发布者证书、Windows 10/11 安装升级 E2E 与 WinGet 清单。
