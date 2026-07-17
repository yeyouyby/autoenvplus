using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Downloads;

public enum ManagedTransferPhase
{
    Probing,
    Downloading,
    Copying,
    Verifying,
    Committing,
    Completed,
    Cancelled,
}

public enum DownloadTransferMode
{
    SingleStream,
    Segmented,
}

public enum DownloadFallbackReason
{
    RangeNotSupported,
    StableEntityUnavailable,
    EntityChanged,
}

public enum ManagedDownloadOrigin
{
    Network,
    LocalImport,
}

public enum SegmentedDownloadCancellationBehavior
{
    DeleteStaging,
}

public sealed record PackageHashExpectation(
    PackageHashAlgorithm Algorithm,
    string ExpectedHash);

public sealed record ManagedTransferProgress(
    ManagedTransferPhase Phase,
    long CompletedBytes,
    long? TotalBytes,
    int CompletedSegments,
    int TotalSegments,
    bool IsSegmented);

public sealed record SegmentedDownloadRequest(
    Uri SourceUri,
    string FileName,
    int ConnectionCount,
    long MaximumBytes,
    PackageHashExpectation? Integrity = null,
    bool Overwrite = false);

public sealed record SegmentedDownloadResult(
    string FilePath,
    long TotalBytes,
    DownloadTransferMode TransferMode,
    int SegmentCount,
    DownloadFallbackReason? FallbackReason,
    string ContentSha256,
    PackageHashAlgorithm? ExpectedHashAlgorithm,
    string? ExpectedHash,
    string? VerifiedHash)
{
    public bool WasRangeFallback => FallbackReason is not null;

    public bool HasVerifiedExpectedHash =>
        ExpectedHashAlgorithm is not null
        && ExpectedHash is not null
        && VerifiedHash is not null;
}

public sealed record LocalPackageImportRequest(
    string SourcePath,
    string? FileName,
    long MaximumBytes,
    PackageHashExpectation? Integrity = null,
    bool Overwrite = false);

public sealed record LocalPackageImportResult(
    string FilePath,
    long TotalBytes,
    string ContentSha256,
    PackageHashAlgorithm? ExpectedHashAlgorithm,
    string? ExpectedHash,
    string? VerifiedHash)
{
    public bool HasVerifiedExpectedHash =>
        ExpectedHashAlgorithm is not null
        && ExpectedHash is not null
        && VerifiedHash is not null;
}

public sealed record ManagedDownloadDeleteResult(
    string FileName,
    bool Deleted,
    bool ManifestUpdated);

internal static class PackageHashExpectationValidator
{
    public static void Validate(PackageHashExpectation? expectation, string parameterName)
    {
        if (expectation is null)
        {
            return;
        }

        if (!Enum.IsDefined(expectation.Algorithm)
            || !expectation.Algorithm.IsValidHash(expectation.ExpectedHash))
        {
            throw new ArgumentException(
                $"Expected hash must be a valid {expectation.Algorithm.DisplayName()} value.",
                parameterName);
        }
    }

    public static async Task<TransferIntegrityEvidence> IdentifyAndVerifyAsync(
        string path,
        PackageHashExpectation? expectation,
        CancellationToken cancellationToken)
    {
        string contentSha256 = await PackageHashAlgorithm.Sha256.ComputeFileHashAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        if (expectation is null)
        {
            return new TransferIntegrityEvidence(contentSha256, null, null, null);
        }

        string actualHash = expectation.Algorithm == PackageHashAlgorithm.Sha256
            ? contentSha256
            : await expectation.Algorithm.ComputeFileHashAsync(
                path,
                cancellationToken).ConfigureAwait(false);
        if (!actualHash.Equals(expectation.ExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{expectation.Algorithm.DisplayName()} mismatch for '{Path.GetFileName(path)}'. "
                + $"Expected {expectation.ExpectedHash.ToLowerInvariant()}, but received {actualHash}.");
        }

        return new TransferIntegrityEvidence(
            contentSha256,
            expectation.Algorithm,
            expectation.ExpectedHash.ToLowerInvariant(),
            actualHash);
    }
}

internal sealed record TransferIntegrityEvidence(
    string ContentSha256,
    PackageHashAlgorithm? ExpectedHashAlgorithm,
    string? ExpectedHash,
    string? VerifiedHash);
