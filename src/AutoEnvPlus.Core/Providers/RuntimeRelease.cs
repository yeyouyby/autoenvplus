using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Providers;

public sealed record RuntimeRelease(
    string ProviderId,
    string ProviderVersion,
    RuntimeKind Kind,
    RuntimeVersion Version,
    RuntimeArchitecture Architecture,
    string Vendor,
    DateOnly? ReleaseDate,
    IReadOnlyCollection<string> Channels,
    bool IsSecurityRelease);

public enum RuntimePackageFormat
{
    Zip,
}

public sealed record RuntimePackageAsset(
    RuntimeRelease Release,
    Uri DownloadUri,
    string FileName,
    string Sha256,
    RuntimePackageFormat Format,
    string? ArchiveRootDirectory,
    IReadOnlyCollection<PackageVerification> Verifications,
    IReadOnlyCollection<PackageSignatureVerification> SignatureVerifications,
    PackageAuthenticityRequirement AuthenticityRequirement,
    PackageSignatureRequirement? SignatureRequirement = null);

public enum PackageAuthenticityRequirement
{
    ChecksumEvidence,
    SignedChecksumManifest,
    DetachedPackageSignature,
}

public enum PackageVerificationKind
{
    ProviderChecksum,
    VerifiedManifest,
}

public sealed record PackageVerification(
    PackageVerificationKind Kind,
    Uri SourceUri,
    string Subject,
    string Algorithm,
    string Value);

public enum PackageSignatureVerificationKind
{
    OpenPgpCleartext,
    OpenPgpDetached,
}

public enum PackageSignerTrust
{
    ActiveAtTrustSnapshot,
    Historical,
}

public sealed record PackageSignatureVerification(
    PackageSignatureVerificationKind Kind,
    Uri SignatureUri,
    Uri KeySourceUri,
    string SignedSubject,
    string HashAlgorithm,
    string PrimaryKeyFingerprint,
    string SigningKeyId,
    DateTimeOffset CreatedAtUtc,
    PackageSignerTrust SignerTrust);

public sealed record PackageSignatureRequirement(
    PackageSignatureVerificationKind Kind,
    Uri SignatureUri,
    Uri KeySourceUri,
    string SignedSubject,
    string ExpectedPrimaryKeyFingerprint,
    PackageSignerTrust SignerTrust);
