using System.Text.RegularExpressions;

namespace AutoEnvPlus.App.Text;

/// <summary>
/// Maps the most safety-relevant English error messages produced by the core
/// layer to resource keys resolved through <see cref="Loc"/> for display.
/// Core messages stay English because the core test suite asserts on their
/// exact text; this translator runs at the UI boundary only. Unknown messages
/// pass through unchanged.
/// </summary>
public static class CoreErrorText
{
    private sealed record Rule(Regex Pattern, string Key);

    private static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant);

    private static readonly Rule[] Rules =
    [
        // Integrity failures - the most safety-critical messages a user can see.
        new(
            R(@"^(SHA-256|SHA-512) mismatch for '(.+?)'\. Expected ([0-9a-f]+), but received ([0-9a-f]+)\.$"),
            "CoreError.Integrity.HashMismatch"),
        new(
            R(@"^The archive does not contain '(.+?)' in its payload root\.$"),
            "CoreError.Integrity.ArchivePayloadMissing"),
        new(
            R(@"^The completed detached signature verification does not match the install plan\.$"),
            "CoreError.Integrity.SignatureVerificationMismatch"),
        new(
            R(@"^The assembled download length does not match the probed entity length\.$"),
            "CoreError.Integrity.DownloadLengthMismatch"),
        new(
            R(@"^Segment (\d+) returned a mismatched Content-Range value\.$"),
            "CoreError.Integrity.SegmentContentRangeMismatch"),
        new(
            R(@"^The range probe omitted Content-Range\.$"),
            "CoreError.Integrity.MissingContentRange"),
        new(
            R(@"^Segmented transfers require a known content length\.$"),
            "CoreError.Integrity.SegmentedRequiresKnownLength"),
        new(
            R(@"^Segmented transfers require a stable entity identity\.$"),
            "CoreError.Integrity.SegmentedRequiresStableEntity"),
        new(
            R(@"^Expected hash must be a valid (SHA-256|SHA-512) value\.$"),
            "CoreError.Integrity.InvalidExpectedHash"),

        // Download library destination rules.
        new(
            R(@"^The destination '(.+?)' already exists\.$"),
            "CoreError.Destination.AlreadyExists"),
        new(
            R(@"^The destination must be a direct child of the managed download library\.$"),
            "CoreError.Destination.MustBeDirectChild"),
        new(
            R(@"^The destination must use an approved package or archive extension\.$"),
            "CoreError.Destination.UnapprovedExtension"),
        new(
            R(@"^The destination is not a regular file\.$"),
            "CoreError.Destination.NotRegularFile"),
        new(
            R(@"^The destination is a reparse point and cannot be replaced\.$"),
            "CoreError.Destination.ReparsePoint"),
        new(
            R(@"^Could not allocate a unique transfer staging directory\.$"),
            "CoreError.Destination.StagingDirectoryFailed"),
        new(
            R(@"^The download library manifest is empty\.$"),
            "CoreError.Library.ManifestEmpty"),
        new(
            R(@"^The download library manifest is invalid JSON\.$"),
            "CoreError.Library.ManifestInvalidJson"),
        new(
            R(@"^Connection count must be one of 1, 2, 4, 8, or 16\.$"),
            "CoreError.Library.ConnectionCountInvalid"),

        // pip local package install.
        new(
            R(@"^The wheel must be a top-level file in the managed downloads library\.$"),
            "CoreError.Pip.WheelNotTopLevel"),
        new(
            R(@"^Environment names must use 1-64 ASCII letters, digits, periods, underscores, or hyphens, with an alphanumeric first and last character\.$"),
            "CoreError.Environment.InvalidName"),

        // Project / CMake / storage.
        new(
            R(@"^Project directory does not exist: (.+)$"),
            "CoreError.Project.DirectoryMissing"),
        new(
            R(@"^The generated CMake configure preset does not match the AutoEnvPlus template\.$"),
            "CoreError.CMake.PresetMismatch"),
        new(
            R(@"^Cache source does not exist: (.+)$"),
            "CoreError.Cache.SourceMissing"),
        new(
            R(@"^Cache directory does not exist: (.+)$"),
            "CoreError.Cache.DirectoryMissing"),
        new(
            R(@"^Cache cleanup cannot target an isolation directory\.$"),
            "CoreError.Cache.IsolationTargetNotAllowed"),
        new(
            R(@"^Cache migration does not follow reparse points or device entries: (.+)$"),
            "CoreError.Cache.MigrationSkipsReparseOrDevice"),
        new(
            R(@"^Cache migration does not follow reparse points: (.+)$"),
            "CoreError.Cache.MigrationSkipsReparse"),

        // Session resolution.
        new(
            R(@"^The pinned (.+?) runtime does not match the requested kind, architecture, active version selector, or Provider\. Refresh the session selection\.$"),
            "CoreError.Session.PinnedRuntimeMismatch"),

        // PowerShell integration.
        new(
            R(@"^The PowerShell Profile preview does not match its original bytes\.$"),
            "CoreError.PowerShell.ProfilePreviewMismatch"),
        new(
            R(@"^The PowerShell module preview does not match its original bytes\.$"),
            "CoreError.PowerShell.ModulePreviewMismatch"),
    ];

    public static string Localize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        foreach (Rule rule in Rules)
        {
            if (rule.Pattern.Match(message) is { Success: true } match)
            {
                return match.Result(Loc.Get(rule.Key));
            }
        }

        return message;
    }
}
