using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.Toolchains;

namespace AutoEnvPlus.Core.Projects;

public sealed record CMakeUserPresetsPlan(
    string ProjectRoot,
    string PresetsPath,
    bool PresetsExisted,
    string Before,
    string After,
    string ConfigurePresetName,
    string BuildPresetName,
    string VisualStudioInstanceId,
    CppArchitecturePair ArchitecturePair)
{
    public bool Changed => !PresetsExisted || !Before.Equals(After, StringComparison.Ordinal);
}

public sealed record CMakeUserPresetsSnapshot(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ProjectRoot,
    string PresetsPath,
    bool PresetsExisted,
    string Before,
    string After);

public sealed record CMakeUserPresetsResult(
    bool Success,
    bool Changed,
    string PresetsPath,
    string? SnapshotPath,
    string? Error);

public sealed class CMakeUserPresetsService
{
    public const string PresetsFileName = "CMakeUserPresets.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Encoding FileEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private readonly string _managedRoot;
    private readonly string _projectRoot;
    private readonly string _presetsPath;

    public CMakeUserPresetsService(string managedRoot, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(_projectRoot))
        {
            throw new DirectoryNotFoundException($"Project directory does not exist: {_projectRoot}");
        }

        _presetsPath = Path.Combine(_projectRoot, PresetsFileName);
        EnsureDirectChild(_projectRoot, _presetsPath, "CMake user presets file");
    }

    public CMakeUserPresetsPlan CreatePlan(
        VisualCppInstallation installation,
        CppArchitecturePair pair)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(pair);
        if (!Directory.Exists(installation.InstallationPath))
        {
            throw new DirectoryNotFoundException(
                $"Visual Studio installation does not exist: {installation.InstallationPath}");
        }

        IReadOnlyList<CppArchitecturePair> available = installation.AvailableArchitecturePairs ?? [];
        if (available.Count > 0 && !available.Contains(pair))
        {
            throw new InvalidOperationException(
                $"{installation.DisplayName} does not contain the requested MSVC architecture pair.");
        }

        bool existed = File.Exists(_presetsPath);
        string before = existed ? File.ReadAllText(_presetsPath) : string.Empty;
        JsonObject root = existed ? ParseRoot(before) : new JsonObject();
        int version = ReadVersion(root);
        root["version"] = Math.Max(version, 3);

        string target = ArchitectureName(pair.TargetArchitecture);
        string host = ArchitectureName(pair.HostArchitecture).ToLowerInvariant();
        string presetName = $"autoenvplus-msvc-{target.ToLowerInvariant()}-host-{host}";
        string buildPresetName = presetName + "-build";
        string generator = VisualStudioGenerator(installation.VisualStudioVersion);

        JsonArray configurePresets = GetOrCreateArray(root, "configurePresets");
        RemovePresetByName(configurePresets, presetName);
        configurePresets.Add(new JsonObject
        {
            ["name"] = presetName,
            ["displayName"] = $"AutoEnvPlus MSVC {target} ({host} host)",
            ["description"] = $"Managed by AutoEnvPlus for {installation.DisplayName}",
            ["generator"] = generator,
            ["architecture"] = target,
            ["toolset"] = $"host={host}",
            ["binaryDir"] = $"${{sourceDir}}/out/build/{presetName}",
            ["cacheVariables"] = new JsonObject
            {
                ["CMAKE_GENERATOR_INSTANCE"] = Path.GetFullPath(installation.InstallationPath),
            },
            ["vendor"] = new JsonObject
            {
                ["com.autoenvplus/project-preset/1.0"] = new JsonObject
                {
                    ["instanceId"] = installation.InstanceId,
                    ["hostArchitecture"] = pair.HostArchitecture.ToString(),
                    ["targetArchitecture"] = pair.TargetArchitecture.ToString(),
                    ["vcVarsArgument"] = pair.VcVarsArgument,
                },
            },
        });

        JsonArray buildPresets = GetOrCreateArray(root, "buildPresets");
        RemovePresetByName(buildPresets, buildPresetName);
        buildPresets.Add(new JsonObject
        {
            ["name"] = buildPresetName,
            ["displayName"] = $"Build {target} with AutoEnvPlus MSVC",
            ["configurePreset"] = presetName,
            ["configuration"] = "Debug",
        });

        string after = Serialize(root, DetectNewLine(before));
        return new CMakeUserPresetsPlan(
            _projectRoot,
            _presetsPath,
            existed,
            before,
            after,
            presetName,
            buildPresetName,
            installation.InstanceId,
            pair);
    }

    public async Task<CMakeUserPresetsResult> ApplyAsync(
        CMakeUserPresetsPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureAuthorizedPlan(plan);
        bool existsNow = File.Exists(_presetsPath);
        string beforeNow = existsNow
            ? await File.ReadAllTextAsync(_presetsPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        if (existsNow != plan.PresetsExisted
            || !beforeNow.Equals(plan.Before, StringComparison.Ordinal))
        {
            return Failure(
                "CMakeUserPresets.json changed after the preview was created; refresh and review the new plan.");
        }

        if (!plan.Changed)
        {
            return new CMakeUserPresetsResult(true, false, _presetsPath, null, null);
        }

        string? snapshotPath = null;
        try
        {
            CMakeUserPresetsSnapshot snapshot = new(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                _projectRoot,
                _presetsPath,
                plan.PresetsExisted,
                plan.Before,
                plan.After);
            snapshotPath = Path.Combine(GetSnapshotDirectory(), snapshot.Id + ".json");
            await WriteAtomicallyAsync(
                snapshotPath,
                JsonSerializer.Serialize(snapshot, SnapshotOptions),
                cancellationToken,
                overwrite: false).ConfigureAwait(false);
            await WriteAtomicallyAsync(
                _presetsPath,
                plan.After,
                cancellationToken).ConfigureAwait(false);
            return new CMakeUserPresetsResult(
                true,
                true,
                _presetsPath,
                snapshotPath,
                null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or JsonException)
        {
            return new CMakeUserPresetsResult(
                false,
                false,
                _presetsPath,
                snapshotPath,
                exception.Message);
        }
    }

    public async Task<CMakeUserPresetsResult> RollbackAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        string fullSnapshot = Path.GetFullPath(snapshotPath);
        try
        {
            EnsureChildPath(GetSnapshotDirectory(), fullSnapshot, "CMake preset snapshot");
            CMakeUserPresetsSnapshot? snapshot = JsonSerializer.Deserialize<CMakeUserPresetsSnapshot>(
                await File.ReadAllTextAsync(fullSnapshot, cancellationToken).ConfigureAwait(false),
                SnapshotOptions);
            if (!IsAuthorizedSnapshot(snapshot, fullSnapshot))
            {
                throw new InvalidDataException("The CMake preset snapshot is invalid.");
            }

            if (!File.Exists(_presetsPath)
                || !File.ReadAllText(_presetsPath).Equals(snapshot!.After, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "CMakeUserPresets.json changed after this snapshot; automatic rollback would overwrite newer changes.");
            }

            if (snapshot.PresetsExisted)
            {
                await WriteAtomicallyAsync(
                    _presetsPath,
                    snapshot.Before,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Delete(_presetsPath);
            }

            return new CMakeUserPresetsResult(
                true,
                true,
                _presetsPath,
                fullSnapshot,
                null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return new CMakeUserPresetsResult(
                false,
                false,
                _presetsPath,
                fullSnapshot,
                exception.Message);
        }
    }

    private static JsonObject ParseRoot(string content)
    {
        JsonNode? node = JsonNode.Parse(
            content,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        return node as JsonObject
            ?? throw new InvalidDataException("CMakeUserPresets.json root must be a JSON object.");
    }

    private static int ReadVersion(JsonObject root)
    {
        if (root["version"] is null)
        {
            return 3;
        }

        if (root["version"] is not JsonValue value
            || !value.TryGetValue(out int version)
            || version < 1)
        {
            throw new InvalidDataException("CMakeUserPresets.json version must be a positive integer.");
        }

        return version;
    }

    private static JsonArray GetOrCreateArray(JsonObject root, string property)
    {
        if (root[property] is null)
        {
            JsonArray created = [];
            root[property] = created;
            return created;
        }

        return root[property] as JsonArray
            ?? throw new InvalidDataException(
                $"CMakeUserPresets.json property '{property}' must be an array.");
    }

    private static void RemovePresetByName(JsonArray presets, string name)
    {
        for (int index = presets.Count - 1; index >= 0; index--)
        {
            if (presets[index] is JsonObject preset
                && preset["name"] is JsonValue value
                && value.TryGetValue(out string? existingName)
                && name.Equals(existingName, StringComparison.Ordinal))
            {
                presets.RemoveAt(index);
            }
        }
    }

    private static string VisualStudioGenerator(string version)
    {
        string majorText = version.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        return majorText switch
        {
            "17" => "Visual Studio 17 2022",
            "16" => "Visual Studio 16 2019",
            _ => throw new NotSupportedException(
                $"Visual Studio version '{version}' is not supported for CMake preset generation."),
        };
    }

    private static string ArchitectureName(RuntimeArchitecture architecture) => architecture switch
    {
        RuntimeArchitecture.X86 => "Win32",
        RuntimeArchitecture.X64 => "x64",
        RuntimeArchitecture.Arm64 => "ARM64",
        _ => throw new NotSupportedException($"Unsupported CMake target architecture: {architecture}"),
    };

    private static string Serialize(JsonObject root, string newLine)
    {
        string content = root.ToJsonString(WriteOptions);
        if (!newLine.Equals("\n", StringComparison.Ordinal))
        {
            content = content.Replace("\n", newLine, StringComparison.Ordinal);
        }

        return content + newLine;
    }

    private static string DetectNewLine(string content)
    {
        int lineFeed = content.IndexOf('\n', StringComparison.Ordinal);
        return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
    }

    private void EnsureAuthorizedPlan(CMakeUserPresetsPlan plan)
    {
        if (!Path.GetFullPath(plan.ProjectRoot).Equals(
                _projectRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(plan.PresetsPath).Equals(
                _presetsPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The CMake preset plan must target the authorized project root.",
                nameof(plan));
        }
    }

    private bool IsAuthorizedSnapshot(
        CMakeUserPresetsSnapshot? snapshot,
        string snapshotPath) => snapshot is not null
        && Guid.TryParseExact(snapshot.Id, "N", out _)
        && snapshot.Id.Equals(
            Path.GetFileNameWithoutExtension(snapshotPath),
            StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(snapshot.ProjectRoot).Equals(
            _projectRoot,
            StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(snapshot.PresetsPath).Equals(
            _presetsPath,
            StringComparison.OrdinalIgnoreCase);

    private string GetSnapshotDirectory() => Path.Combine(
        _managedRoot,
        "state",
        "cmake-preset-snapshots");

    private CMakeUserPresetsResult Failure(string error) => new(
        false,
        false,
        _presetsPath,
        null,
        error);

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken,
        bool overwrite = true)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The target file requires a parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                FileEncoding,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureDirectChild(string root, string candidate, string description)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidate);
        if (!Path.GetDirectoryName(fullCandidate)!.Equals(
            fullRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} must be directly inside the project root.");
        }
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} escaped its authorized directory.");
        }
    }
}
