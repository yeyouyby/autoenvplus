# AutoEnvPlus 第三方组件声明

本文件记录 AutoEnvPlus 当前直接使用或随自包含发布复制的主要第三方组件。AutoEnvPlus 本体按仓库根目录 [`LICENSE`](LICENSE) 中的 `AGPL-3.0-only` 发布；该许可证不会替代或改变下列第三方代码的原许可证。

## Sigstore.Net 1.0.3

- 用途：Python release manifest 的 Sigstore bundle、Fulcio 证书链、Rekor SET/Merkle inclusion proof/checkpoint 和 artifact signature 底层验证；
- NuGet：`Sigstore.Net` 1.0.3；
- 审核源码：[`ozimakov/sigstore-dotnet`](https://github.com/ozimakov/sigstore-dotnet) 提交 `d592bc978c6a27cc46e502da9792a1b6ec1c588f`；
- 许可证：Apache License 2.0；
- 许可证原文：[`third_party/licenses/Sigstore.Net-Apache-2.0.txt`](third_party/licenses/Sigstore.Net-Apache-2.0.txt)。

AutoEnvPlus 不使用该库的默认在线 TUF bootstrap，并为 Python RFC822 身份、固定 trusted root、canonicalized body 与 SCT 签名增加独立的 fail-closed 校验。具体边界见 [`docs/SECURITY.md`](docs/SECURITY.md)。

## BouncyCastle.Cryptography 2.6.2

- 用途：Node.js/Temurin OpenPGP 验证，以及 Sigstore.Net 的密码学依赖；
- 项目：[`bcgit/bc-csharp`](https://github.com/bcgit/bc-csharp)；
- 许可证：MIT License，见 [`third_party/licenses/BouncyCastle-MIT.md`](third_party/licenses/BouncyCastle-MIT.md)。

## Google.Protobuf 3.29.3

- 用途：Sigstore bundle 与 trusted-root protobuf JSON 模型；
- 项目：[`protocolbuffers/protobuf`](https://github.com/protocolbuffers/protobuf)；
- 许可证：BSD 3-Clause License，见 [`third_party/licenses/Google-Protobuf-BSD-3-Clause.txt`](third_party/licenses/Google-Protobuf-BSD-3-Clause.txt)。

## Microsoft .NET 运行库与 Extensions 9.0.0

Sigstore.Net 的传递依赖包括 `Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Http`、`Microsoft.Extensions.Logging.Abstractions`、`Microsoft.Extensions.Options`、`System.Security.Cryptography.Pkcs` 及其 .NET 运行库依赖。这些组件由 Microsoft 在 MIT License 下发布；许可证见 [`third_party/licenses/Microsoft-DotNet-MIT.txt`](third_party/licenses/Microsoft-DotNet-MIT.txt)，准确版本与 NuGet 内容哈希固定在 [`src/AutoEnvPlus.Core/packages.lock.json`](src/AutoEnvPlus.Core/packages.lock.json)。

每个 NuGet 包中的许可证文件和包元数据仍是该组件许可条款的权威文本。生成正式分发包前，应保留本声明并复核锁文件中的新增依赖。
