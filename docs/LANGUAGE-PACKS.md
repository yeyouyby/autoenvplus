# 语言、语言工具与语言包

AutoEnvPlus 把语言与用于该语言的程序分开建模。语言是稳定的导航和配置容器；语言工具是编译器、解释器、Runtime、SDK、包管理器、构建系统、调试器、格式化器、linter、语言服务器、版本管理器或虚拟环境实现。

例如 Python 是语言，CPython 与 PyPy 是工具；C/C++ 是语言，MSVC、GCC、Clang、CMake 和 Ninja 是工具。

## 内置目录

内嵌 schema 1 目录包含：

| 对象 | 数量 | 说明 |
|---|---:|---|
| 语言 | 45 | 默认启用恰好 Top 10 |
| 工具 | 136 | 真实软件条目，不等于安装器 |
| Provider Profile | 140 | 工具作用域能力/分发元数据，复用 5 个 Provider ID |
| Provider 来源槽 | 83 | 精确归属于工具、Provider 和槽 |
| 受管安装适配器 | 9 | 4 个官方归档 + 5 个固定 WinGet |

Top 10 是 Python、JavaScript、TypeScript、Java、C、C++、C#、Go、Rust 和 PHP。其他语言仍在目录中，可由用户启用，或在显式 PATH 重检写入的快照发现相关命令后显示。

语言页打开只读取 `language-tool-inventory.json`，不会自动扫描 PATH。语言可见性状态与搜索/筛选分开持久化；只有启用、隐藏或重置会改变状态。

## 能力不是承诺

每个工具 Profile 明确声明发现、安装、版本切换、项目固定、会话激活、包管理、虚拟环境、缓存、镜像、调试、格式化和 lint 等能力。`capabilities.install` 只有在真实适配器桥接时才为真。

当前 9 个适配器为：

- 官方归档：`cpython`、`nodejs`、`eclipse-temurin`、`dotnet-sdk`；
- 固定 WinGet：`msvc-build-tools`、`clang`、`gcc`、`cmake`、`ninja`。

`windows-sdk` 有发现证据，但没有独立 WinGet/归档安装适配器。140 条 Profile 不能描述成 140 个插件，136 个工具也不能描述成 136 个可安装工具。

## Provider 来源

镜像属于具体 `languageToolId + providerId`，来源槽再增加稳定 `slotId`。一个 CPython Provider 可以声明 PyPI 与 Python 下载目录，一个 Node.js Provider 可以声明 npm registry；这些端点没有跨 Provider 的通用拼接语义。

用户可覆盖允许修改的内置槽、恢复默认值，或添加/启停/删除具名 HTTPS 来源。HTTP(S) 代理和 `NO_PROXY` 是独立传输策略，只在设置页管理。

## data-only 语言包

[`language-pack.schema.json`](../schemas/language-pack.schema.json) 定义 schema 1。语言包可以：

- 添加新语言；
- 添加属于新语言或现有内置语言的工具；
- 声明工具 Provider 元数据、能力和 HTTPS 来源槽。

语言包不能覆盖任何内置或已导入 ID。导入先严格解析和预览，受管副本初始状态固定为停用；只有用户显式启用后才合并进有效目录。

语言包故意没有以下能力：

- DLL、程序集、COM 或原生插件；
- PowerShell、批处理、JavaScript、Python 等脚本；
- 任意命令、安装/卸载参数、安装钩子；
- 可执行资产下载；
- 注册表、用户/系统环境变量或 PATH 写入；
- 自定义目标目录或受管根外输出。

发现命令只能是用于 PATH 检查的裸可执行名称，不会作为 Shell 文本执行。解析器拒绝未知/重复字段、超限文档、危险路径、非 HTTPS 或包含凭据/query/fragment 的 URI。导入、状态和锁路径拒绝重解析点。

模板见 [`language-pack.template.json`](../examples/language-pack.template.json)。模板使用 manual Provider，只准确表示发现能力，不会冒充受管安装。

## 与 Runtime Provider 插件的区别

| 扩展 | 负责什么 | 不负责什么 |
|---|---|---|
| 语言包 schema 1 | 增加语言、工具、Provider Profile 与来源元数据 | 不携带可安装资产，不创造执行适配器 |
| Runtime Provider schema 2 | 为 9 个已桥接工具增加受限 HTTPS ZIP 发行目录 | 不创建任意新语言/工具，不执行脚本或安装钩子 |

Runtime Provider schema 2 用 `languageToolId` 精确绑定上述 9 个工具；旧 schema 1 `runtimeKind` 清单仍可导入，并立即规范化保存成 schema 2。Provider ID 使用 `plugin:<id>`，目录和安装必须显式选择，失败时不会回退到另一个 Provider。

Windows SDK 发现单独映射到 `windows-sdk` 工具，不属于插件桥接。完整插件契约见 [Provider 插件指南](PROVIDER-PLUGINS.md)。
