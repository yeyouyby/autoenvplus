using System.Security.Cryptography;

namespace AutoEnvPlus.Core.Providers;

public enum PackageHashAlgorithm
{
    Sha256,
    Sha512,
}

public static class PackageHashAlgorithmExtensions
{
    public static string DisplayName(this PackageHashAlgorithm algorithm) => algorithm switch
    {
        PackageHashAlgorithm.Sha256 => "SHA-256",
        PackageHashAlgorithm.Sha512 => "SHA-512",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static int HexLength(this PackageHashAlgorithm algorithm) => algorithm switch
    {
        PackageHashAlgorithm.Sha256 => 64,
        PackageHashAlgorithm.Sha512 => 128,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static bool IsValidHash(
        this PackageHashAlgorithm algorithm,
        string? value) =>
        value is not null
        && value.Length == algorithm.HexLength()
        && value.All(Uri.IsHexDigit);

    public static async Task<string> ComputeFileHashAsync(
        this PackageHashAlgorithm algorithm,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = algorithm switch
        {
            PackageHashAlgorithm.Sha256 => await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false),
            PackageHashAlgorithm.Sha512 => await SHA512.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
