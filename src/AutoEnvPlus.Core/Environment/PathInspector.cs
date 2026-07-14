namespace AutoEnvPlus.Core.Environment;

public sealed class PathInspector
{
    private static readonly string[] DefaultExtensions = [".exe", ".cmd", ".bat", ".com"];

    public PathInspectionReport InspectCurrent(IEnumerable<string>? commands = null)
    {
        string processPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string userPath = System.Environment.GetEnvironmentVariable(
            "PATH",
            EnvironmentVariableTarget.User) ?? string.Empty;
        string machinePath = System.Environment.GetEnvironmentVariable(
            "PATH",
            EnvironmentVariableTarget.Machine) ?? string.Empty;

        return Inspect(processPath, userPath, machinePath, commands);
    }

    public PathInspectionReport InspectCurrentAndPersisted(IEnumerable<string>? commands = null)
    {
        string processPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string userPath = System.Environment.GetEnvironmentVariable(
            "PATH",
            EnvironmentVariableTarget.User) ?? string.Empty;
        string machinePath = System.Environment.GetEnvironmentVariable(
            "PATH",
            EnvironmentVariableTarget.Machine) ?? string.Empty;
        return InspectMerged(
            processPath,
            userPath,
            machinePath,
            commands);
    }

    public PathInspectionReport InspectMerged(
        string? processPath,
        string? userPath,
        string? machinePath,
        IEnumerable<string>? commands = null) =>
        Inspect(
            MergePathValues(processPath, userPath, machinePath),
            userPath,
            machinePath,
            commands);

    public PathInspectionReport Inspect(
        string? processPath,
        string? userPath,
        string? machinePath,
        IEnumerable<string>? commands = null)
    {
        HashSet<string> userEntries = ParseAndNormalize(userPath);
        HashSet<string> machineEntries = ParseAndNormalize(machinePath);
        Dictionary<string, int> firstOccurrences = new(StringComparer.OrdinalIgnoreCase);
        List<PathInspectionEntry> entries = [];

        string[] rawEntries = SplitPath(processPath).ToArray();
        for (int index = 0; index < rawEntries.Length; index++)
        {
            string raw = rawEntries[index];
            string expanded = Expand(raw);
            string normalized = Normalize(expanded);
            bool duplicate = firstOccurrences.TryGetValue(normalized, out int firstIndex);
            if (!duplicate)
            {
                firstOccurrences[normalized] = index;
            }

            entries.Add(new PathInspectionEntry(
                index,
                raw,
                expanded,
                normalized,
                DetermineScope(normalized, userEntries, machineEntries),
                Directory.Exists(expanded),
                duplicate,
                duplicate ? firstIndex : null));
        }

        IReadOnlyList<CommandResolution> resolutions = FindCommands(entries, commands ?? []);
        return new PathInspectionReport(entries, resolutions);
    }

    private static IReadOnlyList<CommandResolution> FindCommands(
        IReadOnlyList<PathInspectionEntry> entries,
        IEnumerable<string> commands)
    {
        List<CommandResolution> resolutions = [];
        foreach (string command in commands
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            List<CommandCandidate> candidates = [];
            HashSet<string> candidatePaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (PathInspectionEntry entry in entries.Where(value => value.Exists && !value.IsDuplicate))
            {
                foreach (string fileName in CandidateFileNames(command.Trim()))
                {
                    string candidatePath = Path.Combine(entry.ExpandedValue, fileName);
                    if (File.Exists(candidatePath) && candidatePaths.Add(candidatePath))
                    {
                        candidates.Add(new CommandCandidate(command, candidatePath, entry.Index));
                    }
                }
            }

            resolutions.Add(new CommandResolution(command, candidates));
        }

        return resolutions;
    }

    private static IEnumerable<string> CandidateFileNames(string command)
    {
        if (Path.HasExtension(command))
        {
            yield return command;
            yield break;
        }

        foreach (string extension in DefaultExtensions)
        {
            yield return command + extension;
        }
    }

    private static PathEntryScope DetermineScope(
        string normalized,
        HashSet<string> userEntries,
        HashSet<string> machineEntries)
    {
        bool isUser = userEntries.Contains(normalized);
        bool isMachine = machineEntries.Contains(normalized);

        return (isUser, isMachine) switch
        {
            (true, true) => PathEntryScope.UserAndMachine,
            (true, false) => PathEntryScope.User,
            (false, true) => PathEntryScope.Machine,
            _ => PathEntryScope.Process,
        };
    }

    private static HashSet<string> ParseAndNormalize(string? path) =>
        new(
            SplitPath(path).Select(value => Normalize(Expand(value))),
            StringComparer.OrdinalIgnoreCase);

    private static string MergePathValues(params string?[] pathValues)
    {
        List<string> merged = [];
        HashSet<string> normalizedEntries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in pathValues.SelectMany(SplitPath))
        {
            if (normalizedEntries.Add(Normalize(Expand(entry))))
            {
                merged.Add(entry);
            }
        }

        return string.Join(';', merged);
    }

    private static IEnumerable<string> SplitPath(string? value) =>
        (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0);

    private static string Expand(string value) =>
        System.Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));

    private static string Normalize(string value)
    {
        try
        {
            string fullPath = Path.GetFullPath(value);
            string? root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
