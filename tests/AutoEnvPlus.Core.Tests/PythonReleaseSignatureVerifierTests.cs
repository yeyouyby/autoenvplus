using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Providers.Python;

namespace AutoEnvPlus.Core.Tests;

public sealed class PythonReleaseSignatureVerifierTests
{
    private static readonly Uri ManifestUri =
        new("https://www.python.org/ftp/python/3.14.6/windows-3.14.6.json");

    private static readonly Uri BundleUri =
        new("https://www.python.org/ftp/python/3.14.6/windows-3.14.6.json.sigstore");

    [Fact]
    public async Task VerifyAsync_VerifiesOfficialPython3146BundleWithPinnedIdentityAndTrustRoot()
    {
        byte[] manifest = ReadFixture("windows-3.14.6.json");
        byte[] bundle = ReadFixture("windows-3.14.6.json.sigstore");
        int requests = 0;
        using HttpClient client = CreateClient(bundle, () => requests++);
        PythonReleaseSignatureVerifier verifier = new(client);

        PackageSignatureVerification result = await verifier.VerifyAsync(
            manifest,
            ManifestUri,
            BundleUri,
            PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6")));

        Assert.Equal(PackageSignatureVerificationKind.SigstoreBundle, result.Kind);
        Assert.Equal(ManifestUri, result.SignedContentUri);
        Assert.Equal(BundleUri, result.SignatureUri);
        Assert.Equal(PythonReleaseSignatureVerifier.TrustRootSourceUri, result.KeySourceUri);
        Assert.Equal(PythonReleaseSignatureVerifier.TrustRootSha256, result.TrustRootSha256);
        Assert.Equal("hugo@python.org", result.CertificateIdentity);
        Assert.Equal("https://github.com/login/oauth", result.CertificateOidcIssuer);
        Assert.Equal(1779455023, result.TransparencyLogIndex);
        Assert.Equal(1657550766, result.TransparencyLogTreeSize);
        Assert.Equal("SHA-256", result.HashAlgorithm);
        Assert.Equal(64, result.PrimaryKeyFingerprint.Length);
        Assert.Equal(40, result.SigningKeyId.Length);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperedManifest()
    {
        byte[] manifest = ReadFixture("windows-3.14.6.json");
        manifest[0] ^= 0x01;
        using HttpClient client = CreateClient(ReadFixture("windows-3.14.6.json.sigstore"));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                manifest,
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongCertificateIdentity()
    {
        using HttpClient client = CreateClient(ReadFixture("windows-3.14.6.json.sigstore"));
        PythonReleaseSigningPolicy policy = new(
            "attacker@example.test",
            "https://github.com/login/oauth",
            PythonReleaseSigningPolicy.PythonOrgPolicySourceUri);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                policy));

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongOidcIssuer()
    {
        using HttpClient client = CreateClient(ReadFixture("windows-3.14.6.json.sigstore"));
        PythonReleaseSigningPolicy policy = new(
            "hugo@python.org",
            "https://issuer.example.test",
            PythonReleaseSigningPolicy.PythonOrgPolicySourceUri);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                policy));

        Assert.Contains("OIDC issuer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnknownBundleMediaType()
    {
        byte[] bundle = MutateBundle(root =>
            root["mediaType"] = "application/vnd.dev.sigstore.bundle.v9.9+json");
        using HttpClient client = CreateClient(bundle);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.Contains("bundle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsInvalidTransparencyInclusionProof()
    {
        byte[] bundle = MutateBundle(root =>
        {
            JsonObject proof = GetProof(root);
            proof["rootHash"] = Convert.ToBase64String(new byte[32]);
        });
        using HttpClient client = CreateClient(bundle);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task VerifyAsync_RejectsInvalidSignedEntryTimestamp()
    {
        byte[] bundle = MutateBundle(root =>
        {
            JsonObject entry = GetEntry(root);
            JsonObject promise = entry["inclusionPromise"]!.AsObject();
            promise["signedEntryTimestamp"] = Convert.ToBase64String(new byte[64]);
        });
        using HttpClient client = CreateClient(bundle);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperedRekorCanonicalizedBody()
    {
        byte[] bundle = MutateBundle(root =>
        {
            JsonObject entry = GetEntry(root);
            byte[] bodyBytes = Convert.FromBase64String(entry["canonicalizedBody"]!.GetValue<string>());
            JsonObject body = JsonNode.Parse(bodyBytes)!.AsObject();
            body["spec"]!["data"]!["hash"]!["value"] = new string('0', 64);
            entry["canonicalizedBody"] = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(body.ToJsonString()));
        });
        using HttpClient client = CreateClient(bundle);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.Contains("Rekor body", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperedCheckpointSignature()
    {
        byte[] bundle = MutateBundle(root =>
        {
            JsonObject checkpoint = GetProof(root)["checkpoint"]!.AsObject();
            string envelope = checkpoint["envelope"]!.GetValue<string>();
            int signatureIndex = envelope.LastIndexOf('A');
            Assert.True(signatureIndex >= 0);
            checkpoint["envelope"] = string.Concat(
                envelope.AsSpan(0, signatureIndex),
                "B",
                envelope.AsSpan(signatureIndex + 1));
        });
        using HttpClient client = CreateClient(bundle);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task VerifyAsync_RejectsIntegratedTimeOutsideCertificateValidity()
    {
        byte[] bundle = MutateBundle(root =>
        {
            GetEntry(root)["integratedTime"] = "1781100700";
        });
        using HttpClient client = CreateClient(bundle);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                BundleUri,
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.Contains("validity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsBundleUriThatDoesNotMatchManifest()
    {
        using HttpClient client = CreateClient(ReadFixture("windows-3.14.6.json.sigstore"));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PythonReleaseSignatureVerifier(client).VerifyAsync(
                ReadFixture("windows-3.14.6.json"),
                ManifestUri,
                new Uri("https://www.python.org/ftp/python/3.14.6/other.sigstore"),
                PythonReleaseSigningPolicy.ForVersion(Runtimes.RuntimeVersion.Parse("3.14.6"))));

        Assert.Contains("matching", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient(byte[] bundle, Action? onRequest = null) =>
        new(new StubHttpMessageHandler(request =>
        {
            onRequest?.Invoke();
            Assert.Equal(BundleUri, request.RequestUri);
            return StubHttpMessageHandler.Bytes(bundle);
        }));

    private static byte[] MutateBundle(Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(ReadFixture("windows-3.14.6.json.sigstore"))!.AsObject();
        mutation(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        }));
    }

    private static JsonObject GetEntry(JsonObject root) =>
        root["verificationMaterial"]!["tlogEntries"]![0]!.AsObject();

    private static JsonObject GetProof(JsonObject root) =>
        GetEntry(root)["inclusionProof"]!.AsObject();

    private static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Python",
            fileName));
}
