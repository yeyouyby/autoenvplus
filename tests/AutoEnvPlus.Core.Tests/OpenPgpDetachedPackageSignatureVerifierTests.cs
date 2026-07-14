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

    private string CreatePackage(byte[] content)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "package.zip");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static HttpClient CreateClient(OpenPgpFixture fixture, List<Uri> requests) =>
        new(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri == SignatureUri
                ? StubHttpMessageHandler.Bytes(fixture.Signature)
                : StubHttpMessageHandler.Bytes(fixture.PublicKey);
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
        public static OpenPgpFixture Create()
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
            PgpKeyRingGenerator ringGenerator = new(
                PgpSignature.PositiveCertification,
                keyPair,
                "AutoEnvPlus Test Key <test@example.test>",
                SymmetricKeyAlgorithmTag.Aes256,
                password,
                true,
                null,
                null,
                random);
            PgpSecretKey secretKey = ringGenerator.GenerateSecretKeyRing().GetSecretKey();
            PgpPrivateKey privateKey = secretKey.ExtractPrivateKey(password);
            PgpPublicKeyRing publicKeyRing = ringGenerator.GeneratePublicKeyRing();
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
}
