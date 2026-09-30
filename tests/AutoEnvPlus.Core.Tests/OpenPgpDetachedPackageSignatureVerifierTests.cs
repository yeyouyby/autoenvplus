using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AutoEnvPlus.Core.Tests;

public sealed class OpenPgpDetachedPackageSignatureVerifierTests : IDisposable
{
    private static readonly Uri SignatureUri = new("https://example.test/package.zip.sig");
    private static readonly Uri KeyUri = new("https://keys.example.test/release.asc");
    private static readonly Lazy<OpenPgpFixture> Fixture = new(OpenPgpFixture.Create);
    private static readonly Lazy<OpenPgpFixture> NonSigningPrimaryFixture = new(
        () => OpenPgpFixture.Create(PgpKeyFlags.CanCertify));
    private static readonly Lazy<OpenPgpFixture> ExpiredPrimaryFixture = new(
        () => OpenPgpFixture.Create(
            PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign,
            keyValidSeconds: 1));
    private static readonly Lazy<OpenPgpFixture> RevokedPrimaryFixture = new(
        () => OpenPgpFixture.Create(
            PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign,
            revokePrimary: true));
    private static readonly Lazy<OpenPgpSubkeyFixture> BoundSubkeyFixture = new(
        () => OpenPgpSubkeyFixture.Create(removeBinding: false, keyFlags: PgpKeyFlags.CanSign));
    private static readonly Lazy<OpenPgpSubkeyFixture> UnboundSubkeyFixture = new(
        () => OpenPgpSubkeyFixture.Create(removeBinding: true, keyFlags: PgpKeyFlags.CanSign));
    private static readonly Lazy<OpenPgpSubkeyFixture> NonSigningSubkeyFixture = new(
        () => OpenPgpSubkeyFixture.Create(removeBinding: false, keyFlags: 0));
    private static readonly Lazy<OpenPgpSubkeyFixture> ExpiredSubkeyFixture = new(
        () => OpenPgpSubkeyFixture.Create(
            removeBinding: false,
            keyFlags: PgpKeyFlags.CanSign,
            keyValidSeconds: 1));
    private static readonly Lazy<OpenPgpSubkeyFixture> RevokedSubkeyFixture = new(
        () => OpenPgpSubkeyFixture.Create(
            removeBinding: false,
            keyFlags: PgpKeyFlags.CanSign,
            revokeSubkey: true));
    private static readonly Lazy<OpenPgpSubkeyFixture> MissingBackSignatureFixture = new(
        () => OpenPgpSubkeyFixture.Create(
            removeBinding: false,
            keyFlags: PgpKeyFlags.CanSign,
            crossCertified: false));
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-DetachedSignature-{Guid.NewGuid():N}");

    [Fact]
    public async Task VerifyAsync_VerifiesBinaryDocumentWithPinnedPrimaryFingerprint()
    {
        OpenPgpFixture fixture = Fixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        List<Uri> requests = [];
        using HttpClient client = CreateClient(fixture, requests);

        PackageSignatureVerification result = await new OpenPgpDetachedPackageSignatureVerifier(client)
            .VerifyAsync(packagePath, CreateRequirement(fixture.Fingerprint));

        Assert.Equal(PackageSignatureVerificationKind.OpenPgpDetached, result.Kind);
        Assert.Equal("SHA-512", result.HashAlgorithm);
        Assert.Equal(fixture.Fingerprint, result.PrimaryKeyFingerprint);
        Assert.Equal(fixture.KeyId, result.SigningKeyId);
        Assert.Equal(SignatureUri, result.SignatureUri);
        Assert.Equal(KeyUri, result.KeySourceUri);
        Assert.Equal([SignatureUri, KeyUri], requests);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPackageChangedAfterSigning()
    {
        OpenPgpFixture fixture = Fixture.Value;
        byte[] changed = [.. fixture.Content, 0xFF];
        string packagePath = CreatePackage(changed);
        using HttpClient client = CreateClient(fixture, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.Fingerprint)));

        Assert.Contains("signature is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPublicKeyWithDifferentPinnedFingerprint()
    {
        OpenPgpFixture fixture = Fixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(new string('A', 40))));

        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsDeclaredSignatureOverByteLimitBeforeParsing()
    {
        OpenPgpFixture fixture = Fixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        List<Uri> requests = [];
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            HttpResponseMessage response = StubHttpMessageHandler.Bytes(fixture.Signature);
            response.Content.Headers.ContentLength =
                OpenPgpDetachedPackageSignatureVerifier.MaximumSignatureBytes + 1L;
            return response;
        }));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.Fingerprint)));

        Assert.Contains("byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([SignatureUri], requests);
    }

    [Fact]
    public async Task VerifyAsync_RejectsDeclaredPublicKeyOverByteLimitBeforeParsing()
    {
        OpenPgpFixture fixture = Fixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        List<Uri> requests = [];
        using HttpClient client = new(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            HttpResponseMessage response = request.RequestUri == SignatureUri
                ? StubHttpMessageHandler.Bytes(fixture.Signature)
                : StubHttpMessageHandler.Bytes(fixture.PublicKey);
            if (request.RequestUri == KeyUri)
            {
                response.Content.Headers.ContentLength =
                    OpenPgpDetachedPackageSignatureVerifier.MaximumPublicKeyBytes + 1L;
            }

            return response;
        }));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.Fingerprint)));

        Assert.Contains("byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([SignatureUri, KeyUri], requests);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPrimaryKeyWithoutSigningKeyFlag()
    {
        OpenPgpFixture fixture = NonSigningPrimaryFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.Fingerprint)));

        Assert.Contains("not authorized for signing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPrimaryKeyExpiredWhenPackageWasSigned()
    {
        OpenPgpFixture fixture = ExpiredPrimaryFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.Fingerprint)));

        Assert.Contains("primary key expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPrimaryKeyRevokedBeforePackageWasSigned()
    {
        OpenPgpFixture fixture = RevokedPrimaryFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.Fingerprint)));

        Assert.Contains("primary key was revoked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_VerifiesSigningSubkeyBoundByPinnedPrimary()
    {
        OpenPgpSubkeyFixture fixture = BoundSubkeyFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture.Signature, fixture.PublicKey, []);

        PackageSignatureVerification result = await new OpenPgpDetachedPackageSignatureVerifier(client)
            .VerifyAsync(packagePath, CreateRequirement(fixture.PrimaryFingerprint));

        Assert.Equal(fixture.SigningKeyId, result.SigningKeyId);
        Assert.Equal(fixture.PrimaryFingerprint, result.PrimaryKeyFingerprint);
    }

    [Fact]
    public async Task VerifyAsync_RejectsInjectedSigningSubkeyWithoutPrimaryBinding()
    {
        OpenPgpSubkeyFixture fixture = UnboundSubkeyFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture.Signature, fixture.PublicKey, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.PrimaryFingerprint)));

        Assert.Contains("no valid binding", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsBoundSubkeyWithoutSigningKeyFlag()
    {
        OpenPgpSubkeyFixture fixture = NonSigningSubkeyFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture.Signature, fixture.PublicKey, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.PrimaryFingerprint)));

        Assert.Contains("authorize", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsSigningSubkeyWithoutBackSignature()
    {
        OpenPgpSubkeyFixture fixture = MissingBackSignatureFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture.Signature, fixture.PublicKey, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.PrimaryFingerprint)));

        Assert.Contains("back-signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsSubkeyExpiredWhenPackageWasSigned()
    {
        OpenPgpSubkeyFixture fixture = ExpiredSubkeyFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture.Signature, fixture.PublicKey, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.PrimaryFingerprint)));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsSubkeyRevokedBeforePackageWasSigned()
    {
        OpenPgpSubkeyFixture fixture = RevokedSubkeyFixture.Value;
        string packagePath = CreatePackage(fixture.Content);
        using HttpClient client = CreateClient(fixture.Signature, fixture.PublicKey, []);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpenPgpDetachedPackageSignatureVerifier(client).VerifyAsync(
                packagePath,
                CreateRequirement(fixture.PrimaryFingerprint)));

        Assert.Contains("revoked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string CreatePackage(byte[] content)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "package.zip");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static HttpClient CreateClient(OpenPgpFixture fixture, List<Uri> requests) =>
        CreateClient(fixture.Signature, fixture.PublicKey, requests);

    private static HttpClient CreateClient(
        byte[] signature,
        byte[] publicKey,
        List<Uri> requests) =>
        new(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri == SignatureUri
                ? StubHttpMessageHandler.Bytes(signature)
                : StubHttpMessageHandler.Bytes(publicKey);
        }));

    private static PackageSignatureRequirement CreateRequirement(string fingerprint) => new(
        PackageSignatureVerificationKind.OpenPgpDetached,
        SignatureUri,
        KeyUri,
        "package.zip",
        fingerprint,
        PackageSignerTrust.ActiveAtTrustSnapshot);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record OpenPgpFixture(
        byte[] Content,
        byte[] Signature,
        byte[] PublicKey,
        string Fingerprint,
        string KeyId)
    {
        public static OpenPgpFixture Create() => Create(
            PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);

        public static OpenPgpFixture Create(
            int keyFlags,
            long keyValidSeconds = 0,
            bool revokePrimary = false)
        {
            SecureRandom random = new();
            RsaKeyPairGenerator generator = new();
            generator.Init(new RsaKeyGenerationParameters(
                Org.BouncyCastle.Math.BigInteger.ValueOf(65_537),
                random,
                2048,
                25));
            PgpKeyPair keyPair = new(
                PublicKeyAlgorithmTag.RsaGeneral,
                generator.GenerateKeyPair(),
                DateTime.UtcNow.AddMinutes(-1));
            char[] password = "test-password".ToCharArray();
            PgpSignatureSubpacketGenerator primaryMetadata = new();
            primaryMetadata.SetKeyFlags(isCritical: true, flags: keyFlags);
            if (keyValidSeconds > 0)
            {
                primaryMetadata.SetKeyExpirationTime(isCritical: true, seconds: keyValidSeconds);
            }

            PgpKeyRingGenerator ringGenerator = new(
                PgpSignature.PositiveCertification,
                keyPair,
                "AutoEnvPlus Test Key <test@example.test>",
                SymmetricKeyAlgorithmTag.Aes256,
                password,
                true,
                primaryMetadata.Generate(),
                null,
                random);
            PgpSecretKeyRing secretKeyRing = ringGenerator.GenerateSecretKeyRing();
            PgpSecretKey secretKey = secretKeyRing.GetSecretKey();
            PgpPrivateKey privateKey = secretKey.ExtractPrivateKey(password);
            PgpPublicKeyRing publicKeyRing = ringGenerator.GeneratePublicKeyRing();
            if (revokePrimary)
            {
                PgpPublicKey primaryPublicKey = publicKeyRing.GetPublicKey();
                PgpSignatureGenerator revocationGenerator = new(
                    PublicKeyAlgorithmTag.RsaGeneral,
                    HashAlgorithmTag.Sha256);
                revocationGenerator.InitSign(PgpSignature.KeyRevocation, privateKey, random);
                PgpSignature revocation = revocationGenerator.GenerateCertification(primaryPublicKey);
                primaryPublicKey = PgpPublicKey.AddCertification(primaryPublicKey, revocation);
                publicKeyRing = PgpPublicKeyRing.RemovePublicKey(publicKeyRing, primaryPublicKey)
                    ?? throw new InvalidOperationException(
                        "The generated primary key could not be replaced after revocation.");
                publicKeyRing = PgpPublicKeyRing.InsertPublicKey(publicKeyRing, primaryPublicKey);
            }
            byte[] content = "AutoEnvPlus detached signature fixture"u8.ToArray();

            PgpSignatureGenerator signatureGenerator = new(
                PublicKeyAlgorithmTag.RsaGeneral,
                HashAlgorithmTag.Sha512);
            signatureGenerator.InitSign(PgpSignature.BinaryDocument, privateKey, random);
            signatureGenerator.Update(content);
            using MemoryStream signatureStream = new();
            signatureGenerator.Generate().Encode(signatureStream);
            using MemoryStream publicKeyStream = new();
            publicKeyRing.Encode(publicKeyStream);

            return new OpenPgpFixture(
                content,
                signatureStream.ToArray(),
                publicKeyStream.ToArray(),
                Convert.ToHexString(keyPair.PublicKey.GetFingerprint()),
                unchecked((ulong)keyPair.KeyId).ToString("X16"));
        }
    }

    private sealed record OpenPgpSubkeyFixture(
        byte[] Content,
        byte[] Signature,
        byte[] PublicKey,
        string PrimaryFingerprint,
        string SigningKeyId)
    {
        public static OpenPgpSubkeyFixture Create(
            bool removeBinding,
            int keyFlags,
            long keyValidSeconds = 0,
            bool revokeSubkey = false,
            bool crossCertified = true)
        {
            SecureRandom random = new();
            PgpKeyPair primaryKeyPair = CreateKeyPair(random, DateTime.UtcNow.AddMinutes(-2));
            PgpKeyPair signingKeyPair = CreateKeyPair(random, DateTime.UtcNow.AddMinutes(-1));
            char[] password = "test-password".ToCharArray();
            PgpSignatureSubpacketGenerator primaryMetadata = new();
            primaryMetadata.SetKeyFlags(
                isCritical: true,
                flags: PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
            PgpKeyRingGenerator ringGenerator = new(
                PgpSignature.PositiveCertification,
                primaryKeyPair,
                "AutoEnvPlus Test Primary <test@example.test>",
                SymmetricKeyAlgorithmTag.Aes256,
                password,
                true,
                primaryMetadata.Generate(),
                null,
                random);
            PgpSignatureSubpacketGenerator bindingMetadata = new();
            bindingMetadata.SetKeyFlags(isCritical: true, flags: keyFlags);
            if (keyValidSeconds > 0)
            {
                bindingMetadata.SetKeyExpirationTime(isCritical: true, seconds: keyValidSeconds);
            }

            if (crossCertified)
            {
                ringGenerator.AddSubKey(
                    signingKeyPair,
                    bindingMetadata.Generate(),
                    null,
                    HashAlgorithmTag.Sha256,
                    HashAlgorithmTag.Sha256);
            }
            else
            {
                ringGenerator.AddSubKey(
                    signingKeyPair,
                    bindingMetadata.Generate(),
                    null,
                    HashAlgorithmTag.Sha256);
            }

            PgpSecretKeyRing secretRing = ringGenerator.GenerateSecretKeyRing();
            PgpPublicKeyRing publicRing = ringGenerator.GeneratePublicKeyRing();
            if (removeBinding)
            {
                PgpPublicKey signingPublicKey = publicRing.GetPublicKey(signingKeyPair.KeyId)
                    ?? throw new InvalidOperationException("The generated signing subkey is missing.");
                PgpSignature[] certifications = signingPublicKey.GetKeySignatures().ToArray();
                foreach (PgpSignature certification in certifications)
                {
                    signingPublicKey = PgpPublicKey.RemoveCertification(
                        signingPublicKey,
                        certification)
                        ?? throw new InvalidOperationException(
                            "The generated signing-subkey certification could not be removed.");
                }

                publicRing = PgpPublicKeyRing.RemovePublicKey(publicRing, signingPublicKey)
                    ?? throw new InvalidOperationException(
                        "The generated signing subkey could not be removed from its ring.");
                publicRing = PgpPublicKeyRing.InsertPublicKey(publicRing, signingPublicKey);
            }
            else if (revokeSubkey)
            {
                PgpPublicKey primaryPublicKey = publicRing.GetPublicKey();
                PgpPublicKey signingPublicKey = publicRing.GetPublicKey(signingKeyPair.KeyId)
                    ?? throw new InvalidOperationException("The generated signing subkey is missing.");
                PgpPrivateKey primaryPrivateKey = secretRing.GetSecretKey()
                    .ExtractPrivateKey(password);
                PgpSignatureGenerator revocationGenerator = new(
                    PublicKeyAlgorithmTag.RsaGeneral,
                    HashAlgorithmTag.Sha256);
                revocationGenerator.InitSign(
                    PgpSignature.SubkeyRevocation,
                    primaryPrivateKey,
                    random);
                PgpSignature revocation = revocationGenerator.GenerateCertification(
                    primaryPublicKey,
                    signingPublicKey);
                signingPublicKey = PgpPublicKey.AddCertification(
                    signingPublicKey,
                    revocation);
                publicRing = PgpPublicKeyRing.RemovePublicKey(publicRing, signingPublicKey)
                    ?? throw new InvalidOperationException(
                        "The generated signing subkey could not be replaced after revocation.");
                publicRing = PgpPublicKeyRing.InsertPublicKey(publicRing, signingPublicKey);
            }

            PgpPrivateKey signingPrivateKey = secretRing.GetSecretKey(signingKeyPair.KeyId)
                .ExtractPrivateKey(password);
            byte[] content = "AutoEnvPlus detached signing-subkey fixture"u8.ToArray();
            PgpSignatureGenerator signatureGenerator = new(
                PublicKeyAlgorithmTag.RsaGeneral,
                HashAlgorithmTag.Sha512);
            signatureGenerator.InitSign(PgpSignature.BinaryDocument, signingPrivateKey, random);
            signatureGenerator.Update(content);
            using MemoryStream signatureStream = new();
            signatureGenerator.Generate().Encode(signatureStream);
            using MemoryStream publicKeyStream = new();
            publicRing.Encode(publicKeyStream);

            return new OpenPgpSubkeyFixture(
                content,
                signatureStream.ToArray(),
                publicKeyStream.ToArray(),
                Convert.ToHexString(primaryKeyPair.PublicKey.GetFingerprint()),
                unchecked((ulong)signingKeyPair.KeyId).ToString("X16"));
        }

        private static PgpKeyPair CreateKeyPair(SecureRandom random, DateTime createdAt)
        {
            RsaKeyPairGenerator generator = new();
            generator.Init(new RsaKeyGenerationParameters(
                Org.BouncyCastle.Math.BigInteger.ValueOf(65_537),
                random,
                2048,
                25));
            return new PgpKeyPair(
                PublicKeyAlgorithmTag.RsaGeneral,
                generator.GenerateKeyPair(),
                createdAt);
        }
    }
}
