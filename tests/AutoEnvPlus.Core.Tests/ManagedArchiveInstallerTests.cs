using System.IO.Compression;
using System.Security.Cryptography;
using AutoEnvPlus.Core.Installation;
using AutoEnvPlus.Core.Providers;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedArchiveInstallerTests : IDisposable
{
    private const string ArchiveRoot = "node-v22.17.0-win-x64";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Install-{Guid.NewGuid():N}");

    public ManagedArchiveInstallerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InstallAsync_VerifiesExtractsAndAtomicallyPromotesPayload()
    {
        byte[] archive = CreateZip(
            ($"{ArchiveRoot}/node.exe", "node-binary"),
            ($"{ArchiveRoot}/npm.cmd", "npm-command"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal("node-binary", File.ReadAllText(Path.Combine(plan.DestinationRoot, "node.exe")));
        Assert.Equal("npm-command", File.ReadAllText(Path.Combine(plan.DestinationRoot, "npm.cmd")));
        AssertStagingIsEmpty();

        InstallResult secondResult = await new ManagedArchiveInstaller(client).InstallAsync(plan);
        Assert.Equal(InstallOutcome.AlreadyInstalled, secondResult.Outcome);
    }

    [Fact]
    public async Task InstallAsync_ExistingDestinationMustMatchManagedReceiptAndPackageHash()
    {
        byte[] originalArchive = CreateZip(($"{ArchiveRoot}/node.exe", "original-node"));
        ArchiveInstallPlan originalPlan = CreatePlan(originalArchive);
        using HttpClient originalClient = new(new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Bytes(originalArchive)));
        InstallResult installed = await new ManagedArchiveInstaller(originalClient)
            .InstallAsync(originalPlan);
        Assert.Equal(InstallOutcome.Installed, installed.Outcome);

        byte[] replacementArchive = CreateZip(($"{ArchiveRoot}/node.exe", "replacement"));
        ArchiveInstallPlan replacementPlan = CreatePlan(replacementArchive);
        int requests = 0;
        using HttpClient replacementClient = new(new StubHttpMessageHandler(_ =>
        {
            requests++;
            return StubHttpMessageHandler.Bytes(replacementArchive);
        }));

        InstallResult replacement = await new ManagedArchiveInstaller(replacementClient)
            .InstallAsync(replacementPlan);

        Assert.Equal(InstallOutcome.Failed, replacement.Outcome);
        Assert.Contains("different or unverified", replacement.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requests);
        Assert.Equal(
            "original-node",
            File.ReadAllText(Path.Combine(originalPlan.DestinationRoot, "node.exe")));
        InstallResult originalAgain = await new ManagedArchiveInstaller(originalClient)
            .InstallAsync(originalPlan);
        Assert.Equal(InstallOutcome.AlreadyInstalled, originalAgain.Outcome);
    }

    [Fact]
    public async Task InstallAsync_ExistingExecutableWithoutReceiptIsNotTrustedAsInstalled()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "reviewed-node"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        Directory.CreateDirectory(plan.DestinationRoot);
        await File.WriteAllTextAsync(
            Path.Combine(plan.DestinationRoot, "node.exe"),
            "unreceipted-node");
        int requests = 0;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requests++;
            return StubHttpMessageHandler.Bytes(archive);
        }));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("receipt", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requests);
        Assert.Equal(
            "unreceipted-node",
            File.ReadAllText(Path.Combine(plan.DestinationRoot, "node.exe")));
    }

    [Fact]
    public async Task InstallAsync_ReceiptDoesNotHideEntryPointContentTampering()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "reviewed-node"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Bytes(archive)));
        InstallResult installed = await new ManagedArchiveInstaller(client).InstallAsync(plan);
        Assert.Equal(InstallOutcome.Installed, installed.Outcome);
        await File.WriteAllTextAsync(
            Path.Combine(plan.DestinationRoot, "node.exe"),
            "tampered-node");

        InstallResult repeated = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, repeated.Outcome);
        Assert.Contains("changed", repeated.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_VerifiesSha512AndPartitionsDownloadCacheByAlgorithm()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive, PackageHashAlgorithm.Sha512);
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(plan.DestinationRoot, "node.exe")));
        Assert.True(File.Exists(Path.Combine(
            _root,
            "downloads",
            "sha512",
            plan.Asset.PackageHash,
            plan.Asset.FileName)));
        AssertStagingIsEmpty();
    }

    [Fact]
    public async Task InstallAsync_Sha512MismatchDeletesPackageAndLeavesNoInstallation()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan validPlan = CreatePlan(archive, PackageHashAlgorithm.Sha512);
        RuntimePackageAsset invalidAsset = validPlan.Asset with
        {
            PackageHash = new string('0', 128),
        };
        invalidAsset = invalidAsset with
        {
            Verifications =
            [
                CreateVerification(
                    invalidAsset.FileName,
                    invalidAsset.PackageHash,
                    PackageHashAlgorithm.Sha512),
            ],
        };
        ArchiveInstallPlan plan = validPlan with { Asset = invalidAsset };
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("SHA-512 mismatch", result.Error);
        Assert.False(Directory.Exists(plan.DestinationRoot));
        Assert.False(File.Exists(Path.Combine(
            _root,
            "downloads",
            "sha512",
            invalidAsset.PackageHash,
            invalidAsset.FileName)));
        AssertStagingIsEmpty();
    }

    [Fact]
    public async Task InstallAsync_ChecksumMismatchLeavesNoInstallation()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan validPlan = CreatePlan(archive);
        RuntimePackageAsset invalidAsset = validPlan.Asset with { PackageHash = new string('0', 64) };
        invalidAsset = invalidAsset with
        {
            Verifications =
            [
                CreateVerification(invalidAsset.FileName, invalidAsset.PackageHash),
            ],
        };
        ArchiveInstallPlan plan = validPlan with { Asset = invalidAsset };
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("SHA-256 mismatch", result.Error);
        Assert.False(Directory.Exists(plan.DestinationRoot));
        AssertStagingIsEmpty();
    }

    [Fact]
    public async Task InstallAsync_DoesNotExposeSignedUrlFromTransportException()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
            throw new HttpRequestException(
                "Failed https://example.test/node.zip?token=do-not-log")));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("do-not-log", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("?token", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transport", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_RejectsArchiveTraversalWithoutWritingOutsideExtractionRoot()
    {
        byte[] archive = CreateZip(
            ("../../../../escape.txt", "should-not-escape"),
            ($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("escapes the extraction root", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(Directory.Exists(plan.DestinationRoot));
        AssertStagingIsEmpty();
    }

    [Fact]
    public async Task InstallAsync_RejectsDestinationOutsideManagedRootBeforeDownloading()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive) with
        {
            DestinationRoot = Path.Combine(Path.GetDirectoryName(_root)!, "outside", Guid.NewGuid().ToString("N")),
        };
        bool requested = false;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requested = true;
            return StubHttpMessageHandler.Bytes(archive);
        }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ManagedArchiveInstaller(client).InstallAsync(plan));
        Assert.False(requested);
    }

    [Fact]
    public async Task InstallAsync_RejectsUnrelatedVerificationEvidenceBeforeDownloading()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        plan = plan with
        {
            Asset = plan.Asset with
            {
                Verifications =
                [
                    CreateVerification("node.zip", new string('f', 64)),
                ],
            },
        };
        bool requested = false;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requested = true;
            return StubHttpMessageHandler.Bytes(archive);
        }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ManagedArchiveInstaller(client).InstallAsync(plan));
        Assert.False(requested);
    }

    [Fact]
    public async Task InstallAsync_RejectsMissingVerificationEvidenceBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            Verifications = [],
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsNonHttpsVerificationSourceBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            Verifications =
            [
                CreateVerification(asset.FileName, asset.PackageHash) with
                {
                    SourceUri = new Uri("http://example.test/SHASUMS256.txt"),
                },
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsRelativeVerificationSourceBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            Verifications =
            [
                CreateVerification(asset.FileName, asset.PackageHash) with
                {
                    SourceUri = new Uri("SHASUMS256.txt", UriKind.Relative),
                },
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsUnsupportedVerificationAlgorithmBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            Verifications =
            [
                CreateVerification(asset.FileName, asset.PackageHash) with
                {
                    Algorithm = "SHA-1",
                },
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsInvalidVerificationHashBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            Verifications =
            [
                CreateVerification(asset.FileName, asset.PackageHash) with
                {
                    Value = "not-a-sha256",
                },
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsBlankVerificationSubjectBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            Verifications =
            [
                CreateVerification(asset.FileName, asset.PackageHash) with
                {
                    Subject = " ",
                },
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsMissingRequiredSignatureBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            AuthenticityRequirement = PackageAuthenticityRequirement.SignedChecksumManifest,
        });
    }

    [Fact]
    public async Task InstallAsync_RejectsSignatureThatDoesNotCoverAssetChecksumSource()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            AuthenticityRequirement = PackageAuthenticityRequirement.SignedChecksumManifest,
            SignatureVerifications =
            [
                CreateSignature(new Uri("https://example.test/unrelated.asc")),
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_AcceptsRequiredSignatureCoveringAssetChecksumSource()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        plan = plan with
        {
            Asset = plan.Asset with
            {
                AuthenticityRequirement = PackageAuthenticityRequirement.SignedChecksumManifest,
                SignatureVerifications =
                [
                    CreateSignature(new Uri("https://example.test/SHASUMS256.txt")),
                ],
            },
        };
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
    }

    [Fact]
    public async Task InstallAsync_AcceptsSigstoreBundleCoveringAssetChecksumSource()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = CreatePlan(archive);
        plan = plan with
        {
            Asset = plan.Asset with
            {
                AuthenticityRequirement = PackageAuthenticityRequirement.SignedChecksumManifest,
                SignatureVerifications =
                [
                    CreateSigstoreSignature(new Uri("https://example.test/SHASUMS256.txt")),
                ],
            },
        };
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
    }

    [Fact]
    public async Task InstallAsync_RejectsIncompleteSigstoreEvidenceBeforeDownloading()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            AuthenticityRequirement = PackageAuthenticityRequirement.SignedChecksumManifest,
            SignatureVerifications =
            [
                CreateSigstoreSignature(new Uri("https://example.test/SHASUMS256.txt")) with
                {
                    CertificateIdentity = null,
                },
            ],
        });
    }

    [Fact]
    public async Task InstallAsync_VerifiesRequiredDetachedSignatureBeforeExtraction()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = AddDetachedSignatureRequirement(CreatePlan(archive));
        bool verified = false;
        FakeDetachedSignatureVerifier verifier = new((packagePath, requirement) =>
        {
            verified = File.ReadAllBytes(packagePath).SequenceEqual(archive);
            return CreateDetachedSignature(requirement);
        });
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client, verifier).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.True(verified);
        Assert.Equal(1, verifier.Calls);
        Assert.True(File.Exists(Path.Combine(plan.DestinationRoot, "node.exe")));
    }

    [Fact]
    public async Task InstallAsync_InvalidDetachedSignatureDeletesPackageAndDoesNotExtract()
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan plan = AddDetachedSignatureRequirement(CreatePlan(archive));
        FakeDetachedSignatureVerifier verifier = new((_, _) =>
            throw new InvalidDataException("detached signature is invalid"));
        using HttpClient client = new(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Bytes(archive)));

        InstallResult result = await new ManagedArchiveInstaller(client, verifier).InstallAsync(plan);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("signature is invalid", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(plan.DestinationRoot));
        Assert.False(File.Exists(Path.Combine(
            _root,
            "downloads",
            "sha256",
            plan.Asset.PackageHash,
            plan.Asset.FileName)));
        AssertStagingIsEmpty();
    }

    [Fact]
    public async Task InstallAsync_RejectsDetachedSignatureRequirementWithoutMetadata()
    {
        await AssertVerificationRejectedBeforeDownloadingAsync(asset => asset with
        {
            AuthenticityRequirement = PackageAuthenticityRequirement.DetachedPackageSignature,
        });
    }

    private ArchiveInstallPlan CreatePlan(
        byte[] archive,
        PackageHashAlgorithm hashAlgorithm = PackageHashAlgorithm.Sha256)
    {
        RuntimeRelease release = new(
            "nodejs-official",
            "v22.17.0",
            RuntimeKind.NodeJs,
            RuntimeVersion.Parse("22.17.0"),
            RuntimeArchitecture.X64,
            "Node.js",
            new DateOnly(2025, 6, 24),
            ["lts"],
            false);
        string hash = Convert.ToHexString(hashAlgorithm switch
        {
            PackageHashAlgorithm.Sha256 => SHA256.HashData(archive),
            PackageHashAlgorithm.Sha512 => SHA512.HashData(archive),
            _ => throw new ArgumentOutOfRangeException(nameof(hashAlgorithm)),
        }).ToLowerInvariant();
        RuntimePackageAsset asset = new(
            release,
            new Uri("https://example.test/node.zip"),
            "node.zip",
            hash,
            RuntimePackageFormat.Zip,
            ArchiveRoot,
            [
                CreateVerification("node.zip", hash, hashAlgorithm),
            ],
            [],
            PackageAuthenticityRequirement.ChecksumEvidence,
            HashAlgorithm: hashAlgorithm);
        string destination = Path.Combine(_root, "runtimes", "node", "22.17.0", "x64");
        return new ArchiveInstallPlan(asset, _root, destination, "node.exe");
    }

    private static PackageVerification CreateVerification(
        string subject,
        string value,
        PackageHashAlgorithm hashAlgorithm = PackageHashAlgorithm.Sha256) =>
        new(
            PackageVerificationKind.ProviderChecksum,
            new Uri("https://example.test/SHASUMS256.txt"),
            subject,
            hashAlgorithm.DisplayName(),
            value);

    private static PackageSignatureVerification CreateSignature(Uri signatureUri) =>
        new(
            PackageSignatureVerificationKind.OpenPgpCleartext,
            signatureUri,
            new Uri("https://keys.example.test/node-release.asc"),
            "SHASUMS256.txt",
            "SHA-256",
            "C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C",
            "C43CEC45C17AB93C",
            new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero),
            PackageSignerTrust.ActiveAtTrustSnapshot);

    private static PackageSignatureVerification CreateSigstoreSignature(Uri signedContentUri) =>
        new(
            PackageSignatureVerificationKind.SigstoreBundle,
            new Uri(signedContentUri.AbsoluteUri + ".sigstore"),
            new Uri("https://raw.githubusercontent.com/sigstore/root-signing/commit/trusted_root.json"),
            Path.GetFileName(signedContentUri.LocalPath),
            "SHA-256",
            new string('a', 64),
            new string('b', 40),
            new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero),
            PackageSignerTrust.ActiveAtTrustSnapshot,
            signedContentUri,
            "release@example.test",
            "https://issuer.example.test",
            123,
            456,
            Convert.ToBase64String(new byte[32]),
            new string('c', 64),
            new Uri("https://example.test/signing-policy"));

    private static ArchiveInstallPlan AddDetachedSignatureRequirement(ArchiveInstallPlan plan)
    {
        PackageSignatureRequirement requirement = new(
            PackageSignatureVerificationKind.OpenPgpDetached,
            new Uri("https://example.test/node.zip.sig"),
            new Uri("https://keys.example.test/release.asc"),
            plan.Asset.FileName,
            "3B04D753C9050D9A5D343F39843C48A565F8F04B",
            PackageSignerTrust.ActiveAtTrustSnapshot);
        return plan with
        {
            Asset = plan.Asset with
            {
                AuthenticityRequirement = PackageAuthenticityRequirement.DetachedPackageSignature,
                SignatureRequirement = requirement,
            },
        };
    }

    private static PackageSignatureVerification CreateDetachedSignature(
        PackageSignatureRequirement requirement) => new(
            PackageSignatureVerificationKind.OpenPgpDetached,
            requirement.SignatureUri,
            requirement.KeySourceUri,
            requirement.SignedSubject,
            "SHA-512",
            requirement.ExpectedPrimaryKeyFingerprint,
            "843C48A565F8F04B",
            DateTimeOffset.UtcNow,
            requirement.SignerTrust);

    private async Task AssertVerificationRejectedBeforeDownloadingAsync(
        Func<RuntimePackageAsset, RuntimePackageAsset> mutateAsset)
    {
        byte[] archive = CreateZip(($"{ArchiveRoot}/node.exe", "node-binary"));
        ArchiveInstallPlan validPlan = CreatePlan(archive);
        ArchiveInstallPlan plan = validPlan with
        {
            Asset = mutateAsset(validPlan.Asset),
        };
        bool requested = false;
        using HttpClient client = new(new StubHttpMessageHandler(_ =>
        {
            requested = true;
            return StubHttpMessageHandler.Bytes(archive);
        }));

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ManagedArchiveInstaller(client).InstallAsync(plan));
        Assert.False(requested);
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using StreamWriter writer = new(entry.Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private void AssertStagingIsEmpty()
    {
        string staging = Path.Combine(_root, ".staging");
        if (Directory.Exists(staging))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeDetachedSignatureVerifier(
        Func<string, PackageSignatureRequirement, PackageSignatureVerification> verify)
        : IDetachedPackageSignatureVerifier
    {
        public int Calls { get; private set; }

        public Task<PackageSignatureVerification> VerifyAsync(
            string packagePath,
            PackageSignatureRequirement requirement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(verify(packagePath, requirement));
        }
    }
}
