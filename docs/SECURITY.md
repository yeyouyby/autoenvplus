# AutoEnvPlus 安全模型

## 运行时安装信任链

AutoEnvPlus 把“下载来源”“checksum 来源”和“发布者签名”作为不同层级处理：

```text
Python:  固定 trusted root + 版本系列身份 -> 验证 manifest Sigstore bundle -> 读取包 SHA-256
Node:    固定发布密钥 --------------------> 验证签名清单 ------------> 读取包 SHA-256
Temurin: 固定发布密钥 ----------------------------------------------> 验证 ZIP detached 签名
                                                        下载 ZIP -> 逐字节 SHA-256 -> 安全解压 -> 原子提交
```

HTTPS 用于传输保护，SHA-256 用于内容完整性，OpenPGP/Sigstore 数字签名用于把清单或包绑定到受信发布身份。任何一层都不能替代其他层。

当前 Provider 策略：

| Provider | checksum | 发布者签名 |
|---|---|---|
| Node.js | 已签名 `SHASUMS256.txt.asc` 中的 SHA-256 | 验证 OpenPGP cleartext signature |
| python.org | Sigstore 签名 Windows manifest 中的 PythonCore ZIP SHA-256 | 验证 Fulcio 身份、SCT、Rekor 透明日志与 manifest 签名 |
| Eclipse Temurin | Adoptium API/checksum link 提供的 SHA-256 | 下载后验证包本体的 OpenPGP detached signature |

签名必需的安装资产不能降级为 checksum-only。对 Python，安装器要求包哈希证据的来源 URI 等于 Sigstore 实际验证的 manifest URI，bundle URI 必须是该 manifest URI 加 `.sigstore`；无关 bundle 不能授权另一个清单。对 Node.js，安装器要求提供当前包哈希的 checksum 证据 URI 与已验证签名正文 URI 完全一致；无关清单上的有效签名不能授权另一个包。对 Temurin，安装计划必须携带与包文件名完全一致的 detached 签名要求；安装器完成 SHA-256 后流式验证同一个缓存包，签名无效、指纹错配或证据与计划不一致时删除缓存包且不进入解压。

## Python Sigstore 信任策略

Python 3.14 起不再发布 OpenPGP 签名，python.org 明确要求使用 Sigstore。AutoEnvPlus 验证 Windows release manifest，而不是只验证 ZIP 中的 `python.exe`：只有签名 manifest 提供的 PythonCore ZIP SHA-256 才能进入下载与安装。

身份策略固定到 python.org 的 [`Sigstore Information`](https://www.python.org/downloads/metadata/sigstore/) 表格，而不是从 bundle 自报身份推断。当前源码覆盖 3.7–3.17 的官方 release manager 邮件和 OIDC Issuer；例如 Python 3.14/3.15 必须精确匹配：

- RFC822 SAN：`hugo@python.org`；
- Fulcio OIDC Issuer：`https://github.com/login/oauth`。

未知 Python 系列会安全失败并要求先审核 python.org 的新身份策略。验证流程还强制执行：

- release-file API 必须同时提供 Windows manifest、SHA-256 和匹配的 `.sigstore` bundle URI；
- 只接受 `application/vnd.dev.sigstore.bundle.v0.3+json`，一个 SHA-256 message signature、一个 Fulcio 叶证书和一个 Rekor entry；
- 叶证书必须具有 critical digital-signature key usage、code-signing EKU、单一 critical RFC822 SAN 和精确 OIDC Issuer；
- 证书链必须在 Rekor integrated time 上有效，并终止于固定 trusted root 中当时有效的 Fulcio trust anchor；
- 重建 RFC 6962 precertificate TBS，使用固定 CT log 公钥实际验证至少一个 SCT 签名，而不只比较 SCT log ID；
- canonicalized body 必须是 `hashedrekord 0.0.1`，其中 artifact SHA-256、签名字节和 PEM 证书必须分别与 bundle/manifest/叶证书逐项一致；
- Rekor log ID 必须存在于固定 trusted root；SET、Merkle inclusion proof、tree checkpoint 签名和最终 artifact ECDSA 签名全部验证成功；
- bundle 缺失、身份不匹配、manifest 篡改、SET/包含证明/检查点/证书链/SCT 任一失败时拒绝该 release，不回退到 release-file API checksum-only。

因此，目录中仍可显示 python.org 的历史稳定版本，但只有实际发布了 Sigstore-signed Windows manifest 的版本才能生成安装资产；例如缺少该 bundle 的旧版会返回明确错误，而不是恢复此前的双层 SHA-256-only 安装路径。

Sigstore 信任根不在运行时通过任意在线 TUF bootstrap 建立。AutoEnvPlus 内置并逐字节校验官方 [`sigstore/root-signing`](https://github.com/sigstore/root-signing) 提交 `0287f2e6b92ffaa95621afa7732d30f46344040c` 的 `targets/trusted_root.json`：

```text
SHA-256 6494e21ea73fa7ee769f85f57d5a3e6a08725eae1e38c755fc3517c9e6bc0b66
信任快照 2026-07-14
```

底层密码学管线使用固定 NuGet `Sigstore.Net 1.0.3`（源码提交 `d592bc978c6a27cc46e502da9792a1b6ec1c588f`），`packages.lock.json` 固定包及传递依赖内容哈希。源码审计发现该版本自带 `TufClient` 会在线下载第 14 版 root 后只做自签名检查，缺少完整 root 轮换、过期、回滚及 target length/hash 验证；它的默认身份解析也不适用于 Python 的 RFC822 SAN，SCT 路径只比较 log ID。AutoEnvPlus 因此注入拒绝联网的 TUF 实现、始终显式传入内置 trusted root，并在调用底层管线前后独立执行邮件/OIDC、trusted-root 时间窗口、canonicalized body 与完整 SCT 签名验证。不能改回库的默认网络 TUF 路径。

当前离线真实夹具与在线验证均覆盖 Python 3.14.6：manifest SHA-256 `610e427f32889496c08584f0397d2d1d649ed0096be8bcb4341bbe2d3c27138c`，PythonCore x64 ZIP SHA-256 `75afa83f93b284d19040e24bc440ab741c09582c0d5310504d607a4e08c3dbaf`。更新 trusted root 时必须固定新的官方提交和文件 SHA-256，复核 CA/Rekor/CT 时间窗口，更新真实 bundle 夹具，并重新运行身份、manifest 篡改、SET、inclusion proof、checkpoint 与 SCT 失败测试。

## Node.js 发布密钥

Node.js 信任根来自官方 [`nodejs/release-keys`](https://github.com/nodejs/release-keys) 仓库，但运行时不追踪漂移的 `main`：

- 密钥文件固定到提交 `b28073028e6d6855cfb53bf7fa0137599c01f967`；
- 源码固定 29 个官方发布主密钥及 66 个主钥/签名子钥 Key ID 映射；
- 下载单把公钥后重新计算完整 160-bit 主指纹，不能只比较 64-bit Key ID；
- 拒绝不在固定映射中的签名钥、指纹错配、撤销钥、签名时已过期钥和非 SHA-256 签名；
- 签名日期必须位于 Node 发布索引日期前后两天内；
- 信任快照日期为 2026-07-14，此日期及之后的版本只允许快照中的活跃发布密钥；历史密钥只能验证更早版本。

密钥本身通过 HTTPS 下载是为了减少应用体积；真正的信任锚是源码中的完整主指纹、签名子钥映射和固定提交。网络返回不同密钥会在验签前因指纹不匹配而失败。

## 密钥轮换流程

Node.js 增加、轮换或撤销发布密钥时，AutoEnvPlus 必须发布更新。维护步骤：

1. 同时核对 `nodejs/release-keys` README、`keys.list` 与 nodejs.org 验证说明；
2. 固定新的已审核 Git 提交，不使用 `main` 作为发布信任来源；
3. 解析每个主密钥的全部签名子钥，更新 Key ID 到完整主指纹映射；
4. 更新活跃密钥集合与信任快照日期；
5. 用最新官方 `SHASUMS256.txt.asc` 和对应公钥更新离线测试夹具；
6. 验证正文篡改、错误日期、未知密钥、历史密钥和签名降级均被拒绝；
7. 发布新的签名 AutoEnvPlus 构建。

如果遇到未知合法新钥，旧版 AutoEnvPlus 应安全失败并要求更新信任根，不能自动信任网络返回的新指纹。

## Eclipse Temurin 发布密钥

Adoptium API 的 `binaries[].package.signature_link` 指向每个 JDK ZIP 的 detached OpenPGP 签名。AutoEnvPlus 的信任策略为：

- 固定官方验证文档公布的完整主指纹 `3B04D753C9050D9A5D343F39843C48A565F8F04B`；
- 通过 HTTPS 从 Ubuntu Keyserver 的精确完整指纹查询端点取得公钥，再重新计算 160-bit 主指纹；
- 只接受 OpenPGP binary-document detached signature 和 SHA-256/SHA-384/SHA-512 摘要，当前官方包使用 RSA/SHA-512；
- 拒绝撤销钥、签名时已过期钥、早于密钥创建时间或位于未来的签名；
- 先验证 Adoptium SHA-256，再验证包本体签名；两者都成功才允许解压；
- 公钥网络来源只负责传输，源码中的完整主指纹才是信任锚。

当前已用 2026-04 Temurin 8、11、17、21、25 最新 Windows x64 签名元数据核对同一主指纹和 `843C48A565F8F04B` Key ID。Adoptium API 的发布时间与包签名时间并不严格相等，历史 GA 包可相差数周，因此不把二者的人为时间窗口作为真实性条件；签名本身直接覆盖完整包字节。

## AutoEnvPlus 自身分发链

MSIX 发布脚本不从包清单或更新元数据自报的 Publisher 推断信任。生产模式要求显式 PFX、密码和 Publisher，加载证书后验证私钥、有效期、digital-signature key usage、code-signing EKU、非 CA 属性，并要求 Publisher 与证书规范 Subject 按字节表现完全一致。缺少任一输入时在构建前失败，不能自动降级为未签名包。MSIX 使用 SHA-256 签名；指定 `TimestampUri` 时同时要求 RFC 3161/SHA-256 时间戳。

打包后重新读取 `AppxSignature.p7x`，去掉 PKCX 包装并执行 SignedCms 密码学验签，再核对签名者指纹。随后使用 MakeAppx 解包并把清单 Name、Publisher、四段版本和 x64 架构与 AppInstaller 的 MainPackage 及命令参数逐项比较。生产证书还必须通过系统 Authenticode 信任链验证；开发证书只允许完成密码学验证，不会被写入 Windows 信任库，私钥和 PFX 在签名后删除。

AppInstaller 本身是 XML，当前 Windows SDK SignTool 不识别为可 Authenticode 签名格式。其安全边界是 HTTPS 分发和目标 MSIX 的 Publisher 签名：元数据只能选择与声明 Name、Publisher 和架构一致且签名有效的包，不能凭一个被篡改的 URL 授权不同发布者。SHA-256 sidecar 用于发布审计，不冒充 Windows 自动执行的元数据签名。

## 尚未完成

- 正式 AutoEnvPlus 发布者证书、可信时间戳和证书撤销/轮换流程；
- 可审计的密钥撤销在线更新通道；
- Windows 11、代理劫持、时间异常和离线密钥缓存的完整端到端测试。

## 项目终端隔离

项目终端计划不包含任意用户命令或 Shell 拼接内容。可执行文件固定为系统 Windows PowerShell，参数固定为 `-NoLogo -NoExit`，工作目录来自包含已解析 manifest 的项目根。启动时重新验证清单哈希、托管运行时、Shim 和环境覆盖；只把白名单会话变量与 Shim 优先 PATH 写入新子进程环境块。用户 PATH、系统 PATH、父进程环境和全局版本配置不会被修改。

vcpkg 与 Conan 存储迁移沿用相同的白名单配置目标：快照中的 `VCPKG_DEFAULT_BINARY_CACHE` 或 `CONAN_HOME` 被篡改为 `PATH` 等其他变量时，回滚验证会拒绝执行。迁移只在逐文件 SHA-256 复制验证完成后切换用户变量，原目录和回滚后的迁移副本均保留，删除由用户另行确认。
