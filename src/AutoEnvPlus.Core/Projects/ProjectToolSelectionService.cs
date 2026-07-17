using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Projects;

public sealed record ProjectToolSelectionPlan(
    string ProjectRoot,
    string ManifestPath,
    bool ManifestExisted,
    bool Utf8Bom,
    string? BeforeSha256,
    string AfterSha256,
    string Before,
    string After,
    RuntimeKind Kind,
    VersionSelector Selector,
    ManagedRuntimeEntry ExpectedEntry)
{
    public bool Changed => !ManifestExisted || !Before.Equals(After, StringComparison.Ordinal);
}

public sealed record ProjectToolSelectionResult(
    bool Success,
    bool Changed,
    string ManifestPath,
    string? Error);

public sealed class ProjectToolSelectionService
{
    public const int MaximumManifestBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _managedRoot;
    private readonly string _projectRoot;
    private readonly string _manifestPath;
    private readonly ManagedRuntimeRegistry _registry;
    private readonly ManagedStateLock _runtimeTransactionLock;
    private readonly SemaphoreSlim _projectGate;

    public ProjectToolSelectionService(string managedRoot, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _managedRoot = Path.GetFullPath(managedRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
        _manifestPath = Path.Combine(_projectRoot, ProjectManifestService.ManifestFileName);
        if (!Directory.Exists(_projectRoot))
        {
            throw new DirectoryNotFoundException($"Project directory does not exist: {_projectRoot}");
        }

        EnsureProjectPathsSafe();
        _registry = new ManagedRuntimeRegistry(_managedRoot);
        _runtimeTransactionLock = ManagedStateLock.CreateRuntimeTransaction(_managedRoot);
        _projectGate = ProjectGates.GetOrAdd(_manifestPath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<ProjectToolSelectionPlan> CreatePlanAsync(
        ManagedRuntimeEntry selectedEntry,
        VersionSelector? selector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedEntry);
        selector ??= VersionSelector.Parse(selectedEntry.Version.ToString());
        if (!selector.Matches(selectedEntry.ToRuntimeInstallation()))
        {
            throw new ArgumentException(
                "The project selector must match the selected managed runtime.",
                nameof(selector));
        }

        EnsureTomlValue(selectedEntry.Id, "runtime ID");
        EnsureTomlValue(selectedEntry.ProviderId, "Provider ID");
        EnsureTomlValue(selector.ToString(), "version selector");

        using ManagedStateLock.Lease transactionLock = await _runtimeTransactionLock.AcquireAsync(
            cancellationToken).ConfigureAwait(false);
        RegistryLoadResult registry = await _registry.LoadWithinTransactionAsync(
            cancellationToken).ConfigureAwait(false);
        ManagedRuntimeEntry currentEntry = RequireCurrentEntry(registry, selectedEntry);
        ManifestSnapshot snapshot = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Exists)
        {
            ProjectManifestLoadResult current = new ProjectManifestService().Load(_manifestPath);
            if (!current.Success)
            {
                throw new InvalidDataException(
                    $"The existing project manifest must be valid before it can be updated: {FormatManifestErrors(current.Errors)}");
            }
        }

        string after = BuildUpdatedContent(
            snapshot.Content,
            currentEntry.Kind,
            selector,
            currentEntry.Id,
            currentEntry.ProviderId);
        byte[] afterBytes = Encode(after, snapshot.Utf8Bom);
        if (afterBytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The updated project manifest exceeds the {MaximumManifestBytes}-byte limit.");
        }

        return new ProjectToolSelectionPlan(
            _projectRoot,
            _manifestPath,
            snapshot.Exists,
            snapshot.Utf8Bom,
            snapshot.Sha256,
            ComputeSha256(afterBytes),
            snapshot.Content,
            after,
            currentEntry.Kind,
            selector,
            currentEntry);
    }

    public async Task<ProjectToolSelectionResult> ApplyAsync(
        ProjectToolSelectionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            EnsureAuthorizedPlan(plan);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return Failure(exception.Message);
        }

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using ManagedStateLock.Lease transactionLock = await _runtimeTransactionLock.AcquireAsync(
                cancellationToken).ConfigureAwait(false);
            RegistryLoadResult registry = await _registry.LoadWithinTransactionAsync(
                cancellationToken).ConfigureAwait(false);
            _ = RequireCurrentEntry(registry, plan.ExpectedEntry);

            ManifestSnapshot current = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
            if (!SnapshotMatches(plan, current))
            {
                return Failure(
                    $"{ProjectManifestService.ManifestFileName} changed after the preview was created; refresh and review the new content.");
            }

            if (!plan.Changed)
            {
                return new ProjectToolSelectionResult(true, false, _manifestPath, null);
            }

            byte[] afterBytes = Encode(plan.After, plan.Utf8Bom);
            string temporaryPath = Path.Combine(
                _projectRoot,
                $".{ProjectManifestService.ManifestFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                EnsureProjectPathsSafe();
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16_384,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    EnsureOrdinaryFile(temporaryPath, "temporary project manifest");
                    await stream.WriteAsync(afterBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                ManifestSnapshot immediatelyBeforeReplace = await ReadManifestAsync(
                    cancellationToken).ConfigureAwait(false);
                if (!SnapshotMatches(plan, immediatelyBeforeReplace))
                {
                    return Failure(
                        $"{ProjectManifestService.ManifestFileName} changed while the update was being prepared; no project file was overwritten.");
                }

                EnsureProjectPathsSafe();
                File.Move(temporaryPath, _manifestPath, overwrite: plan.ManifestExisted);
                EnsureOrdinaryFile(_manifestPath, "project manifest");
                return new ProjectToolSelectionResult(true, true, _manifestPath, null);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            return Failure(exception.Message);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    private void EnsureAuthorizedPlan(ProjectToolSelectionPlan plan)
    {
        if (!Path.GetFullPath(plan.ProjectRoot).Equals(
                _projectRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(plan.ManifestPath).Equals(
                _manifestPath,
                StringComparison.OrdinalIgnoreCase)
            || plan.ExpectedEntry.Kind != plan.Kind
            || !plan.Selector.Matches(plan.ExpectedEntry.ToRuntimeInstallation()))
        {
            throw new ArgumentException(
                "The project selection plan does not target this service or selected runtime.",
                nameof(plan));
        }

        string expectedAfter = BuildUpdatedContent(
            plan.Before,
            plan.Kind,
            plan.Selector,
            plan.ExpectedEntry.Id,
            plan.ExpectedEntry.ProviderId);
        if (!expectedAfter.Equals(plan.After, StringComparison.Ordinal)
            || !ComputeSha256(Encode(plan.After, plan.Utf8Bom)).Equals(
                plan.AfterSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The project selection plan content changed after it was generated.");
        }

        string? reconstructedBeforeHash = plan.ManifestExisted
            ? ComputeSha256(Encode(plan.Before, plan.Utf8Bom))
            : null;
        if (!string.Equals(
                reconstructedBeforeHash,
                plan.BeforeSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The project selection plan does not contain its original manifest content.");
        }
    }

    private async Task<ManifestSnapshot> ReadManifestAsync(CancellationToken cancellationToken)
    {
        EnsureProjectPathsSafe();
        FileAttributes? attributes = TryGetAttributes(_manifestPath);
        if (attributes is null)
        {
            return new ManifestSnapshot(false, false, null, string.Empty);
        }

        if ((attributes.Value & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The project manifest must be an ordinary file.");
        }

        await using FileStream stream = new(
            _manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The project manifest exceeds the {MaximumManifestBytes}-byte limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(
                bytes.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The project manifest changed while it was being read.");
            }

            offset += read;
        }

        bool utf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        int textOffset = utf8Bom ? Encoding.UTF8.Preamble.Length : 0;
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes, textOffset, bytes.Length - textOffset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The project manifest must use valid UTF-8.", exception);
        }

        EnsureProjectPathsSafe();
        return new ManifestSnapshot(true, utf8Bom, ComputeSha256(bytes), content);
    }

    private void EnsureProjectPathsSafe()
    {
        ManagedPathSafety.EnsureNoReparsePointInPath(_projectRoot);
        FileAttributes attributes = File.GetAttributes(_projectRoot);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The project root must be an ordinary directory.");
        }

        ManagedPathSafety.EnsureNoReparsePointInPath(_manifestPath);
    }

    private static void EnsureOrdinaryFile(string path, string description)
    {
        ManagedPathSafety.EnsureNoReparsePointInPath(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException($"The {description} must be an ordinary file.");
        }
    }

    private static ManagedRuntimeEntry RequireCurrentEntry(
        RegistryLoadResult registry,
        ManagedRuntimeEntry expected)
    {
        if (registry.Errors.Count > 0)
        {
            throw new InvalidDataException(
                "The managed runtime registry is invalid: " + string.Join("; ", registry.Errors));
        }

        ManagedRuntimeEntry[] matches = registry.Entries
            .Where(entry => entry.Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || !EntriesEquivalent(matches[0], expected))
        {
            throw new InvalidOperationException(
                "The selected managed runtime changed or was removed after it was displayed; refresh and select it again.");
        }

        return matches[0];
    }

    private static bool EntriesEquivalent(
        ManagedRuntimeEntry left,
        ManagedRuntimeEntry right) =>
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)
        && left.ProviderId.Equals(right.ProviderId, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && left.Version == right.Version
        && left.Architecture == right.Architecture
        && Path.GetFullPath(left.InstallRoot).Equals(
            Path.GetFullPath(right.InstallRoot),
            StringComparison.OrdinalIgnoreCase)
        && left.ExecutableRelativePath.Equals(
            right.ExecutableRelativePath,
            StringComparison.OrdinalIgnoreCase)
        && left.PackageHashAlgorithm == right.PackageHashAlgorithm
        && left.PackageHash.Equals(right.PackageHash, StringComparison.OrdinalIgnoreCase)
        && left.InstalledAtUtc == right.InstalledAtUtc
        && (left.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            (right.Channels ?? []).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static string BuildUpdatedContent(
        string before,
        RuntimeKind kind,
        VersionSelector selector,
        string runtimeId,
        string providerId)
    {
        EditableDocument document = EditableDocument.Parse(before);
        string toolName = ProjectManifestService.GetCanonicalToolName(kind);
        document.UpdateField(
            "tools",
            key => ProjectManifestService.TryMapToolName(key, out RuntimeKind parsed)
                && parsed == kind,
            toolName,
            selector.ToString());
        document.UpdateField(
            "tool-identities",
            key => ProjectManifestService.TryMapIdentityKey(
                    key,
                    out RuntimeKind parsed,
                    out bool isRuntimeId)
                && parsed == kind
                && isRuntimeId,
            toolName + ".runtime-id",
            runtimeId);
        document.UpdateField(
            "tool-identities",
            key => ProjectManifestService.TryMapIdentityKey(
                    key,
                    out RuntimeKind parsed,
                    out bool isRuntimeId)
                && parsed == kind
                && !isRuntimeId,
            toolName + ".provider-id",
            providerId);
        return document.ToString();
    }

    private static bool SnapshotMatches(
        ProjectToolSelectionPlan plan,
        ManifestSnapshot snapshot) =>
        snapshot.Exists == plan.ManifestExisted
        && string.Equals(snapshot.Sha256, plan.BeforeSha256, StringComparison.OrdinalIgnoreCase);

    private static byte[] Encode(string content, bool emitBom)
    {
        byte[] text = StrictUtf8.GetBytes(content);
        if (!emitBom)
        {
            return text;
        }

        byte[] preamble = Encoding.UTF8.Preamble.ToArray();
        byte[] bytes = new byte[preamble.Length + text.Length];
        preamble.CopyTo(bytes, 0);
        text.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string FormatManifestErrors(IReadOnlyList<ProjectManifestError> errors) =>
        string.Join(
            "; ",
            errors.Take(3).Select(error => error.LineNumber > 0
                ? $"line {error.LineNumber}: {error.Message}"
                : error.Message));

    private static void EnsureTomlValue(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(char.IsControl)
            || value.IndexOfAny(['"', '\'', '\\']) >= 0)
        {
            throw new InvalidDataException(
                $"The {description} cannot be represented safely in an AutoEnvPlus project manifest.");
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private ProjectToolSelectionResult Failure(string error) => new(
        false,
        false,
        _manifestPath,
        error);

    private sealed record ManifestSnapshot(
        bool Exists,
        bool Utf8Bom,
        string? Sha256,
        string Content);

    private sealed class EditableDocument
    {
        private readonly List<EditableLine> _lines;
        private readonly string _newLine;

        private EditableDocument(List<EditableLine> lines, string newLine)
        {
            _lines = lines;
            _newLine = newLine;
        }

        public static EditableDocument Parse(string content)
        {
            List<EditableLine> lines = [];
            string? firstNewLine = null;
            int start = 0;
            for (int index = 0; index < content.Length; index++)
            {
                int terminatorLength = content[index] switch
                {
                    '\r' when index + 1 < content.Length && content[index + 1] == '\n' => 2,
                    '\r' or '\n' => 1,
                    _ => 0,
                };
                if (terminatorLength == 0)
                {
                    continue;
                }

                string terminator = content.Substring(index, terminatorLength);
                firstNewLine ??= terminator;
                lines.Add(new EditableLine(content[start..index], terminator));
                index += terminatorLength - 1;
                start = index + 1;
            }

            if (start < content.Length)
            {
                lines.Add(new EditableLine(content[start..], string.Empty));
            }

            return new EditableDocument(lines, firstNewLine ?? System.Environment.NewLine);
        }

        public void UpdateField(
            string sectionName,
            Func<string, bool> keyMatches,
            string canonicalKey,
            string value)
        {
            List<SectionRange> sections = FindSections(sectionName);
            if (sections.Count == 0)
            {
                AppendSection(sectionName, canonicalKey, value);
                return;
            }

            List<int> matchingLines = [];
            foreach (SectionRange section in sections)
            {
                for (int index = section.Start; index < section.End; index++)
                {
                    if (TryGetAssignmentKey(_lines[index].Text, out string? key)
                        && keyMatches(key))
                    {
                        matchingLines.Add(index);
                    }
                }
            }

            if (matchingLines.Count > 0)
            {
                int first = matchingLines[0];
                _lines[first].Text = ReplaceAssignmentValue(_lines[first].Text, value);
                for (int index = matchingLines.Count - 1; index >= 1; index--)
                {
                    _lines.RemoveAt(matchingLines[index]);
                }

                return;
            }

            InsertAssignment(sections[0].End, canonicalKey, value);
        }

        public override string ToString()
        {
            StringBuilder output = new();
            foreach (EditableLine line in _lines)
            {
                output.Append(line.Text);
                output.Append(line.Terminator);
            }

            return output.ToString();
        }

        private List<SectionRange> FindSections(string sectionName)
        {
            List<SectionRange> result = [];
            int? matchingStart = null;
            for (int index = 0; index < _lines.Count; index++)
            {
                if (!TryGetSectionName(_lines[index].Text, out string? parsed))
                {
                    continue;
                }

                if (matchingStart is int start)
                {
                    result.Add(new SectionRange(start, index));
                    matchingStart = null;
                }

                if (parsed.Equals(sectionName, StringComparison.OrdinalIgnoreCase))
                {
                    matchingStart = index + 1;
                }
            }

            if (matchingStart is int finalStart)
            {
                result.Add(new SectionRange(finalStart, _lines.Count));
            }

            return result;
        }

        private void AppendSection(string sectionName, string key, string value)
        {
            if (_lines.Count > 0)
            {
                EnsurePreviousLineTerminated(_lines.Count);
                if (_lines[^1].Text.Length > 0)
                {
                    _lines.Add(new EditableLine(string.Empty, _newLine));
                }
            }

            _lines.Add(new EditableLine($"[{sectionName}]", _newLine));
            _lines.Add(new EditableLine($"{key} = {Quote(value)}", _newLine));
        }

        private void InsertAssignment(int index, string key, string value)
        {
            bool insertingAtEnd = index == _lines.Count;
            bool preservedFinalNewLine = insertingAtEnd
                && index > 0
                && _lines[index - 1].Terminator.Length > 0;
            EnsurePreviousLineTerminated(index);
            _lines.Insert(
                index,
                new EditableLine(
                    $"{key} = {Quote(value)}",
                    insertingAtEnd && !preservedFinalNewLine ? string.Empty : _newLine));
        }

        private void EnsurePreviousLineTerminated(int insertionIndex)
        {
            if (insertionIndex > 0 && _lines[insertionIndex - 1].Terminator.Length == 0)
            {
                _lines[insertionIndex - 1].Terminator = _newLine;
            }
        }

        private static string ReplaceAssignmentValue(string line, string value)
        {
            int equals = FindUnquoted(line, '=');
            int comment = FindUnquoted(line, '#', equals + 1);
            int contentEnd = comment >= 0 ? comment : line.Length;
            int leadingEnd = equals + 1;
            while (leadingEnd < contentEnd && char.IsWhiteSpace(line[leadingEnd]))
            {
                leadingEnd++;
            }

            int trailingStart = contentEnd;
            while (trailingStart > leadingEnd && char.IsWhiteSpace(line[trailingStart - 1]))
            {
                trailingStart--;
            }

            return line[..(equals + 1)]
                + line[(equals + 1)..leadingEnd]
                + Quote(value)
                + line[trailingStart..];
        }

        private static bool TryGetAssignmentKey(string line, out string key)
        {
            string uncommented = StripComment(line);
            int equals = FindUnquoted(uncommented, '=');
            if (equals <= 0)
            {
                key = string.Empty;
                return false;
            }

            key = uncommented[..equals].Trim();
            return key.Length > 0;
        }

        private static bool TryGetSectionName(string line, out string sectionName)
        {
            string candidate = StripComment(line).Trim();
            if (candidate.Length < 3 || candidate[0] != '[' || candidate[^1] != ']')
            {
                sectionName = string.Empty;
                return false;
            }

            sectionName = candidate[1..^1].Trim();
            return sectionName.Length > 0;
        }

        private static string StripComment(string line)
        {
            int comment = FindUnquoted(line, '#');
            return comment < 0 ? line : line[..comment];
        }

        private static int FindUnquoted(string line, char target, int start = 0)
        {
            char quote = '\0';
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character is '"' or '\'')
                {
                    quote = quote == character ? '\0' : quote == '\0' ? character : quote;
                }
                else if (index >= start && character == target && quote == '\0')
                {
                    return index;
                }
            }

            return -1;
        }

        private static string Quote(string value) => $"\"{value}\"";

        private sealed class EditableLine(string text, string terminator)
        {
            public string Text { get; set; } = text;

            public string Terminator { get; set; } = terminator;
        }

        private sealed record SectionRange(int Start, int End);
    }
}
