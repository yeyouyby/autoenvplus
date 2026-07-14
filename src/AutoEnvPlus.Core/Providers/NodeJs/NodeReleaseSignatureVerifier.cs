using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace AutoEnvPlus.Core.Providers.NodeJs;

public interface INodeReleaseSignatureVerifier
{
    Task<VerifiedNodeReleaseChecksums> GetVerifiedChecksumsAsync(
        Uri signedChecksumsUri,
        DateOnly? releaseDate,
        CancellationToken cancellationToken = default);
}

public sealed record VerifiedNodeReleaseChecksums(
    string Content,
    PackageSignatureVerification Verification);

public sealed class NodeReleaseSignatureVerifier : INodeReleaseSignatureVerifier
{
    private const int MaximumSignedManifestBytes = 1_048_576;
    private const int MaximumPublicKeyBytes = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, byte[]> _keyCache = new(StringComparer.Ordinal);

    public NodeReleaseSignatureVerifier(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<VerifiedNodeReleaseChecksums> GetVerifiedChecksumsAsync(
        Uri signedChecksumsUri,
        DateOnly? releaseDate,
        CancellationToken cancellationToken = default)
    {
        EnsureHttpsUri(signedChecksumsUri, nameof(signedChecksumsUri));
        byte[] signedDocument = await DownloadLimitedAsync(
            signedChecksumsUri,
            MaximumSignedManifestBytes,
            cancellationToken).ConfigureAwait(false);
        ParsedClearSignedDocument parsed = ParseClearSignedDocument(signedDocument);

        if (parsed.Signature.SignatureType != PgpSignature.CanonicalTextDocument)
        {
            throw new InvalidDataException("The Node.js checksum signature is not a canonical-text signature.");
        }

        if (parsed.Signature.HashAlgorithm != HashAlgorithmTag.Sha256)
        {
            throw new InvalidDataException("The Node.js checksum signature must use SHA-256.");
        }

        string signingKeyId = FormatKeyId(parsed.Signature.KeyId);
        if (!NodeReleaseTrustStore.TryGet(signingKeyId, out NodeReleaseTrustedKey trustedKey))
        {
            throw new InvalidDataException(
                $"The Node.js checksum signature uses an untrusted signing key: {signingKeyId}.");
        }

        if (releaseDate is DateOnly date
            && date >= NodeReleaseTrustStore.TrustSnapshotDate
            && !trustedKey.ActiveAtSnapshot)
        {
            throw new InvalidDataException(
                $"Node.js release {date:yyyy-MM-dd} was signed by a historical release key: {trustedKey.PrimaryFingerprint}.");
        }

        byte[] publicKeyBytes = await GetPublicKeyAsync(
            trustedKey,
            cancellationToken).ConfigureAwait(false);
        PgpPublicKeyRingBundle keyBundle;
        try
        {
            using MemoryStream keyStream = new(publicKeyBytes, writable: false);
            using Stream decodedKey = PgpUtilities.GetDecoderStream(keyStream);
            keyBundle = new PgpPublicKeyRingBundle(decodedKey);
        }
        catch (Exception exception) when (exception is IOException or PgpException)
        {
            throw new InvalidDataException("The pinned Node.js release key could not be parsed.", exception);
        }

        PgpPublicKeyRing? keyRing = keyBundle.GetPublicKeyRing(parsed.Signature.KeyId);
        PgpPublicKey? primaryKey = keyRing?.GetPublicKey();
        PgpPublicKey? signingKey = keyRing?.GetPublicKey(parsed.Signature.KeyId);
        if (primaryKey is null || signingKey is null)
        {
            throw new InvalidDataException(
                $"The pinned Node.js release key does not contain signing key {signingKeyId}.");
        }

        string actualPrimaryFingerprint = Convert.ToHexString(primaryKey.GetFingerprint());
        if (!actualPrimaryFingerprint.Equals(
            trustedKey.PrimaryFingerprint,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The Node.js release key fingerprint does not match the pinned value {trustedKey.PrimaryFingerprint}.");
        }

        if (primaryKey.IsRevoked() || signingKey.IsRevoked())
        {
            throw new InvalidDataException("The Node.js release signature uses a revoked OpenPGP key.");
        }

        DateTimeOffset signatureTime = AsUtc(parsed.Signature.CreationTime);
        ValidateSignatureTime(signatureTime, releaseDate, signingKey);

        try
        {
            parsed.Signature.InitVerify(signingKey);
            UpdateCanonicalTextSignature(parsed.Signature, parsed.Lines);
            if (!parsed.Signature.Verify())
            {
                throw new InvalidDataException("The Node.js checksum OpenPGP signature is invalid.");
            }
        }
        catch (PgpException exception)
        {
            throw new InvalidDataException("The Node.js checksum OpenPGP signature could not be verified.", exception);
        }

        string content;
        try
        {
            content = string.Join('\n', parsed.Lines.Select(StrictUtf8.GetString)) + "\n";
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The signed Node.js checksum manifest is not valid UTF-8.", exception);
        }

        return new VerifiedNodeReleaseChecksums(
            content,
            new PackageSignatureVerification(
                PackageSignatureVerificationKind.OpenPgpCleartext,
                signedChecksumsUri,
                trustedKey.SourceUri,
                "SHASUMS256.txt",
                "SHA-256",
                trustedKey.PrimaryFingerprint,
                signingKeyId,
                signatureTime,
                trustedKey.ActiveAtSnapshot
                    ? PackageSignerTrust.ActiveAtTrustSnapshot
                    : PackageSignerTrust.Historical));
    }

    private async Task<byte[]> GetPublicKeyAsync(
        NodeReleaseTrustedKey trustedKey,
        CancellationToken cancellationToken)
    {
        if (_keyCache.TryGetValue(trustedKey.PrimaryFingerprint, out byte[]? cached))
        {
            return cached;
        }

        byte[] downloaded = await DownloadLimitedAsync(
            trustedKey.SourceUri,
            MaximumPublicKeyBytes,
            cancellationToken).ConfigureAwait(false);
        _keyCache.TryAdd(trustedKey.PrimaryFingerprint, downloaded);
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
                $"Security metadata at '{uri}' exceeds the {maximumBytes}-byte limit.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using MemoryStream destination = new();
        byte[] buffer = new byte[16_384];
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
                    $"Security metadata at '{uri}' exceeds the {maximumBytes}-byte limit.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static ParsedClearSignedDocument ParseClearSignedDocument(byte[] value)
    {
        try
        {
            using MemoryStream input = new(value, writable: false);
            using ArmoredInputStream armored = new(input);
            List<byte[]> lines = ReadClearTextLines(armored);
            if (lines.Count == 0)
            {
                throw new InvalidDataException("The Node.js signed checksum manifest is empty.");
            }

            PgpObjectFactory factory = new(armored);
            if (factory.NextPgpObject() is not PgpSignatureList signatures
                || signatures.Count != 1)
            {
                throw new InvalidDataException(
                    "The Node.js checksum manifest must contain exactly one OpenPGP signature.");
            }

            return new ParsedClearSignedDocument(lines, signatures[0]);
        }
        catch (Exception exception) when (exception is IOException or PgpException)
        {
            throw new InvalidDataException("The Node.js clear-signed checksum manifest is malformed.", exception);
        }
    }

    private static List<byte[]> ReadClearTextLines(ArmoredInputStream input)
    {
        List<byte[]> lines = [];
        using MemoryStream line = new();
        int lookAhead = ReadInputLine(line, input);
        if (lookAhead != -1 && input.IsClearText())
        {
            lines.Add(TrimLine(line.ToArray()));
            while (lookAhead != -1 && input.IsClearText())
            {
                lookAhead = ReadInputLine(line, lookAhead, input);
                lines.Add(TrimLine(line.ToArray()));
            }
        }
        else if (lookAhead != -1)
        {
            lines.Add(TrimLine(line.ToArray()));
        }

        return lines;
    }

    private static int ReadInputLine(MemoryStream output, Stream input)
    {
        output.SetLength(0);
        while (input.ReadByte() is int character && character >= 0)
        {
            output.WriteByte((byte)character);
            if (character is '\r' or '\n')
            {
                return ReadPastLineEnding(output, character, input);
            }
        }

        return -1;
    }

    private static int ReadInputLine(MemoryStream output, int lookAhead, Stream input)
    {
        output.SetLength(0);
        int character = lookAhead;
        do
        {
            output.WriteByte((byte)character);
            if (character is '\r' or '\n')
            {
                return ReadPastLineEnding(output, character, input);
            }

            character = input.ReadByte();
        }
        while (character >= 0);

        return -1;
    }

    private static int ReadPastLineEnding(MemoryStream output, int lastCharacter, Stream input)
    {
        int lookAhead = input.ReadByte();
        if (lastCharacter == '\r' && lookAhead == '\n')
        {
            output.WriteByte((byte)lookAhead);
            lookAhead = input.ReadByte();
        }

        return lookAhead;
    }

    private static byte[] TrimLine(byte[] line)
    {
        int length = line.Length;
        while (length > 0 && line[length - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
        {
            length--;
        }

        return line.AsSpan(0, length).ToArray();
    }

    private static void UpdateCanonicalTextSignature(
        PgpSignature signature,
        IReadOnlyList<byte[]> lines)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                signature.Update((byte)'\r');
                signature.Update((byte)'\n');
            }

            if (lines[index].Length > 0)
            {
                signature.Update(lines[index]);
            }
        }
    }

    private static void ValidateSignatureTime(
        DateTimeOffset signatureTime,
        DateOnly? releaseDate,
        PgpPublicKey signingKey)
    {
        DateTimeOffset keyCreated = AsUtc(signingKey.CreationTime);
        if (signatureTime < keyCreated.AddMinutes(-5))
        {
            throw new InvalidDataException("The Node.js release signature predates its signing key.");
        }

        long validSeconds = signingKey.GetValidSeconds();
        if (validSeconds > 0 && signatureTime > keyCreated.AddSeconds(validSeconds).AddMinutes(5))
        {
            throw new InvalidDataException("The Node.js release signature was created after its signing key expired.");
        }

        if (releaseDate is DateOnly date)
        {
            DateOnly signedDate = DateOnly.FromDateTime(signatureTime.UtcDateTime);
            if (signedDate < date.AddDays(-2) || signedDate > date.AddDays(2))
            {
                throw new InvalidDataException(
                    $"The Node.js checksum signature date {signedDate:yyyy-MM-dd} does not match release date {date:yyyy-MM-dd}.");
            }
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

    private static string FormatKeyId(long keyId) => unchecked((ulong)keyId).ToString("X16");

    private static void EnsureHttpsUri(Uri value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Node.js signature metadata must use an absolute HTTPS URI.", parameterName);
        }
    }

    private sealed record ParsedClearSignedDocument(
        IReadOnlyList<byte[]> Lines,
        PgpSignature Signature);
}

internal sealed record NodeReleaseTrustedKey(
    string PrimaryFingerprint,
    bool ActiveAtSnapshot,
    Uri SourceUri);

internal static class NodeReleaseTrustStore
{
    private const string KeyCommit = "b28073028e6d6855cfb53bf7fa0137599c01f967";
    private static readonly Uri KeyBaseUri = new(
        $"https://raw.githubusercontent.com/nodejs/release-keys/{KeyCommit}/keys/");

    private static readonly IReadOnlySet<string> ActivePrimaryFingerprints = new HashSet<string>(
        [
            "5BE8A3F6C8A5C01D106C0AD820B1A390B168D356",
            "DD792F5973C6DE52C432CBDAC77ABFA00DDBF2B7",
            "CC68F5A3106FF448322E48ED27F5E38D5B0A215F",
            "8FCCA13FEF1D0C2E91008E09770F7A9A5AE15600",
            "890C08DB8579162FEE0DF9DB8BEAB4DFCF555EF4",
            "C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C",
            "108F52B48DB57BB0CC439B2997B01419BD92F80A",
            "655F3B5C1FB3FA8D1A0CA6BDE4A7D232B936D2FD",
            "A363A499291CBBC940DD62E41F10027AF002F8B0",
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> SigningKeyToPrimary =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["D7062848A1AB005C"] = "4ED778F539E3634C779C87C6D7062848A1AB005C",
            ["B2CCB982D6DCC25D"] = "4ED778F539E3634C779C87C6D7062848A1AB005C",
            ["7434390BDBE9B9C5"] = "94AE36675C464D64BAFA68DD7434390BDBE9B9C5",
            ["4FEC3ECC9B596CE2"] = "94AE36675C464D64BAFA68DD7434390BDBE9B9C5",
            ["92EF661D867B9DFA"] = "1C050899334244A8AF75E53792EF661D867B9DFA",
            ["8CDB0392359F9454"] = "1C050899334244A8AF75E53792EF661D867B9DFA",
            ["B63B535A4C206CA9"] = "B9AE9905FFD7803F25714661B63B535A4C206CA9",
            ["A39CBCAE8D765781"] = "B9AE9905FFD7803F25714661B63B535A4C206CA9",
            ["B01FBB92821C587A"] = "77984A986EBC2AA786BC0F66B01FBB92821C587A",
            ["919AC8A92C482931"] = "77984A986EBC2AA786BC0F66B01FBB92821C587A",
            ["C97EC7A07EDE3FC1"] = "71DCFD284A79C3B38668286BC97EC7A07EDE3FC1",
            ["7341B15C070877AC"] = "71DCFD284A79C3B38668286BC97EC7A07EDE3FC1",
            ["8975BA8B6100C6B1"] = "71DCFD284A79C3B38668286BC97EC7A07EDE3FC1",
            ["973F295594EC4689"] = "61FC681DFB92A079F1685E77973F295594EC4689",
            ["C06C369CA38E029B"] = "61FC681DFB92A079F1685E77973F295594EC4689",
            ["09FE44734EB7990E"] = "FD3A5288F042B6850C66B31F09FE44734EB7990E",
            ["45F5EEBD813DAE8E"] = "FD3A5288F042B6850C66B31F09FE44734EB7990E",
            ["770F7A9A5AE15600"] = "8FCCA13FEF1D0C2E91008E09770F7A9A5AE15600",
            ["4708964F8085DFE7"] = "8FCCA13FEF1D0C2E91008E09770F7A9A5AE15600",
            ["4DAA80D1E737BC9F"] = "8FCCA13FEF1D0C2E91008E09770F7A9A5AE15600",
            ["E73BC641CC11F4C8"] = "C4F0DFFF4E8C1A8236409D08E73BC641CC11F4C8",
            ["933B01F40B5CA946"] = "C4F0DFFF4E8C1A8236409D08E73BC641CC11F4C8",
            ["A250501325FA7297"] = "C4F0DFFF4E8C1A8236409D08E73BC641CC11F4C8",
            ["DEA16371974031A5"] = "C4F0DFFF4E8C1A8236409D08E73BC641CC11F4C8",
            ["8BEAB4DFCF555EF4"] = "890C08DB8579162FEE0DF9DB8BEAB4DFCF555EF4",
            ["05DE7928107F3DC0"] = "890C08DB8579162FEE0DF9DB8BEAB4DFCF555EF4",
            ["C43CEC45C17AB93C"] = "C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C",
            ["E140F61BC5979DCC"] = "C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C",
            ["C273792F7D83545D"] = "DD8F2338BAE7501E3DD5AC78C273792F7D83545D",
            ["1BDC911B8B6AED76"] = "DD8F2338BAE7501E3DD5AC78C273792F7D83545D",
            ["F07496B3EB3C1762"] = "A48C2BEE680E841632CD4E44F07496B3EB3C1762",
            ["F320153C71827C7B"] = "A48C2BEE680E841632CD4E44F07496B3EB3C1762",
            ["F13993A75599653C"] = "B9E2F5981AA6E0CD28160D9FF13993A75599653C",
            ["3049F7B98AED0C89"] = "B9E2F5981AA6E0CD28160D9FF13993A75599653C",
            ["97B01419BD92F80A"] = "108F52B48DB57BB0CC439B2997B01419BD92F80A",
            ["E04111EEE1A956A5"] = "108F52B48DB57BB0CC439B2997B01419BD92F80A",
            ["6D5A82AC7E37093B"] = "9554F04D7259F04124DE6B476D5A82AC7E37093B",
            ["3F4049298959D8C2"] = "9554F04D7259F04124DE6B476D5A82AC7E37093B",
            ["B0A78B0A6C481CF6"] = "93C7E9E91B49E432C2F75674B0A78B0A6C481CF6",
            ["12DAF9ECEDE8123E"] = "93C7E9E91B49E432C2F75674B0A78B0A6C481CF6",
            ["23EFEFE93C4CFFFE"] = "56730D5401028683275BD23C23EFEFE93C4CFFFE",
            ["D3C55C2AAEC2131D"] = "56730D5401028683275BD23C23EFEFE93C4CFFFE",
            ["50A3051F888C628D"] = "114F43EE0176B71C7BC219DD50A3051F888C628D",
            ["926EC77D21D4BD24"] = "114F43EE0176B71C7BC219DD50A3051F888C628D",
            ["7D33FF9D0246406D"] = "7937DFD2AB06298B2293C3187D33FF9D0246406D",
            ["4ED91D4DBD94604D"] = "7937DFD2AB06298B2293C3187D33FF9D0246406D",
            ["59EB1E31F56368C1"] = "7937DFD2AB06298B2293C3187D33FF9D0246406D",
            ["D3A89613643B6201"] = "74F12602B6F1C4E913FAA37AD3A89613643B6201",
            ["EE3D476257C362B1"] = "74F12602B6F1C4E913FAA37AD3A89613643B6201",
            ["7405533BE57C7D57"] = "141F07595B7B3FFE74309A937405533BE57C7D57",
            ["2248F59603A1688D"] = "141F07595B7B3FFE74309A937405533BE57C7D57",
            ["996B7490EF1AEF97"] = "141F07595B7B3FFE74309A937405533BE57C7D57",
            ["C77ABFA00DDBF2B7"] = "DD792F5973C6DE52C432CBDAC77ABFA00DDBF2B7",
            ["0D0792FB9ACAD426"] = "DD792F5973C6DE52C432CBDAC77ABFA00DDBF2B7",
            ["1F10027AF002F8B0"] = "A363A499291CBBC940DD62E41F10027AF002F8B0",
            ["3C7824F39A895758"] = "A363A499291CBBC940DD62E41F10027AF002F8B0",
            ["04CD3F2FDE079578"] = "A363A499291CBBC940DD62E41F10027AF002F8B0",
            ["900A296076CFC675"] = "A363A499291CBBC940DD62E41F10027AF002F8B0",
            ["27F5E38D5B0A215F"] = "CC68F5A3106FF448322E48ED27F5E38D5B0A215F",
            ["C0AB7FA4DC8F4063"] = "CC68F5A3106FF448322E48ED27F5E38D5B0A215F",
            ["21D900FFDB233756"] = "C0D6248439F1D5604AAFFB4021D900FFDB233756",
            ["85E7AF54D684A75F"] = "C0D6248439F1D5604AAFFB4021D900FFDB233756",
            ["20B1A390B168D356"] = "5BE8A3F6C8A5C01D106C0AD820B1A390B168D356",
            ["039F94E89826F891"] = "5BE8A3F6C8A5C01D106C0AD820B1A390B168D356",
            ["E4A7D232B936D2FD"] = "655F3B5C1FB3FA8D1A0CA6BDE4A7D232B936D2FD",
            ["C24F1F789162BC98"] = "655F3B5C1FB3FA8D1A0CA6BDE4A7D232B936D2FD",
        };

    internal static DateOnly TrustSnapshotDate { get; } = new(2026, 7, 14);

    internal static bool TryGet(string signingKeyId, out NodeReleaseTrustedKey trustedKey)
    {
        if (!SigningKeyToPrimary.TryGetValue(signingKeyId, out string? primaryFingerprint))
        {
            trustedKey = null!;
            return false;
        }

        trustedKey = new NodeReleaseTrustedKey(
            primaryFingerprint,
            ActivePrimaryFingerprints.Contains(primaryFingerprint),
            new Uri(KeyBaseUri, primaryFingerprint + ".asc"));
        return true;
    }
}
