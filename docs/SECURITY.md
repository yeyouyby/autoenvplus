# AutoEnvPlus 安全模型

## 运行时安装信任链

AutoEnvPlus 把“下载来源”“checksum 来源”和“发布者签名”作为不同层级处理：

```text
Node:    固定发布密钥 -> 验证签名清单 -> 从已签名正文读取包 SHA-256
Temurin: 固定发布密钥 ---------------------> 验证 ZIP detached 签名
                                      下载 ZIP -> 逐字节 SHA-256 -> 安全解压 -> 原子提交
```

HTTPS 用于传输保护，SHA-256 用于内容完整性，OpenPGP 等数字签名用于把清单绑定到受信发布密钥。任何一层都不能替代其他层。

当前 Provider 策略：

| Provider | checksum | 发布者签名 |
|---|---|---|
| Node.js | 已签名 `SHASUMS256.txt.asc` 中的 SHA-256 | 验证 OpenPGP cleartext signature |
| python.org | 经 release-file API SHA-256 验证的 Windows 清单及包哈希 | 尚未验证 |
| Eclipse Temurin | Adoptium API/checksum link 提供的 SHA-256 | 下载后验证包本体的 OpenPGP detached signature |

签名必需的安装资产不能降级为 checksum-only。对 Node.js，安装器要求提供当前包哈希的 checksum 证据 URI 与已验证签名 URI 完全一致；无关清单上的有效签名不能授权另一个包。对 Temurin，安装计划必须携带与包文件名完全一致的 detached 签名要求；安装器完成 SHA-256 后流式验证同一个缓存包，签名无效、指纹错配或证据与计划不一致时删除缓存包且不进入解压。

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

## 尚未完成

- Python 的 Authenticode、PGP、Sigstore 或等价发布者身份验证；
- AutoEnvPlus 自身的代码签名、签名安装器与自动更新元数据签名；
- 可审计的密钥撤销在线更新通道；
- Windows 11、代理劫持、时间异常和离线密钥缓存的完整端到端测试。

## 项目终端隔离

项目终端计划不包含任意用户命令或 Shell 拼接内容。可执行文件固定为系统 Windows PowerShell，参数固定为 `-NoLogo -NoExit`，工作目录来自包含已解析 manifest 的项目根。启动时重新验证清单哈希、托管运行时、Shim 和环境覆盖；只把白名单会话变量与 Shim 优先 PATH 写入新子进程环境块。用户 PATH、系统 PATH、父进程环境和全局版本配置不会被修改。

vcpkg 与 Conan 存储迁移沿用相同的白名单配置目标：快照中的 `VCPKG_DEFAULT_BINARY_CACHE` 或 `CONAN_HOME` 被篡改为 `PATH` 等其他变量时，回滚验证会拒绝执行。迁移只在逐文件 SHA-256 复制验证完成后切换用户变量，原目录和回滚后的迁移副本均保留，删除由用户另行确认。
