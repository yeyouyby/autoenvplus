using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Runtimes;

public sealed record RuntimeVersion(
    int Major,
    int Minor = 0,
    int Patch = 0,
    int Revision = 0,
    string? PreRelease = null,
    string? BuildMetadata = null) : IComparable<RuntimeVersion>
{
    private static readonly Regex VersionPattern = new(
        @"^[vV]?(?<core>\d+(?:\.\d+){0,3})(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+(?<meta>[0-9A-Za-z.-]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsPrerelease => !string.IsNullOrWhiteSpace(PreRelease);

    public static RuntimeVersion Parse(string value)
    {
        if (!TryParse(value, out RuntimeVersion? version))
        {
            throw new FormatException($"'{value}' is not a supported runtime version.");
        }

        return version!;
    }

    public static bool TryParse(string? value, out RuntimeVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Match match = VersionPattern.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        string[] parts = match.Groups["core"].Value.Split('.');
        int[] numeric = new int[4];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numeric[index]))
            {
                return false;
            }
        }

        version = new RuntimeVersion(
            numeric[0],
            numeric[1],
            numeric[2],
            numeric[3],
            NullIfEmpty(match.Groups["pre"].Value),
            NullIfEmpty(match.Groups["meta"].Value));

        return true;
    }

    public int CompareTo(RuntimeVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int numericComparison = CompareNumeric(other);
        if (numericComparison != 0)
        {
            return numericComparison;
        }

        if (PreRelease is null && other.PreRelease is not null)
        {
            return 1;
        }

        if (PreRelease is not null && other.PreRelease is null)
        {
            return -1;
        }

        return ComparePrerelease(PreRelease, other.PreRelease);
    }

    public override string ToString()
    {
        string core = Revision != 0
            ? $"{Major}.{Minor}.{Patch}.{Revision}"
            : $"{Major}.{Minor}.{Patch}";

        if (PreRelease is not null)
        {
            core += $"-{PreRelease}";
        }

        if (BuildMetadata is not null)
        {
            core += $"+{BuildMetadata}";
        }

        return core;
    }

    private int CompareNumeric(RuntimeVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result == 0 ? Revision.CompareTo(other.Revision) : result;
    }

    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        string[] leftParts = left!.Split('.');
        string[] rightParts = right!.Split('.');
        int length = Math.Max(leftParts.Length, rightParts.Length);

        for (int index = 0; index < length; index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }

            if (index >= rightParts.Length)
            {
                return 1;
            }

            string leftPart = leftParts[index];
            string rightPart = rightParts[index];
            bool leftNumeric = int.TryParse(leftPart, NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(rightPart, NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);

            int result = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
