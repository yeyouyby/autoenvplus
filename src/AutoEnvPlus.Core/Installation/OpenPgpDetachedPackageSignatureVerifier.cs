using System.Collections.Concurrent;
using System.Globalization;
using AutoEnvPlus.Core.Providers;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace AutoEnvPlus.Core.Installation;

public interface IDetachedPackageSignatureVerifier
{
    Task<PackageSignatureVerification> VerifyAsync(
        string packagePath,
        PackageSignatureRequirement requirement,
        CancellationToken cancellationToken = default);
}

public sealed class OpenPgpDetachedPackageSignatureVerifier : IDetachedPackageSignatureVerifier
{
    private const int MaximumSignatureBytes = 65_536;
    private const int MaximumPublicKeyBytes = 262_144;
    private const int StreamBufferBytes = 81_920;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new(StringComparer.Ordinal);

    public OpenPgpDetachedPackageSignatureVerifier(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<PackageSignatureVerification> VerifyAsync(
        string packagePath,
        PackageSignatureRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(requirement);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("The package to verify does not exist.", packagePath);
        }

        ValidateRequirement(requirement);
        byte[] signatureBytes = await DownloadLimitedAsync(
            requirement.SignatureUri,
            MaximumSignatureBytes,
            cancellationToken).ConfigureAwait(false);
        PgpSignature signature = ParseDetachedSignature(signatureBytes);
        if (signature.SignatureType != PgpSignature.BinaryDocument)
        {
            throw new InvalidDataException("The package signature is not an OpenPGP binary-document signature.");
        }

        string hashAlgorithm = GetHashAlgorithmName(signature.HashAlgorithm);
        byte[] publicKeyBytes = await GetPublicKeyAsync(
            requirement,
            cancellationToken).ConfigureAwait(false);
        (PgpPublicKey primaryKey, PgpPublicKey signingKey) = ParseTrustedKey(
            publicKeyBytes,
            signature.KeyId,
            requirement.ExpectedPrimaryKeyFingerprint);
        if (primaryKey.IsRevoked() || signingKey.IsRevoked())
        {
            throw new InvalidDataException("The package signature uses a revoked OpenPGP key.");
        }

        DateTimeOffset signatureTime = AsUtc(signature.CreationTime);
        ValidateSignatureTime(signatureTime, signingKey);

        try
        {
            signature.InitVerify(signingKey);
            await using FileStream package = new(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[StreamBufferBytes];
            int read;
            while ((read = await package.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                signature.Update(buffer, 0, read);
            }

            if (!signature.Verify())
            {
                throw new InvalidDataException("The detached OpenPGP package signature is invalid.");
            }
        }
        catch (PgpException exception)
        {
            throw new InvalidDataException(
                "The detached OpenPGP package signature could not be verified.",
                exception);
        }

        return new PackageSignatureVerification(
            PackageSignatureVerificationKind.OpenPgpDetached,
            requirement.SignatureUri,
            requirement.KeySourceUri,
            requirement.SignedSubject,
            hashAlgorithm,
            requirement.ExpectedPrimaryKeyFingerprint,
            FormatKeyId(signature.KeyId),
            signatureTime,
            requirement.SignerTrust);
    }

    private async Task<byte[]> GetPublicKeyAsync(
        PackageSignatureRequirement requirement,
        CancellationToken cancellationToken)
    {
        string cacheKey = $"{requirement.ExpectedPrimaryKeyFingerprint}|{requirement.KeySourceUri}";
        if (_keyCache.TryGetValue(cacheKey, out byte[]? cached))
        {
            return cached;
        }

        byte[] downloaded = await DownloadLimitedAsync(
            requirement.KeySourceUri,
            MaximumPublicKeyBytes,
            cancellationToken).ConfigureAwait(false);
        _keyCache.TryAdd(cacheKey, downloaded);
        return downloaded;
    }

    private async Task<byte[]> DownloadLimitedAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"The OpenPGP response exceeds the {maximumBytes}-byte limit.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using MemoryStream target = new();
        byte[] buffer = new byte[16_384];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (target.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The OpenPGP response exceeds the {maximumBytes}-byte limit.");
            }

            target.Write(buffer, 0, read);
        }

        return target.ToArray();
    }

    private static PgpSignature ParseDetachedSignature(byte[] bytes)
    {
        try
        {
            using MemoryStream input = new(bytes, writable: false);
            using Stream decoded = PgpUtilities.GetDecoderStream(input);
            PgpObjectFactory factory = new(decoded);
            if (factory.NextPgpObject() is not PgpSignatureList signatures
                || signatures.Count != 1
                || factory.NextPgpObject() is not null)
            {
                throw new InvalidDataException(
                    "The detached package signature must contain exactly one OpenPGP signature.");
            }

            return signatures[0];
        }
        catch (Exception exception) when (exception is IOException or PgpException)
        {
            throw new InvalidDataException("The detached OpenPGP signature could not be parsed.", exception);
        }
    }

    private static (PgpPublicKey PrimaryKey, PgpPublicKey SigningKey) ParseTrustedKey(
        byte[] bytes,
        long signingKeyId,
        string expectedPrimaryFingerprint)
    {
        try
        {
            using MemoryStream input = new(bytes, writable: false);
            using Stream decoded = PgpUtilities.GetDecoderStream(input);
            PgpPublicKeyRingBundle bundle = new(decoded);
            PgpPublicKeyRing? ring = bundle.GetPublicKeyRing(signingKeyId);
            PgpPublicKey? primaryKey = ring?.GetPublicKey();
            PgpPublicKey? signingKey = ring?.GetPublicKey(signingKeyId);
            if (primaryKey is null || signingKey is null)
            {
                throw new InvalidDataException(
                    $"The pinned OpenPGP key does not contain signing key {FormatKeyId(signingKeyId)}.");
            }

            string actualFingerprint = Convert.ToHexString(primaryKey.GetFingerprint());
            if (!actualFingerprint.Equals(expectedPrimaryFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The OpenPGP primary key fingerprint does not match the pinned value {expectedPrimaryFingerprint}.");
            }

            return (primaryKey, signingKey);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or PgpException)
        {
            throw new InvalidDataException("The pinned OpenPGP public key could not be parsed.", exception);
        }
    }

    private static void ValidateRequirement(PackageSignatureRequirement requirement)
    {
        if (requirement.Kind != PackageSignatureVerificationKind.OpenPgpDetached
            || !IsAbsoluteHttps(requirement.SignatureUri)
            || !IsAbsoluteHttps(requirement.KeySourceUri)
            || string.IsNullOrWhiteSpace(requirement.SignedSubject)
            || requirement.ExpectedPrimaryKeyFingerprint.Length != 40
            || !requirement.ExpectedPrimaryKeyFingerprint.All(Uri.IsHexDigit)
            || !Enum.IsDefined(requirement.SignerTrust))
        {
            throw new ArgumentException("The detached package signature requirement is invalid.", nameof(requirement));
        }
    }

    private static void ValidateSignatureTime(
        DateTimeOffset signatureTime,
        PgpPublicKey signingKey)
    {
        DateTimeOffset keyCreated = AsUtc(signingKey.CreationTime);
        if (signatureTime < keyCreated.AddMinutes(-5))
        {
            throw new InvalidDataException("The package signature predates its signing key.");
        }

        long validSeconds = signingKey.GetValidSeconds();
        if (validSeconds > 0 && signatureTime > keyCreated.AddSeconds(validSeconds).AddMinutes(5))
        {
            throw new InvalidDataException("The package signature was created after its signing key expired.");
        }

        if (signatureTime > DateTimeOffset.UtcNow.AddMinutes(10))
        {
            throw new InvalidDataException("The package signature creation time is in the future.");
        }
    }

    private static string GetHashAlgorithmName(HashAlgorithmTag algorithm) => algorithm switch
    {
        HashAlgorithmTag.Sha256 => "SHA-256",
        HashAlgorithmTag.Sha384 => "SHA-384",
        HashAlgorithmTag.Sha512 => "SHA-512",
        _ => throw new InvalidDataException(
            $"The detached package signature uses unsupported hash algorithm '{algorithm}'."),
    };

    private static DateTimeOffset AsUtc(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc);
    }

    private static string FormatKeyId(long keyId) => unchecked((ulong)keyId)
        .ToString("X16", CultureInfo.InvariantCulture);

    private static bool IsAbsoluteHttps(Uri? value) =>
        value is { IsAbsoluteUri: true }
        && value.Scheme == Uri.UriSchemeHttps;
}
