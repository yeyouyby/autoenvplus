# AutoEnvPlus 快速开始

本指南面向 AutoEnvPlus 测试版本（当前 `v0.0.3`）。AutoEnvPlus 当前只发布 Windows x64 目标；Windows 10 和 Windows 11 均在产品范围内，但完整真机安装、升级、可访问性和视觉矩阵仍属于发布前验证项。

## 1. 选择运行方式

### GitHub 预发布包

只从 [GitHub Releases](https://github.com/yeyouyby/autoenvplus/releases) 获取资产，并核对同一版本附带的 SHA-256 文件或 `SHA256SUMS.txt`。不要从非官方镜像取得同名包。

| 文件 | 选择它的场景 |
|---|---|
| `AutoEnvPlus-win-x64.exe` | 只需要 WinUI 主程序的单文件分发。它不是 CLI，运行时可能自解压 bundle 内容到临时目录。 |
| `AutoEnvPlus-win-x64-portable.zip` | 需要可移动的完整目录，以及 `cli\autoenvplus.exe` 和原生 Shim。先完整解压，再运行根目录 `AutoEnvPlus.App.exe`。 |
| `AutoEnvPlus-win-x64.msi` | 需要 per-user 安装、开始菜单和“应用和功能”卸载。卸载保留用户设置、受管数据和已安装工具。 |

每个版本的三个主资产缺一不可，并必须通过发布工作流的签名门禁。单文件 EXE 与 MSI 外层可直接检查 Authenticode；便携 ZIP 应同时核对 ZIP SHA-256，并在解压后检查其中第一方 EXE/DLL 的签名。签名使用自签名测试证书，Windows 显示“未知发布者”属预期。工作流尚未成功发布并回读前，不要把本地候选或 CI artifact 当成正式预发布资产。

### 从源码运行

要求：

- Windows 10/11 x64；
- 仓库 `global.json` 指定的 .NET SDK `10.0.200`；
- 构建 WinUI 与原生 Shim 时，需要 Visual Studio 2022 C++ 桌面构建工具和 Windows SDK。

```powershell
git clone https://github.com/yeyouyby/autoenvplus.git
cd autoenvplus

dotnet restore AutoEnvPlus.sln
dotnet build AutoEnvPlus.sln -c Debug -p:Platform=x64 -m:1
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj -c Release -m:1
dotnet run --project src\AutoEnvPlus.App -p:Platform=x64
```

解决方案使用 `-m:1`，避免 WinUI、CLI 和原生 Shim 并行写共享输出造成构建争用。

## 2. 可选：把数据和构建缓存放到 D 盘

AutoEnvPlus 受管根的解析优先级是：

1. CLI `--root <absolute-path>`；
2. `AUTOENVPLUS_HOME`；
3. `%LOCALAPPDATA%\AutoEnvPlus`。

当前 PowerShell 会话可这样配置：

```powershell
$env:AUTOENVPLUS_HOME = 'D:\AutoEnvPlus\data'
$env:NUGET_PACKAGES = 'D:\AutoEnvPlus\build-cache\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = 'D:\AutoEnvPlus\build-cache\nuget\http'
$env:NUGET_PLUGINS_CACHE_PATH = 'D:\AutoEnvPlus\build-cache\nuget\plugins'
$env:DOTNET_CLI_HOME = 'D:\AutoEnvPlus\build-cache\dotnet'
$env:TEMP = 'D:\AutoEnvPlus\build-cache\tmp'
$env:TMP = $env:TEMP
$env:AUTOENVPLUS_BUILD_CACHE_ROOT = 'D:\AutoEnvPlus\build-cache'
```

也可在设置页选择受管根；修改写入用户级 `AUTOENVPLUS_HOME`，重启应用后生效。更改根不会自动迁移、合并或删除旧目录。

> [!NOTE]
> D 盘配置只约束 AutoEnvPlus 自己创建的运行时、状态、插件、下载、缓存和暂存目录。Windows 组件、WinGet、pip 依赖构建脚本和其他第三方程序仍可能按自身规则写入 C 盘或用户 Profile。

## 3. 第一次打开

首页和语言页只加载快照，因此首次打开时出现空状态是正常的：

1. 在“概览”选择完整环境扫描；
2. 在“语言”按需执行 PATH 重检；
3. 检查“PATH 与命令”中的 Shim 状态；
4. 在“环境诊断”只选择需要的扫描域，联网检查必须单独勾选；
5. 再进入语言详情查看工具、Provider、来源和真实可用操作。

打开页面本身不会扫描 PATH、执行版本命令、遍历缓存或访问 Provider。

## 4. 先做只读检查

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- doctor
dotnet run --project src\AutoEnvPlus.Cli -- doctor --json
dotnet run --project src\AutoEnvPlus.Cli -- list
dotnet run --project src\AutoEnvPlus.Cli -- provider list
dotnet run --project src\AutoEnvPlus.Cli -- storage list
```

## 5. 预览再执行

很多持久操作在不带 `--yes` 时只输出计划：

```powershell
# 先预览目录资产和安装计划
dotnet run --project src\AutoEnvPlus.Cli -- catalog python --limit 5
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6

# 确认来源、版本、架构、哈希和签名状态后再执行
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6 --yes

# 只有明确审核过用户覆盖或自定义 Provider 来源后，才增加来源确认
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6 --accept-non-default-source --yes

# Shim 同样先预览
dotnet run --project src\AutoEnvPlus.Cli -- shim install
dotnet run --project src\AutoEnvPlus.Cli -- shim install --yes
```

版本号只是示例；应先以实际 `catalog` 输出为准。默认来源安装不需要 `--accept-non-default-source`；该参数不能绕过哈希、签名或安装计划确认。不要把模板中的 `example.invalid`、全零哈希或占位 Provider 当成可用来源。

## 6. 项目固定与解析

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- project status D:\work\my-project
dotnet run --project src\AutoEnvPlus.Cli -- project import D:\work\my-project
dotnet run --project src\AutoEnvPlus.Cli -- project lock D:\work\my-project
dotnet run --project src\AutoEnvPlus.Cli -- project terminal D:\work\my-project
```

项目解析和终端启动分别经过只读预览与执行前复检。`autoenvplus.toml` 中的 `[tool-identities]` 将 Runtime ID 与 Provider ID 成对保存，避免同版本不同来源被静默替代。

## 7. 用 MCP 调用全部功能

`autoenvplus mcp` 启动一个 stdio MCP（Model Context Protocol）服务器，把 CLI 暴露给 MCP 客户端（例如 AI 助手）：

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- mcp
```

在 MCP 客户端中按如下方式注册（路径换成实际安装位置）：

```json
{
  "mcpServers": {
    "autoenvplus": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\AutoEnvPlus\\cli\\autoenvplus.exe",
      "args": ["mcp"]
    }
  }
}
```

服务器提供六个工具：`doctor`、`list_runtimes`、`catalog`、`provider_list`、`which`，以及通用的 `cli`——后者接受任意 CLI 参数（如 `["install", "python", "3.14.6"]`），因此全部 CLI 功能都可以通过 MCP 调用。每次工具调用都会启动真实 CLI 进程，返回结构化的退出码、stdout 与 stderr；变更操作仍遵循 CLI 自身的安全规则（预览、`--yes` 确认等）。

下一步阅读 [用户指南](USER-GUIDE.md)；遇到失败关闭或快照为空时查看 [故障排除](TROUBLESHOOTING.md)。
