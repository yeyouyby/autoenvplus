# AutoEnvPlus 安全模型

## 运行时安装信任链

AutoEnvPlus 把“下载来源”“checksum 来源”和“发布者签名”作为不同层级处理：

```text
Python:  固定 trusted root + 版本系列身份 -> 验证 manifest Sigstore bundle -> 读取包 SHA-256
Node:    固定发布密钥 --------------------> 验证签名清单 ------------> 读取包 SHA-256
Temurin: 固定发布密钥 ----------------------------------------------> 验证 ZIP detached 签名
.NET:    Microsoft HTTPS release metadata --------------------------> 读取 Windows ZIP SHA-512
Plugin: 第三方 JSON + 第三方 checksum 来源 ------------------------> 匹配声明的 SHA-256/SHA-512
                                                        下载 ZIP -> 逐字节匹配声明算法 -> 安全解压 -> 原子提交
```

HTTPS 用于传输保护，SHA-256/SHA-512 用于内容完整性，OpenPGP/Sigstore 数字签名用于把清单或包绑定到受信发布身份。任何一层都不能替代其他层；只有 checksum evidence 的 Provider 不得显示成已经验证发布者签名。

当前 Provider 策略：

| Provider | checksum | 发布者签名 |
|---|---|---|
| Node.js | 已签名 `SHASUMS256.txt.asc` 中的 SHA-256 | 验证 OpenPGP cleartext signature |
| python.org | Sigstore 签名 Windows manifest 中的 PythonCore ZIP SHA-256 | 验证 Fulcio 身份、SCT、Rekor 透明日志与 manifest 签名 |
| Eclipse Temurin | Adoptium API/checksum link 提供的 SHA-256 | 下载后验证包本体的 OpenPGP detached signature |
| Microsoft .NET SDK | 官方 channel `releases.json` 中的 Windows ZIP SHA-512 | 当前无独立包签名证据；明确使用 checksum-evidence 模式 |
| 声明式插件 | 插件 JSON 声明的 SHA-256/SHA-512；`checksumSourceUri` 只是未抓取的核对引用 | 不视为已验证的上游清单、官方签名或 AutoEnvPlus 验证的发布者身份 |

签名必需的安装资产不能降级为 checksum-only。对 Python，安装器要求包哈希证据的来源 URI 等于 Sigstore 实际验证的 manifest URI，bundle URI 必须是该 manifest URI 加 `.sigstore`；无关 bundle 不能授权另一个清单。对 Node.js，安装器要求提供当前包哈希的 checksum 证据 URI 与已验证签名正文 URI 完全一致；无关清单上的有效签名不能授权另一个包。对 Temurin，安装计划必须携带与包文件名完全一致的 detached 签名要求；安装器完成 SHA-256 后流式验证同一个缓存包，签名无效、指纹错配或证据与计划不一致时删除缓存包且不进入解压。

通用安装器按 Provider 声明的算法分区缓存并逐字节计算 SHA-256 或 SHA-512；计划中的算法、哈希长度、值或 HTTPS 来源证据不匹配会在解压前安全失败，实际缓存包字节的哈希不匹配还会删除该完成包。托管注册表 schema 2 同时保存 `packageHashAlgorithm` 与 `packageHash`。为平滑升级，Core 和原生 Shim 仍读取 schema 1 的 `packageSha256` 并只将其解释为 SHA-256；schema 2 中未知算法、长度错误或缺少字段会安全失败，而不是猜测算法。项目锁也完整验证 manifest SHA-256、版本、架构、Provider、算法和哈希；旧 schema 1 锁按 SHA-256 迁移读取，损坏或未来 schema 的锁会让引用扫描安全阻止普通卸载，而不是静默失去保护。

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

## .NET SDK 元数据边界

.NET Provider 只从 Microsoft 官方 HTTPS `releases-index.json` 发现通道，再读取通道自身声明的 HTTPS `releases.json`。它限制到 active/maintenance 支持阶段，忽略预览 SDK，只接受 RID 为 `win-x64`、`win-x86` 或 `win-arm64` 的 ZIP，并要求元数据中的 SHA-512 值由 128 个十六进制字符组成。安装器校验同一个 ZIP 的 SHA-512 后才允许安全解压并原子提交到 `<managed-root>\runtimes\dotnet\sdk\<version>\<architecture>`。

Microsoft release metadata 在当前实现中是 checksum 来源，不是 AutoEnvPlus 已验证的独立发布者数字签名。界面和 CLI 必须把它显示为 SHA-512 checksum evidence，不得沿用 Python、Node.js 或 Temurin 的签名成功提示。运行 `dotnet` Shim 或 CLI `exec` 时，`DOTNET_ROOT` 只指向本次解析出的受管 SDK 根，并为该子进程设置 `DOTNET_MULTILEVEL_LOOKUP=0`；不会持久写入用户或系统环境。

## 声明式 Provider 插件边界

Runtime Provider 插件是 schema 1 JSON 数据，不是进程内扩展代码。公开契约支持 Python、Node.js、Java、.NET SDK、MSVC、LLVM、MinGW、CMake 和 Ninja。导入器只读取本地普通 `.json` 文件，限制大小并拒绝重解析点，随后用严格解析器拒绝未知字段、重复属性、注释、尾随逗号、超限 release/asset、非 HTTPS URI、URI user-info/query/fragment、路径逃逸、非 ZIP 文件名和非 `.exe` 入口。机器可读契约见 [JSON Schema](../schemas/runtime-provider-plugin.schema.json)，完整使用边界见 [Provider 插件指南](PROVIDER-PLUGINS.md)。

清单不能声明 DLL、程序集、脚本、任意命令、安装/卸载参数、注册表或环境变量写入、PATH 修改、服务、计划任务、安装后钩子、自定义 WinGet ID 或任意目标目录。导入只把规范化 JSON 原子复制到 `<managed-root>\plugins\runtime-providers\<id>.json`，初始状态固定为停用；用户必须在 WinUI 或 CLI 中再次显式启用。启用集合单独原子写入 `<managed-root>\state\runtime-provider-plugins.json`，进程内门闩和受管 lock 文件覆盖导入、状态变化及删除。损坏的已启用清单、悬空启用 ID 或损坏 state 会让插件 Registry fail closed，而不是静默忽略并继续安装。

启用后仍要求精确 Provider 选择。第三方 Provider ID 固定为 `plugin:<id>`；未启用、类别不匹配或版本/架构不存在时直接失败，不回退到同类别内置 Provider 或其他插件。目标固定为 `<managed-root>\runtimes\<kind>\plugins\<plugin-id>\<version>\<architecture>`，运行时 ID、托管注册表和项目锁保存实际 Provider。相同版本/架构的多个来源互不覆盖，WinUI 不自动把冲突来源设为全局默认。停用只阻止后续目录查询和新安装；删除只移除插件清单与启用状态。两者都不卸载已经登记的运行时、不改项目锁，也不删除共享下载缓存。

插件 asset 必须声明 HTTPS `downloadUri`、HTTPS `checksumSourceUri` 及恰好一个 SHA-256/SHA-512。AutoEnvPlus 会确认下载字节与插件 JSON 中的哈希完全一致，但两个值都由插件作者选择，所以这只能证明“字节匹配该第三方声明”，不能证明发布者身份。schema 1 不抓取、解析或验证 `checksumSourceUri` 的响应；它只是让用户核对插件作者声称的独立 checksum 页面或清单。它与下载 URL 相同时界面会提示证据更弱。声明式插件不继承 python.org Sigstore、Node.js OpenPGP 或 Temurin detached signature 的成功结论，UI/CLI 必须显示“插件声明的 checksum 引用”，不能写成 “Verified by”。

插件的显式 asset URL 不经过内置 Provider 镜像改写，只使用类别对应的受管代理与 `NO_PROXY`；C/C++ 五类共用 `runtime-cpp`。这可防止把官方 API base/release-index 语义错误套到第三方路径，但代理仍只改变传输路径，不增加发布者信任。

通用安装器继续强制下载大小、ZIP 条目数和解压大小上限，拒绝 Zip Slip、重解析点与受管根逃逸，复检预期入口后才原子提交。安装目录的受管收据绑定 Provider、版本、类别、架构、包名、哈希算法和值、预期入口及入口 `.exe` SHA-256；重复安装时重新计算入口哈希，缺失/不匹配收据、包摘要变化或入口篡改都会失败，而不是仅凭同名 `.exe` 复用旧目录。数据-only 清单防止“导入时执行代码”，不代表 ZIP 内的 `.exe` 安全；下载或导入不会自动执行资产，用户实际启动第三方运行时时仍在执行第三方代码。将 `AUTOENVPLUS_HOME` 指向 D 盘可约束插件清单、状态、下载、暂存和安装目录，但不能把第三方运行时变成文件系统沙箱，也不能保证其以后不写 C 盘或用户 Profile。

C/C++ 插件只增加便携 ZIP 来源，不能替代固定 WinGet 白名单和内置工具链激活。特别是 MSVC 插件不能声明 `vcvarsall.bat`、Visual Studio workload 或 `INCLUDE`/`LIB` 注入；一个预期 `cl.exe` 存在只满足插件入口复检，不足以证明完整的 MSVC/Windows SDK 环境可用。

## 网络、代理与镜像边界

代理设置文件位于受管根 `state\network-settings.json`；Provider 源位于 `state\provider-source-preferences.json`。前者保存通用 HTTP(S) 代理与 `NO_PROXY`，后者以精确 `languageToolId + providerId + slotId` 保存目录槽覆盖和用户自定义源。保存前的结构化验证执行以下约束：

- 代理必须是绝对 HTTP/HTTPS URI；镜像必须是绝对 HTTPS URI；
- 代理和镜像都拒绝 URI user-info、query 与 fragment，因此 `https://user:pass@host/`、`?token=...` 或签名 query 不能进入配置文件；
- Provider 源只引用有效语言目录中的工具、Provider 和槽；不可覆盖槽、重复所有权、未知引用和未来 schema 安全失败；
- `NO_PROXY` 每项只能是单个 host、address、CIDR 或 wildcard，拒绝空白、控制字符和逗号/分号拼接项；
- Core 模型的 `ToString()` 不输出端点，WinUI/CLI 展示再次清除凭据、query 与 fragment，活动记录执行通用凭据与敏感 query 脱敏。

AutoEnvPlus 当前没有独立代理用户名/密码的凭据保险库，不能把密码放进 JSON。`NetworkProxyPolicy.Credentials` 使用 Windows `CredentialCache.DefaultCredentials`，因此 AutoEnvPlus 自己的 HTTP Client 可以与支持 Windows 集成身份的代理协商，但不会保存或发送用户在 URI 中填写的独立密码。CLI 启动受管工具时只向本次子进程设置有效代理和已解析 Provider 源，不改用户的 pip/npm 配置；这些工具是否使用 Windows 集成身份由各自实现决定。普通终端中由用户直接启动的工具不会因为配置文件存在就自动受控。

代理或镜像只改变网络路径，不构成发布者信任。运行时 Provider 仍必须执行各自的哈希与签名契约：Python 的签名 manifest、Node.js 的签名 checksum 清单和 Temurin 的包本体签名不能因镜像而降级。`.NET` 是特殊弱边界：官方 index 模式只有 Microsoft HTTPS 元数据中的 SHA-512 checksum evidence；若用户把 `runtime-dotnet` 指向自定义 index，该镜像同时控制资产 URL 与预期 checksum，而当前没有独立签名把它绑定到 Microsoft。界面和文档必须把这描述为“信任用户配置的镜像元数据”，不能称为 Microsoft 发布者验证。

pip/npm 等包工具的来源同样遵循各生态自身的 TLS、包索引和包签名/哈希机制；AutoEnvPlus 只传入端点，不额外证明来源中的包可信。诊断中的连接检查只证明受限 HTTP 请求得到响应，不证明端点身份、内容正确或未来下载安全。Provider 源没有跨协议“全局镜像”回退，避免把一个 URL 错误套给路径语义不同的工具。

## 受管下载边界

下载中心与 Provider 运行时安装是不同信任域。前者接受用户给出的 HTTPS URL 或本地文件，后者接受带 Provider 证据链的精确资产。受管下载器要求初始 URL 与所有重定向都是 HTTPS，并拒绝嵌入式 URI 凭据。下载 URL 可以携带临时签名 query，但计划/来源展示、活动记录和库清单只展示或持久化移除 query/user-info/fragment 后的来源；`HttpRequestException` 对用户只报告 HTTP 状态或 `transport failure`，不把底层异常中的原始签名 URL 写入 UI、CLI 或日志。

多连接只在服务器证明可安全拆分时启用。`HEAD` 和 `bytes=0-0` 探测总长度、Range 语义以及强 ETag 或 Last-Modified；每个分段使用 `If-Range` 并复核 206、精确 `Content-Range`、长度和实体标识。Range 不受支持、缺少稳定实体或实体变化时转为有原因记录的单流；畸形范围响应直接失败，不能把可能来自不同版本的字节拼接。单流与分段都受最大字节数约束。多连接是可选传输策略，不是必然加速或绕过服务端限流的承诺。

网络下载与本地导入只在 `<managed-root>\downloads\library\.autoenvplus-staging` 暂存，提交目标必须是库根直接子普通文件和白名单扩展，路径及已有祖先不能经过 reparse point。取消会传播到并发分段并进入本次暂存清理路径；如果 IO/权限阻止尽力清理，残留仍位于受管 staging，不能据此宣称清理绝对成功。导入复制来源文件，并在复制前后检查普通文件身份、长度和哈希；它不会授予外部来源路径对受管库的后续写权限。最终提交、覆盖和删除按目标锁再库级 `FileShare.None` 锁的顺序串行化；网络传输不长期占用全库锁。目标文件与原子清单作为一个补偿事务更新，失败时恢复旧文件，恢复失败则保留受管 recovery evidence。

下载库清单在反序列化前限制为 8 MiB，并最多接受 8,192 个条目；写入路径执行同样限制，避免 AutoEnvPlus 自己生成下一次无法读取的清单。超限清单会让列举、删除和后续提交失败关闭，已有下载文件不会因此被自动删除。

所有完成文件都会计算 SHA-256 作为内容标识。这只能用于判断库中记录对应哪些字节，不能证明发布者或下载来源；攻击者提供的文件也有确定的 SHA-256。只有用户从可信独立渠道取得预期 SHA-256/SHA-512，并在下载/导入时提供且成功匹配，才能声称文件与该预期值一致。即使匹配，也只有预期值本身绑定到可信发布者时才构成来源信任。没有预期哈希时，UI 必须显示“仅记录内容 SHA-256”，不能显示成“已验证可信”。有预期证据时，列举会重新计算当前文件身份；若文件在提交后被替换，即使长度相同也只保留历史记录并标记当前证据失效，pip 计划会据此拒绝继续。

下载、导入和列举都不执行库内容；允许保存 `.exe`、`.msi`、`.msix` 等扩展不等于授权自动启动。删除需要 WinUI 明确确认，Core 只处理库根直接子普通文件，并先移入操作暂存；清单更新失败时恢复，恢复也失败时保留隔离证据。成功删除是永久操作，不冒充可恢复缓存清理协议。

## 本地 wheel 安装边界

本地 wheel 安装只接受下载库顶层 `.whl` 和托管注册表中的 Python，不接受任意 Python 路径、任意输出目录或用户拼接命令。虚拟环境名受字符、长度、保留设备名和路径逃逸限制；环境固定在 `<managed-root>\environments\python`，`TEMP`/`TMP` 固定在 `<managed-root>\temporary\pip`，`PIP_CACHE_DIR` 固定在 `<managed-root>\caches\pip`，所有这些路径都拒绝 reparse point。

WinUI 先选择运行时、环境名和离线/联网依赖模式，再展示参数化的 venv 与 pip 命令、wheel、目录和回滚边界。计划包含自身完整性 SHA-256，以及受管 Python、wheel 和已有环境 Python 的规范路径、长度、时间与内容 SHA-256。执行开始时重新生成计划并比较；新建环境后、调用 pip 前再次复检关键文件。计划、网络设置或输入发生变化时拒绝沿用旧确认。stdout/stderr 始终被 drain 以免子进程堵塞，每路只在内存保留末尾 65,536 字符并携带独立截断标志；WinUI 不得把该尾部显示成完整日志。

托管运行时状态使用固定锁序：运行时事务锁先于注册表或全局 profile 的文件锁。注册、全局选择、安装补偿事务和卸载在同一跨进程事务域内重新读取状态；卸载不能依据过期预览跳过新引用，全局选择也不能在运行时被移除后写出悬空 selector。状态、锁文件、临时文件及现有祖先都拒绝 reparse point。当前 global/project selector schema 尚不持久化 Provider 身份，因此同版本多 Provider 时必须失败关闭，只有会话级 RuntimeId/ProviderId pin 能精确执行。

托管注册表在解析前限制为 4 MiB/4,096 个安装条目，全局 profile 限制为 256 KiB/64 个选择；Core 的原子写入和原生 Shim 的读取执行相同边界。原生 Shim 读取项目 `autoenvplus.toml` 时还施加 256 KiB 上限。超限文件按损坏状态处理，不尝试部分解析或截断后继续执行。

这不是事务安装。创建 venv 成功后 pip 可能失败；pip 也可能已写入部分包、入口点、缓存或联网依赖。取消会终止当前进程树，但不会删除整个环境或猜测哪些文件属于本次操作。严格离线模式使用 `--no-index --find-links <managed-library> --no-deps`，只安装当前已审核 wheel，不读取或安装其依赖；联网模式才把 `pip` 作用域的代理与镜像传入子进程并由真实 pip 解析依赖。两种模式都设置 `PIP_CONFIG_FILE=NUL`，并显式清理父进程的额外索引、代理、`ALL_PROXY` 等 pip 网络环境。联网依赖与第三方构建/安装逻辑仍可能忽略 `TEMP`/`TMP`/cache 设置并写入 C 盘、用户 Profile 或其他位置，因此“AutoEnvPlus 自有目录在受管根”不能扩展成“任意第三方脚本绝不写 C 盘”。

## AutoEnvPlus 自身分发链

MSIX 发布脚本不从包清单或更新元数据自报的 Publisher 推断信任。生产模式要求显式 PFX、密码和 Publisher，加载证书后验证私钥、有效期、digital-signature key usage、code-signing EKU、非 CA 属性，并要求 Publisher 与证书规范 Subject 按字节表现完全一致。缺少任一输入时在构建前失败，不能自动降级为未签名包。MSIX 使用 SHA-256 签名；指定 `TimestampUri` 时同时要求 RFC 3161/SHA-256 时间戳。

打包后重新读取 `AppxSignature.p7x`，去掉 PKCX 包装并执行 SignedCms 密码学验签，再核对签名者指纹。随后使用 MakeAppx 解包并把清单 Name、Publisher、四段版本和 x64 架构与 AppInstaller 的 MainPackage 及命令参数逐项比较。AppInstaller 还必须通过仓库内固定的封闭安全 profile schema：只允许一个 `MainPackage`、固定的更新设置和 2018 namespace，未知/重复元素、未知属性、强制降级、非 HTTPS URI、DTD 与外部实体全部安全失败。生产证书还必须通过系统 Authenticode 信任链验证；开发证书只允许完成密码学验证，不会被写入 Windows 信任库，私钥和 PFX 在签名后删除，因此生成物只是开发测试签名，不能作为生产签名发布。

AppInstaller 本身是 XML，当前 Windows SDK SignTool 不识别为可 Authenticode 签名格式。其安全边界是 HTTPS 分发和目标 MSIX 的 Publisher 签名：元数据只能选择与声明 Name、Publisher 和架构一致且签名有效的包，不能凭一个被篡改的 URL 授权不同发布者。SHA-256 sidecar 用于发布审计，不冒充 Windows 自动执行的元数据签名。

## 尚未完成

- 正式 AutoEnvPlus 发布者证书、可信时间戳和证书撤销/轮换流程；
- 可审计的密钥撤销在线更新通道；
- 声明式插件包的独立签名信任根、受审计更新源、撤销列表和组织允许策略；schema 1 当前是手动导入的静态 JSON；
- 托管运行时目录当前仍是“扫描后按字符串路径移动/删除”，同账户主动进程可在检查与操作间制造 rename/reparse 竞态；完整防护需要基于 Windows 目录句柄和禁止共享删除的实现；
- 运行时安装事务锁当前覆盖网络下载，长下载会让其他进程在 5 秒后失败、同进程调用等待到完成或取消；后续应拆分下载阶段与短状态提交阶段；
- pip 在内容复检后仍按路径启动 Python，同账户主动替换在哈希与 CreateProcess 之间存在竞态；完整防护需要私有执行副本或稳定文件身份/句柄策略；
- Windows 11、代理劫持、时间异常和离线密钥缓存的完整端到端测试。

## 项目终端隔离

项目终端计划不包含任意用户命令或 Shell 拼接内容。直接模式的可执行文件固定为系统 Windows PowerShell，参数固定为 `-NoLogo -NoExit`；Windows Terminal 模式固定为已发现的 `wt.exe`，并只生成 `new-tab --startingDirectory <project-root> <powershell.exe> -NoLogo -NoExit`。WinUI 允许在可用宿主之间选择；请求 Windows Terminal 但 `wt.exe` 不可用时，核心计划明确回退到 Windows PowerShell，CLI 默认请求 PowerShell。工作目录来自包含已解析 manifest 的项目根。启动时重新验证宿主、固定参数、清单哈希、网络设置哈希、托管运行时、Shim、环境覆盖和显式移除集合；只把白名单会话变量、网络变量与 Shim 优先 PATH 写入新子进程环境块。.NET 使用 `AUTOENVPLUS_DOTNET_VERSION` 选择精确 SDK，随后由 `dotnet` Shim 为其子进程设置 `DOTNET_ROOT`。用户 PATH、系统 PATH、父进程环境和全局版本配置不会被修改。

终端中的 pip/npm 镜像分别来自对应作用域。`HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` 是终端级共享变量：Python-only 使用 pip 作用域，Node-only 使用 npm 作用域，两者设置一致时使用共同值；设置冲突时计划明确警告并使用 `downloads` 作用域代理。这个规则避免静默随机选择，但不提供每条命令独立代理；需要完全隔离时应分别打开只含相应运行时的项目终端或通过 CLI `tool` 启动单个受管工具。大小写两套禁用变量会从新环境块移除，未建模的 `ALL_PROXY`/`all_proxy` 也始终移除，避免父进程残留的 SOCKS/全局代理绕过已审核设置。

PATH 快照不能仅凭位于受管目录就获得信任。回滚前必须重新验证它是直接子 JSON 文件、不是链接、大小受限、GUID 与文件名一致、Shim 目录为规范绝对路径，并能从 `Before` 唯一重建 `After`。当前 PATH 已被其他程序或用户改动时，快照状态只显示为 `PathChanged` 并拒绝覆盖。

vcpkg 与 Conan 存储迁移沿用相同的白名单配置目标：快照中的 `VCPKG_DEFAULT_BINARY_CACHE` 或 `CONAN_HOME` 被篡改为 `PATH` 等其他变量时，回滚验证会拒绝执行。迁移只在逐文件 SHA-256 复制验证完成后切换用户变量，原目录和回滚后的迁移副本均保留，删除由用户另行确认。

NuGet 的三个存储目标也使用彼此独立的白名单变量：`nuget` 只能修改 `NUGET_PACKAGES`，`nuget-http` 只能修改 `NUGET_HTTP_CACHE_PATH`，`nuget-plugins` 只能修改 `NUGET_PLUGINS_CACHE_PATH`。快照回滚会重新按缓存 ID 校验变量映射，不能通过篡改快照把任一迁移改为写入 `PATH` 或其他用户变量。

## 缓存清理边界

缓存清理不直接对发现到的任意目录执行递归删除。Core 只为固定白名单中的纯缓存定义创建计划，并拒绝驱动器根、网络共享根、重解析点、AutoEnvPlus 受管根任一方向的重叠，以及等于或包含用户 Profile、AppData、Windows、Program Files 等保护位置的路径。Maven/pnpm 的配置文件若落在候选缓存内部也会拒绝清理。Gradle User Home 和 Conan Home 包含非缓存配置，明确不在清理白名单中。

第一阶段只把经过两次清单比对的顶层项同卷移动到固定命名的相邻隔离根，manifest ID 必须与直接子目录名一致。执行变更前会用 `CreateFileW(FILE_FLAG_OPEN_REPARSE_POINT)` 逐段锁定目录路径，句柄不共享删除权限；因此源、隔离目录及永久清空树的祖先不能在检查后被换成 junction。恢复与永久清空都会重新读取大小受限、拒绝未知字段的 manifest，重新绑定缓存 ID、源路径、隔离路径与当前发现结果。恢复不会覆盖工具后来写入的新缓存；永久清空只处理隔离根内已枚举的普通文件，并按深度非递归移除空目录，不调用可能穿越链接的递归删除。任何链接、嵌套隔离根、额外顶层项或身份变化都会停止操作。部分永久清空是不可逆状态，只能继续清空，不能重新标为可恢复。

## 活动记录边界

活动记录只接受固定操作类型与终态，摘要和路径字段均有长度与数量上限。写入前会隐藏密码、令牌、Bearer 值、代理/PFX 凭据、URI user-info 和敏感查询参数；受影响路径必须是本地绝对路径，不能是网络 URI，也不能穿过已存在的重解析点。日志总大小、单行大小和条目数都有上限，超限文件不会被读取或自动覆盖。

同一进程的写入由路径级门闩串行化，不同进程通过受管目录内的独占锁文件覆盖完整的“读取—裁剪—原子替换”区间。损坏或未来 schema 的行不会阻断其他有效记录的只读加载，但存在这类行时会拒绝追加和重写，避免静默丢弃原始记录；错误信息不回显原始行内容。活动页面只读显示快照与回滚路径，不依据日志内容执行命令、打开任意 URI 或直接触发回滚。
