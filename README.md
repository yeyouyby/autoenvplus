# AutoEnvPlus

AutoEnvPlus 是一个面向 Windows 10 和 Windows 11 的 Fluent 风格开发环境控制中心。它的目标是统一管理 Python、Node.js、Java 与 C/C++ 工具链的安装、版本选择、PATH、项目环境以及 pip/npm 等工具的缓存目录。

当前仓库处于功能原型阶段。核心路径、自包含 x64 便携发布以及参数化 MSIX/AppInstaller 发布链已经可运行，但正式发布者证书、可信时间戳和 Win11 安装/升级端到端验证仍未完成。已经包含：

- WinUI 3 桌面应用骨架；
- 按系统能力自适应的 Fluent 窗口背景：Windows 11 优先 Mica，Windows 10 使用 Desktop Acrylic，高对比度、关闭透明效果或材料不可用时切换为纯色，并在设置页显示实际状态；
- 独立的领域核心与命令行入口；
- `会话 > 项目 > 全局 > 自动选择` 的版本解析模型；
- PATH 重复、失效和命令冲突检查；
- WinUI/CLI 共用的 PATH、命令赢家、版本探测、托管状态和全局选择聚合诊断；
- python.org、Node.js、Eclipse Temurin 官方发行目录、SHA-256 安装资产和校验来源证据；
- Python Windows release manifest 的 Sigstore v0.3 验证：固定 python.org 版本系列发布身份、内置 trusted-root 快照、Fulcio 证书链/SCT、Rekor SET/Merkle 包含证明/签名检查点和清单签名，缺失或失败时禁止降级；
- Node.js `SHASUMS256.txt.asc` OpenPGP 验证、固定主指纹/签名子钥映射和历史密钥时限策略；
- Eclipse Temurin 包本体的 detached OpenPGP 验证、固定 Adoptium 主指纹和签名失败删除缓存策略；
- WinUI 安装确认页会逐条展示下载地址、包哈希和校验来源，并明确提示数字签名验证状态；
- 防 Zip Slip、限制下载/解压大小、原子提交的受管理 ZIP 安装器；
- 带 ETag/Last-Modified/If-Range 的断点续传与 SHA-256 下载缓存；
- 安装注册表、全局版本选择、`which`/`exec`、Win32 原生 Shim 与 CMD 回退；
- 运行时目录提交、注册表登记和全局选择之间的补偿式安装事务；
- PowerShell 会话模块、受管 Profile 块、修改前快照与并发安全回滚；
- 项目现有版本文件导入、精确 `autoenvplus.lock` 和引用保护卸载；
- 项目清单预解析、精确运行时会话变量与独立 PowerShell 终端启动，不修改父进程或用户 PATH；
- pip/npm/pnpm/Yarn/NuGet/Maven/Gradle/vcpkg/Conan 存储发现、统计、事务迁移、配置快照与回滚；
- Visual Studio/MSVC、Windows SDK、clang/GCC/CMake/Ninja 工具链发现；
- 通过精确 WinGet 白名单安装 MSVC、LLVM、MinGW-w64/GCC、CMake 和 Ninja；
- 基于实际 `cl.exe` 布局的 MSVC Host/Target 架构选择与开发终端预览；
- 保留用户内容、可预览和回滚的项目级 `CMakeUserPresets.json` 生成；
- 第一组自动化测试；
- 可复现组合 WinUI、单文件 CLI、原生 Shim 和逐文件 SHA-256 清单的自包含便携发布脚本；
- 生产证书缺失时安全失败、严格绑定 Publisher/版本/架构/HTTPS 更新 URI 的 MSIX 与 AppInstaller 发布脚本；
- 产品范围、架构和安全约束文档。

## 构建

要求：

- Windows 10 1809 或更高版本；
- Visual Studio 2022（包含 Windows 应用 SDK/C++ 桌面构建工具），或 .NET SDK 10.0.200；
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
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6
dotnet run --project src\AutoEnvPlus.Cli -- install python 3.14.6 --yes
dotnet run --project src\AutoEnvPlus.Cli -- list --managed
dotnet run --project src\AutoEnvPlus.Cli -- uninstall python-3.14.6-x64
dotnet run --project src\AutoEnvPlus.Cli -- use python 3.14 --global
dotnet run --project src\AutoEnvPlus.Cli -- which python
dotnet run --project src\AutoEnvPlus.Cli -- exec python -- --version
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

该模式生成短期开发证书，只输出公钥 `.cer`，签名后删除私钥和 PFX，且不会修改 Windows 证书信任库。正式发布必须提供代码签名 PFX、精确等于证书 Subject 的 `-Publisher`，并建议提供 RFC 3161 `-TimestampUri`；详见 [分发说明](docs/DISTRIBUTION.md)。

## 设计原则

1. PATH 只固定加入一次 Shim 目录，日常切换不反复重写 PATH。
2. 已有安装默认只读，只有 AutoEnvPlus 托管的安装才能直接卸载。
3. 所有系统修改都经过计划、预览、快照、验证和回滚。
4. 默认只修改当前用户；系统级修改才按需启动提权 Helper。
5. GUI、CLI、Shell 集成共用同一个核心引擎。

PowerShell 安装命令默认只输出计划。只有同时提供 `--install-profile --yes` 才会生成模块、会话 Shim 和受管 Profile 块；回滚同样先预览，再通过 `--yes` 确认。开发构建既支持 `autoenvplus.exe`，也支持 `dotnet autoenvplus.dll` 形式的固定前置参数。

原生 Shim 是不加载 CLR 的 x64 Win32 程序，直接读取同一份托管注册表、项目清单、全局 Profile 和会话变量。它覆盖 `python`/`python3`、`pip`/`pip3`、`node`、`npm`/`npx`、`java`、`javac`、`jar`；构建产物缺失时安装器才显式回退到 CMD 包装器。在本机 60 次交替差分基准中，直接子进程中位耗时为 11.73 ms，经 Shim 为 27.28 ms，中位额外开销 15.55 ms。

详细内容见 [产品规格](docs/PRODUCT.md)、[技术架构](docs/ARCHITECTURE.md)、[安全模型](docs/SECURITY.md)、[分发说明](docs/DISTRIBUTION.md)、[第三方组件声明](THIRD-PARTY-NOTICES.md) 和 [路线图](docs/ROADMAP.md)。

## 许可证

AutoEnvPlus 本体以 [`AGPL-3.0-only`](LICENSE) 发布。分发修改版时必须按 GNU Affero General Public License v3 提供对应源码；如果修改版允许用户通过网络与程序交互，还必须遵守 AGPLv3 第 13 节的网络源码提供要求。第三方组件不被重新许可，仍分别适用 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) 中列出的 Apache-2.0、MIT、BSD 等原许可证。
