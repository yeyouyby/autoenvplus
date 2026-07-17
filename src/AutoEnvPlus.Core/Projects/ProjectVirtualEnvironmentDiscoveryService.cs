using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Projects;

public enum ProjectVirtualEnvironmentHealth
{
    Healthy,
    NeedsAttention,
    Invalid,
}

public sealed record ProjectVirtualEnvironmentDiscoveryOptions
{
    public const int DefaultMaxInspectedPaths = 96;
    public const int DefaultMaxConfigFileBytes = 256 * 1024;
    public const int DefaultMaxResults = 64;

    public int MaxInspectedPaths { get; init; } = DefaultMaxInspectedPaths;

    public int MaxConfigFileBytes { get; init; } = DefaultMaxConfigFileBytes;

    public int MaxResults { get; init; } = DefaultMaxResults;

    internal void Validate()
    {
        if (MaxInspectedPaths is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInspectedPaths),
                "The inspected-path limit must be between 1 and 512.");
        }

        if (MaxConfigFileBytes is < 16 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConfigFileBytes),
                "The configuration-file limit must be between 16 bytes and 1 MiB.");
        }

        if (MaxResults is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResults),
                "The result limit must be between 1 and 128.");
        }
    }
}

public sealed record ProjectVirtualEnvironment(
    string LanguageId,
    string Kind,
    string Manager,
    string Root,
    string? Executable,
    string? Version,
    ProjectVirtualEnvironmentHealth Health,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings);

public sealed record ProjectVirtualEnvironmentDiscoveryResult(
    string ProjectRoot,
    IReadOnlyList<ProjectVirtualEnvironment> Environments,
    IReadOnlyList<string> Warnings,
    int InspectedPathCount,
    bool ScanLimitReached);

/// <summary>
/// Reads a fixed set of project-local environment markers without invoking any tools.
/// It never performs a recursive directory enumeration or follows a reparse point.
/// </summary>
public sealed class ProjectVirtualEnvironmentDiscoveryService
{
    public ProjectVirtualEnvironmentDiscoveryResult Discover(
        string projectRoot,
        ProjectVirtualEnvironmentDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ProjectVirtualEnvironmentDiscoveryOptions effectiveOptions = options ?? new();
        effectiveOptions.Validate();

        ScanContext scan = new(projectRoot, effectiveOptions, cancellationToken);
        scan.ValidateProjectRoot();
        DiscoverPython(scan);
        DiscoverNode(scan);
        DiscoverDotNet(scan);
        DiscoverJava(scan);
        DiscoverRust(scan);
        DiscoverGo(scan);

        ProjectVirtualEnvironment[] environments = scan.Results
            .OrderBy(result => result.LanguageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Root, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Manager, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProjectVirtualEnvironmentDiscoveryResult(
            scan.ProjectRoot,
            environments,
            scan.Warnings.ToArray(),
            scan.InspectedPathCount,
            scan.ScanLimitReached);
    }

    private static void DiscoverPython(ScanContext scan)
    {
        ManagerEvidence poetry = FindPythonManager(
            scan,
            "poetry",
            ["poetry.lock"],
            "pyproject.toml",
            "[tool.poetry]");
        ManagerEvidence pipenv = FindPythonManager(
            scan,
            "pipenv",
            ["Pipfile.lock", "Pipfile"],
            null,
            null);
        HashSet<string> representedManagers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in new[] { ".venv", "venv", "env", ".conda", "conda" })
        {
            scan.ThrowIfCancellationRequested();
            CandidateProbe directory = scan.Probe(candidate);
            if (directory.Kind != CandidateKind.Directory)
            {
                continue;
            }

            TextReadResult condaHistory = scan.ReadText(
                CombineRelative(candidate, "conda-meta", "history"));
            if (condaHistory.Exists)
            {
                AddCondaEnvironment(scan, candidate, directory.FullPath, condaHistory);
                representedManagers.Add("conda");
                continue;
            }

            string configurationRelative = CombineRelative(candidate, "pyvenv.cfg");
            TextReadResult configuration = scan.ReadText(configurationRelative);
            CandidateProbe scriptsPython = scan.Probe(
                CombineRelative(candidate, "Scripts", "python.exe"));
            if (!configuration.Exists && scriptsPython.Kind != CandidateKind.File)
            {
                continue;
            }

            ManagerEvidence manager = poetry.Found
                ? poetry
                : pipenv.Found
                    ? pipenv
                    : new ManagerEvidence("python-venv", [], false);
            representedManagers.Add(manager.Manager);
            List<string> evidence = [configuration.FullPath];
            evidence.AddRange(manager.Evidence);
            List<string> warnings = [];
            string? version = null;
            ProjectVirtualEnvironmentHealth health = ProjectVirtualEnvironmentHealth.Healthy;

            if (!configuration.Success)
            {
                health = ProjectVirtualEnvironmentHealth.Invalid;
                warnings.Add(configuration.Error ?? "pyvenv.cfg could not be read.");
            }
            else
            {
                Dictionary<string, string> values = ParseKeyValueLines(
                    configuration.Content!,
                    warnings);
                AddConfigurationEvidence(values, "home", evidence);
                AddConfigurationEvidence(values, "include-system-site-packages", evidence);
                version = GetFirstValue(values, "version", "version_info");
                if (version is not null)
                {
                    evidence.Add($"version={version}");
                }

                if (values.TryGetValue("include-system-site-packages", out string? includeSystem)
                    && !bool.TryParse(includeSystem, out _))
                {
                    warnings.Add("pyvenv.cfg contains an invalid include-system-site-packages value.");
                }

                if (warnings.Count > 0)
                {
                    health = ProjectVirtualEnvironmentHealth.NeedsAttention;
                }
            }

            string? executable = scriptsPython.Kind == CandidateKind.File
                ? scriptsPython.FullPath
                : null;
            if (executable is null)
            {
                health = ProjectVirtualEnvironmentHealth.Invalid;
                warnings.Add("Scripts\\python.exe is missing; the virtual environment is not runnable.");
            }
            else
            {
                evidence.Add(executable);
            }

            scan.AddResult(new ProjectVirtualEnvironment(
                "python",
                "virtual-environment",
                manager.Manager,
                directory.FullPath,
                executable,
                version,
                health,
                LimitEvidence(evidence),
                warnings));
        }

        AddUnmaterializedPythonManager(scan, poetry, representedManagers);
        AddUnmaterializedPythonManager(scan, pipenv, representedManagers);
    }

    private static ManagerEvidence FindPythonManager(
        ScanContext scan,
        string manager,
        IReadOnlyList<string> markerFiles,
        string? contentFile,
        string? contentMarker)
    {
        List<string> evidence = [];
        foreach (string markerFile in markerFiles)
        {
            CandidateProbe marker = scan.Probe(markerFile);
            if (marker.Kind == CandidateKind.File)
            {
                evidence.Add(marker.FullPath);
            }
        }

        if (contentFile is not null && contentMarker is not null)
        {
            TextReadResult content = scan.ReadText(contentFile);
            if (content.Success
                && content.Content!.Contains(contentMarker, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add($"{content.FullPath}: {contentMarker}");
            }
        }

        return new ManagerEvidence(manager, evidence, evidence.Count > 0);
    }

    private static void AddUnmaterializedPythonManager(
        ScanContext scan,
        ManagerEvidence manager,
        ISet<string> representedManagers)
    {
        if (!manager.Found || representedManagers.Contains(manager.Manager))
        {
            return;
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "python",
            "environment-definition",
            manager.Manager,
            scan.ProjectRoot,
            null,
            null,
            ProjectVirtualEnvironmentHealth.NeedsAttention,
            LimitEvidence(manager.Evidence),
            ["A project-local virtual environment was not found in .venv, venv, or env."]));
    }

    private static void AddCondaEnvironment(
        ScanContext scan,
        string candidate,
        string environmentRoot,
        TextReadResult history)
    {
        List<string> warnings = [];
        List<string> evidence = [history.FullPath];
        ProjectVirtualEnvironmentHealth health = ProjectVirtualEnvironmentHealth.Healthy;
        string? version = null;
        if (!history.Success)
        {
            health = ProjectVirtualEnvironmentHealth.Invalid;
            warnings.Add(history.Error ?? "conda-meta/history could not be read.");
        }
        else
        {
            MatchCollection matches = Regex.Matches(
                history.Content!,
                @"(?:^|[\s:/])python-([0-9]+(?:\.[0-9]+){1,3})(?:-|\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matches.Count > 0)
            {
                version = matches[^1].Groups[1].Value;
                evidence.Add($"python={version}");
            }
        }

        CandidateProbe rootPython = scan.Probe(CombineRelative(candidate, "python.exe"));
        CandidateProbe scriptsPython = rootPython.Kind == CandidateKind.File
            ? CandidateProbe.Missing(CombineRelative(candidate, "Scripts", "python.exe"))
            : scan.Probe(CombineRelative(candidate, "Scripts", "python.exe"));
        string? executable = rootPython.Kind == CandidateKind.File
            ? rootPython.FullPath
            : scriptsPython.Kind == CandidateKind.File
                ? scriptsPython.FullPath
                : null;
        if (executable is null)
        {
            health = ProjectVirtualEnvironmentHealth.Invalid;
            warnings.Add("python.exe is missing from the Conda environment.");
        }
        else
        {
            evidence.Add(executable);
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "python",
            "conda-environment",
            "conda",
            environmentRoot,
            executable,
            version,
            health,
            LimitEvidence(evidence),
            warnings));
    }

    private static void DiscoverNode(ScanContext scan)
    {
        List<(string Manager, string Path)> lockEvidence = [];
        foreach ((string lockManager, string relativePath) in new[]
        {
            ("pnpm", "pnpm-lock.yaml"),
            ("yarn", "yarn.lock"),
            ("npm", "package-lock.json"),
            ("npm", "npm-shrinkwrap.json"),
            ("bun", "bun.lock"),
            ("bun", "bun.lockb"),
        })
        {
            CandidateProbe candidate = scan.Probe(relativePath);
            if (candidate.Kind == CandidateKind.File)
            {
                lockEvidence.Add((lockManager, candidate.FullPath));
            }
        }

        string? corepackManager = null;
        string? corepackVersion = null;
        List<string> warnings = [];
        List<string> evidence = lockEvidence.Select(item => item.Path).ToList();
        TextReadResult packageJson = scan.ReadText("package.json");
        if (packageJson.Exists)
        {
            if (!packageJson.Success)
            {
                warnings.Add(packageJson.Error ?? "package.json could not be read.");
            }
            else
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(
                        packageJson.Content!,
                        new JsonDocumentOptions { MaxDepth = 32 });
                    if (TryReadCorepackSelection(
                        document.RootElement,
                        out corepackManager,
                        out corepackVersion,
                        out string? rawSelection))
                    {
                        evidence.Add($"{packageJson.FullPath}: packageManager={rawSelection}");
                    }
                }
                catch (JsonException exception)
                {
                    warnings.Add($"package.json is invalid JSON: {exception.Message}");
                }
            }
        }

        CandidateProbe binaries = scan.Probe(CombineRelative("node_modules", ".bin"));
        if (binaries.Kind != CandidateKind.Directory
            && lockEvidence.Count == 0
            && corepackManager is null)
        {
            return;
        }

        string manager = corepackManager
            ?? lockEvidence.Select(item => item.Manager).FirstOrDefault()
            ?? "node";
        if (binaries.Kind == CandidateKind.Directory)
        {
            evidence.Add(binaries.FullPath);
        }
        else
        {
            warnings.Add("node_modules\\.bin is missing; project dependencies may not be restored.");
        }

        if (corepackManager is null && lockEvidence.Count == 0)
        {
            warnings.Add("No package-manager lock or Corepack packageManager declaration was found.");
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "nodejs",
            "dependency-environment",
            manager,
            binaries.Kind == CandidateKind.Directory
                ? Path.GetDirectoryName(binaries.FullPath)!
                : scan.ProjectRoot,
            null,
            corepackVersion,
            warnings.Count == 0
                ? ProjectVirtualEnvironmentHealth.Healthy
                : ProjectVirtualEnvironmentHealth.NeedsAttention,
            LimitEvidence(evidence),
            warnings));
    }

    private static bool TryReadCorepackSelection(
        JsonElement root,
        out string? manager,
        out string? version,
        out string? rawSelection)
    {
        manager = null;
        version = null;
        rawSelection = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("packageManager", out JsonElement packageManager)
            && packageManager.ValueKind == JsonValueKind.String)
        {
            rawSelection = packageManager.GetString()?.Trim();
        }
        else if (root.TryGetProperty("devEngines", out JsonElement devEngines)
            && devEngines.ValueKind == JsonValueKind.Object
            && devEngines.TryGetProperty("packageManager", out JsonElement devPackageManager)
            && devPackageManager.ValueKind == JsonValueKind.Object
            && devPackageManager.TryGetProperty("name", out JsonElement name)
            && name.ValueKind == JsonValueKind.String)
        {
            manager = name.GetString()?.Trim();
            if (devPackageManager.TryGetProperty("version", out JsonElement declaredVersion)
                && declaredVersion.ValueKind == JsonValueKind.String)
            {
                version = declaredVersion.GetString()?.Trim();
            }

            rawSelection = version is null ? manager : $"{manager}@{version}";
            return !string.IsNullOrWhiteSpace(manager);
        }

        if (string.IsNullOrWhiteSpace(rawSelection))
        {
            return false;
        }

        int separator = rawSelection.LastIndexOf('@');
        if (separator > 0 && separator < rawSelection.Length - 1)
        {
            manager = rawSelection[..separator];
            version = rawSelection[(separator + 1)..];
        }
        else
        {
            manager = rawSelection;
        }

        return true;
    }

    private static void DiscoverDotNet(ScanContext scan)
    {
        TextReadResult manifest = scan.ReadText(
            CombineRelative(".config", "dotnet-tools.json"));
        if (!manifest.Exists)
        {
            return;
        }

        List<string> evidence = [manifest.FullPath];
        List<string> warnings = [];
        ProjectVirtualEnvironmentHealth health = ProjectVirtualEnvironmentHealth.Healthy;
        string? version = null;
        if (!manifest.Success)
        {
            health = ProjectVirtualEnvironmentHealth.Invalid;
            warnings.Add(manifest.Error ?? "dotnet-tools.json could not be read.");
        }
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    manifest.Content!,
                    new JsonDocumentOptions { MaxDepth = 32 });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("tools", out JsonElement tools)
                    || tools.ValueKind != JsonValueKind.Object)
                {
                    health = ProjectVirtualEnvironmentHealth.Invalid;
                    warnings.Add("dotnet-tools.json does not contain a tools object.");
                }
                else
                {
                    List<string> versions = [];
                    int toolCount = 0;
                    foreach (JsonProperty tool in tools.EnumerateObject())
                    {
                        if (toolCount++ >= 24)
                        {
                            warnings.Add("Additional .NET tool entries were omitted from the evidence list.");
                            break;
                        }

                        string? toolVersion = tool.Value.ValueKind == JsonValueKind.Object
                            && tool.Value.TryGetProperty("version", out JsonElement declaredVersion)
                            && declaredVersion.ValueKind == JsonValueKind.String
                                ? declaredVersion.GetString()
                                : null;
                        evidence.Add(toolVersion is null
                            ? $"tool={tool.Name}"
                            : $"tool={tool.Name}@{toolVersion}");
                        if (!string.IsNullOrWhiteSpace(toolVersion))
                        {
                            versions.Add(toolVersion);
                        }
                    }

                    version = versions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                        ? versions[0]
                        : versions.Count > 1
                            ? $"{versions.Count} tool versions"
                            : null;
                    if (toolCount == 0)
                    {
                        health = ProjectVirtualEnvironmentHealth.NeedsAttention;
                        warnings.Add("The local .NET tool manifest contains no tools.");
                    }
                }
            }
            catch (JsonException exception)
            {
                health = ProjectVirtualEnvironmentHealth.Invalid;
                warnings.Add($"dotnet-tools.json is invalid JSON: {exception.Message}");
            }
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "dotnet",
            "local-tool-manifest",
            "dotnet-tools",
            Path.GetDirectoryName(manifest.FullPath)!,
            null,
            version,
            health,
            LimitEvidence(evidence),
            warnings));
    }

    private static void DiscoverJava(ScanContext scan)
    {
        DiscoverWrapper(
            scan,
            manager: "maven-wrapper",
            executableCandidates: ["mvnw.cmd", "mvnw"],
            propertiesRelativePath: CombineRelative(".mvn", "wrapper", "maven-wrapper.properties"),
            versionPattern: @"(?:apache-)?maven-([0-9]+(?:\.[0-9]+){1,3})",
            missingExecutableWarning: "The Maven Wrapper script is missing.");
        DiscoverWrapper(
            scan,
            manager: "gradle-wrapper",
            executableCandidates: ["gradlew.bat", "gradlew"],
            propertiesRelativePath: CombineRelative("gradle", "wrapper", "gradle-wrapper.properties"),
            versionPattern: @"gradle-([0-9]+(?:\.[0-9]+){1,3})",
            missingExecutableWarning: "The Gradle Wrapper script is missing.");
    }

    private static void DiscoverWrapper(
        ScanContext scan,
        string manager,
        IReadOnlyList<string> executableCandidates,
        string propertiesRelativePath,
        string versionPattern,
        string missingExecutableWarning)
    {
        CandidateProbe? executable = null;
        foreach (string candidate in executableCandidates)
        {
            CandidateProbe probe = scan.Probe(candidate);
            if (probe.Kind == CandidateKind.File)
            {
                executable ??= probe;
            }
        }

        TextReadResult properties = scan.ReadText(propertiesRelativePath);
        if (executable is null && !properties.Exists)
        {
            return;
        }

        List<string> evidence = [];
        List<string> warnings = [];
        ProjectVirtualEnvironmentHealth health = ProjectVirtualEnvironmentHealth.Healthy;
        string? version = null;
        if (executable is not null)
        {
            evidence.Add(executable.FullPath);
        }
        else
        {
            health = ProjectVirtualEnvironmentHealth.NeedsAttention;
            warnings.Add(missingExecutableWarning);
        }

        if (!properties.Exists)
        {
            health = ProjectVirtualEnvironmentHealth.NeedsAttention;
            warnings.Add("The wrapper properties file is missing.");
        }
        else if (!properties.Success)
        {
            health = ProjectVirtualEnvironmentHealth.Invalid;
            warnings.Add(properties.Error ?? "The wrapper properties file could not be read.");
            evidence.Add(properties.FullPath);
        }
        else
        {
            evidence.Add(properties.FullPath);
            Match match = Regex.Match(
                properties.Content!,
                versionPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                version = match.Groups[1].Value;
                evidence.Add($"distribution-version={version}");
            }
            else
            {
                health = ProjectVirtualEnvironmentHealth.NeedsAttention;
                warnings.Add("The wrapper distribution version could not be parsed.");
            }
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "java",
            "build-wrapper",
            manager,
            scan.ProjectRoot,
            executable?.FullPath,
            version,
            health,
            LimitEvidence(evidence),
            warnings));
    }

    private static void DiscoverRust(ScanContext scan)
    {
        TextReadResult toml = scan.ReadText("rust-toolchain.toml");
        TextReadResult plain = toml.Exists
            ? TextReadResult.Missing(Path.Combine(scan.ProjectRoot, "rust-toolchain"))
            : scan.ReadText("rust-toolchain");
        TextReadResult marker = toml.Exists ? toml : plain;
        CandidateProbe target = scan.Probe("target");
        if (!marker.Exists && target.Kind != CandidateKind.Directory)
        {
            return;
        }

        List<string> evidence = [];
        List<string> warnings = [];
        ProjectVirtualEnvironmentHealth health = ProjectVirtualEnvironmentHealth.Healthy;
        string? version = null;
        if (marker.Exists)
        {
            evidence.Add(marker.FullPath);
            if (!marker.Success)
            {
                health = ProjectVirtualEnvironmentHealth.Invalid;
                warnings.Add(marker.Error ?? "The Rust toolchain file could not be read.");
            }
            else
            {
                version = toml.Exists
                    ? ParseTomlStringValue(marker.Content!, "channel")
                    : marker.Content!
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(line => !line.StartsWith('#'));
                if (string.IsNullOrWhiteSpace(version))
                {
                    health = ProjectVirtualEnvironmentHealth.NeedsAttention;
                    warnings.Add("The Rust toolchain channel could not be parsed.");
                }
                else
                {
                    evidence.Add($"channel={version}");
                }
            }
        }

        if (target.Kind == CandidateKind.Directory)
        {
            evidence.Add(target.FullPath);
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "rust",
            marker.Exists ? "toolchain-selection" : "build-output",
            marker.Exists ? "rustup" : "cargo",
            marker.Exists ? scan.ProjectRoot : target.FullPath,
            null,
            version,
            health,
            LimitEvidence(evidence),
            warnings));
    }

    private static void DiscoverGo(ScanContext scan)
    {
        TextReadResult workspace = scan.ReadText("go.work");
        if (!workspace.Exists)
        {
            return;
        }

        List<string> evidence = [workspace.FullPath];
        List<string> warnings = [];
        ProjectVirtualEnvironmentHealth health = ProjectVirtualEnvironmentHealth.Healthy;
        string? version = null;
        if (!workspace.Success)
        {
            health = ProjectVirtualEnvironmentHealth.Invalid;
            warnings.Add(workspace.Error ?? "go.work could not be read.");
        }
        else
        {
            Match toolchain = Regex.Match(
                workspace.Content!,
                @"(?m)^\s*toolchain\s+go([^\s]+)",
                RegexOptions.CultureInvariant);
            Match language = Regex.Match(
                workspace.Content!,
                @"(?m)^\s*go\s+([^\s]+)",
                RegexOptions.CultureInvariant);
            version = toolchain.Success
                ? toolchain.Groups[1].Value
                : language.Success
                    ? language.Groups[1].Value
                    : null;
            if (version is null)
            {
                health = ProjectVirtualEnvironmentHealth.NeedsAttention;
                warnings.Add("go.work does not declare a Go or toolchain version.");
            }
            else
            {
                evidence.Add($"go={version}");
            }
        }

        scan.AddResult(new ProjectVirtualEnvironment(
            "go",
            "workspace",
            "go",
            scan.ProjectRoot,
            null,
            version,
            health,
            LimitEvidence(evidence),
            warnings));
    }

    private static Dictionary<string, string> ParseKeyValueLines(
        string content,
        ICollection<string> warnings)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                warnings.Add("pyvenv.cfg contains a malformed line.");
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!values.TryAdd(key, value))
            {
                warnings.Add($"pyvenv.cfg repeats the {key} key.");
            }
        }

        return values;
    }

    private static string? ParseTomlStringValue(string content, string key)
    {
        Match match = Regex.Match(
            content,
            $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*[""']([^""']+)[""']",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static void AddConfigurationEvidence(
        IReadOnlyDictionary<string, string> values,
        string key,
        ICollection<string> evidence)
    {
        if (values.TryGetValue(key, out string? value))
        {
            evidence.Add($"{key}={value}");
        }
    }

    private static string? GetFirstValue(
        IReadOnlyDictionary<string, string> values,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> LimitEvidence(IEnumerable<string> evidence) => evidence
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(32)
        .ToArray();

    private static string CombineRelative(params string[] segments) => Path.Combine(segments);

    private sealed record ManagerEvidence(
        string Manager,
        IReadOnlyList<string> Evidence,
        bool Found);

    private enum CandidateKind
    {
        Missing,
        File,
        Directory,
        Rejected,
        LimitReached,
    }

    private sealed record CandidateProbe(
        string RelativePath,
        string FullPath,
        CandidateKind Kind)
    {
        public static CandidateProbe Missing(string relativePath) => new(
            relativePath,
            relativePath,
            CandidateKind.Missing);
    }

    private sealed record TextReadResult(
        string FullPath,
        bool Exists,
        bool Success,
        string? Content,
        string? Error)
    {
        public static TextReadResult Missing(string fullPath) => new(
            fullPath,
            false,
            false,
            null,
            null);
    }

    private sealed class ScanContext
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        private readonly ProjectVirtualEnvironmentDiscoveryOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, CandidateProbe> _probeCache = new(
            StringComparer.OrdinalIgnoreCase);
        private bool _pathLimitWarningAdded;
        private bool _resultLimitWarningAdded;

        public ScanContext(
            string projectRoot,
            ProjectVirtualEnvironmentDiscoveryOptions options,
            CancellationToken cancellationToken)
        {
            ProjectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public string ProjectRoot { get; }

        public List<ProjectVirtualEnvironment> Results { get; } = [];

        public List<string> Warnings { get; } = [];

        public int InspectedPathCount { get; private set; }

        public bool ScanLimitReached { get; private set; }

        public void ValidateProjectRoot()
        {
            ThrowIfCancellationRequested();
            if (!Directory.Exists(ProjectRoot))
            {
                throw new DirectoryNotFoundException($"Project directory does not exist: {ProjectRoot}");
            }

            for (DirectoryInfo? directory = new(ProjectRoot);
                 directory is not null;
                 directory = directory.Parent)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(directory.FullName);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                    throw new InvalidDataException(
                        $"The project path cannot be inspected safely: {directory.FullName}",
                        exception);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"The project path crosses a reparse point and will not be scanned: {directory.FullName}");
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new InvalidDataException(
                        $"The project path contains a non-directory ancestor: {directory.FullName}");
                }
            }
        }

        public CandidateProbe Probe(string relativePath)
        {
            ThrowIfCancellationRequested();
            string normalized = NormalizeRelativePath(relativePath);
            if (_probeCache.TryGetValue(normalized, out CandidateProbe? cached))
            {
                return cached;
            }

            string fullPath = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
            EnsureWithinProject(fullPath);
            if (InspectedPathCount >= _options.MaxInspectedPaths)
            {
                ScanLimitReached = true;
                if (!_pathLimitWarningAdded)
                {
                    Warnings.Add(
                        $"The scan stopped after {_options.MaxInspectedPaths} distinct candidate paths.");
                    _pathLimitWarningAdded = true;
                }

                return new CandidateProbe(normalized, fullPath, CandidateKind.LimitReached);
            }

            InspectedPathCount++;
            string current = ProjectRoot;
            string[] segments = normalized.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception exception) when (exception is FileNotFoundException
                    or DirectoryNotFoundException)
                {
                    CandidateProbe missing = new(normalized, fullPath, CandidateKind.Missing);
                    _probeCache[normalized] = missing;
                    return missing;
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                    Warnings.Add($"Skipped an unreadable candidate path: {fullPath} ({exception.GetType().Name})");
                    CandidateProbe rejected = new(normalized, fullPath, CandidateKind.Rejected);
                    _probeCache[normalized] = rejected;
                    return rejected;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Warnings.Add($"Skipped a reparse point without following it: {current}");
                    CandidateProbe rejected = new(normalized, fullPath, CandidateKind.Rejected);
                    _probeCache[normalized] = rejected;
                    return rejected;
                }

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (index < segments.Length - 1 && !isDirectory)
                {
                    Warnings.Add($"Skipped a candidate below a non-directory path: {fullPath}");
                    CandidateProbe rejected = new(normalized, fullPath, CandidateKind.Rejected);
                    _probeCache[normalized] = rejected;
                    return rejected;
                }

                if (index == segments.Length - 1)
                {
                    CandidateProbe found = new(
                        normalized,
                        fullPath,
                        isDirectory ? CandidateKind.Directory : CandidateKind.File);
                    _probeCache[normalized] = found;
                    return found;
                }
            }

            CandidateProbe invalid = new(normalized, fullPath, CandidateKind.Rejected);
            _probeCache[normalized] = invalid;
            return invalid;
        }

        public TextReadResult ReadText(string relativePath)
        {
            CandidateProbe probe = Probe(relativePath);
            if (probe.Kind == CandidateKind.Missing)
            {
                return TextReadResult.Missing(probe.FullPath);
            }

            if (probe.Kind != CandidateKind.File)
            {
                return new TextReadResult(
                    probe.FullPath,
                    probe.Kind is CandidateKind.Directory or CandidateKind.Rejected,
                    false,
                    null,
                    probe.Kind == CandidateKind.Directory
                        ? "The expected configuration file is a directory."
                        : probe.Kind == CandidateKind.LimitReached
                            ? "The file was not read because the candidate-path limit was reached."
                            : "The configuration file was rejected by the path-safety check.");
            }

            try
            {
                ThrowIfCancellationRequested();
                using FileStream stream = new(
                    probe.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    8_192,
                    FileOptions.SequentialScan);
                if (stream.Length > _options.MaxConfigFileBytes)
                {
                    return Oversized(probe.FullPath, stream.Length);
                }

                FileAttributes openAttributes = File.GetAttributes(probe.FullPath);
                if ((openAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    Warnings.Add($"Skipped a reparse point without following it: {probe.FullPath}");
                    return new TextReadResult(
                        probe.FullPath,
                        true,
                        false,
                        null,
                        "The configuration file became a reparse point during the scan.");
                }

                byte[] bytes = new byte[_options.MaxConfigFileBytes + 1];
                int total = 0;
                while (total < bytes.Length)
                {
                    ThrowIfCancellationRequested();
                    int read = stream.Read(bytes, total, bytes.Length - total);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                if (total > _options.MaxConfigFileBytes || stream.ReadByte() != -1)
                {
                    return Oversized(probe.FullPath, Math.Max(stream.Length, total));
                }

                FileAttributes finalAttributes = File.GetAttributes(probe.FullPath);
                if ((finalAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    Warnings.Add($"Skipped a reparse point without following it: {probe.FullPath}");
                    return new TextReadResult(
                        probe.FullPath,
                        true,
                        false,
                        null,
                        "The configuration file became a reparse point during the scan.");
                }

                string content = StrictUtf8.GetString(bytes, 0, total).TrimStart('\uFEFF');
                return new TextReadResult(probe.FullPath, true, true, content, null);
            }
            catch (DecoderFallbackException)
            {
                return new TextReadResult(
                    probe.FullPath,
                    true,
                    false,
                    null,
                    "The configuration file is not valid UTF-8 text.");
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new TextReadResult(
                    probe.FullPath,
                    true,
                    false,
                    null,
                    $"The configuration file could not be read ({exception.GetType().Name}).");
            }
        }

        public void AddResult(ProjectVirtualEnvironment result)
        {
            ThrowIfCancellationRequested();
            if (Results.Count >= _options.MaxResults)
            {
                ScanLimitReached = true;
                if (!_resultLimitWarningAdded)
                {
                    Warnings.Add($"The scan kept at most {_options.MaxResults} environment results.");
                    _resultLimitWarningAdded = true;
                }

                return;
            }

            Results.Add(result);
        }

        public void ThrowIfCancellationRequested() => _cancellationToken.ThrowIfCancellationRequested();

        private TextReadResult Oversized(string fullPath, long length) => new(
            fullPath,
            true,
            false,
            null,
            $"The configuration file is {length} bytes and exceeds the {_options.MaxConfigFileBytes}-byte limit.");

        private string NormalizeRelativePath(string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException("A scan candidate must be project-relative.");
            }

            string normalized = relativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".." or "."))
            {
                throw new InvalidDataException("A scan candidate cannot traverse outside the project.");
            }

            return normalized;
        }

        private void EnsureWithinProject(string fullPath)
        {
            string prefix = Path.EndsInDirectorySeparator(ProjectRoot)
                ? ProjectRoot
                : ProjectRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A scan candidate resolves outside the project root.");
            }
        }
    }
}
