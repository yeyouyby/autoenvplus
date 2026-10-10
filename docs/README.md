# AutoEnvPlus 文档

本目录集中保存 AutoEnvPlus 的用户说明、功能边界、开发记录和发布流程（当前发布版本 `v0.0.3`）。根目录 [README](../README.md) 只提供项目概览；需要判断“产品现在究竟能做什么”时，以 [产品规格](PRODUCT.md)、[功能清单](FEATURES.md) 和源代码/测试为准。

## 我应该从哪里开始

| 读者 | 推荐入口 |
|---|---|
| 第一次运行 AutoEnvPlus | [快速开始](QUICKSTART.md) -> [用户指南](USER-GUIDE.md) -> [故障排除](TROUBLESHOOTING.md) |
| 想确认某个功能是否真的可执行 | [功能清单](FEATURES.md) -> [当前限制](FEATURES.md#当前限制) |
| 语言包作者 | [语言与语言包](LANGUAGE-PACKS.md) -> [语言包 schema](../schemas/language-pack.schema.json) |
| Provider 插件作者 | [Provider 插件指南](PROVIDER-PLUGINS.md) -> [插件 schema](../schemas/runtime-provider-plugin.schema.json) |
| 贡献者 | [开发指南](DEVELOPMENT.md) -> [技术架构](ARCHITECTURE.md) -> [安全模型](SECURITY.md) |
| 发布维护者 | [发布指南](RELEASING.md) -> [分发说明](DISTRIBUTION.md) -> [变更记录](CHANGELOG.md) |

## 文档地图

### 使用产品

- [快速开始](QUICKSTART.md)：系统要求、从源码运行、首次扫描和 D 盘受管根；
- [用户指南](USER-GUIDE.md)：页面模型、安装、版本选择、项目、下载、缓存和诊断；
- [故障排除](TROUBLESHOOTING.md)：常见错误、安全失败和最小复核命令；
- [功能清单](FEATURES.md)：完整功能矩阵、目录计数、9 个真实适配器和限制。

### 理解产品

- [产品规格](PRODUCT.md)：权威领域模型、交互合同与验收条件；
- [技术架构](ARCHITECTURE.md)：WinUI、CLI、Core、Shim、存储和网络的边界；
- [安全模型](SECURITY.md)：发布者信任、哈希/签名、文件系统和进程边界；
- [路线图](ROADMAP.md)：各版本已交付范围与后续方向。

### 扩展与交付

- [语言与语言包](LANGUAGE-PACKS.md)：45 门语言、136 个工具目录和 data-only 语言包；
- [Provider 插件指南](PROVIDER-PLUGINS.md)：schema 2、精确工具绑定和第三方 ZIP 信任边界；
- [开发指南](DEVELOPMENT.md)：仓库布局、D 盘构建环境和质量门禁；
- [分发说明](DISTRIBUTION.md)：WinUI 单文件、便携 ZIP、per-user MSI、自签名与校验细节；
- [发布指南](RELEASING.md)：PR、tag、GitHub prerelease 与回读检查；
- [变更记录](CHANGELOG.md)：版本级开发日志；
- [阶段交接](HANDOFF.md)：当前分支继续工作的短版上下文。

## 不变量

文档修改不得模糊以下边界：

1. 产品模型固定为“语言 -> 语言工具 -> Provider -> Provider 来源”。
2. 45 门语言、136 个工具和 140 条 Provider Profile 是元数据目录；只有 9 个真实受管安装适配器。
3. 首页和语言页加载快照，不自动扫描；完整扫描和 PATH 重检由用户显式触发。
4. 新终端会话、项目和全局选择都可同时固定 Runtime ID 与 Provider ID。
5. Provider 来源与通用代理是两套不同状态；镜像没有跨 Provider 的全局语义。
6. 语言包和 Runtime Provider 插件都是 data-only；插件下载的第三方可执行文件并不因此可信。
7. AutoEnvPlus 本体仅以 `AGPL-3.0-only` 发布。
8. AutoEnvPlus 是 Windows 10/11 x64 测试版本；没有对应版本的 GitHub Release 资产回读时，不得把该版本发布写成已完成。

## 文档维护

- 用户可见行为变化：同步更新 `FEATURES.md`、`USER-GUIDE.md` 与相关专题页；
- 安全边界变化：同步更新 `SECURITY.md`，并说明验证证据与残余风险；
- 版本或发布流程变化：同步更新 `CHANGELOG.md`、`RELEASING.md` 和版本权威源；
- 新增目录能力：同时核对“元数据 Profile”与“可执行适配器”的表述，不能只根据目录计数宣称已支持安装。
