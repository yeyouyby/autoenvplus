using System.Text.RegularExpressions;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Discovery;

public static partial class RuntimeOutputParser
{
    public static bool TryParse(
        RuntimeKind kind,
        string? standardOutput,
        string? standardError,
        out RuntimeVersion? version)
    {
        string output = string.Join(
            System.Environment.NewLine,
            new[] { standardOutput, standardError }.Where(value => !string.IsNullOrWhiteSpace(value)));

        Match match = kind switch
        {
            RuntimeKind.Python => PythonPattern().Match(output),
            RuntimeKind.NodeJs => NodePattern().Match(output),
            RuntimeKind.Java => JavaPattern().Match(output),
            RuntimeKind.DotNet => DotNetPattern().Match(output),
            RuntimeKind.CMake => CMakePattern().Match(output),
            _ => GenericVersionPattern().Match(output),
        };

        if (!match.Success)
        {
            version = null;
            return false;
        }

        string normalized = Normalize(kind, match.Groups["version"].Value);
        return RuntimeVersion.TryParse(normalized, out version);
    }

    private static string Normalize(RuntimeKind kind, string value)
    {
        string normalized = value.Trim().Trim('"').Replace('_', '.');
        if (kind == RuntimeKind.Java
            && normalized.StartsWith("1.8.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "8." + normalized[4..];
        }

        return normalized;
    }

    [GeneratedRegex(@"Python\s+(?<version>v?\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PythonPattern();

    [GeneratedRegex(@"(?:^|\s)(?<version>v\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NodePattern();

    [GeneratedRegex(@"version\s+""(?<version>\d+(?:[._]\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaPattern();

    [GeneratedRegex(@"(?:^|\s)(?<version>\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetPattern();

    [GeneratedRegex(@"cmake\s+version\s+(?<version>\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CMakePattern();

    [GeneratedRegex(@"(?<version>\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericVersionPattern();
}
