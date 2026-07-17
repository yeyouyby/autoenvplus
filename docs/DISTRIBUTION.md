# AutoEnvPlus 分发与安装

## 当前可交付形式

仓库可以生成 Windows x64 自包含便携包。它同时携带 .NET 运行时、Windows App SDK、WinUI 应用、单文件 CLI 和无 CLR 原生 Shim，因此目标机器无需预装 .NET 或 Windows App Runtime。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1
```

默认输出：

```text
artifacts\AutoEnvPlus-win-x64\
  AutoEnvPlus.App.exe
  coreclr.dll
  ... WinUI / Windows App SDK 自包含文件
  cli\
    autoenvplus.exe
    autoenvplus-shim.exe
  LICENSE
  THIRD-PARTY-NOTICES.md
  third_party\licenses\
  SHA256SUMS.txt
artifacts\AutoEnvPlus-win-x64.zip
artifacts\AutoEnvPlus-win-x64.zip.sha256
```

使用 `-NoArchive` 可以只生成目录而不压缩：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1 -NoArchive
```

脚本会生成并复制 WinUI RID 自包含 Build 布局，同时发布自包含单文件 CLI。之所以不直接使用 `dotnet publish -o` 作为 GUI 布局，是因为当前 Windows App SDK 工具链会在该路径漏掉 XBF 页面资源和 PRI 资源索引；脚本把 `App.xbf`、页面 XBF 和 `AutoEnvPlus.App.pri` 作为硬性完整性门禁。随后它移除根目录的框架依赖 CLI apphost，把单文件 CLI 和原生 Shim 固定放入 `cli` 子目录，复制 AutoEnvPlus 的 AGPLv3、第三方组件声明及许可证，并生成逐文件 SHA-256 清单。发布输出和清理操作被限制在仓库的 `artifacts` 目录内。

分发 AutoEnvPlus 或修改版时应同时提供 `LICENSE`、完整对应源码和构建说明。若修改版增加网络服务或远程交互入口，还必须满足 AGPLv3 第 13 节；第三方依赖仍按 `THIRD-PARTY-NOTICES.md` 所列许可证分发。

## MSIX 与 AppInstaller

`eng\publish-msix.ps1` 复用便携发布中已经验证过的 WinUI/CLI 自包含布局，再加入完整信任桌面包清单、开始菜单资产、`autoenvplus.exe` 执行别名和 AppInstaller 更新元数据。Windows 10 最低版本保持 build 17763，包架构固定为 x64。脚本会用 `MakeAppx` 的完整清单校验创建 MSIX，用 SHA-256 代码签名，随后独立执行 PKCX/CMS 验签、Authenticode 验签、解包和以下字段的逐项一致性检查：

- MSIX `Name`、`Publisher`、四段版本与 `x64` 架构；
- AppInstaller 中的同一包身份、版本与架构；
- MSIX 和 AppInstaller 自身的绝对 HTTPS 发布 URI；
- 代码签名证书的私钥、有效期、digital-signature key usage、code-signing EKU、非 CA 属性和精确 Subject；
- 包内 `LICENSE`、第三方声明、CLI、Shim、PRI/XBF 和逐文件 SHA-256 清单。

`packaging\AutoEnvPlus.AppInstallerProfile.xsd` 是构建时固定的封闭 profile，而不是从网络下载的可变 schema。它只接受当前支持的 2018 namespace、一个 `MainPackage` 和固定更新策略（每 6 小时检查、提示用户、不阻塞启动、后台检查、禁止从任意版本强制降级）。解析器禁用 DTD 和外部实体；未知/重复元素、未知属性、策略篡改或身份/HTTPS URI 不一致都会使发布失败。这个检查保护仓库模板和 CI 产物，不改变 Windows 仍以目标 MSIX Publisher 签名作为安装信任边界的事实。

先运行不依赖外部测试框架的打包规则测试：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\test-packaging.ps1
```

开发签名示例：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-msix.ps1 `
  -PackageVersion 0.1.0.0 `
  -PackageUri https://github.com/yeyouyby/autoenvplus/releases/download/v0.1.0/AutoEnvPlus-win-x64.msix `
  -AppInstallerUri https://github.com/yeyouyby/autoenvplus/releases/latest/download/AutoEnvPlus.appinstaller `
  -DevelopmentCertificate
```

开发模式在 `artifacts\.staging` 中创建 30 天自签名代码签名证书，签名完成后删除 PFX、私钥、密码响应文件和整个 staging，只在版本输出目录保留公开 `.cer`。脚本不会把证书写入 CurrentUser 或 LocalMachine 信任库，因此该 MSIX 默认不会被其他机器或当前机器信任；是否显式信任开发证书由测试人员另行决定。不能把该模式的产物称为正式签名版本。

正式发布示例：

```powershell
$password = Read-Host 'PFX password' -AsSecureString
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-msix.ps1 `
  -PackageVersion 1.0.0.0 `
  -PackageUri https://github.com/yeyouyby/autoenvplus/releases/download/v1.0.0/AutoEnvPlus-win-x64.msix `
  -AppInstallerUri https://github.com/yeyouyby/autoenvplus/releases/latest/download/AutoEnvPlus.appinstaller `
  -CertificatePath D:\secrets\autoenvplus-code-signing.pfx `
  -CertificatePassword $password `
  -Publisher 'CN=Exact certificate subject' `
  -TimestampUri https://example.test/rfc3161
```

生产模式没有 PFX、密码或精确 `Publisher` 时会在构建前安全失败。也可仅在 CI 中用 `AUTOENVPLUS_PFX_PASSWORD` 提供密码；私钥文件不得放入仓库、ZIP、MSIX、GitHub Release 或普通构建日志。输出位于 `artifacts\msix\<version>`，包含签名 MSIX、AppInstaller、两个 SHA-256 sidecar、发布元数据 JSON，以及仅开发模式才有的公开 `.cer`。

`.appinstaller` 是 Windows App Installer 消费的 XML 元数据；当前 Windows SDK 的 SignTool 不把它识别为可 Authenticode 签名的文件格式。更新链因此依赖 GitHub HTTPS 传输和每次下载的 MSIX 代码签名：AppInstaller 声明的 Name、Publisher、版本和架构必须与已签包完全一致，攻击者即使改写 URI，也不能在没有同一 Publisher 私钥的情况下替换为可安装更新。`.appinstaller.sha256` 供发布审计和人工校验，不应被描述为 Windows 自动执行的元数据签名。

在当前 D 盘工作区，两个发布脚本都会把 `NUGET_PACKAGES`、NuGet HTTP cache、NuGet 插件 cache、`DOTNET_CLI_HOME`、`TEMP` 和 `TMP` 固定到 `D:\codex`；MSIX 调用 portable 发布时会继续沿用同一缓存根。其他克隆位置默认使用仓库内被忽略的 `.build-cache`，也可通过 `-BuildCacheRoot` 或 `AUTOENVPLUS_BUILD_CACHE_ROOT` 显式指定非系统盘。

## 运行

将 ZIP 完整解压到普通用户可写目录，再启动：

```powershell
.\AutoEnvPlus.App.exe
```

命令行入口位于：

```powershell
.\cli\autoenvplus.exe doctor
```

不要只复制 `AutoEnvPlus.App.exe`；WinUI、Windows App SDK 和 .NET 自包含依赖必须保持原目录结构。应用目标为 Windows 10 build 17763 或更高版本与受支持的 Windows 11 x64。

## 安全与限制

当前便携 ZIP 是开发阶段产物，不是已签名安装器：

- 没有代码签名证书，Windows SmartScreen 可能提示未知发布者；
- ZIP 本身没有 MSIX 安装/卸载注册、开始菜单快捷方式和自动更新；
- 没有 WinGet 社区源发布清单；
- ZIP 的 SHA-256 只能检测字节变化，不能证明发布者身份。

仓库已经具备签名 MSIX 和 AppInstaller 生成/验证钩子，但正式 1.0 分发仍需真实代码签名证书、可信 RFC 3161 时间戳、升级/回滚验证、Windows 10 与 Windows 11 安装测试以及 WinGet 发布流程。不要把开发证书 MSIX 或便携 ZIP 描述为已经建立正式发布者身份。
