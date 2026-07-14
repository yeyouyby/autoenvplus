using System.IO.Compression;
using System.Security.Cryptography;
using AutoEnvPlus.Core.Providers;

namespace AutoEnvPlus.Core.Installation;

public interface IArchiveInstaller
{
    Task<InstallResult> InstallAsync(
        ArchiveInstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ManagedArchiveInstaller : IArchiveInstaller
{
    private readonly HttpClient _httpClient;
    private readonly IDetachedPackageSignatureVerifier _signatureVerifier;

    public ManagedArchiveInstaller(
        HttpClient httpClient,
        IDetachedPackageSignatureVerifier? signatureVerifier = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _signatureVerifier = signatureVerifier
            ?? new OpenPgpDetachedPackageSignatureVerifier(_httpClient);
    }

    public async Task<InstallResult> InstallAsync(
        ArchiveInstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatedPlan paths = ValidatePlan(plan);

        if (Directory.Exists(paths.DestinationRoot))
        {
            return File.Exists(paths.ExpectedExecutable)
                ? new InstallResult(InstallOutcome.AlreadyInstalled, paths.DestinationRoot, null)
                : new InstallResult(
                    InstallOutcome.Failed,
                    null,
                    "The destination already exists but does not contain the expected executable.");
        }

        Directory.CreateDirectory(paths.ManagedRoot);
        _ = new StagingDirectoryReclaimer().Reclaim(
            paths.ManagedRoot,
            TimeSpan.FromHours(24));
        string stagingRoot = Path.Combine(paths.ManagedRoot, ".staging", Guid.NewGuid().ToString("N"));
        EnsureChildPath(paths.ManagedRoot, stagingRoot, "staging directory");
        Directory.CreateDirectory(stagingRoot);

        string downloadDirectory = Path.Combine(
            paths.ManagedRoot,
            "downloads",
            plan.Asset.Sha256.ToLowerInvariant());
        EnsureChildPath(paths.ManagedRoot, downloadDirectory, "download cache directory");
        Directory.CreateDirectory(downloadDirectory);
        string packagePath = Path.Combine(downloadDirectory, plan.Asset.FileName);
        string extractionRoot = Path.Combine(stagingRoot, "extracted");
        bool promoted = false;

        try
        {
            progress?.Report(new InstallProgress("download"));
            await new ResumableHttpDownloader(_httpClient).DownloadAsync(
                plan.Asset.DownloadUri,
                packagePath,
                plan.MaximumDownloadBytes,
                value => progress?.Report(new InstallProgress(
                    "download",
                    value.CompletedBytes,
                    value.TotalBytes)),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new InstallProgress("verify"));
            string actualHash = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(plan.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(packagePath);
                throw new InvalidDataException(
                    $"SHA-256 mismatch for '{plan.Asset.FileName}'. Expected {plan.Asset.Sha256}, got {actualHash}.");
            }

            if (plan.Asset.SignatureRequirement is PackageSignatureRequirement signatureRequirement)
            {
                progress?.Report(new InstallProgress("verify-signature"));
                try
                {
                    PackageSignatureVerification verified = await _signatureVerifier.VerifyAsync(
                        packagePath,
                        signatureRequirement,
                        cancellationToken).ConfigureAwait(false);
                    ValidateCompletedDetachedSignature(plan.Asset, verified);
                }
                catch (Exception exception) when (exception is HttpRequestException
                    or IOException
                    or InvalidDataException
                    or NotSupportedException)
                {
                    TryDeleteFile(packagePath);
                    throw;
                }
            }

            progress?.Report(new InstallProgress("extract"));
            Directory.CreateDirectory(extractionRoot);
            ExtractZipSafely(
                packagePath,
                extractionRoot,
                plan.MaximumArchiveEntries,
                plan.MaximumUncompressedBytes,
                cancellationToken);

            string payloadRoot = ResolvePayloadRoot(extractionRoot, plan.Asset.ArchiveRootDirectory);
            string payloadExecutable = ResolveRelativePath(
                payloadRoot,
                plan.ExpectedExecutableRelativePath,
                "expected executable");
            if (!File.Exists(payloadExecutable))
            {
                throw new InvalidDataException(
                    $"The archive does not contain '{plan.ExpectedExecutableRelativePath}' in its payload root.");
            }

            progress?.Report(new InstallProgress("commit"));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DestinationRoot)!);
            Directory.Move(payloadRoot, paths.DestinationRoot);
            promoted = true;

            if (!File.Exists(paths.ExpectedExecutable))
            {
                throw new InvalidDataException("Post-install validation could not find the expected executable.");
            }

            progress?.Report(new InstallProgress("complete"));
            return new InstallResult(InstallOutcome.Installed, paths.DestinationRoot, null);
        }
        catch (OperationCanceledException)
        {
            if (promoted)
            {
                TryDeleteDirectory(paths.ManagedRoot, paths.DestinationRoot);
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedInstallFailure(exception))
        {
            if (promoted)
            {
                TryDeleteDirectory(paths.ManagedRoot, paths.DestinationRoot);
            }

            return new InstallResult(InstallOutcome.Failed, null, exception.Message);
        }
        finally
        {
            TryDeleteDirectory(paths.ManagedRoot, stagingRoot);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractZipSafely(
        string packagePath,
        string extractionRoot,
        int maximumEntries,
        long maximumUncompressedBytes,
        CancellationToken cancellationToken)
    {
        string rootPrefix = EnsureTrailingSeparator(Path.GetFullPath(extractionRoot));
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > maximumEntries)
        {
            throw new InvalidDataException(
                $"The archive has {archive.Entries.Count} entries, exceeding the {maximumEntries}-entry limit.");
        }

        long uncompressedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"The archive contains a symbolic link: '{entry.FullName}'.");
            }

            uncompressedBytes = checked(uncompressedBytes + entry.Length);
            if (uncompressedBytes > maximumUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"The archive exceeds the {maximumUncompressedBytes}-byte uncompressed size limit.");
            }

            string normalizedName = entry.FullName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedName))
            {
                throw new InvalidDataException($"The archive contains a rooted path: '{entry.FullName}'.");
            }

            string destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, normalizedName));
            if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The archive entry escapes the extraction root: '{entry.FullName}'.");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using Stream source = entry.Open();
            using FileStream target = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static string ResolvePayloadRoot(string extractionRoot, string? archiveRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(archiveRootDirectory))
        {
            return Path.GetFullPath(extractionRoot);
        }

        string payloadRoot = ResolveRelativePath(
            extractionRoot,
            archiveRootDirectory,
            "archive payload root");
        if (!Directory.Exists(payloadRoot))
        {
            throw new InvalidDataException(
                $"The archive payload root '{archiveRootDirectory}' does not exist.");
        }

        return payloadRoot;
    }

    private static ValidatedPlan ValidatePlan(ArchiveInstallPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ManagedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.DestinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ExpectedExecutableRelativePath);

        if (plan.Asset.Format != RuntimePackageFormat.Zip)
        {
            throw new NotSupportedException($"Package format '{plan.Asset.Format}' is not supported.");
        }

        if (plan.Asset.Sha256.Length != 64 || !plan.Asset.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The install asset must contain a valid SHA-256 value.", nameof(plan));
        }


        if (!IsAbsoluteHttps(plan.Asset.DownloadUri)
            || string.IsNullOrWhiteSpace(plan.Asset.FileName)
            || !Path.GetFileName(plan.Asset.FileName).Equals(
                plan.Asset.FileName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The install asset must use an HTTPS download and a plain file name.",
                nameof(plan));
        }

        ValidatePackageVerifications(plan.Asset);
        ValidatePackageSignatures(plan.Asset);

        if (plan.MaximumDownloadBytes <= 0
            || plan.MaximumArchiveEntries <= 0
            || plan.MaximumUncompressedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "Archive safety limits must be positive.");
        }

        string managedRoot = Path.GetFullPath(plan.ManagedRoot);
        string destinationRoot = Path.GetFullPath(plan.DestinationRoot);
        EnsureChildPath(managedRoot, destinationRoot, "install destination");
        string expectedExecutable = ResolveRelativePath(
            destinationRoot,
            plan.ExpectedExecutableRelativePath,
            "expected executable");

        return new ValidatedPlan(managedRoot, destinationRoot, expectedExecutable);
    }

    private static void ValidatePackageVerifications(RuntimePackageAsset asset)
    {
        if (asset.Verifications is not { Count: > 0 })
        {
            throw new ArgumentException("The install asset must contain provider checksum evidence.", nameof(asset));
        }

        bool matchesAssetHash = false;
        foreach (PackageVerification verification in asset.Verifications)
        {
            if (verification is null
                || verification.SourceUri is null
                || !verification.SourceUri.IsAbsoluteUri
                || verification.SourceUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Package verification evidence must come from an absolute HTTPS URI.", nameof(asset));
            }

            if (!verification.Algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase)
                || verification.Value.Length != 64
                || !verification.Value.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Package verification evidence must contain a valid SHA-256 value.", nameof(asset));
            }

            if (string.IsNullOrWhiteSpace(verification.Subject))
            {
                throw new ArgumentException("Package verification evidence must identify its subject.", nameof(asset));
            }

            matchesAssetHash |= verification.Value.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        if (!matchesAssetHash)
        {
            throw new ArgumentException("Package verification evidence must include the install asset SHA-256.", nameof(asset));
        }
    }

    private static void ValidatePackageSignatures(RuntimePackageAsset asset)
    {
        if (asset.SignatureVerifications is null)
        {
            throw new ArgumentException("The install asset signature evidence collection cannot be null.", nameof(asset));
        }

        if (asset.AuthenticityRequirement == PackageAuthenticityRequirement.SignedChecksumManifest
            && asset.SignatureVerifications.Count == 0)
        {
            throw new ArgumentException("The install asset requires a verified signed checksum manifest.", nameof(asset));
        }

        if (asset.AuthenticityRequirement == PackageAuthenticityRequirement.DetachedPackageSignature)
        {
            ValidateDetachedSignatureRequirement(asset);
        }
        else if (asset.SignatureRequirement is not null)
        {
            throw new ArgumentException(
                "Only detached-signature assets may contain a pending signature requirement.",
                nameof(asset));
        }

        foreach (PackageSignatureVerification signature in asset.SignatureVerifications)
        {
            if (signature is null
                || !IsAbsoluteHttps(signature.SignatureUri)
                || !IsAbsoluteHttps(signature.KeySourceUri))
            {
                throw new ArgumentException(
                    "Package signature evidence must use absolute HTTPS signature and key URIs.",
                    nameof(asset));
            }

            if (signature.Kind is not (PackageSignatureVerificationKind.OpenPgpCleartext
                    or PackageSignatureVerificationKind.OpenPgpDetached)
                || !IsStrongSignatureHash(signature.HashAlgorithm)
                || signature.PrimaryKeyFingerprint.Length != 40
                || !signature.PrimaryKeyFingerprint.All(Uri.IsHexDigit)
                || signature.SigningKeyId.Length != 16
                || !signature.SigningKeyId.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(signature.SignedSubject)
                || signature.CreatedAtUtc == default
                || !Enum.IsDefined(signature.SignerTrust))
            {
                throw new ArgumentException("Package signature evidence is invalid.", nameof(asset));
            }
        }

        if (asset.AuthenticityRequirement != PackageAuthenticityRequirement.SignedChecksumManifest)
        {
            return;
        }

        bool signedEvidenceCoversAsset = asset.Verifications.Any(verification =>
            verification.Value.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)
            && asset.SignatureVerifications.Any(signature =>
                signature.SignatureUri.Equals(verification.SourceUri)));
        if (!signedEvidenceCoversAsset)
        {
            throw new ArgumentException(
                "The verified signature must cover the checksum manifest that supplies the install asset SHA-256.",
                nameof(asset));
        }
    }

    private static void ValidateDetachedSignatureRequirement(RuntimePackageAsset asset)
    {
        PackageSignatureRequirement requirement = asset.SignatureRequirement
            ?? throw new ArgumentException(
                "The install asset requires a detached package signature.",
                nameof(asset));
        if (requirement.Kind != PackageSignatureVerificationKind.OpenPgpDetached
            || !IsAbsoluteHttps(requirement.SignatureUri)
            || !IsAbsoluteHttps(requirement.KeySourceUri)
            || !requirement.SignedSubject.Equals(asset.FileName, StringComparison.Ordinal)
            || requirement.ExpectedPrimaryKeyFingerprint.Length != 40
            || !requirement.ExpectedPrimaryKeyFingerprint.All(Uri.IsHexDigit)
            || !Enum.IsDefined(requirement.SignerTrust))
        {
            throw new ArgumentException(
                "The install asset detached signature requirement is invalid.",
                nameof(asset));
        }
    }

    private static void ValidateCompletedDetachedSignature(
        RuntimePackageAsset asset,
        PackageSignatureVerification verified)
    {
        PackageSignatureRequirement requirement = asset.SignatureRequirement!;
        if (verified.Kind != PackageSignatureVerificationKind.OpenPgpDetached
            || !verified.SignatureUri.Equals(requirement.SignatureUri)
            || !verified.KeySourceUri.Equals(requirement.KeySourceUri)
            || !verified.SignedSubject.Equals(asset.FileName, StringComparison.Ordinal)
            || !verified.PrimaryKeyFingerprint.Equals(
                requirement.ExpectedPrimaryKeyFingerprint,
                StringComparison.Ordinal)
            || verified.SignerTrust != requirement.SignerTrust
            || !IsStrongSignatureHash(verified.HashAlgorithm)
            || verified.SigningKeyId.Length != 16
            || !verified.SigningKeyId.All(Uri.IsHexDigit)
            || verified.CreatedAtUtc == default)
        {
            throw new InvalidDataException(
                "The completed detached signature verification does not match the install plan.");
        }
    }

    private static bool IsStrongSignatureHash(string value) =>
        value.Equals("SHA-256", StringComparison.OrdinalIgnoreCase)
        || value.Equals("SHA-384", StringComparison.OrdinalIgnoreCase)
        || value.Equals("SHA-512", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsoluteHttps(Uri? value) =>
        value is { IsAbsoluteUri: true }
        && value.Scheme == Uri.UriSchemeHttps;

    private static string ResolveRelativePath(string root, string relativePath, string description)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"The {description} must be relative.", nameof(relativePath));
        }

        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        EnsureChildPath(fullRoot, candidate, description);
        return candidate;
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string rootPrefix = EnsureTrailingSeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} must remain inside the managed root.");
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        int unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }

    private static bool IsExpectedInstallFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or NotSupportedException
            or OverflowException;

    private static void TryDeleteDirectory(string managedRoot, string path)
    {
        try
        {
            EnsureChildPath(managedRoot, path, "cleanup directory");
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort; the next startup can reclaim orphaned staging directories.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ValidatedPlan(
        string ManagedRoot,
        string DestinationRoot,
        string ExpectedExecutable);
}
