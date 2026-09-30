# AutoEnvPlus 发布指南

本文是 `v0.0.1` 及后续 Windows x64 测试版的不可跳过检查表。文档和工作流定义不证明 PR、SignPath 审批、tag 或 GitHub prerelease 已存在；每个外部状态都必须回读。

## 权威版本与资产

[`Directory.Build.props`](../Directory.Build.props) 是版本权威源：

```text
Product version: 0.0.1
Package version: 0.0.1.0
Release tag:     v0.0.1
License:         AGPL-3.0-only
```

`v0.0.1` 必须同时发布三类主资产，不能只发其中一部分：

```text
AutoEnvPlus-win-x64.exe
AutoEnvPlus-win-x64-portable.zip
AutoEnvPlus-win-x64.msi
```

三者分别配套 `.sha256`，再加一个 `SHA256SUMS.txt`，最终恰好七个 Release 文件。单文件 EXE 是 WinUI GUI，不是 CLI；CLI 只在 portable/MSI 内。

## 1. 范围冻结

- [ ] `CHANGELOG.md` 准确描述候选范围和未完成项；
- [ ] README、功能清单和用户指南使用三个权威文件名；
- [ ] “single-file” 没有被写成 CLI、绿色版、完全不自解压或完全无系统依赖；
- [ ] portable 的签名描述明确指内部第一方 PE，而不是 ZIP 容器 Authenticode；
- [ ] MSI 明确为 per-user，卸载保留用户数据和受管工具；
- [ ] 文档没有把遗留 MSIX/AppInstaller/PFX 路径当成 `v0.0.1` 主发布；
- [ ] 没有提交 token、PFX、私钥、内部 URL、本机构建缓存或生成产物；
- [ ] `git status` 中每个文件都属于本次发布范围。

## 2. 本地质量门禁

在干净的 Windows x64 环境执行：

```powershell
dotnet restore AutoEnvPlus.sln --locked-mode -p:Platform=x64 -r win-x64
dotnet format AutoEnvPlus.sln --verify-no-changes --no-restore
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj `
  -c Release -p:Platform=x64 -r win-x64 --no-restore -m:1
dotnet build src\AutoEnvPlus.App\AutoEnvPlus.App.csproj `
  -c Release -p:Platform=x64 -r win-x64 --self-contained true --no-restore -m:1
dotnet msbuild src\AutoEnvPlus.Shim\AutoEnvPlus.Shim.proj `
  /t:Rebuild /p:Configuration=Release /p:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\test-packaging.ps1
git diff --check
git status --short -- ':(glob)**/packages.lock.json'
```

- [ ] 所有测试通过且跳过项有明确解释；
- [ ] Release x64 WinUI 和原生 Shim 构建无 warning/error；
- [ ] packaging contracts 和 locked dependencies 通过；
- [ ] 候选 commit 上重新记录命令、时间、环境和精确结果，不沿用旧数字。

## 3. 三类本地候选

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-single-file.ps1 `
  -Configuration Release -BuildCacheRoot D:\codex

powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish.ps1 `
  -Configuration Release -Version 0.0.1 -BuildCacheRoot D:\codex -NoArchive

powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-msi.ps1 `
  -Configuration Release -Version 0.0.1 -BuildCacheRoot D:\codex `
  -PortableRoot artifacts\AutoEnvPlus-win-x64
```

- [ ] single-file bundle map 包含 App/Core/CLR/Windows App SDK/PRI/XBF，staging 没有用户必须携带的外部 payload；
- [ ] single-file 在测试机实际启动，并记录首次自解压目录、空间和权限行为；
- [ ] portable 目录含 GUI、CLI、Shim、PRI/XBF、AGPL、第三方声明/许可证和完整树清单；
- [ ] MSI 使用固定 WiX 版本/哈希，包范围为 per-user，开始菜单、ARP、major upgrade 和禁止降级符合预期；
- [ ] MSI 卸载后程序文件被移除，而用户设置、受管根和已安装工具仍保留；
- [ ] 本地候选明确标为未签，不能上传为 GitHub prerelease。

## 4. SignPath OSS 外部配置

在准备 tag 前完成并由另一名维护者复核：

- [ ] SignPath Foundation/open-source 计划申请已获批准；
- [ ] Organization、Project 和 GitHub.com Trusted Build System 已创建并绑定本仓库；
- [ ] PE Artifact Configuration 以 GitHub artifact 的外层 `<zip-file>` 为根，只接收工作流列明的 single/portable 相对路径并返回相同结构；
- [ ] MSI Artifact Configuration 以外层 `<zip-file>` 为根，只返回一个 `AutoEnvPlus-win-x64.msi`；
- [ ] Signing Policy、证书、时间戳和审批规则已审核；
- [ ] `release-signing` GitHub environment 已配置必要保护/审批；
- [ ] secret `SIGNPATH_API_TOKEN` 已配置；
- [ ] variables `SIGNPATH_ORGANIZATION_ID`、`SIGNPATH_PROJECT_SLUG`、`SIGNPATH_SIGNING_POLICY_SLUG`、`SIGNPATH_PE_ARTIFACT_CONFIGURATION_SLUG`、`SIGNPATH_MSI_ARTIFACT_CONFIGURATION_SLUG`、`SIGNPATH_EXPECTED_SIGNER_THUMBPRINT` 全部配置；
- [ ] token 不可用于 PR/fork PR，日志与 artifact 不包含 token 或其他签名凭据。

任一项缺失时不得 tag。release workflow 也会 fail closed，不存在“先发未签 ZIP”的回退。

## 5. 手工体验与安装安全

- [ ] Windows 10 与 Windows 11 真机分别测试 single-file、portable 和 MSI；
- [ ] 浅色/深色、高对比度、关闭透明、100%/125%/150% 缩放；
- [ ] 键盘导航、焦点、Narrator/屏幕阅读器和长文本；
- [ ] single-file 首次和重复启动、自解压失败、临时目录无权限及磁盘不足；
- [ ] portable 完整解压、GUI/CLI/Shim、移动目录和只复制 EXE 的预期失败提示；
- [ ] MSI 全新安装、同版本拒绝、升级、降级拒绝、卸载和数据保留；
- [ ] 9 个受管适配器、精确 Provider 身份、下载/缓存/项目关键路径至少各核对一次；
- [ ] 安全审查没有未处置的发布阻断项。

无法完成的项目必须进入 release notes，不能静默省略。

## 6. PR 与普通 CI

- [ ] 从主题分支推送全部候选改动并创建 PR；
- [ ] PR 正文包含范围、安全影响、测试证据、三类资产策略和已知限制；
- [ ] `.github/workflows/ci.yml` 在候选 commit 上实际通过；
- [ ] 确认 PR/普通 CI 只构建未签测试 artifact，不调用 SignPath；
- [ ] 审查意见已解决，最终 diff 与本地验证 commit 一致；
- [ ] PR 已合并并从 GitHub 回读 merge commit。

本地 `git push` 成功不证明 PR、CI 或合并状态。

## 7. Tag 与签名 prerelease

在目标 commit 完成门禁后：

```powershell
git switch main
git pull --ff-only
git tag -a v0.0.1 -m "AutoEnvPlus v0.0.1 test release"
git push origin v0.0.1
```

`.github/workflows/release.yml` 会验证 tag/仓库/版本，构建未签候选，强制检查 SignPath 配置，签第一方 PE，重建 portable，构建 MSI，签 MSI 外层，逐项验签，再生成七个文件。只有全部完成才创建或发布 prerelease；已有资产时拒绝覆盖或追加。

- [ ] tag 指向已审核的精确 commit；
- [ ] `release-signing` environment 审批和 SignPath 两次请求均成功；
- [ ] 工作流日志确认所有列明 PE 和 MSI Authenticode 状态为 `Valid`；
- [ ] GitHub Release 标记为 **prerelease**，标题明确“test release”；
- [ ] 资产恰好是三个主资产、三个 sidecar 和 `SHA256SUMS.txt`；
- [ ] 没有独立 CLI、MSIX、AppInstaller、PFX、证书私钥、bundle manifest 或未签候选混入 Release；
- [ ] Release 已从 draft 转为公开 prerelease，而不是只上传 workflow artifact。

## 8. 发布后回读

- [ ] 从 GitHub 重新下载七个文件并核对文件名、大小、sidecar 与聚合 SHA-256；
- [ ] 检查 single-file EXE 与 MSI 的 Authenticode Subject、链、时间戳和状态；
- [ ] 解压 portable，检查 App EXE/App DLL/Core DLL/CLI EXE/原生 Shim 的 Authenticode；
- [ ] 在 Windows 10/11 至少各一台机器运行三类资产的适用流程；
- [ ] 回读 tag、commit、prerelease 标记、工作流 URL、SignPath request 与残余风险；
- [ ] 发布后另开 PR，把 `CHANGELOG.md` 的“待发布”更新为真实日期；
- [ ] 任何错误均停止分发，修复后递增版本，不静默替换相同版本资产。

## 失败处理

- SignPath 配置缺失、请求超时、服务失败或签名无效：不创建公开 Release，不回退未签资产；
- single-file/portable/MSI 任一缺失：整体发布失败，不做部分发布；
- 哈希不一致：停止分发并调查构建、签名、重打包和上传链；
- portable 内部 PE 未签但 ZIP 哈希正确：仍视为发布失败；
- MSI 外层已签但内部 payload 不是已审核签名版本：仍视为发布失败；
- GitHub Release 已有资产：拒绝覆盖/追加；如果已公开或可能被下载，使用新版本；
- 发现可利用漏洞：暂停发布，通过私有安全渠道修复和验证后再决定版本。
