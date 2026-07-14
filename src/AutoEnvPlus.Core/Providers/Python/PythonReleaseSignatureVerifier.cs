using System.Formats.Asn1;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AutoEnvPlus.Core.Providers;
using Dev.Sigstore.Bundle.V1;
using Dev.Sigstore.Common.V1;
using Dev.Sigstore.Rekor.V1;
using Dev.Sigstore.Trustroot.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Sigstore.Bundle;
using Sigstore.Crypto;
using Sigstore.Fulcio;
using Sigstore.Rekor;
using Sigstore.Time;
using Sigstore.Tuf;
using Sigstore.Verification;
using BundleModel = Dev.Sigstore.Bundle.V1.Bundle;
using SigstoreHashAlgorithm = Dev.Sigstore.Common.V1.HashAlgorithm;
using SigstoreX509Extensions = Sigstore.Crypto.X509Extensions;

namespace AutoEnvPlus.Core.Providers.Python;

public interface IPythonReleaseSignatureVerifier
{
    Task<PackageSignatureVerification> VerifyAsync(
        ReadOnlyMemory<byte> manifest,
        Uri manifestUri,
        Uri bundleUri,
        PythonReleaseSigningPolicy signingPolicy,
        CancellationToken cancellationToken = default);
}

public sealed class PythonReleaseSignatureVerifier : IPythonReleaseSignatureVerifier
{
    public const string TrustRootSha256 =
        "6494e21ea73fa7ee769f85f57d5a3e6a08725eae1e38c755fc3517c9e6bc0b66";

    public const string TrustRootRepositoryCommit =
        "0287f2e6b92ffaa95621afa7732d30f46344040c";

    public static readonly DateOnly TrustSnapshotDate = new(2026, 7, 14);

    public static readonly Uri TrustRootSourceUri = new(
        $"https://raw.githubusercontent.com/sigstore/root-signing/{TrustRootRepositoryCommit}/targets/trusted_root.json");

    private const string TrustedRootResourceName =
        "AutoEnvPlus.Core.Providers.Python.Trust.sigstore-trusted-root.json";

    private const string RequiredBundleMediaType =
        "application/vnd.dev.sigstore.bundle.v0.3+json";

    private const int MaximumBundleBytes = 1_048_576;
    private const string SubjectAlternativeNameOid = "2.5.29.17";
    private const string SubjectKeyIdentifierOid = "2.5.29.14";
    private const string SignedCertificateTimestampListOid = "1.3.6.1.4.1.11129.2.4.2";
    private const string CodeSigningEnhancedKeyUsageOid = "1.3.6.1.5.5.7.3.3";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Lazy<TrustedRootSnapshot> PinnedTrustedRoot =
        new(LoadTrustedRootSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly HttpClient _httpClient;
    private readonly BundleParser _bundleParser = new();

    public PythonReleaseSignatureVerifier(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<PackageSignatureVerification> VerifyAsync(
        ReadOnlyMemory<byte> manifest,
        Uri manifestUri,
        Uri bundleUri,
        PythonReleaseSigningPolicy signingPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(bundleUri);
        ArgumentNullException.ThrowIfNull(signingPolicy);
        ValidatePythonOrgUris(manifestUri, bundleUri);
        if (manifest.IsEmpty)
        {
            throw new InvalidDataException("The Python Windows release manifest is empty.");
        }

        byte[] bundleBytes = await DownloadLimitedAsync(
            bundleUri,
            MaximumBundleBytes,
            cancellationToken).ConfigureAwait(false);
        string bundleJson;
        try
        {
            bundleJson = StrictUtf8.GetString(bundleBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Python Sigstore bundle is not valid UTF-8.", exception);
        }

        ParsedBundle parsed = ParseAndValidateBundle(
            bundleJson,
            manifest.Span,
            signingPolicy);
        TrustedRootSnapshot trust = PinnedTrustedRoot.Value;
        ValidateTransparencyTrust(parsed, trust.Root);

        Verifier verifier = CreateVerifier();
        VerificationResult result;
        try
        {
            VerificationPolicy pipelinePolicy = VerificationPolicy.ForExact(
                signingPolicy.OidcIssuer,
                parsed.PipelineIdentity);
            result = await verifier.VerifyAsync(
                bundleJson,
                manifest,
                pipelinePolicy,
                trust.Json,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            parsed.LeafCertificate.Dispose();
            throw new InvalidDataException(
                "The Python Windows release manifest Sigstore bundle could not be verified.",
                exception);
        }

        try
        {
            ValidateVerificationResult(result, parsed, trust.Root);
            string certificateFingerprint = Convert.ToHexString(
                SHA256.HashData(parsed.LeafCertificate.RawData));
            string subjectKeyIdentifier = GetSubjectKeyIdentifier(parsed.LeafCertificate);
            TransparencyLogEntry entry = result.TransparencyLogEntry;
            return new PackageSignatureVerification(
                PackageSignatureVerificationKind.SigstoreBundle,
                bundleUri,
                TrustRootSourceUri,
                Path.GetFileName(manifestUri.LocalPath),
                "SHA-256",
                certificateFingerprint,
                subjectKeyIdentifier,
                parsed.IntegratedTime,
                PackageSignerTrust.ActiveAtTrustSnapshot,
                manifestUri,
                signingPolicy.CertificateIdentity,
                signingPolicy.OidcIssuer,
                entry.LogIndex,
                entry.InclusionProof?.TreeSize,
                Convert.ToBase64String(entry.LogId.KeyId.ToByteArray()),
                TrustRootSha256,
                signingPolicy.PolicySourceUri);
        }
        finally
        {
            parsed.LeafCertificate.Dispose();
            foreach (X509Certificate2 certificate in result.CertificateChain)
            {
                certificate.Dispose();
            }
        }
    }

    private ParsedBundle ParseAndValidateBundle(
        string bundleJson,
        ReadOnlySpan<byte> manifest,
        PythonReleaseSigningPolicy signingPolicy)
    {
        SigstoreBundle bundle;
        try
        {
            bundle = _bundleParser.Parse(bundleJson);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("The Python Sigstore bundle is invalid.", exception);
        }

        if (!bundle.MediaType.Equals(RequiredBundleMediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The Python Sigstore bundle must use '{RequiredBundleMediaType}'.");
        }

        BundleModel model = bundle.Model;
        if (model.ContentCase != BundleModel.ContentOneofCase.MessageSignature
            || model.MessageSignature.MessageDigest is null
            || model.MessageSignature.MessageDigest.Algorithm != SigstoreHashAlgorithm.Sha2256
            || model.MessageSignature.MessageDigest.Digest.Length != 32
            || model.MessageSignature.Signature.Length == 0)
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle must contain one SHA-256 message signature.");
        }

        byte[] manifestHash = SHA256.HashData(manifest);
        if (!manifestHash.AsSpan().SequenceEqual(model.MessageSignature.MessageDigest.Digest.Span))
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle digest does not match the Windows release manifest.");
        }

        VerificationMaterial material = model.VerificationMaterial
            ?? throw new InvalidDataException("The Python Sigstore bundle has no verification material.");
        if (material.Certificate is null
            || material.Certificate.RawBytes.Length == 0
            || material.X509CertificateChain is not null)
        {
            throw new InvalidDataException(
                "The Python Sigstore v0.3 bundle must contain exactly one leaf certificate.");
        }

        if (material.TlogEntries.Count != 1)
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle must contain exactly one transparency-log entry.");
        }

        TransparencyLogEntry entry = material.TlogEntries[0];
        ValidateTransparencyEntryShape(entry);
        DateTimeOffset integratedTime;
        try
        {
            integratedTime = DateTimeOffset.FromUnixTimeSeconds(entry.IntegratedTime);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle has an invalid integrated signing time.",
                exception);
        }

        X509Certificate2 leaf;
        try
        {
            leaf = new X509Certificate2(material.Certificate.RawBytes.ToByteArray());
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle leaf certificate is invalid.",
                exception);
        }

        try
        {
            ValidateLeafCertificate(leaf, integratedTime, signingPolicy);
            ValidateCanonicalizedBody(entry, model, leaf, manifestHash);
            string pipelineIdentity = GetPipelineIdentity(leaf);
            return new ParsedBundle(model, leaf, entry, integratedTime, pipelineIdentity);
        }
        catch
        {
            leaf.Dispose();
            throw;
        }
    }

    private static void ValidateTransparencyEntryShape(TransparencyLogEntry entry)
    {
        if (entry.LogId is null
            || entry.LogId.KeyId.Length != 32
            || entry.LogIndex < 0
            || entry.IntegratedTime <= 0
            || entry.CanonicalizedBody.Length == 0
            || entry.InclusionPromise is null
            || entry.InclusionPromise.SignedEntryTimestamp.Length == 0)
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle has incomplete Rekor identity, body, time, or SET evidence.");
        }

        InclusionProof proof = entry.InclusionProof
            ?? throw new InvalidDataException(
                "The Python Sigstore bundle has no Rekor inclusion proof.");
        if (proof.LogIndex < 0
            || proof.TreeSize <= 0
            || proof.LogIndex >= proof.TreeSize
            || proof.RootHash.Length != 32
            || proof.Hashes.Count == 0
            || proof.Hashes.Any(hash => hash.Length != 32)
            || proof.Checkpoint is null
            || string.IsNullOrWhiteSpace(proof.Checkpoint.Envelope))
        {
            throw new InvalidDataException(
                "The Python Sigstore bundle has an incomplete Rekor inclusion proof or checkpoint.");
        }
    }

    private static void ValidateLeafCertificate(
        X509Certificate2 leaf,
        DateTimeOffset integratedTime,
        PythonReleaseSigningPolicy signingPolicy)
    {
        DateTimeOffset notBefore = new(leaf.NotBefore.ToUniversalTime());
        DateTimeOffset notAfter = new(leaf.NotAfter.ToUniversalTime());
        if (integratedTime < notBefore || integratedTime > notAfter)
        {
            throw new InvalidDataException(
                "The Python Sigstore integrated time is outside the leaf certificate validity window.");
        }

        X509KeyUsageExtension? keyUsage = leaf.Extensions
            .OfType<X509KeyUsageExtension>()
            .SingleOrDefault();
        if (keyUsage is null
            || !keyUsage.Critical
            || (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
        {
            throw new InvalidDataException(
                "The Python Sigstore leaf certificate is not restricted to digital signatures.");
        }

        X509EnhancedKeyUsageExtension? enhancedKeyUsage = leaf.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SingleOrDefault();
        if (enhancedKeyUsage is null
            || !enhancedKeyUsage.EnhancedKeyUsages
                .Cast<Oid>()
                .Any(usage => usage.Value == CodeSigningEnhancedKeyUsageOid))
        {
            throw new InvalidDataException(
                "The Python Sigstore leaf certificate does not permit code signing.");
        }

        IReadOnlyList<string> emailIdentities = GetRfc822SubjectAlternativeNames(leaf);
        if (emailIdentities.Count != 1
            || !emailIdentities[0].Equals(
                signingPolicy.CertificateIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The Python Sigstore certificate identity must be exactly '{signingPolicy.CertificateIdentity}'.");
        }

        if (!TryGetFulcioStringExtension(
                leaf,
                SigstoreX509Extensions.OidcIssuerOid,
                allowRawUtf8: false,
                out string issuer)
            || !issuer.Equals(signingPolicy.OidcIssuer, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The Python Sigstore OIDC issuer must be exactly '{signingPolicy.OidcIssuer}'.");
        }

        if (TryGetFulcioStringExtension(
                leaf,
                SigstoreX509Extensions.OidcIssuerOidLegacy,
                allowRawUtf8: true,
                out string legacyIssuer)
            && !legacyIssuer.Equals(signingPolicy.OidcIssuer, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Python Sigstore certificate contains conflicting OIDC issuer extensions.");
        }
    }

    private static IReadOnlyList<string> GetRfc822SubjectAlternativeNames(X509Certificate2 leaf)
    {
        X509Extension[] sanExtensions = leaf.Extensions
            .Cast<X509Extension>()
            .Where(extension => extension.Oid?.Value == SubjectAlternativeNameOid)
            .ToArray();
        if (sanExtensions.Length != 1 || !sanExtensions[0].Critical)
        {
            throw new InvalidDataException(
                "The Python Sigstore leaf certificate must contain one critical SAN extension.");
        }

        List<string> emails = [];
        try
        {
            AsnReader reader = new(sanExtensions[0].RawData, AsnEncodingRules.DER);
            AsnReader sequence = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            while (sequence.HasData)
            {
                Asn1Tag tag = sequence.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 1)
                {
                    emails.Add(sequence.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException exception)
        {
            throw new InvalidDataException(
                "The Python Sigstore certificate SAN extension is malformed.",
                exception);
        }

        return emails;
    }

    private static string GetPipelineIdentity(X509Certificate2 leaf)
    {
        if (TryGetFulcioStringExtension(
                leaf,
                SigstoreX509Extensions.OidcTokenSubjectOid,
                allowRawUtf8: false,
                out string tokenSubject)
            && !string.IsNullOrWhiteSpace(tokenSubject))
        {
            return tokenSubject;
        }

        if (SigstoreX509Extensions.TryGetPrimaryIdentityUri(leaf, out string uri)
            && !string.IsNullOrWhiteSpace(uri))
        {
            return uri;
        }

        if (!string.IsNullOrWhiteSpace(leaf.Subject))
        {
            return leaf.Subject;
        }

        throw new InvalidDataException(
            "The Sigstore.Net pipeline cannot extract an identity from this Python certificate.");
    }

    private static bool TryGetFulcioStringExtension(
        X509Certificate2 certificate,
        string oid,
        bool allowRawUtf8,
        out string value)
    {
        value = string.Empty;
        X509Extension[] extensions = certificate.Extensions
            .Cast<X509Extension>()
            .Where(extension => extension.Oid?.Value == oid)
            .ToArray();
        if (extensions.Length == 0)
        {
            return false;
        }

        if (extensions.Length != 1)
        {
            throw new InvalidDataException(
                $"The Python Sigstore certificate contains duplicate Fulcio extension '{oid}'.");
        }

        byte[] rawData = extensions[0].RawData;
        try
        {
            AsnReader reader = new(rawData, AsnEncodingRules.DER);
            if (reader.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString))
            {
                reader = new AsnReader(reader.ReadOctetString(), AsnEncodingRules.DER);
            }

            value = reader.ReadCharacterString(UniversalTagNumber.UTF8String);
            reader.ThrowIfNotEmpty();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (AsnContentException) when (allowRawUtf8)
        {
            try
            {
                value = StrictUtf8.GetString(rawData);
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"The Python Sigstore Fulcio extension '{oid}' is not valid UTF-8.",
                    exception);
            }
        }
        catch (AsnContentException exception)
        {
            throw new InvalidDataException(
                $"The Python Sigstore Fulcio extension '{oid}' is malformed.",
                exception);
        }
    }

    private static void ValidateCanonicalizedBody(
        TransparencyLogEntry entry,
        BundleModel model,
        X509Certificate2 leaf,
        ReadOnlySpan<byte> manifestHash)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(entry.CanonicalizedBody.ToByteArray());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Python Sigstore Rekor canonicalized body is not valid JSON.",
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (!TryGetString(root, "apiVersion", out string? apiVersion)
                || !apiVersion.Equals("0.0.1", StringComparison.Ordinal)
                || !TryGetString(root, "kind", out string? kind)
                || !kind.Equals("hashedrekord", StringComparison.Ordinal)
                || !root.TryGetProperty("spec", out JsonElement spec)
                || !spec.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("hash", out JsonElement hash)
                || !TryGetString(hash, "algorithm", out string? algorithm)
                || !algorithm.Equals("sha256", StringComparison.Ordinal)
                || !TryGetString(hash, "value", out string? value)
                || !value.Equals(Convert.ToHexString(manifestHash), StringComparison.OrdinalIgnoreCase)
                || !spec.TryGetProperty("signature", out JsonElement signature)
                || !TryGetString(signature, "content", out string? signatureContent)
                || !signature.TryGetProperty("publicKey", out JsonElement publicKey)
                || !TryGetString(publicKey, "content", out string? publicKeyContent))
            {
                throw new InvalidDataException(
                    "The Python Sigstore Rekor body is not an exact SHA-256 hashedrekord for the manifest.");
            }

            byte[] bodySignature = DecodeBase64(signatureContent, "Rekor signature");
            if (!bodySignature.AsSpan().SequenceEqual(model.MessageSignature.Signature.Span))
            {
                throw new InvalidDataException(
                    "The Python Sigstore Rekor signature does not match the bundle signature.");
            }

            byte[] pemBytes = DecodeBase64(publicKeyContent, "Rekor certificate");
            string pem;
            try
            {
                pem = StrictUtf8.GetString(pemBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The Python Sigstore Rekor certificate is not valid UTF-8 PEM.",
                    exception);
            }

            X509Certificate2 loggedCertificate;
            try
            {
                loggedCertificate = X509Certificate2.CreateFromPem(pem);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "The Python Sigstore Rekor certificate is invalid.",
                    exception);
            }

            using (loggedCertificate)
            {
                if (!loggedCertificate.RawData.AsSpan().SequenceEqual(leaf.RawData))
                {
                    throw new InvalidDataException(
                        "The Python Sigstore Rekor certificate does not match the bundle leaf certificate.");
                }
            }
        }
    }

    private static void ValidateTransparencyTrust(ParsedBundle parsed, TrustedRoot trustedRoot)
    {
        TransparencyLogInstance? trustedLog = trustedRoot.Tlogs.FirstOrDefault(log =>
            log.LogId is not null
            && log.LogId.KeyId.Span.SequenceEqual(parsed.Entry.LogId.KeyId.Span));
        if (trustedLog is null
            || trustedLog.PublicKey is null
            || trustedLog.PublicKey.RawBytes.Length == 0
            || !IsWithin(parsed.IntegratedTime, trustedLog.PublicKey.ValidFor))
        {
            throw new InvalidDataException(
                "The Python Sigstore Rekor entry does not match an active log in the pinned trusted root.");
        }
    }

    private static IReadOnlyList<SignedCertificateTimestampEvidence> GetSignedCertificateTimestamps(
        X509Certificate2 leaf)
    {
        X509Extension[] extensions = leaf.Extensions
            .Cast<X509Extension>()
            .Where(extension => extension.Oid?.Value == SignedCertificateTimestampListOid)
            .ToArray();
        if (extensions.Length != 1)
        {
            throw new InvalidDataException(
                "The Python Sigstore certificate must contain one SCT list.");
        }

        byte[] data = extensions[0].RawData;
        try
        {
            if (data.Length > 0 && data[0] == 0x04)
            {
                AsnReader wrapper = new(data, AsnEncodingRules.DER);
                data = wrapper.ReadOctetString();
                wrapper.ThrowIfNotEmpty();
            }

            if (data.Length < 2)
            {
                throw new InvalidDataException("The Python Sigstore SCT list is empty.");
            }

            int listLength = ReadUInt16BigEndian(data, 0);
            if (listLength != data.Length - 2)
            {
                throw new InvalidDataException("The Python Sigstore SCT list length is invalid.");
            }

            List<SignedCertificateTimestampEvidence> timestamps = [];
            int offset = 2;
            while (offset < data.Length)
            {
                if (offset + 2 > data.Length)
                {
                    throw new InvalidDataException("The Python Sigstore SCT record is truncated.");
                }

                int recordLength = ReadUInt16BigEndian(data, offset);
                offset += 2;
                int end = checked(offset + recordLength);
                if (recordLength < 47 || end > data.Length || data[offset] != 0)
                {
                    throw new InvalidDataException("The Python Sigstore SCT record is invalid.");
                }

                byte[] logId = data.AsSpan(offset + 1, 32).ToArray();
                ulong timestamp = ReadUInt64BigEndian(data, offset + 33);
                int cursor = offset + 41;
                int extensionLength = ReadUInt16BigEndian(data, cursor);
                cursor += 2;
                if (cursor + extensionLength > end)
                {
                    throw new InvalidDataException("The Python Sigstore SCT extensions are truncated.");
                }

                byte[] sctExtensions = data.AsSpan(cursor, extensionLength).ToArray();
                cursor += extensionLength;
                if (cursor + 4 > end)
                {
                    throw new InvalidDataException("The Python Sigstore SCT extensions are invalid.");
                }

                byte hashAlgorithm = data[cursor++];
                byte signatureAlgorithm = data[cursor++];
                int signatureLength = ReadUInt16BigEndian(data, cursor);
                cursor += 2;
                if (signatureLength == 0 || cursor + signatureLength != end)
                {
                    throw new InvalidDataException("The Python Sigstore SCT signature is missing or truncated.");
                }

                timestamps.Add(new SignedCertificateTimestampEvidence(
                    logId,
                    timestamp,
                    sctExtensions,
                    hashAlgorithm,
                    signatureAlgorithm,
                    data.AsSpan(cursor, signatureLength).ToArray()));
                offset = end;
            }

            return timestamps.Count > 0
                ? timestamps
                : throw new InvalidDataException("The Python Sigstore SCT list contains no records.");
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The Python Sigstore SCT list is malformed.", exception);
        }
        catch (AsnContentException exception)
        {
            throw new InvalidDataException("The Python Sigstore SCT list is malformed.", exception);
        }
    }

    private static void ValidateVerificationResult(
        VerificationResult result,
        ParsedBundle parsed,
        TrustedRoot trustedRoot)
    {
        if (!result.IsSuccess
            || result.CertificateChain.Count < 2
            || !result.CertificateChain[0].RawData.AsSpan().SequenceEqual(parsed.LeafCertificate.RawData)
            || result.TransparencyLogEntry.LogId is null
            || !result.TransparencyLogEntry.LogId.KeyId.Span.SequenceEqual(parsed.Entry.LogId.KeyId.Span))
        {
            throw new InvalidDataException(
                "The Python Sigstore verifier returned inconsistent certificate or Rekor evidence.");
        }

        X509Certificate2 trustAnchor = result.CertificateChain[^1];
        bool matchesActiveAuthority = trustedRoot.CertificateAuthorities.Any(authority =>
            authority.CertChain is not null
            && authority.CertChain.Certificates.Count > 0
            && IsWithin(parsed.IntegratedTime, authority.ValidFor)
            && authority.CertChain.Certificates[^1].RawBytes.Span.SequenceEqual(trustAnchor.RawData)
            && result.CertificateChain.All(certificate =>
                parsed.IntegratedTime >= new DateTimeOffset(certificate.NotBefore.ToUniversalTime())
                && parsed.IntegratedTime <= new DateTimeOffset(certificate.NotAfter.ToUniversalTime())));
        if (!matchesActiveAuthority)
        {
            throw new InvalidDataException(
                "The Python Sigstore certificate chain does not terminate at an active pinned Fulcio trust anchor.");
        }

        ValidateSignedCertificateTimestamps(
            parsed.LeafCertificate,
            result.CertificateChain[1],
            parsed.IntegratedTime,
            trustedRoot);
    }

    private static void ValidateSignedCertificateTimestamps(
        X509Certificate2 leaf,
        X509Certificate2 issuer,
        DateTimeOffset integratedTime,
        TrustedRoot trustedRoot)
    {
        IReadOnlyList<SignedCertificateTimestampEvidence> timestamps =
            GetSignedCertificateTimestamps(leaf);
        byte[] issuerKeyHash = SHA256.HashData(issuer.PublicKey.ExportSubjectPublicKeyInfo());
        byte[] precertificateTbs = CreatePrecertificateTbs(leaf);
        bool verified = false;
        foreach (SignedCertificateTimestampEvidence timestamp in timestamps)
        {
            DateTimeOffset signedAt;
            try
            {
                signedAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)timestamp.Timestamp));
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException(
                    "The Python Sigstore SCT timestamp is invalid.",
                    exception);
            }

            if (signedAt > integratedTime.AddMinutes(5)
                || signedAt < new DateTimeOffset(leaf.NotBefore.ToUniversalTime()).AddMinutes(-5)
                || signedAt > new DateTimeOffset(leaf.NotAfter.ToUniversalTime()).AddMinutes(5))
            {
                throw new InvalidDataException(
                    "The Python Sigstore SCT timestamp is inconsistent with the certificate or Rekor time.");
            }

            TransparencyLogInstance? ctLog = trustedRoot.Ctlogs.FirstOrDefault(log =>
                log.LogId is not null
                && log.LogId.KeyId.Span.SequenceEqual(timestamp.LogId)
                && log.PublicKey is not null
                && IsWithin(signedAt, log.PublicKey.ValidFor));
            if (ctLog is null)
            {
                continue;
            }

            if (timestamp.HashAlgorithm != 4 || timestamp.SignatureAlgorithm != 3)
            {
                throw new InvalidDataException(
                    "The Python Sigstore SCT uses an unsupported hash or signature algorithm.");
            }

            byte[] signedData = CreateSctSignedData(
                timestamp,
                issuerKeyHash,
                precertificateTbs);
            try
            {
                using ECDsa key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(ctLog.PublicKey.RawBytes.Span, out int bytesRead);
                if (bytesRead != ctLog.PublicKey.RawBytes.Length)
                {
                    throw new InvalidDataException(
                        "The pinned Sigstore CT public key has trailing data.");
                }

                if (key.VerifyData(
                    signedData,
                    timestamp.Signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                {
                    verified = true;
                    break;
                }
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "The pinned Sigstore CT public key or SCT signature is invalid.",
                    exception);
            }
        }

        if (!verified)
        {
            throw new InvalidDataException(
                "The Python Sigstore certificate has no valid SCT signature from an active pinned CT log.");
        }
    }

    private static byte[] CreatePrecertificateTbs(X509Certificate2 leaf)
    {
        try
        {
            AsnReader certificateReader = new(leaf.RawData, AsnEncodingRules.DER);
            AsnReader certificateSequence = certificateReader.ReadSequence();
            ReadOnlyMemory<byte> tbsBytes = certificateSequence.ReadEncodedValue();
            AsnReader tbsReader = new(tbsBytes, AsnEncodingRules.DER);
            AsnReader tbsSequence = tbsReader.ReadSequence();
            AsnWriter writer = new(AsnEncodingRules.DER);
            bool removedSct = false;
            using (writer.PushSequence())
            {
                while (tbsSequence.HasData)
                {
                    Asn1Tag tag = tbsSequence.PeekTag();
                    if (tag == new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true))
                    {
                        AsnReader extensionWrapper = tbsSequence.ReadSequence(
                            new Asn1Tag(TagClass.ContextSpecific, 3));
                        AsnReader extensions = extensionWrapper.ReadSequence();
                        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                        using (writer.PushSequence())
                        {
                            while (extensions.HasData)
                            {
                                ReadOnlyMemory<byte> extensionBytes = extensions.ReadEncodedValue();
                                AsnReader extensionReader = new(extensionBytes, AsnEncodingRules.DER);
                                AsnReader extensionSequence = extensionReader.ReadSequence();
                                string oid = extensionSequence.ReadObjectIdentifier();
                                if (oid is SignedCertificateTimestampListOid
                                    or "1.3.6.1.4.1.11129.2.4.3")
                                {
                                    removedSct |= oid == SignedCertificateTimestampListOid;
                                    continue;
                                }

                                writer.WriteEncodedValue(extensionBytes.Span);
                            }
                        }
                    }
                    else
                    {
                        writer.WriteEncodedValue(tbsSequence.ReadEncodedValue().Span);
                    }
                }
            }

            if (!removedSct)
            {
                throw new InvalidDataException(
                    "The Python Sigstore certificate has no SCT extension to reconstruct its precertificate.");
            }

            return writer.Encode();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                "The Python Sigstore precertificate could not be reconstructed for SCT verification.",
                exception);
        }
    }

    private static byte[] CreateSctSignedData(
        SignedCertificateTimestampEvidence timestamp,
        ReadOnlySpan<byte> issuerKeyHash,
        ReadOnlySpan<byte> precertificateTbs)
    {
        if (issuerKeyHash.Length != 32 || precertificateTbs.Length > 0xFFFFFF)
        {
            throw new InvalidDataException("The Python Sigstore SCT signed input is invalid.");
        }

        using MemoryStream stream = new();
        stream.WriteByte(0);
        stream.WriteByte(0);
        WriteUInt64BigEndian(stream, timestamp.Timestamp);
        WriteUInt16BigEndian(stream, 1);
        stream.Write(issuerKeyHash);
        WriteUInt24BigEndian(stream, precertificateTbs.Length);
        stream.Write(precertificateTbs);
        WriteUInt16BigEndian(stream, timestamp.Extensions.Length);
        stream.Write(timestamp.Extensions);
        return stream.ToArray();
    }

    private static bool IsWithin(DateTimeOffset time, TimeRange? range)
    {
        if (range is null)
        {
            return true;
        }

        if (range.Start is not null && time < range.Start.ToDateTimeOffset())
        {
            return false;
        }

        return range.End is null || time <= range.End.ToDateTimeOffset();
    }

    private static string GetSubjectKeyIdentifier(X509Certificate2 certificate)
    {
        X509Extension[] extensions = certificate.Extensions
            .Cast<X509Extension>()
            .Where(extension => extension.Oid?.Value == SubjectKeyIdentifierOid)
            .ToArray();
        if (extensions.Length != 1)
        {
            throw new InvalidDataException(
                "The Python Sigstore certificate has no unique subject key identifier.");
        }

        X509SubjectKeyIdentifierExtension subjectKeyIdentifier = new(
            extensions[0],
            extensions[0].Critical);
        return subjectKeyIdentifier.SubjectKeyIdentifier
            ?? throw new InvalidDataException(
                "The Python Sigstore certificate subject key identifier is empty.");
    }

    private static Verifier CreateVerifier()
    {
        VerificationPipeline pipeline = new(
            new BundleParser(),
            new CertificateVerifier(),
            new TransparencyLogVerifier(),
            new SignatureVerifier(),
            new DefaultSystemClock(),
            NullLogger<VerificationPipeline>.Instance);
        return new Verifier(
            pipeline,
            DisabledTufClient.Instance,
            NullLogger<Verifier>.Instance);
    }

    private static TrustedRootSnapshot LoadTrustedRootSnapshot()
    {
        Assembly assembly = typeof(PythonReleaseSignatureVerifier).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(TrustedRootResourceName)
            ?? throw new InvalidOperationException(
                "The pinned Sigstore trusted-root resource is missing.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualHash.Equals(TrustRootSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The pinned Sigstore trusted-root hash is invalid. Expected {TrustRootSha256}, got {actualHash}.");
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                "The pinned Sigstore trusted root is not valid UTF-8.",
                exception);
        }

        TrustedRoot root = TrustedRootLoader.Parse(json);
        if (!root.MediaType.Equals(
                "application/vnd.dev.sigstore.trustedroot+json;version=0.1",
                StringComparison.Ordinal)
            || root.Tlogs.Count == 0
            || root.Ctlogs.Count == 0
            || root.CertificateAuthorities.Count == 0)
        {
            throw new InvalidOperationException(
                "The pinned Sigstore trusted root is incomplete or has an unsupported media type.");
        }

        return new TrustedRootSnapshot(json, root);
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
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The Python Sigstore bundle exceeds the {maximumBytes}-byte limit.");
        }

        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream destination = new();
        byte[] buffer = new byte[81_920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The Python Sigstore bundle exceeds the {maximumBytes}-byte limit.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static void ValidatePythonOrgUris(Uri manifestUri, Uri bundleUri)
    {
        if (!IsPythonOrgHttpsUri(manifestUri)
            || !IsPythonOrgHttpsUri(bundleUri)
            || !bundleUri.AbsoluteUri.Equals(
                manifestUri.AbsoluteUri + ".sigstore",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Python release manifests and Sigstore bundles must use matching python.org HTTPS URLs.");
        }
    }

    private static bool IsPythonOrgHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("www.python.org", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static byte[] DecodeBase64(string value, string description)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"The Python Sigstore {description} is not valid base64.",
                exception);
        }
    }

    private static int ReadUInt16BigEndian(byte[] value, int offset)
    {
        if (offset < 0 || offset + 2 > value.Length)
        {
            throw new InvalidDataException("The Python Sigstore binary evidence is truncated.");
        }

        return (value[offset] << 8) | value[offset + 1];
    }

    private static ulong ReadUInt64BigEndian(byte[] value, int offset)
    {
        if (offset < 0 || offset + 8 > value.Length)
        {
            throw new InvalidDataException("The Python Sigstore binary evidence is truncated.");
        }

        ulong result = 0;
        for (int index = 0; index < 8; index++)
        {
            result = (result << 8) | value[offset + index];
        }

        return result;
    }

    private static void WriteUInt16BigEndian(Stream stream, int value)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("The Python Sigstore SCT field exceeds 16 bits.");
        }

        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt24BigEndian(Stream stream, int value)
    {
        if (value is < 0 or > 0xFFFFFF)
        {
            throw new InvalidDataException("The Python Sigstore SCT field exceeds 24 bits.");
        }

        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            stream.WriteByte((byte)(value >> shift));
        }
    }

    private static bool TryGetString(JsonElement item, string name, out string value)
    {
        value = string.Empty;
        return item.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is string parsed
            && (value = parsed) is not null;
    }

    private sealed record ParsedBundle(
        BundleModel Model,
        X509Certificate2 LeafCertificate,
        TransparencyLogEntry Entry,
        DateTimeOffset IntegratedTime,
        string PipelineIdentity);

    private sealed record SignedCertificateTimestampEvidence(
        byte[] LogId,
        ulong Timestamp,
        byte[] Extensions,
        byte HashAlgorithm,
        byte SignatureAlgorithm,
        byte[] Signature);

    private sealed record TrustedRootSnapshot(string Json, TrustedRoot Root);

    private sealed class DisabledTufClient : ITufClient
    {
        public static readonly DisabledTufClient Instance = new();

        public Task<TrustedRoot> FetchPublicGoodTrustedRootAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "AutoEnvPlus does not permit network TUF bootstrap; a pinned trusted-root snapshot is required.");
    }
}
