using System.IO.Compression;
using System.Text.Json;
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
    private const string InstallReceiptFileName = ".autoenvplus-install.json";
    private const int MaximumInstallReceiptBytes = 32 * 1024;
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

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
            return await ValidateExistingInstallationAsync(
                plan,
                paths,
                cancellationToken).ConfigureAwait(false);
        }

        CreateRegularDirectoryPath(paths.ManagedRoot, "managed root");
        string stagingContainer = Path.Combine(paths.ManagedRoot, ".staging");
        EnsureChildPath(paths.ManagedRoot, stagingContainer, "staging root");
        CreateRegularDirectoryPath(stagingContainer, "staging root");
        _ = new StagingDirectoryReclaimer().Reclaim(
            paths.ManagedRoot,
            TimeSpan.FromHours(24));
        string stagingRoot = Path.Combine(stagingContainer, Guid.NewGuid().ToString("N"));
        EnsureChildPath(paths.ManagedRoot, stagingRoot, "staging directory");
        CreateRegularDirectoryPath(stagingRoot, "operation staging directory");

        string downloadDirectory = Path.Combine(
            paths.ManagedRoot,
            "downloads",
            plan.Asset.HashAlgorithm.DisplayName().ToLowerInvariant().Replace("-", string.Empty),
            plan.Asset.PackageHash.ToLowerInvariant());
        EnsureChildPath(paths.ManagedRoot, downloadDirectory, "download cache directory");
        CreateRegularDirectoryPath(downloadDirectory, "download cache directory");
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
            string actualHash = await plan.Asset.HashAlgorithm.ComputeFileHashAsync(
                packagePath,
                cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(plan.Asset.PackageHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(packagePath);
                throw new InvalidDataException(
                    $"{plan.Asset.HashAlgorithm.DisplayName()} mismatch for '{plan.Asset.FileName}'. "
                    + $"Expected {plan.Asset.PackageHash}, got {actualHash}.");
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

            EnsureRegularFile(payloadExecutable, "archive entry point");
            string expectedExecutableSha256 = await PackageHashAlgorithm.Sha256
                .ComputeFileHashAsync(payloadExecutable, cancellationToken)
                .ConfigureAwait(false);
            InstallReceipt installReceipt = CreateInstallReceipt(
                plan,
                expectedExecutableSha256);
            WriteInstallReceipt(payloadRoot, installReceipt);

            progress?.Report(new InstallProgress("commit"));
            CreateRegularDirectoryPath(
                Path.GetDirectoryName(paths.DestinationRoot)!,
                "runtime destination parent");
            EnsureNoReparsePointInPath(paths.DestinationRoot);
            Directory.Move(payloadRoot, paths.DestinationRoot);
            promoted = true;
            EnsureNoReparsePointInPath(paths.DestinationRoot);

            if (!File.Exists(paths.ExpectedExecutable))
            {
                throw new InvalidDataException("Post-install validation could not find the expected executable.");
            }

            EnsureRegularFile(paths.ExpectedExecutable, "installed entry point");
            if (!InstallReceiptMatches(
                    ReadInstallReceipt(paths.DestinationRoot),
                    installReceipt))
            {
                throw new InvalidDataException(
                    "Post-install validation could not verify the managed installation receipt.");
            }

            string installedExecutableSha256 = await PackageHashAlgorithm.Sha256
                .ComputeFileHashAsync(paths.ExpectedExecutable, cancellationToken)
                .ConfigureAwait(false);
            if (!installedExecutableSha256.Equals(
                    installReceipt.ExpectedExecutableSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Post-install validation detected an entry-point content change.");
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

            return new InstallResult(
                InstallOutcome.Failed,
                null,
                DescribeSafeInstallFailure(exception));
        }
        finally
        {
            TryDeleteDirectory(paths.ManagedRoot, stagingRoot);
        }
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

        if (!plan.Asset.HashAlgorithm.IsValidHash(plan.Asset.PackageHash))
        {
            throw new ArgumentException(
                $"The install asset must contain a valid {plan.Asset.HashAlgorithm.DisplayName()} value.",
                nameof(plan));
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
        EnsureNoReparsePointInPath(managedRoot);
        EnsureNoReparsePointInPath(destinationRoot);
        string expectedExecutable = ResolveRelativePath(
            destinationRoot,
            plan.ExpectedExecutableRelativePath,
            "expected executable");

        return new ValidatedPlan(managedRoot, destinationRoot, expectedExecutable);
    }

    private static async Task<InstallResult> ValidateExistingInstallationAsync(
        ArchiveInstallPlan plan,
        ValidatedPlan paths,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureNoReparsePointInPath(paths.DestinationRoot);
            if (!File.Exists(paths.ExpectedExecutable))
            {
                return new InstallResult(
                    InstallOutcome.Failed,
                    null,
                    "The destination already exists but does not contain the expected executable.");
            }

            EnsureRegularFile(paths.ExpectedExecutable, "installed entry point");
            InstallReceipt actual = ReadInstallReceipt(paths.DestinationRoot);
            if (!PackageHashAlgorithm.Sha256.IsValidHash(actual.ExpectedExecutableSha256))
            {
                return new InstallResult(
                    InstallOutcome.Failed,
                    null,
                    "The existing destination installation receipt has no valid entry-point hash.");
            }

            InstallReceipt expected = CreateInstallReceipt(
                plan,
                actual.ExpectedExecutableSha256);
            if (!InstallReceiptMatches(actual, expected))
            {
                return new InstallResult(
                    InstallOutcome.Failed,
                    null,
                    "The destination belongs to a different or unverified package plan. "
                    + "Uninstall it explicitly before installing this provider asset.");
            }

            string executableSha256 = await PackageHashAlgorithm.Sha256
                .ComputeFileHashAsync(paths.ExpectedExecutable, cancellationToken)
                .ConfigureAwait(false);
            if (!executableSha256.Equals(
                    actual.ExpectedExecutableSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new InstallResult(
                    InstallOutcome.Failed,
                    null,
                    "The existing destination entry point changed after its reviewed installation.");
            }

            return new InstallResult(
                InstallOutcome.AlreadyInstalled,
                paths.DestinationRoot,
                null);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException)
        {
            return new InstallResult(
                InstallOutcome.Failed,
                null,
                $"The existing destination could not be verified: {exception.Message}");
        }
    }

    private static InstallReceipt CreateInstallReceipt(
        ArchiveInstallPlan plan,
        string expectedExecutableSha256) => new(
        1,
        plan.Asset.Release.ProviderId,
        plan.Asset.Release.ProviderVersion,
        plan.Asset.Release.Kind.ToString(),
        plan.Asset.Release.Version.ToString(),
        plan.Asset.Release.Architecture.ToString(),
        plan.Asset.FileName,
        plan.Asset.HashAlgorithm.ToString(),
        plan.Asset.PackageHash.ToLowerInvariant(),
        NormalizeReceiptRelativePath(plan.ExpectedExecutableRelativePath),
        expectedExecutableSha256.ToLowerInvariant());

    private static void WriteInstallReceipt(string payloadRoot, InstallReceipt receipt)
    {
        string receiptPath = Path.Combine(payloadRoot, InstallReceiptFileName);
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
        {
            throw new InvalidDataException(
                $"The archive payload reserves '{InstallReceiptFileName}' for AutoEnvPlus metadata.");
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptJsonOptions);
        using FileStream stream = new(
            receiptPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            8_192,
            FileOptions.WriteThrough);
        stream.Write(json);
        stream.Flush(flushToDisk: true);
    }

    private static InstallReceipt ReadInstallReceipt(string destinationRoot)
    {
        string receiptPath = Path.Combine(destinationRoot, InstallReceiptFileName);
        EnsureNoReparsePointInPath(receiptPath);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(receiptPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw new InvalidDataException(
                "The existing destination has no AutoEnvPlus installation receipt.",
                exception);
        }

        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The AutoEnvPlus installation receipt is not a regular file.");
        }

        FileInfo receiptFile = new(receiptPath);
        if (receiptFile.Length is <= 0 or > MaximumInstallReceiptBytes)
        {
            throw new InvalidDataException(
                "The AutoEnvPlus installation receipt has an invalid size.");
        }

        try
        {
            using FileStream stream = new(
                receiptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8_192,
                FileOptions.SequentialScan);
            return JsonSerializer.Deserialize<InstallReceipt>(stream, ReceiptJsonOptions)
                ?? throw new InvalidDataException(
                    "The AutoEnvPlus installation receipt is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The AutoEnvPlus installation receipt is invalid JSON.",
                exception);
        }
    }

    private static bool InstallReceiptMatches(InstallReceipt actual, InstallReceipt expected) =>
        actual.SchemaVersion == expected.SchemaVersion
        && string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal)
        && string.Equals(actual.ProviderVersion, expected.ProviderVersion, StringComparison.Ordinal)
        && string.Equals(actual.RuntimeKind, expected.RuntimeKind, StringComparison.Ordinal)
        && string.Equals(actual.Version, expected.Version, StringComparison.Ordinal)
        && string.Equals(actual.Architecture, expected.Architecture, StringComparison.Ordinal)
        && string.Equals(actual.FileName, expected.FileName, StringComparison.Ordinal)
        && string.Equals(actual.HashAlgorithm, expected.HashAlgorithm, StringComparison.Ordinal)
        && string.Equals(actual.PackageHash, expected.PackageHash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            actual.ExpectedExecutableRelativePath,
            expected.ExpectedExecutableRelativePath,
            StringComparison.OrdinalIgnoreCase)
        && PackageHashAlgorithm.Sha256.IsValidHash(actual.ExpectedExecutableSha256)
        && string.Equals(
            actual.ExpectedExecutableSha256,
            expected.ExpectedExecutableSha256,
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReceiptRelativePath(string path) =>
        path.Replace('\\', '/');

    private static void EnsureRegularFile(string path, string description)
    {
        EnsureNoReparsePointInPath(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"The {description} must be a regular file.");
        }
    }

    private static void CreateRegularDirectoryPath(string path, string description)
    {
        EnsureNoReparsePointInPath(path);
        DirectoryInfo directory = Directory.CreateDirectory(path);
        EnsureNoReparsePointInPath(directory.FullName);
        if ((directory.Attributes & FileAttributes.Directory) == 0
            || (directory.Attributes & (FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be a regular directory and cannot be a reparse point.");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "A managed installation path cannot traverse a reparse point.");
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException
                or DirectoryNotFoundException)
            {
                // Missing tail components are created only after all existing ancestors pass.
            }

            current = current.Parent;
        }
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

            if (!verification.Algorithm.Equals(
                    asset.HashAlgorithm.DisplayName(),
                    StringComparison.OrdinalIgnoreCase)
                || !asset.HashAlgorithm.IsValidHash(verification.Value))
            {
                throw new ArgumentException(
                    $"Package verification evidence must contain a valid "
                    + $"{asset.HashAlgorithm.DisplayName()} value.",
                    nameof(asset));
            }

            if (string.IsNullOrWhiteSpace(verification.Subject))
            {
                throw new ArgumentException("Package verification evidence must identify its subject.", nameof(asset));
            }

            matchesAssetHash |= verification.Value.Equals(
                asset.PackageHash,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!matchesAssetHash)
        {
            throw new ArgumentException(
                $"Package verification evidence must include the install asset "
                + $"{asset.HashAlgorithm.DisplayName()}.",
                nameof(asset));
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

            if (!IsStrongSignatureHash(signature.HashAlgorithm)
                || string.IsNullOrWhiteSpace(signature.SignedSubject)
                || signature.CreatedAtUtc == default
                || !Enum.IsDefined(signature.SignerTrust))
            {
                throw new ArgumentException("Package signature evidence is invalid.", nameof(asset));
            }

            switch (signature.Kind)
            {
                case PackageSignatureVerificationKind.OpenPgpCleartext:
                case PackageSignatureVerificationKind.OpenPgpDetached:
                    if (signature.PrimaryKeyFingerprint.Length != 40
                        || !signature.PrimaryKeyFingerprint.All(Uri.IsHexDigit)
                        || signature.SigningKeyId.Length != 16
                        || !signature.SigningKeyId.All(Uri.IsHexDigit))
                    {
                        throw new ArgumentException(
                            "OpenPGP package signature evidence is invalid.",
                            nameof(asset));
                    }

                    break;
                case PackageSignatureVerificationKind.SigstoreBundle:
                    ValidateSigstoreSignature(signature, asset);
                    break;
                default:
                    throw new ArgumentException(
                        "The package signature evidence kind is unsupported.",
                        nameof(asset));
            }
        }

        if (asset.AuthenticityRequirement != PackageAuthenticityRequirement.SignedChecksumManifest)
        {
            return;
        }

        bool signedEvidenceCoversAsset = asset.Verifications.Any(verification =>
            verification.Value.Equals(asset.PackageHash, StringComparison.OrdinalIgnoreCase)
            && asset.SignatureVerifications.Any(signature =>
                (signature.SignedContentUri ?? signature.SignatureUri)
                    .Equals(verification.SourceUri)));
        if (!signedEvidenceCoversAsset)
        {
            throw new ArgumentException(
                "The verified signature must cover the checksum manifest that supplies "
                + $"the install asset {asset.HashAlgorithm.DisplayName()}.",
                nameof(asset));
        }
    }

    private static void ValidateSigstoreSignature(
        PackageSignatureVerification signature,
        RuntimePackageAsset asset)
    {
        bool hasValidLogId;
        try
        {
            hasValidLogId = Convert.FromBase64String(signature.TransparencyLogId ?? string.Empty).Length == 32;
        }
        catch (FormatException)
        {
            hasValidLogId = false;
        }

        if (signature.SignedContentUri is not { } signedContentUri
            || !IsAbsoluteHttps(signedContentUri)
            || !signature.SignatureUri.AbsoluteUri.Equals(
                signedContentUri.AbsoluteUri + ".sigstore",
                StringComparison.Ordinal)
            || signature.PrimaryKeyFingerprint.Length != 64
            || !signature.PrimaryKeyFingerprint.All(Uri.IsHexDigit)
            || signature.SigningKeyId.Length != 40
            || !signature.SigningKeyId.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(signature.CertificateIdentity)
            || string.IsNullOrWhiteSpace(signature.CertificateOidcIssuer)
            || signature.TransparencyLogIndex is null or < 0
            || signature.TransparencyLogTreeSize is null or <= 0
            || !hasValidLogId
            || signature.TrustRootSha256 is not { Length: 64 } trustRootSha256
            || !trustRootSha256.All(Uri.IsHexDigit)
            || !IsAbsoluteHttps(signature.IdentityPolicyUri)
            || signature.SignerTrust != PackageSignerTrust.ActiveAtTrustSnapshot
            || !signature.SignedSubject.Equals(
                Path.GetFileName(signedContentUri.LocalPath),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Sigstore package signature evidence is invalid.", nameof(asset));
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

    private static string DescribeSafeInstallFailure(Exception exception) => exception switch
    {
        HttpRequestException httpException when httpException.StatusCode is { } statusCode =>
            $"The runtime endpoint returned HTTP {(int)statusCode}.",
        HttpRequestException =>
            "The runtime download transport failed; endpoint query parameters were not retained.",
        _ => exception.Message,
    };

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

    private sealed record InstallReceipt(
        int SchemaVersion,
        string ProviderId,
        string ProviderVersion,
        string RuntimeKind,
        string Version,
        string Architecture,
        string FileName,
        string HashAlgorithm,
        string PackageHash,
        string ExpectedExecutableRelativePath,
        string ExpectedExecutableSha256);
}
