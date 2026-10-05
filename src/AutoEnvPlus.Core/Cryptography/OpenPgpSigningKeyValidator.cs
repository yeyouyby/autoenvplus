using Org.BouncyCastle.Bcpg.OpenPgp;

namespace AutoEnvPlus.Core.Cryptography;

internal static class OpenPgpSigningKeyValidator
{
    public static void Validate(
        PgpPublicKey primaryKey,
        PgpPublicKey signingKey,
        DateTimeOffset signatureTime)
    {
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (!primaryKey.IsMasterKey)
        {
            throw new InvalidDataException("The pinned OpenPGP primary key is not a primary key.");
        }

        ValidateNoApplicablePrimaryRevocation(primaryKey, signatureTime);
        PgpSignature primaryAuthorization = GetApplicablePrimaryAuthorization(
            primaryKey,
            signatureTime);
        PgpSignatureSubpacketVector primaryMetadata = GetAuthenticatedMetadata(
            primaryAuthorization,
            "primary-key self-signature");
        ValidateSignatureLifetime(
            primaryAuthorization,
            signatureTime,
            "OpenPGP primary-key self-signature");
        ValidateKeyLifetime(
            primaryKey,
            primaryMetadata.GetKeyExpirationTime(),
            signatureTime,
            "OpenPGP primary key");
        if (signingKey.IsMasterKey)
        {
            if (!signingKey.GetFingerprint().AsSpan().SequenceEqual(primaryKey.GetFingerprint()))
            {
                throw new InvalidDataException(
                    "The OpenPGP signing key is not the pinned primary key.");
            }

            ValidateKeyFlag(
                primaryMetadata,
                PgpKeyFlags.CanSign,
                "The pinned OpenPGP primary key is not authorized for signing.");
            return;
        }

        ValidateKeyFlag(
            primaryMetadata,
            PgpKeyFlags.CanCertify,
            "The pinned OpenPGP primary key is not authorized to certify signing subkeys.");

        PgpSignature binding = GetApplicableSigningSubkeyBinding(
            primaryKey,
            signingKey,
            signatureTime);
        PgpSignatureSubpacketVector bindingMetadata = GetAuthenticatedMetadata(
            binding,
            "signing-subkey binding");
        ValidateSignatureLifetime(
            binding,
            signatureTime,
            "OpenPGP signing-subkey binding");
        ValidateKeyFlag(
            bindingMetadata,
            PgpKeyFlags.CanSign,
            "The pinned OpenPGP primary key did not authorize the selected subkey for signing.");
        ValidateKeyLifetime(
            signingKey,
            bindingMetadata.GetKeyExpirationTime(),
            signatureTime,
            "OpenPGP signing subkey");
        ValidateSigningSubkeyBackSignature(
            primaryKey,
            signingKey,
            bindingMetadata,
            signatureTime);
        ValidateNoApplicableSubkeyRevocation(primaryKey, signingKey, signatureTime);
    }

    private static PgpSignature GetApplicablePrimaryAuthorization(
        PgpPublicKey primaryKey,
        DateTimeOffset signatureTime)
    {
        List<PgpSignature> authorizations = primaryKey.GetKeySignatures()
            .Where(candidate => candidate.SignatureType == PgpSignature.DirectKey)
            .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
            .Where(candidate => VerifyPrimaryKeyCertification(candidate, primaryKey))
            .ToList();

        foreach (string userId in primaryKey.GetUserIds())
        {
            PgpSignature[] certifications = primaryKey.GetSignaturesForId(userId)
                .Where(candidate => PgpSignature.IsCertification(candidate.SignatureType))
                .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
                .Where(candidate => VerifyUserIdCertification(candidate, userId, primaryKey))
                .ToArray();
            PgpSignature[] revocations = primaryKey.GetSignaturesForId(userId)
                .Where(candidate => candidate.SignatureType == PgpSignature.CertificationRevocation)
                .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
                .Where(candidate => VerifyUserIdCertification(candidate, userId, primaryKey))
                .ToArray();

            authorizations.AddRange(certifications.Where(certification =>
                !revocations.Any(revocation =>
                    AsUtc(revocation.CreationTime) >= AsUtc(certification.CreationTime))));
        }

        return authorizations
            .OrderByDescending(candidate => AsUtc(candidate.CreationTime))
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "The pinned OpenPGP primary key has no valid self-signature at the signature time.");
    }

    private static PgpSignature GetApplicableSigningSubkeyBinding(
        PgpPublicKey primaryKey,
        PgpPublicKey signingKey,
        DateTimeOffset signatureTime)
    {
        return signingKey.GetKeySignatures()
            .Where(candidate => candidate.SignatureType == PgpSignature.SubkeyBinding)
            .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
            .Where(candidate => VerifySubkeyCertification(candidate, primaryKey, signingKey, primaryKey))
            .OrderByDescending(candidate => AsUtc(candidate.CreationTime))
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "The OpenPGP signing subkey has no valid binding from the pinned primary key.");
    }

    private static void ValidateSigningSubkeyBackSignature(
        PgpPublicKey primaryKey,
        PgpPublicKey signingKey,
        PgpSignatureSubpacketVector bindingMetadata,
        DateTimeOffset signatureTime)
    {
        PgpSignature[] embeddedSignatures;
        try
        {
            PgpSignatureList embedded = bindingMetadata.GetEmbeddedSignatures();
            embeddedSignatures = Enumerable.Range(0, embedded.Count)
                .Select(index => embedded[index])
                .ToArray();
        }
        catch (PgpException exception)
        {
            throw new InvalidDataException(
                "The OpenPGP signing-subkey binding contains malformed embedded signatures.",
                exception);
        }

        PgpSignature backSignature = embeddedSignatures
            .Where(candidate => candidate.SignatureType == PgpSignature.PrimaryKeyBinding)
            .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
            .Where(candidate => VerifySubkeyCertification(
                candidate,
                primaryKey,
                signingKey,
                signingKey))
            .OrderByDescending(candidate => AsUtc(candidate.CreationTime))
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "The OpenPGP signing subkey has no valid primary-key binding back-signature.");

        ValidateSignatureLifetime(
            backSignature,
            signatureTime,
            "OpenPGP signing-subkey back-signature");
    }

    private static void ValidateNoApplicablePrimaryRevocation(
        PgpPublicKey primaryKey,
        DateTimeOffset signatureTime)
    {
        bool revoked = primaryKey.GetKeySignatures()
            .Where(candidate => candidate.SignatureType == PgpSignature.KeyRevocation)
            .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
            .Any(candidate => VerifyPrimaryKeyCertification(candidate, primaryKey));
        if (revoked)
        {
            throw new InvalidDataException(
                "The pinned OpenPGP primary key was revoked when the content was signed.");
        }
    }

    private static void ValidateNoApplicableSubkeyRevocation(
        PgpPublicKey primaryKey,
        PgpPublicKey signingKey,
        DateTimeOffset signatureTime)
    {
        bool revoked = signingKey.GetKeySignatures()
            .Where(candidate => candidate.SignatureType == PgpSignature.SubkeyRevocation)
            .Where(candidate => AsUtc(candidate.CreationTime) <= signatureTime)
            .Any(candidate => VerifySubkeyCertification(
                candidate,
                primaryKey,
                signingKey,
                primaryKey));
        if (revoked)
        {
            throw new InvalidDataException(
                "The OpenPGP signing subkey was revoked when the content was signed.");
        }
    }

    private static PgpSignatureSubpacketVector GetAuthenticatedMetadata(
        PgpSignature signature,
        string description) =>
        signature.GetHashedSubPackets()
        ?? throw new InvalidDataException(
            $"The OpenPGP {description} has no authenticated metadata.");

    private static void ValidateKeyFlag(
        PgpSignatureSubpacketVector metadata,
        int requiredFlag,
        string errorMessage)
    {
        if ((metadata.GetKeyFlags() & requiredFlag) == 0)
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private static void ValidateSignatureLifetime(
        PgpSignature signature,
        DateTimeOffset signatureTime,
        string description)
    {
        DateTimeOffset created = AsUtc(signature.CreationTime);
        if (created > signatureTime)
        {
            throw new InvalidDataException($"The {description} was not yet valid when the content was signed.");
        }

        PgpSignatureSubpacketVector metadata = GetAuthenticatedMetadata(signature, description);
        if (IsAtOrAfterExpiration(
            signatureTime,
            created,
            metadata.GetSignatureExpirationTime()))
        {
            throw new InvalidDataException($"The {description} had expired before the content was signed.");
        }
    }

    private static void ValidateKeyLifetime(
        PgpPublicKey key,
        long validSeconds,
        DateTimeOffset signatureTime,
        string description)
    {
        DateTimeOffset created = AsUtc(key.CreationTime);
        if (signatureTime < created)
        {
            throw new InvalidDataException($"The content signature predates its {description}.");
        }

        if (IsAtOrAfterExpiration(signatureTime, created, validSeconds))
        {
            throw new InvalidDataException(
                $"The content signature was created after its {description} expired.");
        }
    }

    private static bool IsAtOrAfterExpiration(
        DateTimeOffset instant,
        DateTimeOffset created,
        long validSeconds) =>
        validSeconds > 0
        && instant >= created
        && (instant - created).TotalSeconds >= validSeconds;

    private static bool VerifyPrimaryKeyCertification(
        PgpSignature certification,
        PgpPublicKey primaryKey)
    {
        try
        {
            certification.InitVerify(primaryKey);
            return certification.VerifyCertification(primaryKey);
        }
        catch (PgpException)
        {
            return false;
        }
    }

    private static bool VerifyUserIdCertification(
        PgpSignature certification,
        string userId,
        PgpPublicKey primaryKey)
    {
        try
        {
            certification.InitVerify(primaryKey);
            return certification.VerifyCertification(userId, primaryKey);
        }
        catch (PgpException)
        {
            return false;
        }
    }

    private static bool VerifySubkeyCertification(
        PgpSignature certification,
        PgpPublicKey primaryKey,
        PgpPublicKey signingKey,
        PgpPublicKey verificationKey)
    {
        try
        {
            certification.InitVerify(verificationKey);
            return certification.VerifyCertification(primaryKey, signingKey);
        }
        catch (PgpException)
        {
            return false;
        }
    }

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
}
