# AutoEnvPlus 产品规格

## 产品目标

AutoEnvPlus 让 Windows 用户能够安装、发现、切换和诊断开发运行时，并集中管理相关环境变量与缓存目录。安装只是入口；产品的核心价值是让当前环境可预测、可解释、可复现、可回滚。

## 支持范围

产品模型分为四类：

| 类别 | 首批对象 | 说明 |
|---|---|---|
| 运行时 | Python、Node.js、Java | 支持多版本并存和项目固定 |
| 编译工具链 | MSVC、Windows SDK、LLVM、MinGW | C/C++ 需要组合、架构和激活环境 |
| 构建工具 | CMake、Ninja、Maven、Gradle | 可独立安装，也可从工具链引用 |
| 缓存与存储 | pip、npm、pnpm、Maven、Gradle、vcpkg、Conan 等 | 支持统计、打开真实目录、安全迁移和纯缓存两阶段清理 |

## 版本解析

命令解析优先级固定为：

1. 当前终端会话；
2. 当前或父级目录中的 `autoenvplus.toml`；
3. 当前用户全局默认；
4. 在满足要求的已安装版本中自动选择最高稳定版。

项目文件示例：

```toml
[tools]
python = "3.12"
node = "22-lts"
java = "21"

[toolchain]
cpp = "msvc-2022"
arch = "x64"
```

解析后的精确版本、架构、来源与包校验值写入 `autoenvplus.lock`。项目配置可以导入 `.python-version`、`.nvmrc`、`.node-version`、`.java-version`、`package.json` 的 `engines` 和 `.NET global.json`。

“打开已激活终端”先把项目选择器解析为当前托管注册表中的精确 Python、Node.js 和 Java 版本，确认真实可执行文件与 Shim 存在，再展示 Shell、Shim 目录、请求选择器、精确版本和会话变量。确认后只为新 PowerShell 创建环境块并把 Shim 目录置于该子进程 PATH 首位；不修改父进程、用户 PATH、全局默认或项目文件。DotNet/CMake 在对应命令 Shim 完成前只显示未激活警告。

## 主要页面

- **概览**：全局版本、当前项目、PATH 健康度、更新和存储摘要；
- **运行时**：发现、安装、切换、修复和卸载 Python、Node.js、Java；Java 版本线实时来自 Adoptium 官方 GA/LTS 目录；
- **工具链**：组合 MSVC/LLVM、Windows SDK、CMake、Ninja 与目标架构；安装任务互斥并支持取消；
- **项目环境**：扫描项目需求，安装缺失项，创建锁文件并打开已激活终端；
- **PATH 与变量**：标记来源、重复项、无效项、顺序和同名命令冲突，并列出可验证回滚的用户 PATH 快照；
- **缓存与存储**：统计 pip/npm 等目录，直接打开实际位置，进行可回滚配置的安全迁移，并将纯缓存先隔离、后恢复或永久清空；
- **环境诊断**：解释实际命令来源以及版本不一致的根因，并通过系统文件选择器导出结构化 JSON；
- **活动记录**：按状态和操作类型筛选脱敏审计记录，复制摘要，并只读显示快照与回滚路径。

## 受管数据根

运行时、Shim、下载、状态、Shell 集成和活动记录使用同一个受管数据根。解析优先级固定为：

1. CLI 显式 `--root`；
2. `AUTOENVPLUS_HOME`；
3. `%LOCALAPPDATA%\AutoEnvPlus`。

候选必须是规范化绝对目录，驱动器根、UNC 共享根、空值和相对路径都会安全失败。设置页只写用户级 `AUTOENVPLUS_HOME`，不在当前进程中途切换根；重启后才启用新目录。修改根不会隐式复制、迁移或删除旧运行时与状态，避免在半迁移状态下让 GUI、CLI 和 Shim 读取不同注册表。

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

## 权限与安全

- 主进程始终以普通用户运行；
- 默认安装到用户目录并修改用户 PATH；
- 系统级修改使用短生命周期提权 Helper；
- 下载包必须使用 HTTPS，安装前按 Provider 给出的 SHA-256 校验字节，并记录 checksum/manifest 来源证据；
- Node.js 必须先用固定发布密钥验证 `SHASUMS256.txt.asc` OpenPGP 签名，再从已签名正文读取包哈希；
- Temurin 必须在 SHA-256 通过后，用固定 Adoptium 主指纹验证 ZIP 包本体的 detached OpenPGP 签名，失败时删除缓存包且不解压；
- Python 必须用内置 Sigstore trusted-root 快照和 python.org 对应版本系列的精确 release-manager 邮件/OIDC 策略验证 Windows manifest，再从该签名 manifest 读取 PythonCore ZIP SHA-256；bundle、身份、Fulcio/SCT/Rekor 或签名任一失败时不能降级；
- 软件目录清单需要签名；
- 外部安装默认只读，不允许 AutoEnvPlus 擅自删除；
- PATH 和配置修改前创建快照；
- 活动记录隐藏令牌、代理密码、PFX 密码和 URI 凭据，并限制文件、记录与字段大小；
- 删除前重新验证目标位于受信隔离目录内，拒绝链接、未知条目和系统/受管根重叠。

## MVP 验收

1. 在 Windows 10 22H2 和 Windows 11 x64 上启动；
2. 无管理员权限安装 Python、Node.js 和 Java；
3. 每种运行时可并存至少两个版本；
4. 全局版本和项目版本能正确解析；
5. PATH 不因切换产生重复项；
6. 能解释 `python`、`node`、`java` 实际来源；
7. pip 与 npm 缓存可以事务式迁移；
8. 安装或迁移失败可回滚；
9. 外部安装不会被误删；
10. 能检测 C/C++ 的 MSVC、SDK、架构和构建工具，并打开已激活终端。
11. 能把受管数据根固定到非系统盘，且 GUI、CLI 与 Shim 使用同一根；
12. 纯缓存能先隔离并恢复，再通过独立确认永久释放空间；
13. 高影响操作能生成不含凭据的有界活动记录。
