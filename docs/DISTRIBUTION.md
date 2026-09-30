# AutoEnvPlus 分发与安装

## v0.0.1 状态

`v0.0.1` 是 Windows 10/11 x64 测试版候选，产品版本为 `0.0.1`，目标 tag 为 `v0.0.1`。仓库已经定义三类构建和强制 SignPath 发布流程，但仓库内容本身不能证明 SignPath OSS 申请已批准、外部项目已配置或 GitHub prerelease 已发布。只有 tag 工作流成功且从 GitHub 回读签名与资产后，才能把它描述为已签名发布。

权威 GitHub Release 主资产只有三类：

| 主资产 | 内容 | 适用场景 |
|---|---|---|
| `AutoEnvPlus-win-x64.exe` | WinUI 主程序的 self-contained single-file bundle | 只运行 GUI，不需要携带 portable 目录 |
| `AutoEnvPlus-win-x64-portable.zip` | 普通 WinUI EXE + DLL 自包含目录，包含 CLI 与原生 Shim | 解压后使用完整 GUI/CLI/Shim 布局 |
| `AutoEnvPlus-win-x64.msi` | per-user Windows Installer | 开始菜单、应用和功能卸载、major upgrade |

CLI 不作为第四类独立资产。它只位于 portable/MSI 的 `cli\autoenvplus.exe`；`AutoEnvPlus-win-x64.exe` 始终是 WinUI GUI。

每个主资产有同名 `.sha256`，Release 还包含聚合 `SHA256SUMS.txt`，合计七个文件。sidecar 和聚合清单是校验元数据，不是额外产品版本。

## WinUI 单文件

构建本地候选：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-single-file.ps1 `
  -Configuration Release `
  -BuildCacheRoot D:\codex
```

默认输出：

```text
artifacts\AutoEnvPlus-win-x64.exe
artifacts\AutoEnvPlus-win-x64.exe.sha256
artifacts\AutoEnvPlus-win-x64.exe.bundle-manifest.txt
```

脚本使用 .NET single-file bundler 和 `WindowsAppSDKSelfContained`，并用 bundle map 强制确认 App/Core、CLR、Windows App SDK、PRI 与 XBF 已进入 bundle。`bundle-manifest.txt` 是本地构建证据，不属于七个公开 Release 文件。

“单文件”只表示用户分发时拿到一个 WinUI EXE，不表示：

- 这是 CLI；
- 完全不写磁盘或不使用临时目录；
- 不依赖 Windows 10 1809+ 的系统 API、加载器和运行策略；
- 可以规避杀毒、应用控制、磁盘空间或临时目录权限；
- 更新时可以只替换 bundle 的一部分。

原生库和 PRI/XBF 等内容启用了 self-extract，运行时可能写入 .NET bundle extraction/临时目录。该 EXE 自包含 .NET 与 Windows App SDK payload，但不是“绿色、零依赖、零临时文件”的承诺。

## 完整便携 ZIP

先生成普通自包含目录：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1 `
  -Configuration Release `
  -BuildCacheRoot D:\codex `
  -NoArchive
```

目录结构包括：

```text
artifacts\AutoEnvPlus-win-x64\
  AutoEnvPlus.App.exe
  AutoEnvPlus.App.dll
  AutoEnvPlus.Core.dll
  ... .NET / WinUI / Windows App SDK 自包含文件
  cli\
    autoenvplus.exe
    autoenvplus-shim.exe
  LICENSE
  THIRD-PARTY-NOTICES.md
  third_party\licenses\
  SHA256SUMS.txt
```

GUI 使用普通 EXE + DLL 布局，必须完整解压；只复制 `AutoEnvPlus.App.exe` 不可运行。CLI 是 `cli\autoenvplus.exe`，原生 Shim 同样必须保留在 `cli` 子目录及受管安装流程预期的位置。

`eng\publish.ps1` 的本地压缩输出和 tag 工作流都使用权威名称 `AutoEnvPlus-win-x64-portable.zip`。tag 工作流使用 `-NoArchive` 先构建目录，把其中列明的第一方 PE 交给 SignPath 并验签，重新生成逐文件清单后再压缩。

ZIP 格式不能承载 Authenticode。发布时所谓“SignPath-signed portable”准确含义是内部第一方 App/CLI/Core/Shim PE 已通过 Authenticode 验证；ZIP 容器本身由 `.sha256` 和聚合清单保护。哈希能检测字节变化，但不能独立建立发布者身份。

## per-user MSI

`publish-msi.ps1` 使用固定的 WiX Toolset SDK 4.0.6（MS-RL）、仓库内版本/上游提交/NuGet SHA-256 锁与 portable payload 构建 MSI。选择 4.0.6 是为了避免 WiX 6/7 的 OSMF EULA 交互门槛：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-msi.ps1 `
  -Configuration Release `
  -Version 0.0.1 `
  -BuildCacheRoot D:\codex `
  -PortableRoot artifacts\AutoEnvPlus-win-x64
```

输出为：

```text
artifacts\AutoEnvPlus-win-x64.msi
artifacts\AutoEnvPlus-win-x64.msi.sha256
```

MSI 具备以下明确边界：

- `perUser` 安装到当前用户的 `%LOCALAPPDATA%\Programs\AutoEnvPlus`；
- 创建开始菜单快捷方式，并在“应用和功能”中提供卸载；
- 使用稳定 UpgradeCode、版本化 ProductCode 和 major-upgrade 规则；
- 不提供系统级安装、服务、驱动程序或任意自定义安装脚本；
- 卸载只移除 MSI 拥有的程序文件和快捷方式，保留用户配置、`AUTOENVPLUS_HOME` 受管数据根和已安装语言工具；
- 不承诺自动更新；新测试版仍需用户从对应 GitHub Release 获取。

本地构建默认可以产生未签候选。`-RequireSignedPayload` 可要求 portable 中列明的第一方 PE 已有有效 Authenticode。tag 工作流总是先合并 SignPath 已签 payload，再构建 MSI，最后单独签 MSI 外层。

## SignPath OSS 发布门禁

`.github\workflows\release.yml` 只监听版本 tag，并在 `release-signing` environment 中运行。正式启用前，维护者必须先通过 SignPath Foundation/open-source 计划审批，在 SignPath 配置 Organization、Project、GitHub.com Trusted Build System、PE/MSI Artifact Configuration 和 Signing Policy，再设置：

- GitHub secret：`SIGNPATH_API_TOKEN`；
- GitHub variables：`SIGNPATH_ORGANIZATION_ID`、`SIGNPATH_PROJECT_SLUG`、`SIGNPATH_SIGNING_POLICY_SLUG`、`SIGNPATH_PE_ARTIFACT_CONFIGURATION_SLUG`、`SIGNPATH_MSI_ARTIFACT_CONFIGURATION_SLUG`、`SIGNPATH_EXPECTED_SIGNER_THUMBPRINT`。

`SIGNPATH_EXPECTED_SIGNER_THUMBPRINT` 是 SignPath 为本项目签发证书的 40 位 SHA-1 thumbprint；每个 PE 与 MSI 都必须精确匹配。GitHub `upload-artifact` 会提供外层 ZIP，因此 PE 和 MSI Artifact Configuration 都必须以 `<zip-file>` 为根并只允许工作流列明的内部路径。任一值缺失、路径漂移或证书不匹配都会在签名前/发布前失败，不会降级发布未签资产。普通 PR、fork PR、push CI 和手工本地构建不读取 `SIGNPATH_API_TOKEN`，也不提交签名请求。

签名顺序固定为：

1. 构建未签 portable 目录和 WinUI single-file；
2. 提交 single-file 以及 portable 中 App EXE/App DLL/Core DLL/CLI EXE/原生 Shim；
3. 等待 SignPath 完成，下载后逐个要求 Authenticode `Valid`，拒绝缺失文件与 reparse point；
4. 合并已签 PE，重建 portable `SHA256SUMS.txt` 和 `AutoEnvPlus-win-x64-portable.zip`；
5. 从已签 portable payload 构建 MSI；
6. 单独提交 MSI 外层，下载后要求唯一同名普通文件且 Authenticode `Valid`；
7. 生成三个 sidecar 与聚合 `SHA256SUMS.txt`，确认七个文件齐全后才创建/发布 GitHub prerelease。

SignPath Action 固定到 `SignPath/github-action-submit-signing-request` v2.2 的提交 `b9d91eadd323de506c0c81cf0c7fe7438f3360fd`。签名请求、下载或本地验签任一失败都会阻断发布。

## 用户验证

下载后先核对聚合清单和单独 sidecar。PowerShell 示例：

```powershell
Get-FileHash .\AutoEnvPlus-win-x64.exe -Algorithm SHA256
Get-FileHash .\AutoEnvPlus-win-x64-portable.zip -Algorithm SHA256
Get-FileHash .\AutoEnvPlus-win-x64.msi -Algorithm SHA256

Get-AuthenticodeSignature .\AutoEnvPlus-win-x64.exe
Get-AuthenticodeSignature .\AutoEnvPlus-win-x64.msi
```

便携 ZIP 解压后，再检查 `AutoEnvPlus.App.exe`、`AutoEnvPlus.App.dll`、`AutoEnvPlus.Core.dll`、`cli\autoenvplus.exe` 和 `cli\autoenvplus-shim.exe`。签名 Subject、证书链和时间戳必须与目标 Release 的发布说明一致。仅看到文件名、哈希匹配或工作流徽章不足以证明签名发布成功。

## 遗留 MSIX/AppInstaller 工具

仓库仍保留 `eng\publish-msix.ps1`、AppxManifest 与 AppInstaller profile，用于开发验证或后续实验。该脚本支持短期开发证书和显式 PFX 模式，并执行包身份/CMS/Authenticode/AppInstaller 交叉检查；开发证书默认不受信任，也不会自动写入证书库。

MSIX 与 AppInstaller 不属于 `v0.0.1` 三类权威 GitHub Release 资产，tag 的 SignPath 主流程不会发布它们。AppInstaller 的 stable `latest` URI 也不构成 prerelease-to-prerelease 更新承诺。不能用本地开发 MSIX 或旧 PFX 脚本成功替代三类 SignPath 资产的实际回读。

## 数据、许可证与构建位置

三种分发都只交付 AutoEnvPlus 程序。默认用户数据和受管工具位于独立受管根；更换程序格式或卸载 MSI 不会自动迁移、合并或删除这些数据。

构建缓存和产物根可通过 `-BuildCacheRoot` / `AUTOENVPLUS_BUILD_CACHE_ROOT` 与 `-ArtifactsRoot` / `AUTOENVPLUS_ARTIFACTS_ROOT` 配置。在当前 D 盘工作区，脚本将 NuGet cache、`DOTNET_CLI_HOME`、`TEMP` 和 `TMP` 固定到 `D:\codex`；其他克隆位置可以选择自己的非系统盘根。

AutoEnvPlus 本体仅以 `AGPL-3.0-only` 发布。三种资产都必须携带或安装 `LICENSE`、`THIRD-PARTY-NOTICES.md` 和所需第三方许可证；分发修改版还必须提供对应源码并遵守 AGPLv3 第 13 节适用要求。
