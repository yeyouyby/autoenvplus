# AutoEnvPlus 路线图

路线图以可验证能力为单位，不以目录条目数量代替实现进度。`v0.0.1` 当前是待发布测试版；只有 tag、工作流和 GitHub Release 回读成功后才算完成发布。

## v0.0.1 测试版候选

### 产品模型与 Fluent 工作台

- [x] 固定“语言 -> 语言工具 -> Provider -> Provider 来源”领域模型；
- [x] 45 门内置语言、136 个工具条目、140 条 Provider Profile、83 个来源槽和显式能力矩阵；
- [x] Top 10 + 快照发现 + 用户启用 - 用户隐藏的语言可见性；
- [x] WinUI 3 导航、主题、材质、密度与 Windows 10/11 回退策略；
- [x] 概览和语言页 snapshot-on-load，完整扫描、PATH 重检和联网诊断显式触发；
- [x] 产品、架构、安全、用户、开发和发布文档体系。

### 安装、版本与命令

- [x] CPython、Node.js、Eclipse Temurin、.NET SDK 四个官方归档适配器；
- [x] MSVC Build Tools、Clang、GCC/WinLibs、CMake、Ninja 五个固定 WinGet 适配器；
- [x] 明确只有上述 9 个真实受管适配器，其余 127 个工具不冒充安装器；
- [x] 新终端会话 -> 项目 -> 全局 -> 自动选择的解析顺序；
- [x] 三层 selector + Runtime ID + Provider ID 精确身份；
- [x] 18 个无 CLR 原生 x64 Shim、一次 PATH 配置和 CMD 回退；
- [x] 安装收据、统一锁序、补偿事务、引用扫描和安全卸载。

### 项目、下载与存储

- [x] 常见版本文件导入、`autoenvplus.toml`、schema 2 项目锁和精确项目终端；
- [x] Python/Node/.NET/Java/Rust/Go/构建 Wrapper 的按需只读解析；
- [x] MSVC Host/Target 激活与 `CMakeUserPresets.json` 预览、快照和回滚；
- [x] HTTPS 单流/分段下载、本地导入、受管下载库和哈希证据；
- [x] WinUI wheel 离线/联网计划与明确非事务边界；
- [x] 常见包缓存发现、统计、事务迁移、隔离恢复和永久清理；
- [x] 六域显式诊断、JSON 导出和有界脱敏活动记录。

### 来源、扩展与安全

- [x] `languageToolId + providerId + slotId` Provider 来源与独立代理模型；
- [x] data-only 语言包 schema 1，导入默认停用；
- [x] Runtime Provider schema 2 精确绑定 9 个桥接工具，兼容导入 schema 1；
- [x] Python Sigstore、Node.js OpenPGP、Temurin detached signature 和 .NET SHA-512 evidence；
- [x] HTTPS/URI/大小边界、安全解压、reparse point 防护、原子状态和脱敏；
- [x] WinUI single-file、完整 portable ZIP 与 per-user MSI 三类 x64 构建路径；
- [x] GitHub Windows CI 与 tag 驱动、SignPath fail-closed 的 prerelease 工作流定义。

### 发布门禁

- [ ] 最终代码和安全审查无发布阻断项；
- [ ] Windows CI 在候选 commit 上实际通过；
- [ ] Windows 10/11 真机启动、缩放、主题、高对比度、键盘和屏幕阅读器验收；
- [ ] SignPath OSS 组织/项目、Trusted Build System、PE/MSI Artifact Configuration、Signing Policy 与 release-signing environment 实际配置并获批；
- [ ] `AutoEnvPlus-win-x64.exe`、`AutoEnvPlus-win-x64-portable.zip`、`AutoEnvPlus-win-x64.msi` 三个主资产及其 sidecar/聚合 SHA-256 从 GitHub 下载回读通过；
- [ ] 单文件 EXE、便携包内第一方 PE、MSI 内 payload 与 MSI 外层 Authenticode 回读通过；
- [ ] `v0.0.1` tag 与 GitHub prerelease 实际创建并回读；
- [ ] 三类主资产缺一、SignPath 失败或签名复检失败时均不发布，且没有未签名回退。

## v0.0.x 稳定化

- [ ] 扩充磁盘不足、权限失败、取消、崩溃恢复和并发故障注入测试；
- [ ] 真实 CDN、认证代理、限流、实体切换和大文件传输矩阵；
- [ ] 完整 Windows 10/11 安装、升级、卸载和回滚 E2E；
- [ ] 键盘、Narrator、高对比度、125%/150%/200% 缩放与长文本修复；
- [ ] 基于 Windows 目录句柄进一步降低同账户 rename/reparse 竞态；
- [ ] 缩短长下载持有的运行时事务锁范围；
- [ ] 完善错误恢复、诊断建议和用户可理解的证据摘要；
- [ ] 完成 SignPath 发布者证书、可信时间戳、轮换和撤销流程的运行记录与维护策略。

## v0.1 产品扩展

- [ ] 环境方案导入/导出：语言启用集、Provider pin、精确版本、来源、缓存策略和项目锁；
- [ ] 工作区自动激活规则与经过审核的目录切换 hook；
- [ ] 下载队列带宽限制、计划执行、失败重试和跨重启恢复；
- [ ] 可审计的 pip/npm/Maven resolver 任务编排与依赖图，不通过透明劫持实现；
- [ ] vcpkg/Conan 项目依赖和锁文件集成；
- [ ] Yarn Berry 与更多生态缓存/来源投影；
- [ ] 容器、WSL、Dev Container、Nix/Guix 等外部环境的只读边界；
- [ ] 离线环境包：工具归档、包、锁和来源证据的验证式导入/导出。

## v0.2 信任与团队能力

- [ ] 签名语言目录、EOL/漏洞/撤销元数据与可解释升级建议；
- [ ] Language Pack v2：包版本、应用兼容、依赖、更新通道和发布者摘要；
- [ ] 签名 Provider 插件包、受审计更新源、撤销列表和组织允许策略；
- [ ] 团队环境基线、漂移时间线、修复预览和可回滚变更集；
- [ ] 团队网络/镜像策略与策略锁；
- [ ] 精确全局/项目/会话选择的组织策略和跨设备迁移；
- [ ] 扩展工具 Provider 声明能力，同时继续限制代码执行面；
- [ ] 沙箱化外部 Provider Host，以受限 RPC 承载确需执行的安装协议。

## 1.0 质量目标

- [ ] 生产签名 Windows x64 安装与更新渠道；
- [ ] 支持范围内 Windows 10/11 的持续 E2E、视觉与可访问性门禁；
- [ ] 稳定的密钥/目录/插件更新与撤销机制；
- [ ] 数据迁移、备份、恢复和兼容策略；
- [ ] ARM64 应用构建与运行时管理；
- [ ] 明确、可测量且由适配器支持的工具覆盖，不以元数据数量作为完成指标。

当前已知限制见 [功能清单](FEATURES.md#当前限制)，版本开发记录见 [变更记录](CHANGELOG.md)，发布操作见 [发布指南](RELEASING.md)。
