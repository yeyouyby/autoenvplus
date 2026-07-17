# 声明式 Runtime Provider 插件

AutoEnvPlus 的 Runtime Provider 插件用于把第三方、社区或组织内部发行版接入同一套语言工具目录、下载校验、受限 ZIP 解压、托管注册表和版本隔离流程。当前公开格式是严格的 schema 2 JSON 数据文件，用 `languageToolId` 绑定一个精确语言工具；它不加载第三方 DLL，也不执行插件声明的脚本、安装命令或安装后钩子。导入器仍接受旧 schema 1 `runtimeKind` 清单，并在导入时映射、规范化为 schema 2。

公开契约见 [JSON Schema](../schemas/runtime-provider-plugin.schema.json)，可复制的起点见 [清单模板](../examples/runtime-provider-plugin.template.json)。模板中的 `example.invalid`、占位许可证、版本和全零哈希都必须替换；该文件不是可用的下载源，也不代表 AutoEnvPlus 为任何第三方发行版背书。

## 支持范围

schema 2 支持以下 9 个 `languageToolId`，每个都对应一个已注册的受管 ZIP 桥接。前四个工具还有内置官方归档 Provider；后五个工具同时保留固定 WinGet ID 的真实受管安装适配器。表中的旧 `runtimeKind` 只用于说明 schema 1 兼容导入映射，不得出现在 schema 2 清单中。

| schema 2 `languageToolId` | 旧 schema 1 `runtimeKind` | 典型入口 | 代理作用域 | 插件边界 |
|---|---|---|---|---|
| `cpython` | `python` | `python.exe` | `runtime-python` | 便携 CPython 兼容发行版 |
| `nodejs` | `nodejs` | `node.exe` | `runtime-node` | 便携 Node.js 兼容发行版 |
| `eclipse-temurin` | `java` | `java.exe` | `runtime-java` | 便携 JDK/JRE 发行版 |
| `dotnet-sdk` | `dotnet` | `dotnet.exe` | `runtime-dotnet` | 便携 .NET SDK 兼容发行版 |
| `msvc-build-tools` | `msvc` | `cl.exe` | `runtime-cpp` | 只能描述 ZIP 与入口，不能声明 `vcvarsall.bat`、VS Installer 或 workload 参数 |
| `clang` | `llvm` | `clang.exe` | `runtime-cpp` | 便携 LLVM/Clang 发行版 |
| `gcc` | `mingw` | `gcc.exe` | `runtime-cpp` | 便携 MinGW-w64/GCC 发行版 |
| `cmake` | `cmake` | `cmake.exe` | `runtime-cpp` | 便携 CMake 发行版 |
| `ninja` | `ninja` | `ninja.exe` | `runtime-cpp` | 便携 Ninja 发行版 |

这里的“支持”是声明式 ZIP 目录和安装支持，不是允许插件定义任意安装协议，也不能把 140 条工具作用域 Provider Profile 误称为 140 个插件。尤其是 MSVC，一个可定位的 `cl.exe` 不能替代 `INCLUDE`、`LIB`、Windows SDK、Host/Target 架构及开发者终端激活；这些仍由 C/C++ 语言详情中的内置发现与激活工作流负责。

## 清单模型

根对象必须声明：

- `schemaVersion`：当前固定为 `2`；
- `id`：小写、稳定的插件 ID；导入后 Provider ID 为 `plugin:<id>`；
- `displayName`、`vendor`、`homepage` 和 `license`：用于审核来源及分发条款；
- `languageToolId`：上表中的一个精确内置工具 ID；该工具必须具有已注册的受管 ZIP 适配桥接；
- `releases`：静态发行目录。

schema 1 兼容导入要求 `runtimeKind`，且不得同时带 `languageToolId`；解析器按上表映射到精确工具 ID，并把受管副本序列化为 schema 2。schema 2 反过来拒绝 `runtimeKind`。公开 JSON Schema 和模板只描述 schema 2，新清单不应继续生成 schema 1。

每条 release 声明精确 `version`、至少一个 `channels`、可选 `releaseDate`，以及按架构区分的 assets。每个 asset 必须声明：

- `architecture`：`x86`、`x64` 或 `arm64`；
- `fileName`：普通、非保留的 `.zip` 文件名；
- `downloadUri`：资产的绝对 HTTPS URI；
- `checksumSourceUri`：发布预期 checksum 的 HTTPS 页面或清单；
- `sha256` 或 `sha512`：恰好一个，且长度必须与算法匹配；
- 可选 `archiveRoot`：ZIP 内需要去除的安全相对根；
- `expectedExecutableRelativePath`：解压后必须存在的 Windows `.exe` 相对路径。

解析器拒绝未知字段、重复 JSON 属性、注释、尾随逗号、路径逃逸、绝对归档路径、Windows 保留名称、重解析点、非 ZIP 资产和非 `.exe` 入口。URI 必须是无 user-info、query、fragment 的绝对 HTTPS URI。单个清单最多 512 KiB、256 条 release，每条最多 8 个 assets；受管根最多导入 256 个插件。JSON Schema 方便编辑器提前提示，实际导入仍以 Core 的严格解析器为最终裁决。

schema 2（以及兼容导入的 schema 1）没有也不接受以下能力：

- DLL、程序集、COM 组件或原生插件加载；
- PowerShell、批处理、JavaScript、Python 或其他脚本；
- 任意可执行文件、命令行、安装参数、安装后钩子或卸载命令；
- 注册表写入、用户/系统环境变量写入、PATH 修改或服务/计划任务创建；
- 自定义安装目标、缓存目标、临时目录或受管根之外的输出路径；
- 自定义 WinGet ID、安装器 URL override 或 MSVC workload 参数。

## 导入、启用与删除

WinUI 在匹配语言详情的“高级设置”中管理插件，不存在独立的“Provider 插件”一级页面；CLI 使用同一个受管插件仓库。生命周期固定为：

```text
本地 JSON -> 严格解析与规范化 -> 导入预览 -> 复制到受管插件目录（默认停用）
          -> 显式启用 -> 在对应语言工具中手动选择 -> 生成安装预览 -> 安装
          -> 停用或删除清单（不卸载已安装运行时）
```

导入预览显示 Provider ID、供应商、`languageToolId`、所属语言、release/asset 数量、下载主机、哈希算法、入口和许可证。只有用户明确导入后，规范化的 schema 2 清单才会复制到受管根；导入不会自动启用。启用前会再次验证清单，损坏的已启用插件或损坏的 activation state 会让插件 Registry 安全失败，而不是跳过问题后继续加载其他第三方来源。

停用只让该 Provider 不再参与后续目录查询和新安装。删除会移除受管 JSON 清单及其启用状态；若删除清理失败，隔离副本仍留在受管插件目录中供审计。两者都不会删除已经登记的运行时、项目锁或共享下载缓存。现有安装仍可按托管注册表运行和卸载，但要再次查询或安装该第三方目录，必须重新导入并启用对应清单。

## CLI 示例

以下命令把受管数据固定到 D 盘。没有 `--yes` 的导入、启用、停用、删除和安装命令只展示计划，不应产生对应变更。

```powershell
$root = 'D:\codex\autoenvplus-data'

# 查看内置与第三方 Provider；plugin list 只显示已导入插件
dotnet run --project src\AutoEnvPlus.Cli -- provider list --root $root
dotnet run --project src\AutoEnvPlus.Cli -- provider list --kind llvm --root $root --json
dotnet run --project src\AutoEnvPlus.Cli -- plugin list --root $root

# 先预览模板或真实清单；模板必须替换全部占位值后才能实际使用
dotnet run --project src\AutoEnvPlus.Cli -- plugin import examples\runtime-provider-plugin.template.json --root $root
dotnet run --project src\AutoEnvPlus.Cli -- plugin import D:\packages\vendor-python.json --root $root --yes
dotnet run --project src\AutoEnvPlus.Cli -- provider inspect plugin:vendor-python --root $root
dotnet run --project src\AutoEnvPlus.Cli -- plugin enable plugin:vendor-python --root $root --yes

# Provider 选择是精确约束；不会静默回退到同一工具的内置或其他插件 Provider
dotnet run --project src\AutoEnvPlus.Cli -- catalog python --provider plugin:vendor-python --root $root
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.13.0 --provider plugin:vendor-python --arch x64 --root $root
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.13.0 --provider plugin:vendor-python --arch x64 --root $root --yes

# C/C++ 便携 ZIP 插件使用相同目录/安装命令；固定 WinGet 流程仍使用 toolchain install
dotnet run --project src\AutoEnvPlus.Cli -- catalog llvm --provider plugin:vendor-llvm --root $root
dotnet run --project src\AutoEnvPlus.Cli -- install llvm 20.1.0 --provider plugin:vendor-llvm --arch x64 --root $root --yes
dotnet run --project src\AutoEnvPlus.Cli -- toolchain install llvm --yes

# 状态变化与删除不会卸载既有运行时
dotnet run --project src\AutoEnvPlus.Cli -- plugin disable plugin:vendor-python --root $root
dotnet run --project src\AutoEnvPlus.Cli -- plugin disable plugin:vendor-python --root $root --yes
dotnet run --project src\AutoEnvPlus.Cli -- plugin delete plugin:vendor-python --root $root --yes
```

插件清单中的显式下载 URL 保持权威，不会套用内置 Provider 的官方镜像基址；镜像始终属于具体 `toolId/providerId`。插件下载只使用设置中通用代理经对应 `runtime-*` 兼容作用域解析后的结果和 `NO_PROXY`，C/C++ 五个工具共用 `runtime-cpp`。这避免把只适用于 python.org、nodejs.org、Adoptium 或 Microsoft 元数据协议的 Provider 来源规则误用到第三方 URL。

## 精确选择与磁盘布局

内置官方 Provider 保持默认推荐。第三方 Provider 只有在显式启用、`languageToolId` 匹配且通过 `--provider plugin:<id>` 或 WinUI Provider 选择后才参与目录查询和安装。插件运行时 ID 固定为 `plugin-<kind>-<plugin-id>-<version>-<architecture>`；这里的 `<kind>` 是兼容现有注册表与磁盘布局的内部适配类别，不是 schema 2 作者字段。相同工具、版本和架构可以由多个 Provider 同时提供，它们的 Provider ID、运行时 ID 和目标路径都不同，不会用一个来源静默替代另一个来源。插件删除后可以保留既有运行时，重导入也不会覆盖旧注册项。发生同版本来源冲突时，WinUI 不自动把新安装设为全局默认；精确全局 profile、项目 `[tool-identities]`、新终端会话变量、项目锁和托管注册表都保留实际 Runtime ID 与 Provider ID。

版本选择按 SemVer precedence 排序，但 CLI `catalog --asset` 与 `install <exact-version>` 会比较包括 build metadata 在内的完整版本身份。托管注册表拒绝同一 Provider、类别和架构下由不同 RuntimeId 登记的等价 precedence（例如 `1.0.0+foo` 与 `1.0.0+bar`），也拒绝大小写不敏感的重复 RuntimeId；要引入替代包身份，必须先卸载旧记录。这样不会把用户确认的 `+bar` 静默解析为仍保留的 `+foo`。

插件与安装内容全部位于受管根：

```text
<managed-root>\plugins\runtime-providers\<plugin-id>.json
<managed-root>\state\runtime-provider-plugins.json
<managed-root>\runtimes\<kind>\plugins\<plugin-id>\<version>\<architecture>
<managed-root>\downloads\sha256|sha512\<package-hash>
```

例如设置 `AUTOENVPLUS_HOME=D:\codex\autoenvplus-data` 后，插件清单、activation state、插件运行时、下载缓存和安装暂存都保持在该 D 盘受管根内。导入来源可以位于其他位置，但导入操作复制而不移动源文件。更改受管根不会自动迁移或删除旧根；系统组件和插件运行时以后启动的第三方程序仍可能按自身规则写入用户 Profile 或 C 盘，AutoEnvPlus 不能把受管布局扩展成任意子进程的文件系统沙箱。

## 信任边界

声明式插件的 `sha256`/`sha512` 和 `checksumSourceUri` 都由插件作者提供。AutoEnvPlus 会逐字节验证下载包与插件 JSON 中的声明哈希一致，但 schema 2（以及兼容导入的 schema 1）不会抓取、解析或验证 `checksumSourceUri` 的内容；该 URI 只是导入预览和安装确认中的人工核对引用。因此它既不是“Verified by”，也不会变成 Python/Node.js/Temurin 内置 Provider 所执行的发布者签名验证。`checksumSourceUri` 应指向独立发布的 checksum 页面或清单；如果它与 `downloadUri` 相同，WinUI 会提示证据较弱，但无论是否独立都不能据此推导发布者身份。

启用插件意味着用户信任清单作者选择下载主机、ZIP、入口和 checksum。数据-only 设计缩小了导入时的能力，但下载到的 `.exe` 本身仍可能恶意；AutoEnvPlus 不会在导入或下载完成时自动执行它，实际运行前仍应核对供应商、许可证、独立 checksum 渠道和组织安全策略。

受管安装器仍执行 HTTPS、算法/长度复检、下载大小上限、ZIP 条目与解压大小上限、Zip Slip 防护、重解析点拒绝、预期入口复检和受管目录原子提交。插件无法关闭这些检查，也无法通过声明目标路径把文件写到受管根之外。

当前清单是用户手动导入的静态目录，没有自动发现、远程自动更新或插件代码签名信任根。精确全局、项目 `[tool-identities]` 和新终端会话 Provider pin 已可用；签名插件包、可审计更新通道、组织策略与撤销列表仍属于后续演进方向。在这些能力完成前，不应把一个曾经审核过的 JSON 当成会自动跟随供应商安全更新的订阅。
