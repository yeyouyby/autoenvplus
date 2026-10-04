# AutoEnvPlus 用户指南

AutoEnvPlus `v0.0.1` 的主界面按工作流组织，而不是为每个运行时或网络设置创建一级页面。语言、工具、Provider 和来源始终遵循：

```text
语言 -> 语言工具 -> Provider -> Provider 来源
```

## 开始之前

- 当前应用目标为 Windows 10/11 x64；
- `v0.0.1` 是测试版本，先在非关键开发环境验证；
- `v0.0.1` 只承认 GitHub Release 中三类同时存在的主资产：WinUI 单文件 EXE、完整便携 ZIP 和 per-user MSI；检查各自 SHA-256，并对 EXE/MSI 或便携包内第一方 PE 核对发布签名（自签名测试证书，Windows 显示“未知发布者”属预期）；
- 持久操作应先阅读预览，再确认版本、Provider、目录、哈希/签名证据和回滚边界。

## 页面加载与扫描

### 概览

概览打开时只读取 `<managed-root>\state\overview-snapshot.json`。首次启动没有快照时显示空状态，不代表扫描失败。

- **快速刷新**：读取受管注册表、选择和状态，不探测 PATH 或缓存；
- **完整环境扫描**：用户显式触发后，才检查 PATH、执行有界版本探测、测量缓存并更新快照；
- 快照显示采集时间，不能当作实时环境。

### 语言

语言页打开时只读取 `<managed-root>\state\language-tool-inventory.json`。要发现 PATH 上的新工具，必须选择 PATH 重检。搜索和筛选只改变当前视图；只有“启用”“隐藏”或“重置”会写持久状态。

默认可见集合是 Top 10，加上快照中已发现工具所属语言和用户显式启用项，再减去显式隐藏项。

## 管理语言工具

1. 打开“语言”，选择语言；
2. 在“工具与版本”查看工具的真实能力状态；
3. 选择 Provider 和架构，刷新发行目录；
4. 打开安装预览，核对来源 URI、哈希算法和值、checksum 来源和发布者签名说明；
5. 确认后安装；安装结束后再决定是否设为全局默认。

只有 9 个工具有真实受管安装适配器。其余目录项出现“发现”“官方链接”或“外部管理”是预期行为，不是功能缺失提示。

## 版本选择与 Shim

解析优先级固定为：

1. 新终端会话精确选择；
2. 当前目录或父目录 `autoenvplus.toml`；
3. 用户全局 profile；
4. 满足 selector 的最高稳定已安装版本。

前三层都可同时约束 selector、Runtime ID 和 Provider ID。项目清单示例：

```toml
[tools]
python = "3.13.5"
node = "22-lts"

[tool-identities]
python.runtime-id = "python-3.13.5-x64"
python.provider-id = "python-org"
```

每个工具的 `runtime-id` 与 `provider-id` 必须成对出现，且必须有对应 `[tools]` selector。精确身份存在时，同版本的另一个 Provider 不会被静默替代。

“PATH 与命令”先预览再把 `<managed-root>\shims` 加入用户 PATH。此后版本切换不反复写 PATH。页面还可查看 18 个 Shim 命令的实际解析结果、冲突和可验证回滚快照。

## 项目环境

### 导入版本要求

项目页可读取 `.python-version`、`.nvmrc`、`.node-version`、`.java-version`、`package.json` 的 engines 和 `.NET global.json`。预览不会写项目；确认后只更新目标工具并尽量保留清单中的未知分区和注释。

### 创建项目锁

`autoenvplus.lock` 保存解析后的版本、架构、Runtime ID、Provider ID、包哈希算法和值。锁用于复现和卸载引用保护，不是包管理器 lockfile 的替代品。

### 解析虚拟环境

解析由用户按需触发，当前覆盖：

- Python venv、Poetry、Pipenv、Conda；
- Node 本地 bin、npm/pnpm/Yarn/Bun 锁与 Corepack；
- `.NET` 本地工具；
- Maven/Gradle Wrapper；
- Rust toolchain/target；
- Go workspace。

扫描只读、不递归执行项目程序、不跟随重解析点，也不会修改项目。

### 打开已激活终端

预览会显示终端宿主、Shell、项目根、Shim、每个选择的精确身份和网络摘要。Windows Terminal 不存在时明确回退到独立 PowerShell。确认后只为新子进程创建环境块，不修改父进程、用户 PATH、项目清单或全局选择；启动前任一清单、注册表、Provider 来源或代理发生变化都会拒绝旧计划。

## Provider 来源与代理

Provider 来源决定“连接哪个目录或包仓库”，通用代理决定“怎样连接”。

- 来源归属于 `languageToolId + providerId + slotId`；
- 可覆盖允许修改的内置槽、恢复默认值，或添加/启停/删除具名 HTTPS 自定义源；
- 通用 HTTP/HTTPS 代理和 `NO_PROXY` 只在“设置”管理；
- URI 不允许嵌入凭据，Provider 来源也不允许 query/fragment；
- 镜像或代理不会降低 Python/Node/Temurin 的签名要求，也不会把 checksum-only 证据升级成发布者签名。

CLI 的 `catalog` 可以只读检查当前解析出的来源。`install` 如果解析到用户覆盖或自定义来源，会在任何目录请求之前停止，并显示完整来源 ID、URL 和类型；审核后必须显式增加 `--accept-non-default-source`。`--yes` 只确认安装计划，不能替代这项来源确认。旧版 `network-settings.json` 中的 runtime mirror 不再参与目录或安装来源选择。

AutoEnvPlus 不保存独立代理用户名/密码。Windows 集成身份可由系统默认凭据协商，具体外部工具是否支持由其自身决定。

## 下载中心

### URL 下载

1. 输入绝对 HTTPS URL；
2. 选择 1/2/4/8/16 个连接和最大大小；
3. 如果有可信独立渠道的 SHA-256 或 SHA-512，填写预期值；
4. 预览来源、文件名、覆盖策略和证据；
5. 确认并观察进度。

分段下载只在服务端提供长度、Range 和稳定实体标识时启用；否则自动单流降级并记录原因。连接数更多不保证更快。

### 本地导入与删除

导入复制源文件到受管 staging，经复检后原子提交到下载库。所有文件都会计算内容 SHA-256，但没有可信预期哈希时只能说明“当前字节身份”，不能说明可信。删除是永久操作，先隔离目标并更新清单；它不是缓存页的可恢复清理。

### wheel 安装

WinUI 可为下载库顶层 `.whl` 生成受管 Python 虚拟环境计划：

- 严格离线：`--no-index --find-links --no-deps`，只安装当前 wheel；
- 联网模式：应用当前 pip Provider 来源与代理，由真实 pip 解析依赖。

创建 venv 和 pip 安装不是事务。失败或取消可能留下部分环境、包或缓存；输出每路只保留尾部 65,536 字符，并明确标记截断。

## 缓存与存储

可发现 pip、npm、pnpm、Yarn、NuGet、Maven、Gradle、vcpkg 和 Conan 等目录。迁移流程先展示目标、配置改动和快照，再确认执行；发生并发编辑时拒绝覆盖。

纯缓存清理分为：

1. 复检后同卷移动到隔离区；
2. 用户可恢复；
3. 二次确认后永久清空，才真正释放空间。

Gradle User Home、Conan Home 等混合目录包含配置，不提供整目录永久清理，只提供安全迁移边界。

## 环境诊断与活动记录

诊断分为 PATH/命令、托管工具、项目、Provider、存储/磁盘和实时连接六域。进入页面不会自动扫描；联网与缓存遍历需要单独选择。JSON 导出包含结构化证据，但仍应在分享前检查本机路径等环境信息。

活动记录按状态和操作筛选，显示脱敏摘要、快照及回滚路径。它有条目、大小和保留期上限，不是完整取证日志，也不会保存 URL 凭据或敏感 query。

## 设置与受管根

设置页可配置启动页、语言可见性、下载默认值、通用代理、日志保留、主题、材质、密度、PowerShell 集成和受管根。镜像不在设置页统一编辑，而在对应语言工具/Provider 中管理。

受管根优先级为 `--root`、`AUTOENVPLUS_HOME`、`%LOCALAPPDATA%\AutoEnvPlus`。更改根需要重启，不自动搬迁旧数据。详见 [快速开始](QUICKSTART.md#2-可选把数据和构建缓存放到-d-盘)。

## 语言包与 Provider 插件

- 语言包增加语言、工具和 Provider 元数据，导入默认停用，不能覆盖现有 ID；
- Runtime Provider 插件 schema 2 只为 9 个桥接工具增加受限 ZIP 来源，导入后同样默认停用；
- 插件必须显式启用并在目录/安装时精确选择 `plugin:<id>`，失败时不会回退到官方或其他 Provider；
- 停用或删除插件不会卸载既有运行时或删除项目锁；
- data-only 只限制清单能力，不能证明 ZIP 中的第三方 `.exe` 安全。

完整作者契约见 [语言与语言包](LANGUAGE-PACKS.md) 和 [Provider 插件指南](PROVIDER-PLUGINS.md)。
