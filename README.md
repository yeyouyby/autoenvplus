# AutoEnvPlus

AutoEnvPlus 是一个面向 Windows 10 和 Windows 11 的 WinUI 3 / Fluent 开发环境控制中心。产品以“语言 → 语言工具 → Provider → Provider 来源”组织 Python、JavaScript、Java、C/C++、.NET、Go、Rust 等生态；编译器、解释器、SDK、包管理器、构建工具和格式化器都是语言工具，镜像与包源归属于精确的 `languageToolId + providerId`，通用代理则是独立传输设置。

当前仓库处于功能原型阶段。核心路径、自包含 x64 便携发布以及参数化 MSIX/AppInstaller 发布链已经可运行，但正式发布者证书、可信时间戳和 Win11 安装/升级端到端验证仍未完成。已经包含：

- WinUI 3 桌面应用，以及“概览 / 语言 / 项目环境 / 下载中心 / PATH 与命令 / 缓存与存储 / 环境诊断 / 活动记录 / 设置”的一级导航；运行时、工具链、Provider 插件和网络不再各占一个一级页面；
- 按系统能力自适应的 Fluent 窗口背景：Windows 11 优先 Mica，Windows 10 使用 Desktop Acrylic，高对比度、关闭透明效果或材料不可用时切换为纯色，并在设置页显示实际状态；
- Fluent 概览首页使用 `<managed-root>\state\overview-snapshot.json`，语言页使用 `<managed-root>\state\language-tool-inventory.json`：打开两个页面都只读已有快照，不自动运行 PATH、版本或缓存扫描；概览完整扫描与语言 PATH 重检都只能由用户显式触发并记录时间；
- 45 门内置语言、136 个真实语言工具目录、140 条工具作用域 Provider Profile（复用 5 个 Provider ID）和 83 个 Provider 来源槽；这 140 条 Profile 是目录与能力元数据，不是 140 个可执行插件；默认显示 Top 10 与快照中已发现的语言，用户显式启用/隐藏状态持久化；
- 每门语言都有独立详情，包含“概览 / 工具与版本 / 包与镜像 / 项目环境 / 高级设置”五个分区；C/C++ 页面内直接管理 MSVC、GCC、Clang、CMake、Ninja 和 Windows SDK，不再设独立工具链页面；
- 目录能力严格区分发现、受管安装、版本切换、项目固定、包管理、虚拟环境、缓存、镜像、调试、格式化和 lint；当前只有 9 个真实受管安装适配器，即 4 个官方归档工具与 5 个固定 WinGet 工具，其余 127 个目录工具只提供其实际具备的发现、官方链接或外部安装边界；
- 数据-only 语言包可新增语言、语言工具和 Provider 元数据；导入先预检、默认停用，启用后合并有效目录，冲突 ID 安全失败；
- Runtime Provider 插件已归入对应语言详情：插件默认停用，启用后可作为 9 个已桥接工具的非官方安装来源；选择精确 Provider 后安装，失败不回退，第三方 checksum 不冒充官方签名；
- Provider 来源偏好以 `languageToolId + providerId + slotId` 为所有权，支持覆盖默认源、恢复默认、添加/启停/删除自定义 HTTPS 源；镜像始终属于具体 `toolId/providerId`，全局 HTTP(S) 代理和 `NO_PROXY` 只在设置页管理，不与镜像组成独立页面；
- 网络设置的真实执行接入：语言详情安装使用所选 Provider 来源与通用代理，下载中心使用 `downloads` 代理，CLI `tool` 与项目终端按语言工具解析 pip/npm 等包源；配置不会静默改写用户的 pip/npm 配置文件；
- 项目页和语言详情可按需只读解析 Python venv/Poetry/Pipenv/Conda、Node 本地依赖、.NET 本地工具、Maven/Gradle Wrapper、Rust toolchain 与 Go workspace；扫描不递归、不执行外部命令、不跟随重解析点；
- 环境诊断提供 PATH/命令与 Shim、托管语言工具、项目环境、Provider 配置、缓存/磁盘、Provider 连通性六个显式扫描域；进入页面不会自动扫描，联网与缓存遍历必须单独选择；
- 独立的领域核心与命令行入口；
- `新终端会话 > 项目 > 全局 > 自动选择` 的解析模型；前三层都可同时固定版本选择器、Runtime ID 和 Provider ID，项目精确身份保存在 `autoenvplus.toml` 的 `[tool-identities]`；
- PATH 重复、失效和命令冲突检查，以及 WinUI 可审计快照列表和安全一键回滚；
- WinUI/CLI 共用的 PATH、命令赢家、版本探测、托管状态和全局选择聚合诊断，以及 WinUI 结构化 JSON 导出；
- python.org、Node.js、Eclipse Temurin 和 Microsoft .NET 官方发行目录；Python、Node.js、Temurin 使用各自官方 SHA-256 资产，.NET SDK 使用 Microsoft release metadata 中的 SHA-512 Windows ZIP；
- 数据-only Runtime Provider 插件：schema 2 用 `languageToolId` 精确绑定 CPython、Node.js、Eclipse Temurin、.NET SDK、MSVC Build Tools、Clang、GCC、CMake 或 Ninja；旧 schema 1 的 `runtimeKind` 仍可兼容导入并规范化为 schema 2。插件不能声明 DLL、脚本、任意命令、注册表/环境写入或自定义安装目录，第三方 checksum 也不会冒充内置 Provider 的官方签名；
- Python Windows release manifest 的 Sigstore v0.3 验证：固定 python.org 版本系列发布身份、内置 trusted-root 快照、Fulcio 证书链/SCT、Rekor SET/Merkle 包含证明/签名检查点和清单签名，缺失或失败时禁止降级；
- Node.js `SHASUMS256.txt.asc` OpenPGP 验证、固定主指纹/签名子钥映射和历史密钥时限策略；
- Eclipse Adoptium 官方 GA/LTS 版本线动态选择、Temurin 包本体 detached OpenPGP 验证、固定主指纹和签名失败删除缓存策略；
- .NET SDK 从 Microsoft 官方 release index 读取 active/maintenance 通道中的稳定 SDK，支持 x64/x86/ARM64 Windows ZIP，并隔离安装到受管根下的版本/架构目录；
- WinUI 安装确认页会逐条展示下载地址、哈希算法、包哈希和校验来源，并明确提示数字签名验证状态；
- 防 Zip Slip、限制下载/解压大小、原子提交的受管理 ZIP 安装器；每个安装目录写入绑定 Provider、版本、架构、包哈希与入口 SHA-256 的收据，重复安装会重新验证收据和入口内容；
- 带 ETag/Last-Modified/If-Range 的断点续传，以及按 SHA-256/SHA-512 算法和包哈希分区的下载缓存；
- 独立的受管下载中心：支持 HTTPS URL、1/2/4/8/16 连接的 HTTP Range 分段、本地文件导入、进度、取消、大小上限和可选 SHA-256/SHA-512 预期哈希；服务器不支持 Range、缺少稳定实体标识或实体变化时使用单流或明确失败，不把分段数量等同于速度保证；
- 下载库为每个完成文件记录内容 SHA-256、来源、传输模式和可选预期哈希证据；列举时会重新计算有预期证据的当前内容身份，同长度替换也会标为 stale；最终提交、覆盖和删除通过同一跨进程库事务锁与原子清单串行化，清单失败时恢复隔离文件；取消残留仍限制在受管 staging，下载或导入内容不会自动执行；
- 下载库中的 `.whl` 可在 WinUI 中生成并复核本地 pip 安装计划：选择受管 Python、受管虚拟环境名及“严格离线（仅当前 wheel、`--no-deps`）”/已配置网络模式，把虚拟环境、`TEMP`/`TMP` 和 pip 缓存固定在受管根；执行前再次核对计划、运行时、wheel 和环境可执行文件；pip 的 stdout/stderr 会持续读取且每路只保留末尾 65,536 字符，界面明确标记截断；
- 安装注册表 schema 2、精确全局选择、`which`/`exec`、Win32 原生 Shim 与 CMD 回退；全局 profile 同时保存 selector、Runtime ID 和 Provider ID，注册表 schema 2 记录哈希算法与值，Core 和原生 Shim 仍可读取旧 schema 1 的 `packageSha256`；
- 运行时目录提交、注册表登记和全局选择之间的补偿式安装事务；注册表、全局 profile、安装、卸载与全局默认切换共享固定跨进程事务锁序，避免并发丢更新、孤立目录和悬空选择；
- PowerShell 会话模块、受管 Profile 块、修改前快照与并发安全回滚；
- 项目现有版本文件导入、精确 `autoenvplus.lock` 和引用保护卸载；
- 项目清单 `[tools]` + `[tool-identities]` 预解析、精确 Runtime ID/Provider ID 新终端会话变量、pip/npm Provider 来源与共享代理环境，以及 WinUI 中 Windows Terminal/Windows PowerShell 宿主选择；未检测到 `wt.exe` 时回退到独立 PowerShell，来源、代理和环境参与启动前复检，不修改父进程、用户 PATH、项目选择或全局选择；
- 可通过 `AUTOENVPLUS_HOME` 和设置页把运行时、Shim、状态与日志放到非系统盘，CLI 的显式 `--root` 仍具有最高优先级；
- pip/npm/pnpm/Yarn/NuGet/Maven/Gradle/vcpkg/Conan 存储发现、统计、事务迁移、配置快照与回滚；纯缓存目录还支持可恢复隔离和二次确认后的永久清空；
- Visual Studio/MSVC、Windows SDK、clang/GCC/CMake/Ninja 工具链发现；
- 通过精确 WinGet 白名单安装 MSVC、LLVM、MinGW-w64/GCC、CMake 和 Ninja，并提供页面级操作锁、进度和进程树取消；
- 基于实际 `cl.exe` 布局的 MSVC Host/Target 架构选择与开发终端预览；
- 保留用户内容、可预览和回滚的项目级 `CMakeUserPresets.json` 生成；
- 有大小与条目上限、跨进程写入锁和凭据脱敏的活动记录，以及可筛选、复制摘要的 WinUI 审计页；
- 第一组自动化测试；
- 可复现组合 WinUI、单文件 CLI、原生 Shim 和逐文件 SHA-256 清单的自包含便携发布脚本；
- 生产证书缺失时安全失败、严格绑定 Publisher/版本/架构/HTTPS 更新 URI 的 MSIX 与 AppInstaller 发布脚本；
- 产品范围、架构和安全约束文档。

## 语言包与 Provider 插件

语言包使用 [schema 1 JSON Schema](schemas/language-pack.schema.json) 和[模板](examples/language-pack.template.json)，用于添加新语言、语言工具及其 Provider 元数据。导入后默认停用，只有显式启用才进入有效目录；它是严格 data-only 格式，不能携带 DLL、脚本、安装钩子或任意命令。完整契约见[语言包说明](docs/LANGUAGE-PACKS.md)。

Runtime Provider 插件清单使用公开的 [schema 2 JSON Schema](schemas/runtime-provider-plugin.schema.json)，以 `languageToolId` 绑定一个具有受管 ZIP 适配器的语言工具；旧 schema 1 `runtimeKind` 清单仍可兼容导入，导入后统一规范化为 schema 2。[模板](examples/runtime-provider-plugin.template.json) 只提供字段结构，其中的 `example.invalid`、占位许可证、版本和全零哈希必须全部替换。完整字段、生命周期、CLI、C/C++ 边界和安全检查见 [Provider 插件指南](docs/PROVIDER-PLUGINS.md)。插件不再拥有独立一级页面，而是在匹配语言的“高级设置”中导入、启停、删除和选择安装来源。

导入先做严格解析和预览，实际复制到 `<managed-root>\plugins\runtime-providers` 后仍保持停用。内置官方 Provider 继续作为默认推荐；启用第三方插件后，目录或安装仍需精确选择 `plugin:<id>`，类别不匹配、插件未启用或来源不存在时不会静默回退。同一版本/架构由多个 Provider 提供时，各自使用 `plugin-<kind>-<plugin-id>-<version>-<arch>` 运行时 ID 和 `runtimes\<kind>\plugins\<plugin-id>` 目录，WinUI 不自动改写全局默认。插件删除后即使同一 ID 被另一类别重新导入，既有运行时也不会被覆盖。

插件只描述 HTTPS ZIP、SHA-256/SHA-512、checksum 来源、架构、归档根和预期 `.exe`。清单及 checksum 均由第三方提供，逐字节哈希匹配不等于 AutoEnvPlus 验证了发布者签名。C/C++ 的声明式便携 ZIP Provider 与现有固定 WinGet 白名单流程并存；插件不能声明 `vcvarsall.bat`、安装器参数或任意激活脚本，完整 MSVC 环境仍由内置发现和工具链激活流程负责。

## 构建

要求：

- Windows 10 1809 或更高版本；
- Visual Studio 2022（包含 Windows 应用 SDK/C++ 桌面构建工具），或 .NET SDK 10.0.200；
- Windows SDK 10.0.26100 或更高版本；本机发布链已用 `10.0.28000.2114` 验证；
- x64 环境。

```powershell
dotnet restore AutoEnvPlus.sln
dotnet build AutoEnvPlus.sln -c Debug -p:Platform=x64
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj
```

运行诊断 CLI：

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- doctor
dotnet run --project src\AutoEnvPlus.Cli -- doctor --json
dotnet run --project src\AutoEnvPlus.Cli -- list
dotnet run --project src\AutoEnvPlus.Cli -- catalog node --lts --limit 10
dotnet run --project src\AutoEnvPlus.Cli -- catalog node --asset 24.18.0
dotnet run --project src\AutoEnvPlus.Cli -- catalog java --feature 21 --asset 21.0.11
dotnet run --project src\AutoEnvPlus.Cli -- catalog python --asset 3.14.6
dotnet run --project src\AutoEnvPlus.Cli -- catalog dotnet --asset 10.0.302
dotnet run --project src\AutoEnvPlus.Cli -- provider list
dotnet run --project src\AutoEnvPlus.Cli -- plugin list --json
dotnet run --project src\AutoEnvPlus.Cli -- plugin import examples\runtime-provider-plugin.template.json
dotnet run --project src\AutoEnvPlus.Cli -- plugin import D:\packages\vendor-python.json --yes
dotnet run --project src\AutoEnvPlus.Cli -- plugin enable plugin:vendor-python --yes
dotnet run --project src\AutoEnvPlus.Cli -- catalog python --provider plugin:vendor-python
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.13.0 --provider plugin:vendor-python --arch x64 --yes
dotnet run --project src\AutoEnvPlus.Cli -- plugin disable plugin:vendor-python --yes
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6 --yes
dotnet run --project src\AutoEnvPlus.Cli -- install dotnet 10.0.302 --yes
dotnet run --project src\AutoEnvPlus.Cli -- list --managed
dotnet run --project src\AutoEnvPlus.Cli -- uninstall python-3.14.6-x64
dotnet run --project src\AutoEnvPlus.Cli -- use python 3.14 --global
dotnet run --project src\AutoEnvPlus.Cli -- which python
dotnet run --project src\AutoEnvPlus.Cli -- exec python -- --version
dotnet run --project src\AutoEnvPlus.Cli -- tool pip -- --version
dotnet run --project src\AutoEnvPlus.Cli -- network show global
dotnet run --project src\AutoEnvPlus.Cli -- network show pip --json
dotnet run --project src\AutoEnvPlus.Cli -- download url https://example.invalid/package.whl --file package.whl --connections 8
dotnet run --project src\AutoEnvPlus.Cli -- download url https://example.invalid/package.whl --file package.whl --connections 8 --sha256 $expectedSha256 --yes
dotnet run --project src\AutoEnvPlus.Cli -- download import D:\packages\package.whl --sha512 $expectedSha512 --yes
dotnet run --project src\AutoEnvPlus.Cli -- download list --json
dotnet run --project src\AutoEnvPlus.Cli -- shim install
dotnet run --project src\AutoEnvPlus.Cli -- shim install --yes
dotnet run --project src\AutoEnvPlus.Cli -- shell powershell
dotnet run --project src\AutoEnvPlus.Cli -- shell powershell --show-module
dotnet run --project src\AutoEnvPlus.Cli -- shell powershell --install-profile --yes
dotnet run --project src\AutoEnvPlus.Cli -- shell powershell --rollback C:\path\to\snapshot.json
dotnet run --project src\AutoEnvPlus.Cli -- shell powershell --rollback C:\path\to\snapshot.json --yes
dotnet run --project src\AutoEnvPlus.Cli -- storage list
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate gradle D:\AutoEnvPlusCaches\gradle
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate pnpm D:\AutoEnvPlusCaches\pnpm
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate nuget D:\AutoEnvPlusCaches\nuget-packages
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate nuget-http D:\AutoEnvPlusCaches\nuget-http
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate nuget-plugins D:\AutoEnvPlusCaches\nuget-plugins
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate vcpkg D:\AutoEnvPlusCaches\vcpkg
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate conan D:\AutoEnvPlusCaches\conan
dotnet run --project src\AutoEnvPlus.Cli -- storage migrate maven D:\AutoEnvPlusCaches\maven --yes
dotnet run --project src\AutoEnvPlus.Cli -- storage rollback C:\path\to\snapshot.json --yes
dotnet run --project src\AutoEnvPlus.Cli -- toolchain list
dotnet run --project src\AutoEnvPlus.Cli -- toolchain install cmake
dotnet run --project src\AutoEnvPlus.Cli -- toolchain install mingw
dotnet run --project src\AutoEnvPlus.Cli -- toolchain activate msvc --instance 42f2210f --host x64 --target x86
dotnet run --project src\AutoEnvPlus.Cli -- resolve python 3.13 3.12.8 3.13.5
dotnet run --project src\AutoEnvPlus.Cli -- project import D:\path\to\project
dotnet run --project src\AutoEnvPlus.Cli -- project import D:\path\to\project --write
dotnet run --project src\AutoEnvPlus.Cli -- project lock D:\path\to\project
dotnet run --project src\AutoEnvPlus.Cli -- project terminal D:\path\to\project
dotnet run --project src\AutoEnvPlus.Cli -- project terminal D:\path\to\project --yes
dotnet run --project src\AutoEnvPlus.Cli -- project cmake-preset D:\path\to\project --instance 42f2210f --host x64 --target x86
dotnet run --project src\AutoEnvPlus.Cli -- project cmake-preset D:\path\to\project --instance 42f2210f --host x64 --target x86 --write --yes
```

上面的 `example.invalid`、`plugin:vendor-python`、`$expectedSha256`、`$expectedSha512` 和本地路径都是待替换的示例值；预期哈希应来自独立可信渠道。模板导入命令仅演示预览，不能直接启用模板中的占位来源。`network show` 只读；`plugin import`/`enable`/`disable`/`delete`、`download url` 与 `download import` 未提供 `--yes` 时只显示计划，不写对应变更，替换既有下载库文件还必须显式提供 `--overwrite`。

NuGet 的全局包、HTTP 下载缓存和插件缓存是三个独立目录。若目标是释放系统盘空间，需要分别迁移 `nuget`、`nuget-http` 和 `nuget-plugins`；只迁移 `nuget` 不会改变另外两个目录。

“缓存与存储”页的清理是两阶段操作：先把通过复检的纯缓存内容同卷移入受控隔离区，此时仍可恢复且尚未释放磁盘空间；只有再次选择“永久清空”并确认后才逐项删除隔离内容。Gradle User Home 与 Conan Home 还包含配置、Profile 等非缓存数据，因此不提供整目录清理，只保留迁移能力。

AutoEnvPlus 的受管数据根按 `--root`、用户环境变量 `AUTOENVPLUS_HOME`、`%LOCALAPPDATA%\AutoEnvPlus` 的顺序解析。设置页可以选择非系统盘上的绝对目录，并在确认后写入用户级 `AUTOENVPLUS_HOME`；新目录在重启 AutoEnvPlus 后生效。Provider 插件清单/启用状态、插件运行时、网络配置、下载库与暂存、受管 Python 虚拟环境、pip `TEMP`/`TMP` 和 pip 缓存都位于该根下。更改数据根不会自动迁移运行时、插件、缓存或快照，也不会删除旧目录。

例如要把 AutoEnvPlus 自有数据固定到 D 盘，可在设置页选择 `D:\codex\autoenvplus-data`，或在启动前设置 `AUTOENVPLUS_HOME`。这只能约束 AutoEnvPlus 创建并传给受管操作的目录；系统组件、既有工具配置以及 pip 依赖的第三方构建/安装脚本仍可能按自身规则写入用户 Profile、系统临时目录或 C 盘，AutoEnvPlus 不宣称能够拦截任意子进程写盘。

运行桌面原型：

```powershell
dotnet run --project src\AutoEnvPlus.App -p:Platform=x64
```

生成无需预装 .NET/Windows App Runtime 的 x64 自包含便携包：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1
```

输出位于 `artifacts\AutoEnvPlus-win-x64` 和 `artifacts\AutoEnvPlus-win-x64.zip`。它仍是未签名的开发阶段便携包，不等同于正式安装器；完整结构和限制见 [分发说明](docs/DISTRIBUTION.md)。

快速验证打包规则并生成仅供本机测试的开发签名 MSIX：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\test-packaging.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-msix.ps1 `
  -PackageVersion 0.1.0.0 `
  -PackageUri https://github.com/yeyouyby/autoenvplus/releases/download/v0.1.0/AutoEnvPlus-win-x64.msix `
  -AppInstallerUri https://github.com/yeyouyby/autoenvplus/releases/latest/download/AutoEnvPlus.appinstaller `
  -DevelopmentCertificate
```

该模式生成短期开发证书，只输出公钥 `.cer`，签名后删除私钥和 PFX，且不会修改 Windows 证书信任库；得到的是仅供开发测试的测试签名 MSIX，不是生产签名。正式发布必须提供代码签名 PFX、精确等于证书 Subject 的 `-Publisher`，并建议提供 RFC 3161 `-TimestampUri`；详见 [分发说明](docs/DISTRIBUTION.md)。

## 当前边界

- 下载中心是显式的 URL/本地文件工作流，不会透明接管 pip resolver，也不会把 pip 正在下载的单个 PyTorch 资产自动改造成多连接任务；可先把已知 wheel 下载/导入受管库再审核安装。严格离线模式只安装当前 wheel 且不解析依赖；依赖解析仅在用户显式选择联网模式时发生。
- 多连接只有在服务器支持字节范围、长度已知且能用强 ETag 或 Last-Modified 保持实体一致时启用。连接数更多不保证更快，也可能受服务端限流、代理、磁盘和网络条件影响。
- 每个文件都会计算内容 SHA-256 作为稳定标识；只有用户同时提供并成功匹配预期 SHA-256 或 SHA-512 时，才有“与该预期值一致”的完整性证据。没有预期哈希的文件不能因此视为可信。
- 镜像和代理改变传输路径，不自动建立发布者信任。Python、Node.js 与 Temurin 仍执行各自签名策略；自定义 .NET release index 当前只有该索引提供的 SHA-512 checksum evidence，缺少独立发布者签名，使用者必须把镜像元数据本身纳入信任判断。
- 声明式插件的清单、下载 URL、checksum 来源和预期哈希都由插件作者提供。默认停用和数据-only 约束降低导入风险，但不能证明第三方 `.exe` 安全；插件资产不会继承内置 Python/Node.js/Temurin 的发布者签名结论，也没有自动更新或撤销通道。
- 代理/镜像 URI 拒绝嵌入式凭据、query 和 fragment。AutoEnvPlus 自己的 HTTP Client 支持 Windows 集成身份，但没有独立用户名/密码代理的凭据存储；受管工具还会清理继承的 `ALL_PROXY`，只应用已审核的 HTTP(S) 代理设置。
- 本地 wheel 安装调用真实 pip，可能创建部分环境状态或运行依赖的构建/安装逻辑；失败和取消都不是事务回滚，界面不会声称已恢复到安装前状态。
- 当前开发 MSIX 不是生产签名，Windows 11 的完整安装、升级和功能端到端验证仍未完成。

## 设计原则

1. PATH 只固定加入一次 Shim 目录，日常切换不反复重写 PATH。
2. 已有安装默认只读，只有 AutoEnvPlus 托管的安装才能直接卸载。
3. AutoEnvPlus 自有的持久配置修改经过计划、预览、快照、验证和回滚；pip、WinGet 等外部进程明确标注其非事务边界。
4. 默认只修改当前用户；系统级修改才按需启动提权 Helper。
5. GUI、CLI、Shell 集成共用同一个核心引擎。

PowerShell 安装命令默认只输出计划。只有同时提供 `--install-profile --yes` 才会生成模块、会话 Shim 和受管 Profile 块；回滚同样先预览，再通过 `--yes` 确认。开发构建既支持 `autoenvplus.exe`，也支持 `dotnet autoenvplus.dll` 形式的固定前置参数。

原生 Shim 是不加载 CLR 的 x64 Win32 程序，直接读取同一份托管注册表、项目清单、全局 Profile 和新终端会话变量。它覆盖 18 个命令：`python`、`python3`、`pip`、`pip3`、`node`、`npm`、`npx`、`java`、`javac`、`jar`、`dotnet`、`cl`、`clang`、`clang++`、`gcc`、`g++`、`cmake`、`ninja`。PATH 只需配置一次 Shim 目录；之后的精确全局、项目和新终端会话切换都不重写用户 PATH。启动受管 .NET SDK 时只为该子进程设置选中安装根对应的 `DOTNET_ROOT`，并关闭多级查找。构建产物缺失时安装器才显式回退到 CMD 包装器。在本机 60 次交替差分基准中，直接子进程中位耗时为 11.73 ms，经 Shim 为 27.28 ms，中位额外开销 15.55 ms。

详细内容见 [产品规格](docs/PRODUCT.md)、[技术架构](docs/ARCHITECTURE.md)、[阶段交接](docs/HANDOFF.md)、[Provider 插件指南](docs/PROVIDER-PLUGINS.md)、[安全模型](docs/SECURITY.md)、[分发说明](docs/DISTRIBUTION.md)、[第三方组件声明](THIRD-PARTY-NOTICES.md) 和 [路线图](docs/ROADMAP.md)。

## 许可证

AutoEnvPlus 本体以 [`AGPL-3.0-only`](LICENSE) 发布。分发修改版时必须按 GNU Affero General Public License v3 提供对应源码；如果修改版允许用户通过网络与程序交互，还必须遵守 AGPLv3 第 13 节的网络源码提供要求。第三方组件不被重新许可，仍分别适用 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) 中列出的 Apache-2.0、MIT、BSD 等原许可证。
