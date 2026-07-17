# AutoEnvPlus 产品规格

## 产品目标

AutoEnvPlus 让 Windows 用户按语言管理编译器工具与运行时（统称“语言工具”），并集中处理版本、命令路由、项目环境、Provider 来源、代理、下载与缓存。安装只是入口；产品的核心价值是让当前环境可预测、可解释、可复现、可回滚。AutoEnvPlus 本体以 `AGPL-3.0-only` 发布，第三方组件仍适用各自许可证。

## 支持范围

产品领域层级固定为“语言 → 语言工具 → Provider → Provider 来源”：

| 类别 | 当前对象 | 说明 |
|---|---|---|
| 语言 | 45 门内置语言 | 稳定目录；默认启用 Top 10，并显示持久快照中已发现的语言 |
| 语言工具 | 136 个真实工具目录 | 编译器、解释器、Runtime、SDK、包管理器、构建、调试、格式化、lint 等角色 |
| Provider Profile | 140 条工具作用域 Profile、5 个复用 ID | 每条 Profile 声明该工具的分发方式、真实能力和来源槽；不是 140 个可执行插件 |
| 受管安装适配器 | 9 个真实适配器 | CPython、Node.js、Eclipse Temurin、.NET SDK 使用官方归档；MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja 使用固定 WinGet 白名单 |
| 语言包 | 数据-only schema 1 | 可添加语言、工具和 Provider 元数据；导入默认停用，不能覆盖既有 ID |
| Runtime Provider 插件 | 数据-only schema 2 | 用 `languageToolId` 为 9 个已桥接工具扩展受限便携 ZIP 来源；兼容导入旧 schema 1 |
| 缓存与存储 | pip、npm、pnpm、Maven、Gradle、vcpkg、Conan 等 | 支持统计、打开真实目录、安全迁移和纯缓存两阶段清理 |
| Provider 来源 | 83 个内置来源槽与用户自定义源 | 所有权为 `languageToolId + providerId + slotId`；镜像不与通用代理混为一层 |
| 网络传输 | 设置中的通用代理、`NO_PROXY` 与兼容工具覆盖 | 只负责代理；镜像由具体 `toolId/providerId` 的 Provider 来源解析 |
| 受管下载 | HTTPS URL、本地包和归档、Python wheel | 支持分段探测、可选哈希验证、受管库与审核后安装 |

默认 Top 10 为 Python、JavaScript、TypeScript、Java、C、C++、C#、Go、Rust 和 PHP。136 个工具目录和 140 条 Provider Profile 都不等于安装器：当前恰有 9 个真实受管安装适配器，即 CPython、Node.js、Eclipse Temurin、.NET SDK 四个官方归档工具，以及 MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja 五个固定 WinGet 工具。

## 版本解析

命令解析优先级固定为：

1. 新终端会话中的精确选择；
2. 当前或父级目录 `autoenvplus.toml` 中的精确项目选择；
3. 当前用户的精确全局选择；
4. 在满足要求的已安装版本中自动选择最高稳定版。

前三层都可以把版本 selector 与 Runtime ID、Provider ID 绑定；存在精确身份时不得仅按版本改选同版本的另一个 Provider。

项目文件示例：

```toml
[tools]
python = "3.13.5"
node = "22-lts"
java = "21"
dotnet = "10.0"

[tool-identities]
python.runtime-id = "python-3.13.5-x64"
python.provider-id = "python-org"
```

`[tool-identities]` 中每个工具的 `runtime-id` 与 `provider-id` 必须成对出现，并且必须有匹配的 `[tools]` selector。项目选择操作会保留清单中的未知分区与注释，只更新目标工具的 selector 和精确身份；解析后的精确版本、架构、Provider、包哈希算法与校验值写入 schema 2 的 `autoenvplus.lock`。项目配置还可以导入 `.python-version`、`.nvmrc`、`.node-version`、`.java-version`、`package.json` 的 `engines` 和 `.NET global.json`。

项目页的“解析虚拟环境”是用户按需触发的只读检查，不随首页打开或选择项目自动运行。它显示语言、环境类型、管理器、根目录、入口/版本、健康度、证据和警告：覆盖 Python `.venv`/`venv`/`env`、Poetry、Pipenv、Conda，Node.js `node_modules/.bin`、npm/pnpm/Yarn/Bun 锁与 Corepack 声明，`.NET` 本地工具清单，Maven/Gradle Wrapper，Rust toolchain/`target`，以及 Go workspace。扫描不会执行 Python、Node、包管理器或构建工具，也不会修改项目文件。

“打开已激活终端”先把项目 `[tools]` 与 `[tool-identities]` 解析为当前托管注册表中的精确 Runtime ID、Provider ID 和版本，确认真实可执行文件与对应 Shim 存在，再展示终端宿主、Shell、Shim 目录、请求选择器、精确身份和会话变量。WinUI 在检测到 `wt.exe` 时允许选择 Windows Terminal 或 Windows PowerShell；Windows Terminal 不可用时明确回退到独立 PowerShell，CLI 默认使用 Windows PowerShell。确认后只为新子进程创建环境块并把已配置的 Shim 目录置于 PATH 首位；不修改父进程、用户 PATH、精确全局选择或项目文件。.NET 项目选择写入版本、Runtime ID 与 Provider ID 会话变量，`dotnet` Shim 启动精确版本时只为子进程设置对应的 `DOTNET_ROOT` 和 `DOTNET_MULTILEVEL_LOOKUP=0`。

包含 Python 或 Node.js 的项目终端还会生成不含端点/凭据的网络摘要：pip 与 npm 的精确 Provider 来源分别保留；共享代理在单一生态时取对应 pip/npm 作用域，两者相同则共用，两者冲突时明确警告并取 `downloads` 作用域。WinUI 预览显示该摘要；CLI 预览显示冲突 warning，不回显端点。被禁用的网络变量从新环境块移除。代理与 Provider 来源文件的路径、SHA-256、摘要、环境覆盖与移除集合都进入启动前复检；任一配置在预览后变化都会拒绝旧计划。

## 主要页面

- **概览**：启动先读取持久快照，默认不做环境扫描；快速刷新只读取受管状态，完整扫描才检查 PATH、执行版本探测和测量缓存，并记录上次完整扫描时间；
- **语言**：打开时只读取持久的语言工具清单快照，不自动扫描 PATH；用户可显式重检、搜索、筛选和启用/隐藏语言、导入语言包，并进入语言详情管理工具与版本、Provider 来源、项目环境和 Runtime Provider 插件；
- **项目环境**：导入项目需求、按需只读解析现有虚拟环境，创建锁文件并打开已激活终端；
- **下载中心**：从 HTTPS URL 分段或单流下载、导入本地包、查看受管库证据、取消传输、安全删除，并为 `.whl` 生成受管虚拟环境安装计划；
- **PATH 与命令**：PATH 只需配置一次 AutoEnvPlus Shim，展示新终端会话、项目、全局、自动选择优先级，解释 18 个命令（`python`、`python3`、`pip`、`pip3`、`node`、`npm`、`npx`、`java`、`javac`、`jar`、`dotnet`、`cl`、`clang`、`clang++`、`gcc`、`g++`、`cmake`、`ninja`）的实际路由，并列出可验证回滚的用户 PATH 快照；
- **缓存与存储**：统计 pip/npm 等目录，直接打开实际位置，进行可回滚配置的安全迁移，并将纯缓存先隔离、后恢复或永久清空；
- **环境诊断**：用户显式选择 PATH/命令、托管工具、项目、Provider、存储或实时连接域；扫描保持只读，可导出结构化 JSON；
- **活动记录**：按状态和操作类型筛选脱敏审计记录，复制摘要，并只读显示快照与回滚路径。
- **设置**：配置启动页、语言可见性、下载默认值、通用代理、活动日志保留、主题、材质、密度、PowerShell 会话集成和非系统盘受管根；镜像不在设置页编辑。首页与语言页固定为加载快照，Shim 固定为版本切换基础设施，卸载、清理和永久写入的确认合同不可关闭，因此不提供会产生虚假状态的开关。

## 语言包与声明式 Provider

语言包负责扩展“有什么语言和语言工具”，Runtime Provider 插件负责为已桥接语言工具扩展“从哪里取得可安装版本”。两者都使用严格 data-only JSON、默认停用和显式启用生命周期，但语言包不能携带下载资产，Provider 插件也不能任意创建新的语言概念。

内置 Provider 为 CPython、Node.js、Eclipse Temurin 和 .NET SDK 提供官方目录及各自的信任策略。第三方、社区和组织内部发行版通过 schema 2 声明式 JSON 接入，以 `languageToolId` 精确绑定 `cpython`、`nodejs`、`eclipse-temurin`、`dotnet-sdk`、`msvc-build-tools`、`clang`、`gcc`、`cmake` 或 `ninja`。旧 schema 1 的 `runtimeKind` 仍可导入，并立即映射到上述工具、规范化为 schema 2；新清单不得同时声明 `runtimeKind`。机器可读契约见 [JSON Schema](../schemas/runtime-provider-plugin.schema.json)，待替换字段的起点见 [模板](../examples/runtime-provider-plugin.template.json)，完整作者说明见 [Provider 插件指南](PROVIDER-PLUGINS.md)。

插件只声明供应商/许可证元数据、静态版本/通道、x86/x64/ARM64 HTTPS ZIP、checksum 来源、SHA-256 或 SHA-512、可选归档根和预期 `.exe`。它不能声明 DLL、脚本、任意命令、注册表或环境写入、自定义目标目录、WinGet ID、安装器参数或安装后钩子。导入操作先严格解析和预览，再把规范化 JSON 复制到受管根，且初始状态固定为停用；只有用户再次明确启用后，该 Provider 才出现在对应 `languageToolId` 的来源选择中。

内置官方 Provider 保持默认推荐。每次第三方目录查询或安装必须精确选择 `plugin:<id>`；插件未启用、`languageToolId` 不匹配或没有请求的版本/架构时直接失败，不回退到其他来源。不同 Provider 可以声明相同工具、版本和架构，安装路径、`plugin-<kind>-<plugin-id>-<version>-<arch>` 运行时 ID、托管注册表和项目锁仍保留实际 Provider；存在来源冲突时，界面不自动设置新的全局默认。完整 exact 版本请求包含 build metadata；同一 Provider/内部适配类别/架构不能同时登记不同 RuntimeId 但 SemVer precedence 等价的包身份。

插件的 `checksumSourceUri` 与预期哈希都是第三方声明。schema 2（以及兼容导入的 schema 1）只把该 URI 保存为供用户核对的引用，安装器不会抓取或解析该页面来证明其中确实发布了哈希。AutoEnvPlus 强制逐字节校验下载包与插件 JSON 中的哈希、ZIP 安全边界和预期入口复检，但不把这些证据显示成已验证的上游清单或官方发布者签名，也不让第三方插件继承 python.org、Node.js 或 Temurin 内置 Provider 的身份结论。停用或删除插件只阻止后续目录加载与新安装，不删除已安装运行时、项目锁或共享包下载缓存。

## Provider 来源与通用代理

Provider 来源偏好保存在 `<managed-root>\state\provider-source-preferences.json`。83 个内置来源槽由语言目录声明，键固定为 `languageToolId + providerId + slotId`；用户可以覆盖可修改槽、恢复目录默认，也可以为同一 Provider 添加、启停和删除具名自定义 HTTPS 源。同一工具/Provider/端点类型只保留一个活动自定义源，启用新来源会在同一原子更新内停用同类旧来源。自定义源声明端点类型和用途，但不获得脚本、安装钩子或任意命令能力。未知工具、Provider、槽、不可覆盖槽、重复 ID、非 HTTPS、URI 凭据、query、fragment 和目录引用漂移都会安全失败。

通用传输设置继续保存在 `<managed-root>\state\network-settings.json`。设置页只编辑 HTTP 代理、HTTPS 代理和 `NO_PROXY`；工具执行仍可使用兼容的代理覆盖，但镜像的权威配置来自 Provider 来源。代理与来源分别解析：代理决定如何连接，Provider 来源决定连接哪个发行目录或包仓库。

`NO_PROXY` 当前是全局列表，没有 Provider 级覆盖。代理只接受不含凭据、query 或 fragment 的绝对 HTTP/HTTPS URI；Provider 来源只接受不含凭据、query 或 fragment 的绝对 HTTPS URI。配置文件不保存代理用户名、密码或令牌，WinUI、CLI 和活动记录在展示 URI 时还会移除 user-info、query 与 fragment。AutoEnvPlus 自己的 HTTP Client 可用 Windows `DefaultCredentials` 完成集成身份认证；独立用户名/密码代理没有凭据存储或输入面。

代理兼容层支持 `runtime-python`、`runtime-node`、`runtime-java`、`runtime-dotnet`、`runtime-cpp`、`downloads`、`pip`、`npm`、`pnpm`、`yarn`、`nuget`、`maven`、`gradle`、`vcpkg` 和 `conan`。Provider 包源执行投影目前精确覆盖 pip、npm、pnpm、Yarn、NuGet CLI、Apache Maven 和 Gradle；配置存在不代表任意外部程序都会自动读取它。当前真实接入边界为：

| 调用路径 | 实际使用的设置 |
|---|---|
| WinUI 语言详情官方安装 | 使用对应 `runtime-*` 代理，并把用户选择的 Provider `GenericDownload` 来源按该 Provider 的协议传入；声明式插件只使用兼容代理，不重写显式 asset URL |
| CLI `catalog` / `install` | 继续使用对应 `runtime-*` 代理和既有兼容设置；不会把无关 Provider 来源套给发行目录 |
| CLI `tool pip|pip3` | `pip` 代理、`NO_PROXY`、有效 pip Provider 来源作为 `PIP_INDEX_URL`，只写本次子进程环境 |
| CLI `tool npm|npx` | `npm` 代理、`NO_PROXY`、有效 npm Provider 来源作为 `NPM_CONFIG_REGISTRY`，只写本次子进程环境 |
| CLI `tool javac|jar` | `runtime-java` 代理环境；Java 镜像不是 javac/jar 可识别的通用环境变量，因此不会注入镜像 |
| WinUI/CLI 下载中心 | `downloads` 代理；显式输入的 URL 保持权威，镜像字段不会重写该 URL |
| WinUI wheel 的“当前 pip 来源与代理”模式 | `pip` 代理、`NO_PROXY` 和有效 pip Provider 来源；严格离线模式不使用网络 |
| WinUI/CLI 项目终端 | 根据项目内 Python/Node.js 应用 pip/npm Provider 来源；代理冲突时警告并使用 `downloads` 共享代理；启动前复检代理与 Provider 来源双文件快照 |

受管工具、wheel 联网模式和应用网络设置的项目终端会先清理继承环境中大小写两套 `HTTP_PROXY`、`HTTPS_PROXY`、`NO_PROXY`，再写入有效值；由于模型不支持 SOCKS/任意全局代理，它们还会显式移除 `ALL_PROXY`/`all_proxy`，避免父进程残留绕过已审核代理。普通 `exec <runtime>` 不是包工具网络启动器，不应用这些覆盖。

pnpm、Yarn、NuGet、Maven 和 Gradle 已有精确 Provider 来源投影模型，但只有经过审核的执行入口才会应用；vcpkg、Conan 和任意 C/C++ 命令仍不会因状态文件存在而被透明接管。CLI `network show [global|tool-id] [--json]` 只读显示脱敏后的代理兼容配置；Provider 来源在语言详情中管理。

Provider 来源没有统一的 URL 拼接语义：Python 使用 release API 基址，Node.js 使用 distribution 基址，Java 使用 Adoptium API 基址，.NET 使用完整 `releases-index.json` URI。因此不存在跨 Provider 的全局镜像；每个槽都保留自己的协议和路径语义。声明式插件的 `downloadUri` 保持权威，不套用内置 Provider 来源，只复用对应代理。来源变化不会取消 Provider 的哈希/签名检查；但 .NET 当前只有 release index 提供的 SHA-512 checksum evidence，自定义 .NET index 没有独立发布者签名，使用者必须信任该来源的元数据，界面也不得把它描述成 Microsoft 签名验证。

## 受管下载与 wheel

下载中心只接受不含嵌入式用户名/密码的绝对 HTTPS URL，并在任何重定向后重新要求 HTTPS。URL 可以包含下载服务需要的 query，但进度、活动记录和下载库清单只保留 scheme、host、port 与 path，不保存或回显 query。用户可选择 1、2、4、8 或 16 个连接，并设置最大文件大小及可选 SHA-256/SHA-512 预期值。

多连接不是盲目并发。下载器先尝试 `HEAD`，再用 `Range: bytes=0-0` 探测长度和范围支持；只有长度已知，并能以强 ETag 或 Last-Modified 绑定同一实体时才拆分。每个分段都发送 `If-Range`，复核状态码、`Content-Range`、响应长度和实体标识。服务端不支持 Range、无法提供稳定实体标识或传输中实体变化时，结果会明确记录降级原因并使用单流；不满足协议约束的畸形响应会直接失败。更多连接不保证更快，也可能被服务端限流、代理、磁盘或网络条件抵消。

网络下载和本地导入都先写入 `<managed-root>\downloads\library\.autoenvplus-staging`，验证后才移动为库根下的直接子普通文件。取消会进入本次暂存清理路径；若文件占用或权限使尽力清理失败，残留仍限制在受管 staging 内。本地导入复制来源文件，不把任意外部路径当成受管资产。文件名、扩展名、大小、重解析点和库根逃逸均受限制。每个完成文件始终计算内容 SHA-256 作为身份；如果用户提供预期 SHA-256 或 SHA-512，还会逐字节匹配后记录预期值与实际值。内容 SHA-256 只能说明“这些字节是谁”，没有受信预期哈希时不能说明“这些字节可信”。有预期证据的条目在列举时重新计算当前内容身份；同长度手工替换也会显示为当前内容已变化，不再沿用旧“已验证”状态。

下载库中的内容不会自动执行。提交、覆盖和删除的最终事务共享固定跨进程库锁；删除只接受库根直接子普通文件，先把目标移动到本次受管暂存隔离区，再更新原子清单。清单更新失败时尝试恢复原文件，成功后才永久删除隔离文件。WinUI 在执行前要求明确确认。

`.whl` 安装当前是 WinUI 工作流，不是 CLI 子命令，也不是对 pip resolver 的透明拦截。用户选择一个受管 Python、虚拟环境名，以及“严格离线（`--no-index --find-links --no-deps`，只安装当前 wheel）”或“当前 pip Provider 来源与代理”模式；只有联网模式解析依赖。界面展示 wheel、目标环境、创建命令、安装命令、`TEMP`/`TMP`、pip 缓存和非事务边界，再要求第二次确认。计划把虚拟环境固定到 `<managed-root>\environments\python`，把临时目录固定到 `<managed-root>\temporary\pip`，把 `PIP_CACHE_DIR` 固定到 `<managed-root>\caches\pip`，并通过 `PIP_CONFIG_FILE=NUL` 禁止继承用户 pip 配置。执行前会验证计划 SHA-256，并重新哈希/复检受管 Python、wheel、下载库记录身份与已有环境 Python；wheel 必须是下载库顶层普通文件，路径不能经过重解析点。子进程输出始终被读取，但 stdout/stderr 每路只保留末尾 65,536 字符；结果会明确标记具体被截断的流。

pip 安装可能已经创建虚拟环境、写入部分包、解析联网依赖或运行第三方构建/安装逻辑；取消会终止进程树，但失败或取消不会事务回滚。AutoEnvPlus 自有环境、暂存和缓存都放在受管根，仍不能保证第三方脚本或工具自身绝不写入 C 盘或其他用户目录。

## 受管数据根

运行时、Provider 插件清单与启用状态、Shim、下载与暂存、受管虚拟环境、AutoEnvPlus 指定的临时/缓存目录、状态、Shell 集成和活动记录使用同一个受管数据根。解析优先级固定为：

1. CLI 显式 `--root`；
2. `AUTOENVPLUS_HOME`；
3. `%LOCALAPPDATA%\AutoEnvPlus`。

候选必须是规范化绝对目录，驱动器根、UNC 共享根、空值和相对路径都会安全失败。设置页只写用户级 `AUTOENVPLUS_HOME`，不在当前进程中途切换根；重启后才启用新目录。修改根不会隐式复制、迁移或删除旧运行时、插件与状态，避免在半迁移状态下让 GUI、CLI、插件 Registry 和 Shim 读取不同注册表。将 `AUTOENVPLUS_HOME` 设为 `D:\codex\autoenvplus-data` 可把 AutoEnvPlus 自有插件与安装内容固定到 D 盘，但不能阻止以后运行的第三方可执行文件按自身规则写入 C 盘。

## 缓存迁移事务

缓存迁移必须按以下顺序执行：检查目标空间、检测占用进程、复制到临时目录、校验、检查配置是否仍等于预览版本、保存配置快照、更新工具配置、执行健康检查、提交切换，最后才允许用户删除旧目录。任一步失败都恢复原配置；如果恢复本身失败，则保留新副本和快照供人工恢复。

首批缓存配置：

| 工具 | 原生配置 |
|---|---|
| pip | `PIP_CACHE_DIR` / pip 配置 |
| npm | `npm config set cache` / `NPM_CONFIG_CACHE` |
| pnpm | `pnpm config set store-dir` |
| Maven | `settings.xml` 中的 `localRepository` |
| Gradle | `GRADLE_USER_HOME` |
| NuGet 全局包 | `NUGET_PACKAGES` |
| NuGet HTTP 缓存 | `NUGET_HTTP_CACHE_PATH` |
| NuGet 插件缓存 | `NUGET_PLUGINS_CACHE_PATH` |
| vcpkg 二进制缓存 | `VCPKG_DEFAULT_BINARY_CACHE` |
| Conan 2 Home | `CONAN_HOME` |

## 缓存安全清理

缓存清理与缓存迁移是两个独立事务。清理计划先重新枚举顶层项、文件数、字节数和目录指纹，拒绝驱动器根、受管数据根重叠、系统保护目录、重解析点、配置文件位于缓存内部以及计划后的目录变化。确认后只通过同卷重命名把顶层内容移入源目录旁的受控隔离区，原缓存根和工具配置保持不变；中断时优先逐项移回，无法回退的内容继续保留为可恢复项。

恢复会在执行时重新验证 manifest、隔离路径和目录清单；源缓存出现后续数据时拒绝覆盖，但隔离项仍可被审计并永久清空。永久清空需要第二次确认，只删除已发现、身份匹配且仍位于隔离目录内的普通文件和空目录，遇到重解析点或未知内容立即停止。清空开始后如果被取消，只能继续清空，不能把已部分删除的数据伪装成可完整恢复。

只有经过审核的纯缓存目录支持整目录内容清理：pip、npm、pnpm、Yarn Classic、NuGet 三类缓存、Maven repository 和 vcpkg binary cache。Gradle User Home 与 Conan Home 同时包含配置、Profile、远端和其他用户状态，因此只支持迁移，不提供整目录清理。

## C/C++ 特殊处理

C/C++ 不是单个运行时。MSVC 激活还需要 `INCLUDE`、`LIB`、Windows SDK 和目标架构。AutoEnvPlus 对 C/C++ 的主要操作是创建工具链并“打开已配置终端”，而不是只把 `cl.exe` 写入 PATH。

声明式 Provider 为 MSVC、LLVM、MinGW、CMake 和 Ninja 增加版本化便携 ZIP 来源，但不取代现有工具链语义。固定 WinGet 白名单仍用于已审核的 MSVC Build Tools、LLVM、WinLibs、CMake 和 Ninja 安装；Visual Studio/MSVC 实例、Windows SDK 与 Host/Target 组合仍由内置发现器验证。插件不能声明安装命令、任意 WinGet ID、`vcvarsall.bat` 或环境激活脚本，因此一个插件内的 `cl.exe` 入口不会被错误描述为已经具备完整 MSVC 开发者环境。

## 权限与安全

- 主进程始终以普通用户运行；
- 默认安装到用户目录；用户 PATH 只在首次配置时加入一次 Shim 目录，工具安装与版本切换不再反复改写 PATH；
- 系统级修改使用短生命周期提权 Helper；
- 下载包必须使用 HTTPS，安装前按 Provider 声明的 SHA-256 或 SHA-512 校验字节，并记录算法、值和 checksum/manifest 来源证据；
- Node.js 必须先用固定发布密钥验证 `SHASUMS256.txt.asc` OpenPGP 签名，再从已签名正文读取包哈希；
- Temurin 必须在 SHA-256 通过后，用固定 Adoptium 主指纹验证 ZIP 包本体的 detached OpenPGP 签名，失败时删除缓存包且不解压；
- Python 必须用内置 Sigstore trusted-root 快照和 python.org 对应版本系列的精确 release-manager 邮件/OIDC 策略验证 Windows manifest，再从该签名 manifest 读取 PythonCore ZIP SHA-256；bundle、身份、Fulcio/SCT/Rekor 或签名任一失败时不能降级；
- .NET SDK 只接受 Microsoft 官方 HTTPS release index/channel metadata 中的 Windows ZIP 与 SHA-512，逐字节校验通过后才解压；该 Provider 当前是 checksum-evidence 模式，不宣称独立发布者签名；
- 声明式插件默认停用，只接受数据-only JSON 与受限 HTTPS ZIP；插件声明的 checksum 是第三方完整性证据，不是 AutoEnvPlus 验证的官方签名；
- 内置签名必需 Provider 的目录或包必须通过其固定发布身份验证；声明式第三方清单没有官方签名结论，必须保持独立的第三方 checksum 提示；
- 外部安装默认只读，不允许 AutoEnvPlus 擅自删除；
- PATH 和配置修改前创建快照；
- 活动记录隐藏令牌、代理密码、PFX 密码和 URI 凭据，并限制文件、记录与字段大小；
- 网络配置拒绝 URI user-info、query 和 fragment，不在 JSON 中持久化代理凭据；镜像只允许 HTTPS；
- 受管下载始终记录内容 SHA-256，但只有成功匹配用户提供或 Provider 声明的预期哈希时才构成相应完整性证据；
- 下载或导入的包不会自动执行；wheel 只有在展示并复检固定参数计划后才交给受管 Python 的 pip，且明确标记为非事务操作；
- 删除前重新验证目标位于受信隔离目录内，拒绝链接、未知条目和系统/受管根重叠。

## MVP 验收

1. 在 Windows 10 22H2 和 Windows 11 x64 上启动；
2. 无管理员权限安装 Python、Node.js、Java 和 .NET SDK；
3. 每种运行时可并存至少两个版本；
4. 精确全局、项目 `[tool-identities]` 与新终端会话选择都能按 Runtime ID/Provider ID 正确解析；
5. PATH 不因切换产生重复项；
6. 能解释 `python`、`node`、`java` 实际来源；
7. pip 与 npm 缓存可以事务式迁移；
8. 安装或迁移失败可回滚；
9. 外部安装不会被误删；
10. 能检测 C/C++ 的 MSVC、SDK、架构和构建工具，并打开已激活终端。
11. 能把受管数据根固定到非系统盘，且 GUI、CLI 与 Shim 使用同一根；
12. 纯缓存能先隔离并恢复，再通过独立确认永久释放空间；
13. 高影响操作能生成不含凭据的有界活动记录。
14. 设置中的通用代理与 `NO_PROXY` 能按已声明调用路径执行；镜像由精确 `languageToolId + providerId + slotId` 的 Provider 来源解析，不存在跨 Provider 的全局镜像；
15. URL 下载在范围不安全时降级为单流；本地导入与取消只在受管 staging 内产生 AutoEnvPlus 暂存，不能把尽力清理描述成绝对成功；
16. 下载库能区分内容 SHA-256 标识和经过预期 SHA-256/SHA-512 验证的证据；
17. 本地 wheel 只能安装到受管根下的 Python 虚拟环境，执行前复检，失败时不虚假宣称事务回滚。
18. `cpython`、`nodejs`、`eclipse-temurin`、`dotnet-sdk`、`msvc-build-tools`、`clang`、`gcc`、`cmake` 和 `ninja` 可通过默认停用的 schema 2 声明式 Provider 扩展来源；选择必须绑定精确 Provider，停用/删除插件不得卸载既有运行时，旧 schema 1 仅作为兼容导入格式。

以上是产品验收目标；当前尚未完成真实 Windows 11 主机上的完整端到端验证，开发 MSIX 也只有本机测试用途的开发签名，不等同于生产签名。
