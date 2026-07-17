using System.Collections.Frozen;

namespace AutoEnvPlus.Core.Languages;

[Flags]
public enum LanguageVisibilityReason
{
    None = 0,
    Default = 1,
    Detected = 2,
    UserEnabled = 4,
    UserHidden = 8,
}

public sealed record LanguageVisibilityState(
    IReadOnlySet<string> EnabledLanguageIds,
    IReadOnlySet<string> HiddenLanguageIds)
{
    public static LanguageVisibilityState Empty { get; } = new(
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase));
}

public sealed record LanguageVisibilityEntry(
    LanguageDefinition Language,
    bool IsVisible,
    LanguageVisibilityReason Reasons)
{
    public bool IsDetected => Reasons.HasFlag(LanguageVisibilityReason.Detected);

    public bool IsExplicitlyEnabled => Reasons.HasFlag(LanguageVisibilityReason.UserEnabled);

    public bool IsExplicitlyHidden => Reasons.HasFlag(LanguageVisibilityReason.UserHidden);
}

public sealed record LanguageVisibilitySnapshot(
    LanguageVisibilityState State,
    IReadOnlyList<LanguageVisibilityEntry> Entries)
{
    public IReadOnlyList<LanguageVisibilityEntry> VisibleLanguages => Entries
        .Where(entry => entry.IsVisible)
        .ToArray();
}

public enum LanguageVisibilityErrorCode
{
    MalformedJson,
    UnsupportedSchema,
    DocumentTooLarge,
    InvalidDocument,
    UnknownLanguage,
    UnsafePath,
    IoFailure,
}

public sealed class LanguageVisibilityException : Exception
{
    public LanguageVisibilityException(
        LanguageVisibilityErrorCode code,
        string message,
        string? field = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Field = field;
    }

    public LanguageVisibilityErrorCode Code { get; }

    public string? Field { get; }
}

public static class LanguageVisibilityEvaluator
{
    public static LanguageVisibilitySnapshot Evaluate(
        LanguageCatalog activeCatalog,
        LanguageVisibilityState state,
        IEnumerable<string>? detectedLanguageIds = null)
    {
        ArgumentNullException.ThrowIfNull(activeCatalog);
        ArgumentNullException.ThrowIfNull(state);
        HashSet<string> detected = new(
            detectedLanguageIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        LanguageVisibilityEntry[] entries = activeCatalog.Languages
            .Select(language =>
            {
                LanguageVisibilityReason reasons = LanguageVisibilityReason.None;
                if (language.DefaultEnabled)
                {
                    reasons |= LanguageVisibilityReason.Default;
                }

                if (detected.Contains(language.Id))
                {
                    reasons |= LanguageVisibilityReason.Detected;
                }

                if (state.EnabledLanguageIds.Contains(language.Id))
                {
                    reasons |= LanguageVisibilityReason.UserEnabled;
                }

                if (state.HiddenLanguageIds.Contains(language.Id))
                {
                    reasons |= LanguageVisibilityReason.UserHidden;
                }

                bool visible = !reasons.HasFlag(LanguageVisibilityReason.UserHidden)
                    && (reasons & (LanguageVisibilityReason.Default
                        | LanguageVisibilityReason.Detected
                        | LanguageVisibilityReason.UserEnabled)) != 0;
                return new LanguageVisibilityEntry(language, visible, reasons);
            })
            .ToArray();
        return new LanguageVisibilitySnapshot(state, entries);
    }
}
