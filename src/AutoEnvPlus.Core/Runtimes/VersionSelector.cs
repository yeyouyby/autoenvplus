using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Runtimes;

public enum VersionSelectorKind
{
    Auto,
    Major,
    MajorMinor,
    Exact,
    Channel,
}

public sealed record VersionSelector(
    VersionSelectorKind Kind,
    RuntimeVersion? Version = null,
    string? Channel = null)
{
    private static readonly Regex MajorChannelPattern = new(
        @"^(?<major>\d+)-(?<channel>lts|current|latest)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static VersionSelector Auto { get; } = new(VersionSelectorKind.Auto);

    public static VersionSelector Parse(string? value)
    {
        if (!TryParse(value, out VersionSelector? selector))
        {
            throw new FormatException($"'{value}' is not a supported version selector.");
        }

        return selector!;
    }

    public static bool TryParse(string? value, out VersionSelector? selector)
    {
        selector = null;
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            selector = Auto;
            return true;
        }

        if (IsChannel(normalized))
        {
            selector = new VersionSelector(VersionSelectorKind.Channel, Channel: normalized.ToLowerInvariant());
            return true;
        }

        Match channelMatch = MajorChannelPattern.Match(normalized);
        if (channelMatch.Success)
        {
            int major = int.Parse(channelMatch.Groups["major"].Value, CultureInfo.InvariantCulture);
            selector = new VersionSelector(
                VersionSelectorKind.Channel,
                new RuntimeVersion(major),
                channelMatch.Groups["channel"].Value.ToLowerInvariant());
            return true;
        }

        if (!RuntimeVersion.TryParse(normalized, out RuntimeVersion? version))
        {
            return false;
        }

        string numericPart = normalized.TrimStart('v', 'V').Split('-', '+')[0];
        int componentCount = numericPart.Count(character => character == '.') + 1;
        VersionSelectorKind kind = componentCount switch
        {
            1 => VersionSelectorKind.Major,
            2 => VersionSelectorKind.MajorMinor,
            _ => VersionSelectorKind.Exact,
        };

        selector = new VersionSelector(kind, version);
        return true;
    }

    public bool Matches(RuntimeInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return Kind switch
        {
            VersionSelectorKind.Auto => !installation.Version.IsPrerelease,
            VersionSelectorKind.Major => installation.Version.Major == Version!.Major,
            VersionSelectorKind.MajorMinor => installation.Version.Major == Version!.Major
                && installation.Version.Minor == Version.Minor,
            VersionSelectorKind.Exact => installation.Version.CompareTo(Version) == 0,
            VersionSelectorKind.Channel => MatchesChannel(installation),
            _ => false,
        };
    }

    public override string ToString() => Kind switch
    {
        VersionSelectorKind.Auto => "auto",
        VersionSelectorKind.Major => Version!.Major.ToString(CultureInfo.InvariantCulture),
        VersionSelectorKind.MajorMinor => $"{Version!.Major}.{Version.Minor}",
        VersionSelectorKind.Exact => Version!.ToString(),
        VersionSelectorKind.Channel when Version is not null => $"{Version.Major}-{Channel}",
        VersionSelectorKind.Channel => Channel!,
        _ => "auto",
    };

    private bool MatchesChannel(RuntimeInstallation installation)
    {
        bool versionMatches = Version is null || installation.Version.Major == Version.Major;
        return versionMatches && installation.Channels.Any(
            value => value.Equals(Channel, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsChannel(string value) =>
        value.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || value.Equals("current", StringComparison.OrdinalIgnoreCase)
        || value.Equals("lts", StringComparison.OrdinalIgnoreCase);
}
