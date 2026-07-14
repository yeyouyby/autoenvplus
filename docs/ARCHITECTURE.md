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

诊断服务是只读聚合层：一次扫描复用 PATH 分析、命令候选解析、运行时版本探测、托管注册表和全局 Profile 解析，生成 GUI 与 CLI 共用的结构化问题、命令赢家、运行时状态和全局选择结果。探测有五秒超时且支持取消，不通过诊断路径写入任何主机状态。

## 文件布局

```text
%LOCALAPPDATA%\AutoEnvPlus\runtimes
%LOCALAPPDATA%\AutoEnvPlus\shims
%LOCALAPPDATA%\AutoEnvPlus\downloads
%LOCALAPPDATA%\AutoEnvPlus\state
%LOCALAPPDATA%\AutoEnvPlus\shell\powershell\AutoEnvPlus.PowerShell.psm1
%LOCALAPPDATA%\AutoEnvPlus\logs
%APPDATA%\AutoEnvPlus\settings.json
```

程序更新、运行时安装和用户数据彼此分离。卸载 AutoEnvPlus 时，不会在未确认的情况下删除运行时与项目锁文件。

## Shim 设计

AutoEnvPlus 只需把 `%LOCALAPPDATA%\AutoEnvPlus\shims` 加入用户 PATH 一次。`python.exe`、`node.exe`、`java.exe` 等无 CLR Win32 Shim 从当前目录向上查找 `autoenvplus.toml`，再按会话、项目、全局顺序解析实际运行时。

Shim 需要满足：

- 冷启动开销目标低于 20 ms；
- 不加载 WinUI；
- 保持参数、标准输入输出、退出码和 Ctrl+C 行为；
- 检测递归调用；
- 对解析结果进行短期缓存，并以配置文件时间戳失效；
- 产生可供 `autoenvplus which` 解释的解析轨迹。

当前原生 Shim 覆盖 `python`/`python3`、`pip`/`pip3`、`node`、`npm`/`npx`、`java`、`javac` 和 `jar`。它使用 `Windows.Data.Json` 结构化解析状态，校验路径始终位于受管根内，执行与 Core 相同的会话、项目、全局、自动选择顺序，使用标准 Windows 参数引用，继承控制台/标准流并返回子进程退出码；递归深度上限为 4。`AUTOENVPLUS_SHIM_TRACE=1` 可输出解析轨迹。若构建输出缺少原生二进制，安装器保留发布版 `autoenvplus.exe` 或开发版 `dotnet autoenvplus.dll` 的 CMD 回退。

性能门禁采用相同受管状态与相同立即退出子进程，交替测量直接启动和经 Shim 启动，以差值排除 PowerShell 与子进程本身开销。Windows 10 22H2 x64 本机 60 次 Release 测量：直接中位 11.73 ms，Shim 中位 27.28 ms，中位额外开销 15.55 ms，达到低于 20 ms 的目标。

## PowerShell 集成

PowerShell 模块只修改当前进程的 `AUTOENVPLUS_PYTHON_VERSION`、`AUTOENVPLUS_NODE_VERSION` 和 `AUTOENVPLUS_JAVA_VERSION`，由现有解析器继续执行 `会话 > 项目 > 全局 > 自动选择`。模块导出：

- `Use-AutoEnvPlusRuntime <python|node|java> <selector>`：先设置会话选择，再调用 `autoenvplus which` 验证；验证失败会恢复旧值；
- `Clear-AutoEnvPlusRuntime [runtime]`：清除一个或全部会话选择；
- 导入时只在当前 PowerShell 进程中把受管 Shim 目录置于 PATH 前部，不修改用户或系统 PATH。

Profile 使用固定的开始/结束标记管理单个块。安装计划会保留块外全部内容、清理重复受管块，并在写入前检查 Profile 与模块是否仍等于预览版本。实际写入顺序为模块原子写入、快照原子写入、Profile 原子替换。回滚只接受 `%LOCALAPPDATA%\AutoEnvPlus\state\powershell-profile-snapshots` 内、ID 与文件名一致的快照；Profile 出现较新修改时拒绝覆盖。

项目已激活终端是独立流程：服务读取最近的 `autoenvplus.toml`，从托管注册表把 Python/Node.js/Java 选择器预解析为精确版本，验证注册可执行文件和对应 Shim，再生成只读启动计划。WinUI/CLI 展示计划后，启动前重新计算 manifest SHA-256、重新加载注册表并比较环境覆盖，任何变化都会拒绝旧计划。实际启动使用 `CreateProcessW`、`CREATE_NEW_CONSOLE` 和独立 Unicode 环境块，工作目录固定为项目根；父进程与持久环境变量均不改变。

## 修改事务

所有主机修改由同一个事务引擎执行：

```text
Plan -> Preview -> Snapshot -> Apply -> Validate -> Commit
                                  └──── failure ───> Rollback
```

操作计划使用白名单步骤，例如下载、验证哈希、解压到临时目录、原子重命名、写用户环境变量、广播 `WM_SETTINGCHANGE`。不把拼接后的命令行当作通用事务步骤。

下载缓存按包 SHA-256 稳定寻址。未完成下载保留 `.partial` 与 URI、ETag、Last-Modified、总长度元数据；重试只在服务器确认同一实体时使用 Range/If-Range，服务器忽略范围或返回无效 Content-Range 时从零重新下载。完成文件仍需通过 Provider 给出的 SHA-256 才能进入解压阶段。安装计划还必须携带至少一条 HTTPS 来源的 SHA-256 证据，且证据值必须覆盖待安装资产哈希。签名清单模式还必须证明提供包哈希的证据 URI 与已验证签名 URI 相同；detached 包签名模式则要求签名对象与包文件名一致，并在 SHA-256 后直接验证缓存包字节。两种模式都不能降级到 checksum-only。

WinUI 安装确认页按证据链展示包文件、下载 URI、目标目录、SHA-256、每条校验对象和值及其 HTTPS 来源。Node.js 展示已验证的 OpenPGP 清单签名、完整主指纹、实际签名 Key ID、活跃/历史状态、签名 URI 和固定公钥来源。Temurin 展示安装时强制执行的 detached 包签名、固定主指纹、签名 URI 和公钥来源；只有 Python 明确显示发布者签名尚未验证。

卸载先扫描全局选择、已知项目 `autoenvplus.toml` 和 `autoenvplus.lock`。无引用时将目录同卷移动到 `.trash`，再更新安装注册表；注册表失败则移回原目录。单个运行时卸载不会删除可能共享的包下载缓存。

运行时安装由协调器串联“归档安装、托管注册表登记、可选全局默认写入”。协调器先验证计划与注册表条目的 Provider、版本、架构、目标目录、可执行文件相对路径和包 SHA-256 完全一致。登记或全局配置失败时恢复原注册表条目和原全局 Profile；只有本次新建且状态已恢复的安装目录才会自动清理，既有安装不会因重新登记失败被删除。

存储迁移共用逐文件 SHA-256 复制验证和原目录保留策略。pip、npm、Yarn Classic、NuGet、Gradle、vcpkg 二进制缓存和 Conan 2 Home 在提交阶段通过受限的用户环境变量存储切换；vcpkg 使用 `VCPKG_DEFAULT_BINARY_CACHE`，默认 `%LOCALAPPDATA%\vcpkg\archives`，Conan 使用 `CONAN_HOME`，默认 `%USERPROFILE%\.conan2`。Maven 使用禁用 DTD 和外部实体解析的结构化 XML 读写器更新用户 `.m2\settings.xml` 中唯一的 `localRepository`，并保留其他节点。pnpm 使用 Windows 官方全局配置 `%LOCALAPPDATA%\pnpm\config\rc` 中唯一的 `store-dir`，保留注释与其他键。配置切换前会检查其仍等于预览版本并保存受管快照；回滚只恢复配置，不自动删除迁移副本。快照绑定缓存 ID、白名单变量或授权的 Maven/pnpm 配置路径，防止篡改后修改其他配置。

## Provider 契约

Provider 描述版本搜索、已有安装检测、安装计划和健康检查。每个 Release 必须记录版本、架构、通道、官方来源、哈希、校验来源证据、签名证据或待执行签名要求、真实性要求、命令入口和许可证信息。Node Provider 使用 Bouncy Castle 验证 cleartext OpenPGP 签名，密钥文件固定到 `nodejs/release-keys` 提交，完整主指纹与签名子钥 Key ID 映射固定在源码；信任快照之后只接受活跃钥，历史钥只能验证更早版本。Temurin Provider 固定 Adoptium 完整主指纹，安装器在下载后流式验证 API 指定的 detached 签名。详细策略见 `docs/SECURITY.md`。

首批 Provider：

- Python：优先受管理的独立目录安装，同时识别 Python Launcher 与官方安装器；
- Node.js：官方 ZIP 适合无管理员、多版本并存；
- Java：支持选定发行版的官方 ZIP，并记录 vendor；
- MSVC：调用 Visual Studio Installer，AutoEnvPlus 自身不重新分发 MSVC。

MSVC 发现读取每个实例的默认工具版本，并检查 `VC\Tools\MSVC\<version>\bin\Host<arch>\<target>\cl.exe`，只暴露实际存在的 Host/Target 组合。终端激活计划使用 `cmd.exe /d /k call "<vcvarsall.bat>" <pair>`，GUI 与 CLI 均先展示实例、Host、Target 和完整参数；只有显式确认后才启动外部终端。

C/C++ 外部组件安装只允许固定 WinGet ID，并始终先展示计划：MSVC Build Tools、LLVM、CMake、Ninja，以及 `BrechtSanders.WinLibs.POSIX.UCRT` 提供的 MinGW-w64/GCC。该 WinLibs 变体使用 POSIX 线程模型和 Windows 10 可用的 UCRT；官方 WinGet portable 清单暴露 `gcc`、`g++`、`gdb` 等命令别名，安装后由同一 PATH 发现器重新检测。AutoEnvPlus 不接收用户提供的包 ID、安装器 URL 或任意 override 参数。

工具链页重检时使用只读的“当前进程 PATH + 最新用户 PATH + 最新系统 PATH”合并视图，保留当前进程的命令优先级并去重，再补入 WinGet 刚写入但 GUI 尚未继承的目录。通用 PATH 诊断仍只报告当前进程的真实解析结果；该刷新不会修改当前进程或主机环境变量。

项目级 CMake 集成只写 `CMakeUserPresets.json`，不改团队共享的 `CMakePresets.json`。AutoEnvPlus 管理名字稳定的 Configure/Build Preset，写入 Visual Studio generator、目标架构、Host toolset、生成目录和 `CMAKE_GENERATOR_INSTANCE`；其他根属性和用户 presets 原样保留。预览后会检查文件未变化，写入前保存受管快照并原子替换；快照绑定项目根与固定文件名，回滚遇到较新改动时拒绝覆盖。

## Win10/Win11 兼容

应用使用 WinUI 3 和 Windows App SDK，目标平台为 Windows 10 1809 以上。窗口背景不按操作系统版本号硬编码，而是读取高对比度与“透明效果”设置，并调用 Windows App SDK 的材料能力检测：高对比度优先纯色，关闭透明效果时使用纯色，否则依次尝试 Mica、Desktop Acrylic，材料不可用或初始化失败时使用主题纯色。启用材料时根视觉层透明，纯色回退时恢复 `ApplicationPageBackgroundThemeBrush`。

系统设置变化优先使用 WinRT 事件；无包身份的 Windows 10 可能拒绝 `AccessibilitySettings.HighContrastChanged` 订阅，此时由 WinUI `ActualThemeChanged` 和两秒低频设置轮询补位。设置页只读显示当前实际选择及回退原因。Windows 10 22H2 build 19045 已通过隐藏启动与 UI Automation 验证，透明效果开启时实际选择 Desktop Acrylic；Windows 11 的 Mica 路径仍需在真实 Windows 11 主机上完成端到端验证。功能层不得依赖仅 Windows 11 存在的 API，使用新 API 前必须做能力检测。

当前发布脚本生成 x64 unpackaged 自包含便携布局：GUI 携带 .NET 与 Windows App SDK，`cli` 子目录携带单文件自包含 CLI 和原生 Shim，并生成逐文件及 ZIP SHA-256。WinUI GUI 取自 RID 自包含 Build 布局，并强制检查 PRI 与 XBF；当前 Windows App SDK 的 `dotnet publish -o` 会漏掉这些 XAML 资源，不能直接作为可运行布局。该布局用于开发验证和无需预装运行时的试用，不提供发布者身份、安装注册或自动升级；正式渠道仍需签名 MSIX/安装器与 WinGet 清单。
