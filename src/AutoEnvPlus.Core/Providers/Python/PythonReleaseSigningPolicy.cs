using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Providers.Python;

public sealed record PythonReleaseSigningPolicy(
    string CertificateIdentity,
    string OidcIssuer,
    Uri PolicySourceUri)
{
    public static readonly Uri PythonOrgPolicySourceUri =
        new("https://www.python.org/downloads/metadata/sigstore/");

    public static PythonReleaseSigningPolicy ForVersion(RuntimeVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Major != 3)
        {
            throw new NotSupportedException(
                $"Python {version.Major}.{version.Minor} has no pinned python.org Sigstore identity policy.");
        }

        return version.Minor switch
        {
            7 => Create("nad@python.org", "https://github.com/login/oauth"),
            8 or 9 => Create("lukasz@langa.pl", "https://github.com/login/oauth"),
            10 or 11 => Create("pablogsal@python.org", "https://accounts.google.com"),
            12 or 13 => Create("thomas@python.org", "https://accounts.google.com"),
            14 or 15 => Create("hugo@python.org", "https://github.com/login/oauth"),
            16 or 17 => Create("savannah@python.org", "https://github.com/login/oauth"),
            _ => throw new NotSupportedException(
                $"Python 3.{version.Minor} has no pinned python.org Sigstore identity policy. " +
                $"Review {PythonOrgPolicySourceUri} before trusting this release series."),
        };
    }

    private static PythonReleaseSigningPolicy Create(string identity, string issuer) =>
        new(identity, issuer, PythonOrgPolicySourceUri);
}
