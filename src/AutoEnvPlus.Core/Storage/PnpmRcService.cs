using System.Text;
using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Storage;

public sealed record PnpmRcReadResult(
    string ConfigPath,
    bool Exists,
    string? Content,
    string? StoreDirectory,
    string? Error);

public sealed record PnpmRcMutation(
    string ConfigPath,
    bool Existed,
    string? Before,
    string After);

public sealed partial class PnpmRcService
{
    public PnpmRcReadResult Read(string configPath, CacheEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(environment);
        string fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            return new PnpmRcReadResult(fullPath, false, null, null, null);
        }

        try
        {
            string content = File.ReadAllText(fullPath);
            IReadOnlyList<ConfigEntry> entries = FindStoreEntries(content);
            if (entries.Count > 1)
            {
                throw new InvalidDataException(
                    "The pnpm global config contains more than one store-dir entry.");
            }

            if (entries.Count == 0 || string.IsNullOrWhiteSpace(entries[0].Value))
            {
                return new PnpmRcReadResult(fullPath, true, content, null, null);
            }

            string resolved = ResolvePath(entries[0].Value, environment);
            return new PnpmRcReadResult(fullPath, true, content, resolved, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new PnpmRcReadResult(fullPath, true, null, null, exception.Message);
        }
    }

    public PnpmRcMutation CreateMutation(string configPath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullPath = Path.GetFullPath(configPath);
        string destination = Path.GetFullPath(destinationPath);
        bool existed = File.Exists(fullPath);
        string? before = existed ? File.ReadAllText(fullPath) : null;
        string newLine = DetectNewLine(before);
        IReadOnlyList<ConfigEntry> entries = FindStoreEntries(before ?? string.Empty);
        if (entries.Count > 1)
        {
            throw new InvalidDataException(
                "The pnpm global config contains more than one store-dir entry.");
        }

        string after;
        if (entries.Count == 1)
        {
            ConfigEntry entry = entries[0];
            after = (before ?? string.Empty)[..entry.LineStart]
                + $"store-dir={destination}"
                + (before ?? string.Empty)[entry.LineContentEnd..];
        }
        else
        {
            StringBuilder output = new(before ?? string.Empty);
            if (output.Length > 0 && output[^1] is not ('\r' or '\n'))
            {
                output.Append(newLine);
            }

            output.Append("store-dir=");
            output.Append(destination);
            output.Append(newLine);
            after = output.ToString();
        }

        return new PnpmRcMutation(fullPath, existed, before, after);
    }

    public async Task WriteAtomicallyAsync(
        string configPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(content);
        string fullPath = Path.GetFullPath(configPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The pnpm config requires a parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static IReadOnlyList<ConfigEntry> FindStoreEntries(string content)
    {
        List<ConfigEntry> entries = [];
        foreach (Match match in LineRegex().Matches(content))
        {
            string line = match.Groups["content"].Value;
            Match assignment = StoreAssignmentRegex().Match(line);
            if (!assignment.Success)
            {
                continue;
            }

            entries.Add(new ConfigEntry(
                match.Index,
                match.Index + line.Length,
                assignment.Groups["value"].Value.Trim()));
        }

        return entries;
    }

    private static string ResolvePath(string configured, CacheEnvironment environment)
    {
        string expanded = BracedEnvironmentRegex().Replace(configured, match =>
            environment.GetVariable(match.Groups[1].Value) ?? match.Value);
        expanded = WindowsEnvironmentRegex().Replace(expanded, match =>
            environment.GetVariable(match.Groups[1].Value) ?? match.Value);
        if (expanded.Contains("${", StringComparison.Ordinal)
            || WindowsEnvironmentRegex().IsMatch(expanded))
        {
            throw new InvalidDataException(
                $"pnpm store-dir contains an unresolved environment expression: {configured}");
        }

        if (expanded.Equals("~", StringComparison.Ordinal)
            || expanded.StartsWith("~\\", StringComparison.Ordinal)
            || expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                environment.UserProfile,
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        expanded = expanded.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new InvalidDataException(
                $"pnpm store-dir must resolve to an absolute path: {configured}");
        }

        return Path.GetFullPath(expanded);
    }

    private static string DetectNewLine(string? content)
    {
        if (content is null)
        {
            return "\r\n";
        }

        int lineFeed = content.IndexOf('\n', StringComparison.Ordinal);
        return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
    }

    [GeneratedRegex(@"(?m)^(?<content>[^\r\n]*)(?:\r\n|\n|\r|$)")]
    private static partial Regex LineRegex();

    [GeneratedRegex(
        @"^\s*store-dir\s*=\s*(?<value>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoreAssignmentRegex();

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracedEnvironmentRegex();

    [GeneratedRegex("%([^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsEnvironmentRegex();

    private sealed record ConfigEntry(
        int LineStart,
        int LineContentEnd,
        string Value);
}
