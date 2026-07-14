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

当前便携包是开发阶段产物，不是已签名安装器：

- 没有代码签名证书，Windows SmartScreen 可能提示未知发布者；
- 没有 MSIX 安装/卸载注册、开始菜单快捷方式和自动更新；
- 没有 WinGet 社区源发布清单；
- ZIP 的 SHA-256 只能检测字节变化，不能证明发布者身份。

正式 1.0 分发仍需代码签名证书、签名安装器、升级/回滚策略、Windows 10 与 Windows 11 安装测试以及 WinGet 发布流程。不要把当前便携包描述为已经完成签名或供应链身份验证。
