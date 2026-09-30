using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;
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
    internal const string VendorMarker = "com.autoenvplus/project-preset/1.0";

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
    private readonly ManagedStateLock _projectTransactionLock;
    private readonly Func<CancellationToken, Task>? _beforeFinalStateCheck;

    internal string TransactionLockPath { get; }

    public CMakeUserPresetsService(string managedRoot, string projectRoot)
        : this(managedRoot, projectRoot, beforeFinalStateCheck: null)
    {
    }

    internal CMakeUserPresetsService(
        string managedRoot,
        string projectRoot,
        Func<CancellationToken, Task>? beforeFinalStateCheck)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(_projectRoot))
        {
            throw new DirectoryNotFoundException($"Project directory does not exist: {_projectRoot}");
        }

        _presetsPath = Path.Combine(_projectRoot, PresetsFileName);
        EnsureDirectChild(_projectRoot, _presetsPath, "CMake user presets file");

        string projectKey = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(_projectRoot.ToUpperInvariant())))
            .ToLowerInvariant();
        string lockFileName = $"cmake-presets-{projectKey}.lock";
        TransactionLockPath = Path.Combine(_managedRoot, "state", lockFileName);
        _projectTransactionLock = new ManagedStateLock(
            _managedRoot,
            Path.Combine(_managedRoot, "state", $"cmake-presets-{projectKey}.transaction"),
            lockFileName);
        _beforeFinalStateCheck = beforeFinalStateCheck;
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
        JsonArray buildPresets = GetOrCreateArray(root, "buildPresets");
        RemoveOwnedBuildPresetByName(
            buildPresets,
            buildPresetName,
            presetName,
            $"Build {target} with AutoEnvPlus MSVC",
            configurePresets);
        RemoveOwnedPresetByName(configurePresets, presetName, "configurePresets");
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
                [VendorMarker] = new JsonObject
                {
                    ["instanceId"] = installation.InstanceId,
                    ["hostArchitecture"] = pair.HostArchitecture.ToString(),
                    ["targetArchitecture"] = pair.TargetArchitecture.ToString(),
                    ["vcVarsArgument"] = pair.VcVarsArgument,
                },
            },
        });

        buildPresets.Add(new JsonObject
        {
            ["name"] = buildPresetName,
            ["displayName"] = $"Build {target} with AutoEnvPlus MSVC",
            ["configurePreset"] = presetName,
            ["configuration"] = "Debug",
            ["vendor"] = new JsonObject
            {
                [VendorMarker] = new JsonObject
                {
                    ["configurePreset"] = presetName,
                },
            },
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
        string? snapshotPath = null;
        try
        {
            using ManagedStateLock.Lease transactionLock =
                await _projectTransactionLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
            PresetsState current = await ReadPresetsStateAsync(cancellationToken).ConfigureAwait(false);
            if (!MatchesExpected(current, plan.PresetsExisted, plan.Before))
            {
                return Failure(
                    "CMakeUserPresets.json changed after the preview was created; refresh and review the new plan.");
            }

            EnsureManagedPlanOutput(plan, current.Content);
            if (!plan.Changed)
            {
                return new CMakeUserPresetsResult(true, false, _presetsPath, null, null);
            }

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
            await CommitPresetsAsync(
                plan.After,
                plan.PresetsExisted,
                plan.Before,
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
            or InvalidDataException
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
            using ManagedStateLock.Lease transactionLock =
                await _projectTransactionLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
            string snapshotContent = await File.ReadAllTextAsync(
                fullSnapshot,
                cancellationToken).ConfigureAwait(false);
            EnsureNoDuplicateJsonProperties(snapshotContent);
            CMakeUserPresetsSnapshot? snapshot =
                JsonSerializer.Deserialize<CMakeUserPresetsSnapshot>(
                    snapshotContent,
                    SnapshotOptions);
            if (!IsAuthorizedSnapshot(snapshot, fullSnapshot))
            {
                throw new InvalidDataException("The CMake preset snapshot is invalid.");
            }

            CMakeUserPresetsSnapshot authorizedSnapshot = snapshot!;
            EnsureManagedSnapshotOutput(authorizedSnapshot);
            PresetsState current = await ReadPresetsStateAsync(cancellationToken).ConfigureAwait(false);
            if (!MatchesExpected(current, expectedExists: true, authorizedSnapshot.After))
            {
                throw new InvalidOperationException(
                    "CMakeUserPresets.json changed after this snapshot; automatic rollback would overwrite newer changes.");
            }

            if (authorizedSnapshot.PresetsExisted)
            {
                await CommitPresetsAsync(
                    authorizedSnapshot.Before,
                    expectedExists: true,
                    authorizedSnapshot.After,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await InvokeBeforeFinalStateCheckAsync(cancellationToken).ConfigureAwait(false);
                PresetsState immediatelyBeforeDelete = await ReadPresetsStateAsync(
                    cancellationToken).ConfigureAwait(false);
                if (!MatchesExpected(
                        immediatelyBeforeDelete,
                        expectedExists: true,
                        authorizedSnapshot.After))
                {
                    throw new InvalidOperationException(
                        "CMakeUserPresets.json changed while rollback was being prepared; no project file was deleted.");
                }

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
        EnsureNoDuplicateJsonProperties(content);
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

    private static void EnsureNoDuplicateJsonProperties(string content)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(content);
        Utf8JsonReader reader = new(
            utf8,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        Stack<HashSet<string>> objectProperties = new();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                string property = reader.GetString()
                    ?? throw new JsonException("A JSON property name cannot be null.");
                if (objectProperties.Count == 0
                    || !objectProperties.Peek().Add(property))
                {
                    throw new InvalidDataException(
                        $"CMakeUserPresets.json contains duplicate property '{property}'.");
                }
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                objectProperties.Pop();
            }
        }
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

    private static void RemoveOwnedPresetByName(
        JsonArray presets,
        string name,
        string collectionName)
    {
        for (int index = presets.Count - 1; index >= 0; index--)
        {
            if (presets[index] is JsonObject preset
                && preset["name"] is JsonValue value
                && value.TryGetValue(out string? existingName)
                && name.Equals(existingName, StringComparison.Ordinal))
            {
                EnsureOwnedPreset(preset, name, collectionName);
                presets.RemoveAt(index);
            }
        }
    }

    private static void RemoveOwnedBuildPresetByName(
        JsonArray presets,
        string name,
        string configurePresetName,
        string displayName,
        JsonArray configurePresets)
    {
        int matchingBuildPresetCount = CountPresetsByName(presets, name);
        for (int index = presets.Count - 1; index >= 0; index--)
        {
            if (presets[index] is JsonObject preset
                && preset["name"] is JsonValue value
                && value.TryGetValue(out string? existingName)
                && name.Equals(existingName, StringComparison.Ordinal))
            {
                if (!HasVendorMarker(preset)
                    && !IsLegacyOwnedBuildPreset(
                        preset,
                        name,
                        configurePresetName,
                        displayName,
                        matchingBuildPresetCount,
                        configurePresets))
                {
                    ThrowUnownedPreset(name, "buildPresets");
                }

                presets.RemoveAt(index);
            }
        }
    }

    private static void EnsureOwnedPreset(
        JsonObject preset,
        string name,
        string collectionName)
    {
        if (!HasVendorMarker(preset))
        {
            ThrowUnownedPreset(name, collectionName);
        }
    }

    private static bool HasVendorMarker(JsonObject preset) =>
        preset["vendor"] is JsonObject vendor
        && vendor[VendorMarker] is JsonObject;

    private static void ThrowUnownedPreset(string name, string collectionName) =>
        throw new InvalidOperationException(
            $"CMakeUserPresets.json {collectionName} already contains preset '{name}', "
            + $"but it is not owned by AutoEnvPlus because vendor marker '{VendorMarker}' is missing.");

    private static void EnsureManagedPlanOutput(
        CMakeUserPresetsPlan plan,
        string currentContent)
    {
        if (plan.Before is null
            || plan.After is null
            || plan.PresetsExisted != (plan.Before.Length > 0)
            || !plan.Before.Equals(currentContent, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CMake preset plan does not describe the current file state.");
        }

        JsonObject after = ParseRoot(plan.After);
        JsonObject? before = currentContent.Length > 0
            ? ParseRoot(currentContent)
            : null;
        EnsureChangedGeneratedPresetsOwned(before, after, "configurePresets");
        EnsureChangedGeneratedPresetsOwned(before, after, "buildPresets");
        EnsureChangedGeneratedPresetsOwned(after, before, "configurePresets");
        EnsureChangedGeneratedPresetsOwned(
            after,
            before,
            "buildPresets",
            allowLegacyBuildPreset: true,
            legacyBuildPresetName: plan.BuildPresetName);
        JsonObject generatedConfigurePreset = EnsureSingleOwnedPreset(
            after,
            "configurePresets",
            plan.ConfigurePresetName);
        JsonObject generatedBuildPreset = EnsureSingleOwnedPreset(
            after,
            "buildPresets",
            plan.BuildPresetName);
        EnsurePlanPresetNames(plan);
        EnsureGeneratedConfigurePreset(
            generatedConfigurePreset,
            plan.ConfigurePresetName,
            plan.VisualStudioInstanceId,
            plan.ArchitecturePair);
        if (!TryGetConfigurePresetIdentity(
                plan.ConfigurePresetName,
                out string? targetDisplayName,
                out _)
            || !IsCurrentOwnedBuildPreset(
                generatedBuildPreset,
                plan.BuildPresetName,
                plan.ConfigurePresetName,
                $"Build {targetDisplayName} with AutoEnvPlus MSVC"))
        {
            throw new InvalidDataException(
                "The CMake preset plan contains an invalid managed build preset.");
        }

        EnsureWriterTransformation(
            before,
            after,
            plan.ConfigurePresetName,
            plan.BuildPresetName);
        EnsureCurrentWriterSerialization(
            plan,
            before,
            generatedConfigurePreset,
            generatedBuildPreset);
    }

    private static void EnsureManagedSnapshotOutput(CMakeUserPresetsSnapshot snapshot)
    {
        if (snapshot.After.Length == 0
            || snapshot.PresetsExisted == (snapshot.Before.Length == 0)
            || snapshot.Before.Equals(snapshot.After, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CMake preset snapshot does not describe a valid generated file state.");
        }

        JsonObject after = ParseRoot(snapshot.After);
        JsonObject? before = snapshot.PresetsExisted
            ? ParseRoot(snapshot.Before)
            : null;
        JsonObject generatedConfigurePreset = GetLastPreset(after, "configurePresets");
        JsonObject generatedBuildPreset = GetLastPreset(after, "buildPresets");
        string configurePresetName = GetRequiredPresetName(
            generatedConfigurePreset,
            "configurePresets");
        string buildPresetName = GetRequiredPresetName(
            generatedBuildPreset,
            "buildPresets");
        bool legacySnapshot = EnsureGeneratedSnapshotPresets(
            after,
            generatedConfigurePreset,
            configurePresetName,
            generatedBuildPreset,
            buildPresetName);
        EnsureWriterTransformation(
            before,
            after,
            configurePresetName,
            buildPresetName);
        EnsureChangedGeneratedPresetsOwned(before, after, "configurePresets");
        EnsureChangedGeneratedPresetsOwned(
            before,
            after,
            "buildPresets",
            allowLegacyBuildPreset: true);
        if (!legacySnapshot)
        {
            EnsureChangedGeneratedPresetsOwned(after, before, "configurePresets");
            EnsureChangedGeneratedPresetsOwned(
                after,
                before,
                "buildPresets",
                allowLegacyBuildPreset: true,
                legacyBuildPresetName: buildPresetName);
        }
    }

    private static void EnsurePlanPresetNames(CMakeUserPresetsPlan plan)
    {
        if (plan.ArchitecturePair is null
            || string.IsNullOrEmpty(plan.VisualStudioInstanceId)
            || plan.ArchitecturePair.VcVarsArgument is null)
        {
            throw new InvalidDataException(
                "The CMake preset plan has incomplete toolchain identity metadata.");
        }

        string target = ArchitectureName(plan.ArchitecturePair.TargetArchitecture);
        string host = ArchitectureName(plan.ArchitecturePair.HostArchitecture).ToLowerInvariant();
        string expectedConfigurePresetName =
            $"autoenvplus-msvc-{target.ToLowerInvariant()}-host-{host}";
        if (!expectedConfigurePresetName.Equals(
                plan.ConfigurePresetName,
                StringComparison.Ordinal)
            || !(expectedConfigurePresetName + "-build").Equals(
                plan.BuildPresetName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CMake preset plan names do not match its architecture pair.");
        }
    }

    private static void EnsureWriterTransformation(
        JsonObject? before,
        JsonObject after,
        string configurePresetName,
        string buildPresetName)
    {
        JsonObject effectiveBefore = before ?? new JsonObject();
        int expectedVersion = Math.Max(ReadVersion(effectiveBefore), 3);
        if (after["version"] is not JsonValue versionValue
            || !versionValue.TryGetValue(out int afterVersion)
            || afterVersion != expectedVersion)
        {
            throw new InvalidDataException(
                "The CMake preset output contains an unexpected version change.");
        }

        JsonObject beforeProjection = CreateUnmanagedRootProjection(effectiveBefore);
        JsonObject afterProjection = CreateUnmanagedRootProjection(after);
        if (!JsonNode.DeepEquals(beforeProjection, afterProjection))
        {
            throw new InvalidDataException(
                "The CMake preset output changes root properties not managed by AutoEnvPlus.");
        }

        EnsurePresetArrayTransformation(
            effectiveBefore,
            after,
            "configurePresets",
            configurePresetName);
        EnsurePresetArrayTransformation(
            effectiveBefore,
            after,
            "buildPresets",
            buildPresetName);
    }

    private static void EnsureCurrentWriterSerialization(
        CMakeUserPresetsPlan plan,
        JsonObject? before,
        JsonObject generatedConfigurePreset,
        JsonObject generatedBuildPreset)
    {
        JsonObject replay = before is null
            ? new JsonObject()
            : before.DeepClone().AsObject();
        replay["version"] = Math.Max(ReadVersion(replay), 3);
        JsonArray configurePresets = GetOrCreateArray(replay, "configurePresets");
        JsonArray buildPresets = GetOrCreateArray(replay, "buildPresets");
        RemovePresetsByName(buildPresets, plan.BuildPresetName);
        RemovePresetsByName(configurePresets, plan.ConfigurePresetName);
        configurePresets.Add(generatedConfigurePreset.DeepClone());
        buildPresets.Add(generatedBuildPreset.DeepClone());
        string replayedAfter = Serialize(replay, DetectNewLine(plan.Before));
        if (!replayedAfter.Equals(plan.After, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CMake preset plan output was not produced by the current writer.");
        }
    }

    private static void RemovePresetsByName(JsonArray presets, string name)
    {
        for (int index = presets.Count - 1; index >= 0; index--)
        {
            if (presets[index] is JsonObject preset
                && HasExactStringProperty(preset, "name", name))
            {
                presets.RemoveAt(index);
            }
        }
    }

    private static JsonObject CreateUnmanagedRootProjection(JsonObject root)
    {
        JsonObject projection = new();
        foreach ((string property, JsonNode? value) in root)
        {
            if (property is not ("version" or "configurePresets" or "buildPresets"))
            {
                projection[property] = value?.DeepClone();
            }
        }

        return projection;
    }

    private static void EnsurePresetArrayTransformation(
        JsonObject before,
        JsonObject after,
        string property,
        string generatedPresetName)
    {
        JsonArray beforePresets;
        if (before[property] is null)
        {
            beforePresets = [];
        }
        else if (before[property] is JsonArray existingBeforePresets)
        {
            beforePresets = existingBeforePresets;
        }
        else
        {
            throw new InvalidDataException(
                $"The CMake preset input property '{property}' is not an array.");
        }

        if (after[property] is not JsonArray afterPresets)
        {
            throw new InvalidDataException(
                $"The CMake preset output property '{property}' is not an array.");
        }

        JsonNode?[] preservedPresets = beforePresets
            .Where(node => node is not JsonObject preset
                || !HasExactStringProperty(preset, "name", generatedPresetName))
            .ToArray();
        if (afterPresets.Count != preservedPresets.Length + 1)
        {
            throw new InvalidDataException(
                $"The CMake preset output changes presets outside managed preset '{generatedPresetName}'.");
        }

        for (int index = 0; index < preservedPresets.Length; index++)
        {
            if (!JsonNode.DeepEquals(preservedPresets[index], afterPresets[index]))
            {
                throw new InvalidDataException(
                    $"The CMake preset output changes presets outside managed preset '{generatedPresetName}'.");
            }
        }

        if (afterPresets[^1] is not JsonObject generatedPreset
            || !HasExactStringProperty(generatedPreset, "name", generatedPresetName))
        {
            throw new InvalidDataException(
                $"The CMake preset output does not append managed preset '{generatedPresetName}'.");
        }
    }

    private static JsonObject GetLastPreset(JsonObject root, string property)
    {
        if (root[property] is not JsonArray presets
            || presets.Count == 0
            || presets[^1] is not JsonObject preset)
        {
            throw new InvalidDataException(
                $"The CMake preset snapshot has no generated '{property}' preset.");
        }

        return preset;
    }

    private static string GetRequiredPresetName(JsonObject preset, string property)
    {
        if (preset["name"] is not JsonValue nameValue
            || !nameValue.TryGetValue(out string? name)
            || string.IsNullOrEmpty(name))
        {
            throw new InvalidDataException(
                $"The CMake preset snapshot has an invalid '{property}' preset name.");
        }

        return name;
    }

    private static bool EnsureGeneratedSnapshotPresets(
        JsonObject after,
        JsonObject configurePreset,
        string configurePresetName,
        JsonObject buildPreset,
        string buildPresetName)
    {
        if (!TryGetConfigurePresetIdentity(
                configurePresetName,
                out string? targetDisplayName,
                out _)
            || !buildPresetName.Equals(
                configurePresetName + "-build",
                StringComparison.Ordinal)
            || !HasExactStringProperty(
                buildPreset,
                "displayName",
                $"Build {targetDisplayName} with AutoEnvPlus MSVC")
            || !HasExactStringProperty(
                buildPreset,
                "configurePreset",
                configurePresetName)
            || !HasExactStringProperty(buildPreset, "configuration", "Debug"))
        {
            throw new InvalidDataException(
                "The CMake preset snapshot does not contain a valid generated preset pair.");
        }

        EnsureGeneratedConfigurePreset(
            configurePreset,
            configurePresetName,
            expectedInstanceId: null,
            expectedPair: null);

        if (HasVendorMarker(buildPreset))
        {
            if (!IsCurrentOwnedBuildPreset(
                    buildPreset,
                    buildPresetName,
                    configurePresetName,
                    $"Build {targetDisplayName} with AutoEnvPlus MSVC"))
            {
                throw new InvalidDataException(
                    "The CMake preset snapshot contains an invalid managed build preset.");
            }

            return false;
        }

        if (after["buildPresets"] is not JsonArray buildPresets
            || after["configurePresets"] is not JsonArray configurePresets
            || !IsLegacyOwnedBuildPreset(
                buildPreset,
                buildPresetName,
                configurePresetName,
                $"Build {targetDisplayName} with AutoEnvPlus MSVC",
                CountPresetsByName(buildPresets, buildPresetName),
                configurePresets))
        {
            throw new InvalidDataException(
                "The CMake preset snapshot contains an invalid legacy build preset.");
        }

        return true;
    }

    private static void EnsureGeneratedConfigurePreset(
        JsonObject preset,
        string name,
        string? expectedInstanceId,
        CppArchitecturePair? expectedPair)
    {
        if (!TryGetConfigurePresetIdentity(
                name,
                out string? targetDisplayName,
                out string? host)
            || targetDisplayName is null
            || host is null)
        {
            throw new InvalidDataException(
                "The generated CMake configure preset has an invalid name.");
        }

        string targetArchitecture = targetDisplayName switch
        {
            "Win32" => RuntimeArchitecture.X86.ToString(),
            "x64" => RuntimeArchitecture.X64.ToString(),
            "ARM64" => RuntimeArchitecture.Arm64.ToString(),
            _ => throw new InvalidDataException(
                "The generated CMake configure preset has an invalid target architecture."),
        };
        string hostArchitecture = host switch
        {
            "win32" => RuntimeArchitecture.X86.ToString(),
            "x64" => RuntimeArchitecture.X64.ToString(),
            "arm64" => RuntimeArchitecture.Arm64.ToString(),
            _ => throw new InvalidDataException(
                "The generated CMake configure preset has an invalid host architecture."),
        };

        if (preset.Count != 9
            || !HasExactStringProperty(preset, "name", name)
            || !HasExactStringProperty(
                preset,
                "displayName",
                $"AutoEnvPlus MSVC {targetDisplayName} ({host} host)")
            || preset["description"] is not JsonValue descriptionValue
            || !descriptionValue.TryGetValue(out string? description)
            || description is null
            || !description.StartsWith("Managed by AutoEnvPlus for ", StringComparison.Ordinal)
            || preset["generator"] is not JsonValue generatorValue
            || !generatorValue.TryGetValue(out string? generator)
            || generator is not ("Visual Studio 17 2022" or "Visual Studio 16 2019")
            || !HasExactStringProperty(preset, "architecture", targetDisplayName)
            || !HasExactStringProperty(preset, "toolset", $"host={host}")
            || !HasExactStringProperty(
                preset,
                "binaryDir",
                $"${{sourceDir}}/out/build/{name}")
            || preset["cacheVariables"] is not JsonObject cacheVariables
            || cacheVariables.Count != 1
            || cacheVariables["CMAKE_GENERATOR_INSTANCE"] is not JsonValue instancePathValue
            || !instancePathValue.TryGetValue(out string? instancePath)
            || string.IsNullOrWhiteSpace(instancePath)
            || !IsCanonicalFullyQualifiedPath(instancePath)
            || preset["vendor"] is not JsonObject vendor
            || vendor.Count != 1
            || vendor[VendorMarker] is not JsonObject marker
            || marker.Count != 4
            || marker["instanceId"] is not JsonValue instanceIdValue
            || !instanceIdValue.TryGetValue(out string? instanceId)
            || instanceId is null
            || !HasExactStringProperty(marker, "hostArchitecture", hostArchitecture)
            || !HasExactStringProperty(marker, "targetArchitecture", targetArchitecture)
            || marker["vcVarsArgument"] is not JsonValue vcVarsValue
            || !vcVarsValue.TryGetValue(out string? vcVarsArgument)
            || vcVarsArgument is null
            || (expectedInstanceId is not null
                && !expectedInstanceId.Equals(instanceId, StringComparison.Ordinal))
            || (expectedPair is not null
                && (!expectedPair.HostArchitecture.ToString().Equals(
                        hostArchitecture,
                        StringComparison.Ordinal)
                    || !expectedPair.TargetArchitecture.ToString().Equals(
                        targetArchitecture,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        expectedPair.VcVarsArgument,
                        vcVarsArgument,
                        StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                "The generated CMake configure preset does not match the AutoEnvPlus template.");
        }
    }

    private static bool IsCanonicalFullyQualifiedPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path)
                && Path.GetFullPath(path).Equals(
                    path,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCurrentOwnedBuildPreset(
        JsonObject preset,
        string name,
        string configurePresetName,
        string displayName)
    {
        if (preset.Count != 5
            || !HasExactStringProperty(preset, "name", name)
            || !HasExactStringProperty(preset, "displayName", displayName)
            || !HasExactStringProperty(preset, "configurePreset", configurePresetName)
            || !HasExactStringProperty(preset, "configuration", "Debug")
            || preset["vendor"] is not JsonObject vendor
            || vendor.Count != 1
            || vendor[VendorMarker] is not JsonObject marker
            || marker.Count != 1
            || !HasExactStringProperty(marker, "configurePreset", configurePresetName))
        {
            return false;
        }

        return true;
    }

    private static void EnsureChangedGeneratedPresetsOwned(
        JsonObject? reference,
        JsonObject? changedSide,
        string property,
        bool allowLegacyBuildPreset = false,
        string? legacyBuildPresetName = null)
    {
        if (changedSide?[property] is not JsonArray presets)
        {
            return;
        }

        Dictionary<(string Name, string Json), int> unchangedCounts = [];
        if (reference?[property] is JsonArray referencePresets)
        {
            foreach (JsonObject preset in referencePresets.OfType<JsonObject>())
            {
                if (TryGetGeneratedPresetName(preset, out string? name))
                {
                    (string Name, string Json) key = (name!, preset.ToJsonString());
                    unchangedCounts[key] = unchangedCounts.GetValueOrDefault(key) + 1;
                }
            }
        }

        foreach (JsonNode? node in presets)
        {
            if (node is JsonObject preset
                && TryGetGeneratedPresetName(preset, out string? name))
            {
                (string Name, string Json) key = (name!, preset.ToJsonString());
                if (unchangedCounts.TryGetValue(key, out int remaining)
                    && remaining > 0)
                {
                    unchangedCounts[key] = remaining - 1;
                }
                else
                {
                    EnsureChangedPresetOwned(
                        changedSide,
                        preset,
                        name!,
                        property,
                        allowLegacyBuildPreset,
                        legacyBuildPresetName);
                }
            }
        }
    }

    private static void EnsureChangedPresetOwned(
        JsonObject root,
        JsonObject preset,
        string name,
        string property,
        bool allowLegacyBuildPreset,
        string? legacyBuildPresetName)
    {
        if (HasVendorMarker(preset))
        {
            return;
        }

        if (allowLegacyBuildPreset
            && property.Equals("buildPresets", StringComparison.Ordinal)
            && (legacyBuildPresetName is null
                || legacyBuildPresetName.Equals(name, StringComparison.Ordinal))
            && root["buildPresets"] is JsonArray buildPresets
            && root["configurePresets"] is JsonArray configurePresets
            && TryGetLegacyBuildIdentity(
                preset,
                name,
                out string? configurePresetName,
                out string? displayName)
            && IsLegacyOwnedBuildPreset(
                preset,
                name,
                configurePresetName!,
                displayName!,
                CountPresetsByName(buildPresets, name),
                configurePresets))
        {
            return;
        }

        ThrowUnownedPreset(name, property);
    }

    private static bool IsLegacyOwnedBuildPreset(
        JsonObject preset,
        string name,
        string configurePresetName,
        string displayName,
        int matchingBuildPresetCount,
        JsonArray configurePresets)
    {
        if (preset.Count != 4
            || !HasExactStringProperty(preset, "name", name)
            || !HasExactStringProperty(preset, "displayName", displayName)
            || !HasExactStringProperty(preset, "configurePreset", configurePresetName)
            || !HasExactStringProperty(preset, "configuration", "Debug"))
        {
            return false;
        }

        if (matchingBuildPresetCount != 1)
        {
            return false;
        }

        JsonObject[] linkedConfigurePresets = configurePresets
            .OfType<JsonObject>()
            .Where(configurePreset =>
                HasExactStringProperty(configurePreset, "name", configurePresetName))
            .ToArray();
        return linkedConfigurePresets.Length == 1
            && HasVendorMarker(linkedConfigurePresets[0]);
    }

    private static bool TryGetLegacyBuildIdentity(
        JsonObject preset,
        string buildPresetName,
        out string? configurePresetName,
        out string? displayName)
    {
        configurePresetName = null;
        displayName = null;
        if (preset["configurePreset"] is not JsonValue configureValue
            || !configureValue.TryGetValue(out configurePresetName)
            || configurePresetName is null
            || !buildPresetName.Equals(configurePresetName + "-build", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGetConfigurePresetIdentity(
                configurePresetName,
                out string? targetDisplayName,
                out _))
        {
            return false;
        }

        displayName = $"Build {targetDisplayName} with AutoEnvPlus MSVC";
        return true;
    }

    private static bool TryGetConfigurePresetIdentity(
        string configurePresetName,
        out string? targetDisplayName,
        out string? host)
    {
        targetDisplayName = null;
        host = null;
        const string prefix = "autoenvplus-msvc-";
        const string hostSeparator = "-host-";
        if (!configurePresetName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string architecturePair = configurePresetName[prefix.Length..];
        int separatorIndex = architecturePair.IndexOf(hostSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0
            || separatorIndex != architecturePair.LastIndexOf(hostSeparator, StringComparison.Ordinal))
        {
            return false;
        }

        string target = architecturePair[..separatorIndex];
        host = architecturePair[(separatorIndex + hostSeparator.Length)..];
        targetDisplayName = target switch
        {
            "win32" => "Win32",
            "x64" => "x64",
            "arm64" => "ARM64",
            _ => null,
        };
        if (targetDisplayName is null
            || host is not ("win32" or "x64" or "arm64"))
        {
            return false;
        }

        return true;
    }

    private static bool HasExactStringProperty(
        JsonObject preset,
        string property,
        string expected) => preset[property] is JsonValue value
        && value.TryGetValue(out string? actual)
        && expected.Equals(actual, StringComparison.Ordinal);

    private static int CountPresetsByName(JsonArray presets, string name) => presets
        .OfType<JsonObject>()
        .Count(preset => HasExactStringProperty(preset, "name", name));

    private static bool TryGetGeneratedPresetName(
        JsonObject preset,
        out string? name)
    {
        name = null;
        return preset["name"] is JsonValue value
            && value.TryGetValue(out name)
            && name is not null
            && name.StartsWith("autoenvplus-msvc-", StringComparison.Ordinal);
    }

    private static JsonObject EnsureSingleOwnedPreset(
        JsonObject root,
        string property,
        string name)
    {
        if (root[property] is not JsonArray presets)
        {
            throw new InvalidDataException(
                $"The CMake preset plan does not contain a '{property}' array.");
        }

        JsonObject[] matches = presets
            .OfType<JsonObject>()
            .Where(preset => preset["name"] is JsonValue value
                && value.TryGetValue(out string? existingName)
                && name.Equals(existingName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"The CMake preset plan must contain exactly one managed preset named '{name}'.");
        }

        EnsureOwnedPreset(matches[0], name, property);
        return matches[0];
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
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.ProjectRoot)).Equals(
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
        && snapshot.Id is not null
        && snapshot.ProjectRoot is not null
        && snapshot.PresetsPath is not null
        && snapshot.Before is not null
        && snapshot.After is not null
        && Guid.TryParseExact(snapshot.Id, "N", out _)
        && snapshot.Id.Equals(
            Path.GetFileNameWithoutExtension(snapshotPath),
            StringComparison.OrdinalIgnoreCase)
        && Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.ProjectRoot)).Equals(
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

    private async Task<PresetsState> ReadPresetsStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string content = await File.ReadAllTextAsync(
                _presetsPath,
                cancellationToken).ConfigureAwait(false);
            return new PresetsState(true, content);
        }
        catch (FileNotFoundException)
        {
            return new PresetsState(false, string.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            return new PresetsState(false, string.Empty);
        }
    }

    private async Task CommitPresetsAsync(
        string content,
        bool expectedExists,
        string expectedContent,
        CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(
            _projectRoot,
            $".{PresetsFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                FileEncoding,
                cancellationToken).ConfigureAwait(false);
            await InvokeBeforeFinalStateCheckAsync(cancellationToken).ConfigureAwait(false);
            PresetsState immediatelyBeforeCommit = await ReadPresetsStateAsync(
                cancellationToken).ConfigureAwait(false);
            if (!MatchesExpected(
                    immediatelyBeforeCommit,
                    expectedExists,
                    expectedContent))
            {
                throw new InvalidOperationException(
                    "CMakeUserPresets.json changed while the update was being prepared; no project file was overwritten.");
            }

            File.Move(temporary, _presetsPath, overwrite: expectedExists);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private Task InvokeBeforeFinalStateCheckAsync(CancellationToken cancellationToken) =>
        _beforeFinalStateCheck?.Invoke(cancellationToken) ?? Task.CompletedTask;

    private static bool MatchesExpected(
        PresetsState state,
        bool expectedExists,
        string expectedContent) => state.Exists == expectedExists
        && state.Content.Equals(expectedContent, StringComparison.Ordinal);

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

    private sealed record PresetsState(bool Exists, string Content);
}
