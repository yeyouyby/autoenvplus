using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Projects;

public sealed class ProjectManifestService
{
    public const string ManifestFileName = "autoenvplus.toml";

    public string? FindManifest(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        string fullPath = Path.GetFullPath(startPath);
        DirectoryInfo? directory = File.Exists(fullPath)
            ? new FileInfo(fullPath).Directory
            : new DirectoryInfo(fullPath);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ManifestFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public ProjectManifestLoadResult? FindAndLoad(string startPath)
    {
        string? manifestPath = FindManifest(startPath);
        return manifestPath is null ? null : Load(manifestPath);
    }

    public ProjectManifestLoadResult Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath = Path.GetFullPath(manifestPath);
        string projectRoot = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The manifest must have a parent directory.", nameof(manifestPath));

        Dictionary<RuntimeKind, VersionSelector> tools = [];
        List<ProjectManifestError> errors = [];
        string? section = null;
        string[] lines = File.ReadAllLines(fullPath);

        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            string line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (!string.Equals(section, "tools", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                errors.Add(new ProjectManifestError(lineNumber, "Expected 'name = version' in the [tools] section."));
                continue;
            }

            string key = line[..separator].Trim();
            string rawValue = line[(separator + 1)..].Trim();
            if (!TryMapTool(key, out RuntimeKind kind))
            {
                errors.Add(new ProjectManifestError(lineNumber, $"Unsupported tool '{key}'."));
                continue;
            }

            if (!TryUnquote(rawValue, out string? value))
            {
                errors.Add(new ProjectManifestError(lineNumber, "Version values must be quoted strings or bare selectors."));
                continue;
            }

            if (!VersionSelector.TryParse(value, out VersionSelector? selector))
            {
                errors.Add(new ProjectManifestError(lineNumber, $"Unsupported version selector '{value}'."));
                continue;
            }

            if (!tools.TryAdd(kind, selector!))
            {
                errors.Add(new ProjectManifestError(lineNumber, $"Tool '{key}' is declared more than once."));
            }
        }

        return new ProjectManifestLoadResult(
            new ProjectEnvironmentManifest(projectRoot, fullPath, tools),
            errors);
    }

    private static bool TryMapTool(string key, out RuntimeKind kind)
    {
        string normalized = key.Trim().Trim('"', '\'').ToLowerInvariant();
        kind = normalized switch
        {
            "python" => RuntimeKind.Python,
            "node" or "nodejs" or "node.js" => RuntimeKind.NodeJs,
            "java" or "jdk" => RuntimeKind.Java,
            "dotnet" or ".net" => RuntimeKind.DotNet,
            "cmake" => RuntimeKind.CMake,
            _ => default,
        };

        return normalized is "python" or "node" or "nodejs" or "node.js"
            or "java" or "jdk" or "dotnet" or ".net" or "cmake";
    }

    private static bool TryUnquote(string rawValue, out string value)
    {
        value = rawValue.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        char first = value[0];
        if (first is not ('"' or '\''))
        {
            return !value.Any(char.IsWhiteSpace);
        }

        if (value.Length < 2 || value[^1] != first)
        {
            return false;
        }

        value = value[1..^1].Trim();
        return value.Length > 0;
    }

    private static string StripComment(string line)
    {
        char? quote = null;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character is '"' or '\'')
            {
                quote = quote == character ? null : quote ?? character;
                continue;
            }

            if (character == '#' && quote is null)
            {
                return line[..index];
            }
        }

        return line;
    }
}
