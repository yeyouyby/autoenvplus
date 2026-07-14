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
| 缓存与存储 | pip、npm、pnpm、Maven、Gradle、vcpkg、Conan 等 | 支持统计、打开真实目录和安全迁移 |

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
- **缓存与存储**：统计 pip/npm 等目录，直接打开实际位置，并进行可回滚配置的安全迁移；
- **环境诊断**：解释实际命令来源以及版本不一致的根因，并通过系统文件选择器导出结构化 JSON；
- **活动日志**：显示修改计划、结果和可用回滚点。

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
- 日志隐藏令牌、代理密码和仓库凭据；
- 删除前验证目标位于受管理目录内。

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
