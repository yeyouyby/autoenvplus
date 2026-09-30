# AutoEnvPlus 故障排除

AutoEnvPlus 对损坏状态、来源漂移和精确身份不一致采用 fail-closed：拒绝继续通常是保护行为，不应通过删除随机状态文件或关闭校验来绕过。

## 首页或语言页没有数据

**原因**：两个页面按设计只读取持久快照，首次启动没有快照。

**处理**：

1. 在概览手动运行完整环境扫描；
2. 在语言页按需执行 PATH 重检；
3. 检查受管根是否切换到了另一个目录；
4. 用 CLI 只读复核：

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- doctor --json
dotnet run --project src\AutoEnvPlus.Cli -- list
```

不要期待打开页面自动扫描 PATH、缓存或网络。

## 新终端仍调用旧工具

1. 在“PATH 与命令”确认 Shim 已安装且 `<managed-root>\shims` 位于冲突工具之前；
2. 关闭并重新打开终端，现有进程不会自动刷新用户 PATH；
3. 运行：

```powershell
dotnet run --project src\AutoEnvPlus.Cli -- which python
dotnet run --project src\AutoEnvPlus.Cli -- which node
```

4. 检查当前目录或父目录是否有 `autoenvplus.toml`；项目选择优先于全局选择；
5. 如果同版本有多个 Provider，确认项目 `[tool-identities]` 或全局 profile 中的 Runtime ID/Provider ID 对仍存在。

不要反复手工把具体运行时目录加入 PATH；AutoEnvPlus 的目标是只加入一次 Shim 目录。

## 更改 D 盘受管根后数据消失

更改 `AUTOENVPLUS_HOME` 只改变下一次启动使用的根，不会自动迁移旧数据。先确认实际优先级：CLI `--root` 高于环境变量，环境变量高于默认目录。

不要把旧根和新根直接合并。使用应用提供的迁移/导入流程，或在完全停止 AutoEnvPlus 后按 [架构和安全文档](ARCHITECTURE.md) 评估状态文件。第三方工具仍可能写 C 盘，这不表示受管根配置未生效。

## 安装目录刷新或下载失败

检查：

- 系统时间、TLS 和 HTTPS 代理；
- 所选 Provider 来源是否属于当前工具和 Provider；
- 自定义 URI 是否包含被拒绝的凭据、query 或 fragment；
- 代理是否只填写了 HTTP/HTTPS，且 `NO_PROXY` 每项格式有效；
- Provider 是否要求签名清单或 detached signature，而镜像没有提供对应证据；
- 目标受管根是否空间不足、只读或经过重解析点。

镜像改变网络路径，不会绕过签名或哈希合同。自定义 .NET index 同时控制资产和 checksum 时，只能显示为“信任该镜像元数据”，不是 Microsoft 发布者签名。

## 多连接下载退回单流

这是协议允许的正常结果。分段要求：

- 已知内容长度；
- 正确支持 byte ranges；
- 强 ETag 或 Last-Modified 可绑定同一实体；
- 每个分段返回一致的 206、`Content-Range`、长度和实体标识。

服务端、CDN 或代理缺少任一条件时使用单流；实体变化或畸形响应则明确失败。提高连接数不保证提速。

## 下载文件显示 stale 或证据失效

下载库会重新计算有预期哈希证据的当前文件身份。文件提交后被手工替换，即使长度相同，也不会继续显示旧验证状态。重新从可信来源下载/导入并提供独立取得的预期哈希；不要修改清单来伪造一致。

## 插件无法启用或目录为空

确认：

- 新清单使用 schema 2 和受支持的 `languageToolId`；
- 旧 schema 1 只用于兼容导入，不能同时出现 `runtimeKind` 与 `languageToolId`；
- 文件小于限制、不是重解析点、没有未知/重复字段、注释或尾随逗号；
- URL 是无凭据/query/fragment 的绝对 HTTPS；
- 每个 ZIP asset 恰好有 SHA-256 或 SHA-512、checksum 引用和安全 `.exe` 入口；
- 导入后已经再次显式启用，并在目录/安装时选择 `plugin:<id>`。

损坏的已启用插件或 activation state 会让 Registry 整体失败关闭。先保留文件用于诊断，再按 [Provider 插件指南](PROVIDER-PLUGINS.md) 修复或通过受支持的删除流程处理。

## 项目终端拒绝启动

计划生成后，AutoEnvPlus 会复检项目清单、托管注册表、Provider 来源、代理、宿主和可执行文件。任何变化都会使旧确认失效。

1. 重新打开项目并生成新预览；
2. 检查 `[tools]` 与 `[tool-identities]` 是否成对且匹配已安装 Runtime ID/Provider ID；
3. 确认对应 Shim 和入口文件存在；
4. Windows Terminal 不可用时选择或接受 PowerShell 回退；
5. 重新生成过期的 `autoenvplus.lock`。

## 缓存清理后空间没有释放

第一阶段只是同卷隔离，仍可恢复，因此不会释放对应磁盘空间。确认不需要恢复后，执行第二次“永久清空”。Gradle/Conan 等混合目录不提供整目录清理，需迁移或使用各工具自身的安全清理命令。

## wheel 安装失败后环境仍存在

这是已知非事务边界。创建 venv 或 pip 写入可能已发生，取消只终止进程树，不会猜测并删除整个环境。检查截断标记与日志尾部，确认环境不再被引用后再手动使用受管 UI 清理；不要将失败状态描述为已经回滚。

## 源码构建失败

先确认 `dotnet --version` 与 `global.json`，然后使用单进程构建：

```powershell
dotnet --info
dotnet restore AutoEnvPlus.sln --locked-mode -p:Platform=x64 -r win-x64
dotnet test tests\AutoEnvPlus.Core.Tests\AutoEnvPlus.Core.Tests.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore -m:1
dotnet build src\AutoEnvPlus.App\AutoEnvPlus.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true --no-restore -m:1
```

原生 Shim 构建还需要 Visual Studio C++ 工具和 Windows SDK。缓存或临时目录权限异常时，按 [快速开始](QUICKSTART.md#2-可选把数据和构建缓存放到-d-盘) 设置独立 D 盘目录。

## 提交问题前收集信息

请提供：

- Windows 版本、架构和 AutoEnvPlus 版本；
- 最小复现步骤与预览/错误文本；
- `doctor --json` 的必要片段；
- 相关活动记录摘要；
- 是否使用自定义受管根、代理、Provider 来源或插件。

分享前移除用户名、本机绝对路径、内部主机名和其他组织信息。安全漏洞不要附在公开 Issue；按仓库安全报告渠道私下提交。
