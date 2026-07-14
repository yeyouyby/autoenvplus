namespace AutoEnvPlus.Core.Environment;

public enum PathEntryScope
{
    Process,
    User,
    Machine,
    UserAndMachine,
}

public sealed record PathInspectionEntry(
    int Index,
    string RawValue,
    string ExpandedValue,
    string NormalizedValue,
    PathEntryScope Scope,
    bool Exists,
    bool IsDuplicate,
    int? FirstOccurrenceIndex);

public sealed record CommandCandidate(
    string Command,
    string ExecutablePath,
    int PathIndex);

public sealed record CommandConflict(
    string Command,
    IReadOnlyList<CommandCandidate> Candidates);

public sealed record CommandResolution(
    string Command,
    IReadOnlyList<CommandCandidate> Candidates)
{
    public CommandCandidate? Winner => Candidates.FirstOrDefault();
}

public sealed record PathInspectionReport(
    IReadOnlyList<PathInspectionEntry> Entries,
    IReadOnlyList<CommandResolution> CommandResolutions)
{
    public IReadOnlyList<CommandConflict> Conflicts => CommandResolutions
        .Where(resolution => resolution.Candidates.Count > 1)
        .Select(resolution => new CommandConflict(resolution.Command, resolution.Candidates))
        .ToArray();

    public int MissingCount => Entries.Count(entry => !entry.Exists);

    public int DuplicateCount => Entries.Count(entry => entry.IsDuplicate);

    public bool IsHealthy => MissingCount == 0 && DuplicateCount == 0 && Conflicts.Count == 0;
}
