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
        Dictionary<RuntimeKind, string> runtimeIds = [];
        Dictionary<RuntimeKind, string> providerIds = [];
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

            bool toolsSection = string.Equals(
                section,
                "tools",
                StringComparison.OrdinalIgnoreCase);
            bool identitiesSection = string.Equals(
                section,
                "tool-identities",
                StringComparison.OrdinalIgnoreCase);
            if (!toolsSection && !identitiesSection)
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
            RuntimeKind kind = default;
            if (toolsSection && !TryMapToolName(key, out kind))
            {
                errors.Add(new ProjectManifestError(lineNumber, $"Unsupported tool '{key}'."));
                continue;
            }

            if (!TryUnquote(rawValue, out string? value))
            {
                errors.Add(new ProjectManifestError(lineNumber, "Version values must be quoted strings or bare selectors."));
                continue;
            }

            if (toolsSection)
            {
                if (!VersionSelector.TryParse(value, out VersionSelector? selector))
                {
                    errors.Add(new ProjectManifestError(
                        lineNumber,
                        $"Unsupported version selector '{value}'."));
                    continue;
                }

                if (!tools.TryAdd(kind, selector!))
                {
                    errors.Add(new ProjectManifestError(
                        lineNumber,
                        $"Tool '{key}' is declared more than once."));
                }

                continue;
            }

            if (!TryMapIdentityKey(
                    key,
                    out RuntimeKind identityKind,
                    out bool isRuntimeId))
            {
                errors.Add(new ProjectManifestError(
                    lineNumber,
                    $"Unsupported tool identity '{key}'."));
                continue;
            }

            if (!ValidateIdentityValue(value))
            {
                errors.Add(new ProjectManifestError(
                    lineNumber,
                    "Runtime and Provider IDs must be non-empty strings of at most 256 characters without control characters."));
                continue;
            }

            Dictionary<RuntimeKind, string> destination = isRuntimeId
                ? runtimeIds
                : providerIds;
            if (!destination.TryAdd(identityKind, value))
            {
                errors.Add(new ProjectManifestError(
                    lineNumber,
                    $"Tool identity '{key}' is declared more than once."));
            }
        }

        Dictionary<RuntimeKind, RuntimeSelectionIdentity> identities = [];
        foreach (RuntimeKind kind in runtimeIds.Keys.Concat(providerIds.Keys).Distinct())
        {
            bool hasRuntime = runtimeIds.TryGetValue(kind, out string? runtimeId);
            bool hasProvider = providerIds.TryGetValue(kind, out string? providerId);
            if (!hasRuntime || !hasProvider)
            {
                errors.Add(new ProjectManifestError(
                    0,
                    $"The {kind} project identity requires both runtime-id and provider-id."));
                continue;
            }

            if (!tools.ContainsKey(kind))
            {
                errors.Add(new ProjectManifestError(
                    0,
                    $"The {kind} project identity requires a matching [tools] selector."));
                continue;
            }

            identities[kind] = new RuntimeSelectionIdentity(runtimeId!, providerId!);
        }

        return new ProjectManifestLoadResult(
            new ProjectEnvironmentManifest(projectRoot, fullPath, tools, identities),
            errors);
    }

    public static bool TryMapToolName(string key, out RuntimeKind kind)
    {
        ArgumentNullException.ThrowIfNull(key);
        string normalized = key.Trim().Trim('"', '\'').ToLowerInvariant();
        kind = normalized switch
        {
            "python" => RuntimeKind.Python,
            "node" or "nodejs" or "node.js" => RuntimeKind.NodeJs,
            "java" or "jdk" => RuntimeKind.Java,
            "dotnet" or ".net" => RuntimeKind.DotNet,
            "msvc" or "visual-cpp" or "visual-c++" => RuntimeKind.Msvc,
            "llvm" or "clang" => RuntimeKind.Llvm,
            "mingw" or "gcc" => RuntimeKind.Mingw,
            "cmake" => RuntimeKind.CMake,
            "ninja" => RuntimeKind.Ninja,
            _ => default,
        };

        return normalized is "python" or "node" or "nodejs" or "node.js"
            or "java" or "jdk" or "dotnet" or ".net"
            or "msvc" or "visual-cpp" or "visual-c++"
            or "llvm" or "clang" or "mingw" or "gcc" or "cmake" or "ninja";
    }

    public static string GetCanonicalToolName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Python => "python",
        RuntimeKind.NodeJs => "node",
        RuntimeKind.Java => "java",
        RuntimeKind.DotNet => "dotnet",
        RuntimeKind.Msvc => "msvc",
        RuntimeKind.Llvm => "llvm",
        RuntimeKind.Mingw => "mingw",
        RuntimeKind.CMake => "cmake",
        RuntimeKind.Ninja => "ninja",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported project tool kind."),
    };

    internal static bool TryMapIdentityKey(
        string key,
        out RuntimeKind kind,
        out bool isRuntimeId)
    {
        string normalized = key.Trim().Trim('"', '\'').ToLowerInvariant();
        const string runtimeSuffix = ".runtime-id";
        const string providerSuffix = ".provider-id";
        string toolKey;
        if (normalized.EndsWith(runtimeSuffix, StringComparison.Ordinal))
        {
            isRuntimeId = true;
            toolKey = normalized[..^runtimeSuffix.Length];
        }
        else if (normalized.EndsWith(providerSuffix, StringComparison.Ordinal))
        {
            isRuntimeId = false;
            toolKey = normalized[..^providerSuffix.Length];
        }
        else
        {
            kind = default;
            isRuntimeId = false;
            return false;
        }

        return TryMapToolName(toolKey, out kind);
    }

    private static bool ValidateIdentityValue(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

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
