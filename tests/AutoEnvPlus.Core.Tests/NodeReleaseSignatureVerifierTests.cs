using AutoEnvPlus.Core.Providers.NodeJs;
using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Tests;

public sealed class NodeReleaseSignatureVerifierTests
{
    private static readonly Uri ManifestUri = new(
        "https://nodejs.org/dist/v26.5.0/SHASUMS256.txt.asc");

    [Fact]
    public async Task GetVerifiedChecksumsAsync_VerifiesOfficialClearSignedManifest()
    {
        List<Uri> requests = [];
        using HttpClient client = CreateFixtureClient(requests);

        VerifiedNodeReleaseChecksums result = await new NodeReleaseSignatureVerifier(client)
            .GetVerifiedChecksumsAsync(ManifestUri, new DateOnly(2026, 7, 8));

        Assert.Contains(
            "d3b2277dbcccfdf24ef6302928f64f484cff1d77a6d3caa3a28f4d20ce9158f6  node-v26.5.0-win-x64.zip",
            result.Content,
            StringComparison.Ordinal);
        Assert.Equal(PackageSignatureVerificationKind.OpenPgpCleartext, result.Verification.Kind);
        Assert.Equal("C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C", result.Verification.PrimaryKeyFingerprint);
        Assert.Equal("C43CEC45C17AB93C", result.Verification.SigningKeyId);
        Assert.Equal(PackageSignerTrust.ActiveAtTrustSnapshot, result.Verification.SignerTrust);
        Assert.Equal("SHA-256", result.Verification.HashAlgorithm);
        Assert.Equal(2, requests.Count);
        Assert.Equal(ManifestUri, requests[0]);
        Assert.Equal(result.Verification.KeySourceUri, requests[1]);
    }

    [Fact]
    public async Task GetVerifiedChecksumsAsync_RejectsTamperedSignedContent()
    {
        string signedManifest = ReadFixture("SHASUMS256-v26.5.0.txt.asc").Replace(
            "d3b2277dbcccfdf24ef6302928f64f484cff1d77a6d3caa3a28f4d20ce9158f6  node-v26.5.0-win-x64.zip",
            "e3b2277dbcccfdf24ef6302928f64f484cff1d77a6d3caa3a28f4d20ce9158f6  node-v26.5.0-win-x64.zip",
            StringComparison.Ordinal);
        using HttpClient client = CreateFixtureClient([], signedManifest);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NodeReleaseSignatureVerifier(client).GetVerifiedChecksumsAsync(
                ManifestUri,
                new DateOnly(2026, 7, 8)));

        Assert.Contains("signature is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetVerifiedChecksumsAsync_RejectsSignatureDateOutsideReleaseWindow()
    {
        using HttpClient client = CreateFixtureClient([]);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NodeReleaseSignatureVerifier(client).GetVerifiedChecksumsAsync(
                ManifestUri,
                new DateOnly(2026, 6, 1)));

        Assert.Contains("does not match release date", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateFixtureClient(
        List<Uri> requests,
        string? signedManifest = null) =>
        new(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri == ManifestUri
                ? StubHttpMessageHandler.Text(
                    signedManifest ?? ReadFixture("SHASUMS256-v26.5.0.txt.asc"),
                    "application/pgp-signature")
                : StubHttpMessageHandler.Text(
                    ReadFixture("C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C.asc"),
                    "application/pgp-keys");
        }));

    private static string ReadFixture(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "NodeJs", fileName));
}
