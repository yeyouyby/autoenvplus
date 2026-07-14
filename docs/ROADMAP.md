# AutoEnvPlus 路线图

## 原型阶段（当前）

- [x] 产品边界与版本解析优先级；
- [x] WinUI 3、Core、CLI、Tests 解决方案结构；
- [x] 版本选择与 PATH 检查的第一组领域实现；
- [x] PATH 运行时发现器与版本输出解析；
- [x] `autoenvplus.toml` 向上定位、`[tools]` 读取与选择器校验；
- [x] `.python-version`、`.nvmrc`、`.node-version`、`.java-version`、`package.json`、`global.json` 导入；
- [x] 精确版本、架构、Provider、包哈希的 `autoenvplus.lock`；
- [x] PATH 快照/回滚与缓存迁移事务；
- [x] Node.js 官方发行目录、SHA-256 资产解析与安全 ZIP 安装器；
- [x] Adoptium 官方 Java GA/LTS 版本线动态选择、Temurin 资产解析与 ZIP 安装计划；
- [x] python.org 双层 SHA-256 Windows manifest 与 PythonCore ZIP 安装计划；

## MVP 0.1

- [x] Python、Node.js、Java 发现与用户级托管安装；
- [x] 全局、项目、环境变量会话选择与 `exec`；
- [x] 预览、并发重检与子进程隔离的项目已激活 PowerShell；
- [x] CMD Shim 原型；
- [x] PowerShell 会话模块、受管 Profile 块、预览、快照与回滚；
- [x] 无 CLR Win32 原生 Shim、派生命令、递归保护与 CMD 回退；
- [x] PATH 编辑预览、受信快照枚举、WinUI 状态展示和执行时复检回滚；
- [x] pip/npm 与 NuGet 全局包、HTTP、插件缓存统计和迁移；
- [x] Maven `settings.xml` 与 Gradle User Home 事务迁移、快照和回滚；
- [x] pnpm `store-dir` 与 Yarn Classic 缓存事务迁移；
- [x] PATH、命令冲突和运行时健康诊断；
- [x] WinUI/CLI 聚合诊断页、结构化报告与 WinUI JSON 导出；
- [x] 下载与归档 SHA-256 校验；
- [x] HTTP Range、ETag/Last-Modified、If-Range 断点续传；
- [x] 引用扫描、隔离目录和注册表回滚的安全卸载；
- [x] 安装目录、托管注册表和全局选择的补偿式事务；
- [x] WinUI 安装预览中的逐条 SHA-256 来源证据与签名状态提示；
- [x] Node.js clear-signed checksum 清单的 OpenPGP 验证与固定发布密钥策略；
- [x] Eclipse Temurin 包本体 detached OpenPGP 验证与固定 Adoptium 主指纹；
- [x] Python Windows manifest 的 Sigstore v0.3、固定 release-manager 身份、Fulcio/SCT/Rekor 与内置 trusted-root 验证；

## 0.2

- Yarn Berry、.NET SDK；
- 项目模板与 Windows Terminal 集成；
- 代理、镜像、离线包与团队锁文件。

## 0.3

- [x] Visual Studio/Build Tools、Windows SDK、MSVC 发现与 x64/x86 Host/Target 激活终端；
- [x] LLVM/Clang、CMake、Ninja PATH 发现；
- [x] 项目级 MSVC `CMakeUserPresets.json` 预览、快照与回滚；
- [x] MinGW-w64/GCC PATH 发现、WinLibs POSIX/UCRT 白名单安装与 WinGet 单任务取消；
- [x] vcpkg 二进制缓存与 Conan 2 Home 的发现、事务迁移和回滚；
- [ ] vcpkg/Conan 项目依赖与锁文件集成；
- x64/x86/ARM64 目标环境。

## 1.0 质量门槛

- [x] x64 WinUI + 单文件 CLI + 原生 Shim 自包含便携发布与 SHA-256 清单；
- [x] 参数化 MSIX/AppInstaller、开发签名、生产证书 fail-closed 与包身份/更新 URI 验证；
- [x] Mica / Desktop Acrylic / 高对比度与透明效果纯色回退的能力检测和 Windows 10 22H2 隐藏 UI 验证；
- Windows 10 22H2 与受支持 Windows 11 构建的端到端测试；
- 中断、磁盘不足、权限失败与网络失败的恢复测试；
- 可访问性、高对比度和完整键盘导航；
- 真实发布者证书与可信时间戳、MSIX 安装/升级/回滚 E2E 和 WinGet 发布；
- ARM64 应用构建与运行时管理。
