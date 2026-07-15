using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutoEnvPlus.Core.Activity;

public sealed class ActivityLogStore
{
    public const int DefaultMaxEntries = 1_000;
    public const long DefaultMaxBytes = 2 * 1024 * 1024;
    public const int MaxSummaryLength = 1_024;
    public const int MaxPathLength = 2_048;
    public const int MaxAffectedPathCount = 32;
    public const int MaxLineBytes = 64 * 1024;

    private const int CurrentSchemaVersion = 1;
    private const int MinimumMaxEntries = 1;
    private const long MinimumMaxBytes = 256;
    private const int InterprocessLockTimeoutMilliseconds = 5_000;
    private const int InterprocessLockRetryMilliseconds = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private static readonly Regex SensitiveAssignmentRegex = new(
        """(?<name>(?:"|')?\b(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|api[_-]?key|authorization|credential|client[_-]?secret|private[_-]?key|secret[_-]?key|(?:https?|all)[_-]?proxy|proxy(?:[_-]?(?:user|username|password|pass))?|pfx(?:[_-]?(?:password|pass|secret))?)\b(?:"|')?)\s*[:=]\s*(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\s]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveCommandArgumentRegex = new(
        """(?<name>--?(?:password|passwd|pwd|token|access[-_]token|refresh[-_]token|api[-_]key|authorization|credential|client[-_]secret|private[-_]key|secret[-_]key|proxy[-_]?(?:password|pass|user|username)|pfx[-_]?(?:password|pass|secret)))\s+(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\S+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BearerTokenRegex = new(
        @"\bBearer\s+\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationHeaderRegex = new(
        @"(?<name>\b(?:proxy[-_ ]?)?authorization\b\s*[:=]\s*)(?:basic|bearer|digest|negotiate|ntlm)\s+\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UriCredentialRegex = new(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)[^/@\s:]+:[^/@\s]+@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveQueryRegex = new(
        @"(?<prefix>[?&](?:token|access[_-]?token|refresh[_-]?token|password|passwd|pwd|secret|secret[_-]?key|api[_-]?key|credential|client[_-]?secret|private[_-]?key|sig|signature|x[-_]amz[-_]?(?:signature|credential|security[-_]token)|x[-_]goog[-_]?(?:signature|credential))=)[^&#\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly string _managedRoot;
    private readonly string _logPath;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate;

    public ActivityLogStore(
        string managedRoot,
        string? logPath = null,
        int maxEntries = DefaultMaxEntries,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        if (!Path.IsPathRooted(managedRoot))
        {
            throw new ArgumentException("The managed root must be an absolute path.", nameof(managedRoot));
        }

        if (maxEntries < MinimumMaxEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        if (maxBytes < MinimumMaxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        _managedRoot = Path.GetFullPath(managedRoot);
        _logPath = Path.GetFullPath(
            logPath ?? Path.Combine(_managedRoot, "state", "activity.jsonl"));
        EnsureChildPath(_managedRoot, _logPath, "activity log");
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _gate = Gates.GetOrAdd(_logPath, static _ => new SemaphoreSlim(1, 1));
    }

    public string ManagedRoot => _managedRoot;

    public string LogPath => _logPath;

    public async Task<ActivityLogEntry> AppendAsync(
        ActivityOperationType operationType,
        ActivityStatus status,
        string summary,
        IEnumerable<string>? affectedPaths = null,
        string? snapshotPath = null,
        string? rollbackPath = null,
        DateTimeOffset? timestampUtc = null,
        CancellationToken cancellationToken = default)
    {
        ActivityLogEntry entry = CreateEntry(
            operationType,
            status,
            summary,
            affectedPaths,
            snapshotPath,
            rollbackPath,
            timestampUtc);
        await AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<ActivityLogEntry> AppendAsync(
        ActivityLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ActivityLogEntry normalized = NormalizeEntry(entry);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSafeLogLocation(createDirectory: true);
            using InterprocessLock interprocessLock = await AcquireInterprocessLockAsync(
                cancellationToken).ConfigureAwait(false);
            EnsureSafeLogLocation(createDirectory: true);
            ActivityLogLoadResult existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Errors.Count > 0)
            {
                throw new InvalidDataException(
                    "The activity log contains invalid records and cannot be rewritten safely.");
            }

            List<ActivityLogEntry> entries = existing.Entries.ToList();
            entries.Add(normalized);
            entries = TrimToLimits(entries);
            if (!entries.Any(item => item.Id == normalized.Id))
            {
                throw new InvalidOperationException(
                    "The activity record exceeds the configured activity log size limit.");
            }

            await SaveCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActivityLogLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                EnsureSafeLogLocation(createDirectory: false);
                if (!File.Exists(_logPath))
                {
                    return new ActivityLogLoadResult([], []);
                }

                using InterprocessLock interprocessLock = await AcquireInterprocessLockAsync(
                    cancellationToken).ConfigureAwait(false);
                EnsureSafeLogLocation(createDirectory: false);
                ActivityLogLoadResult loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                return new ActivityLogLoadResult(
                    loaded.Entries
                        .OrderByDescending(entry => entry.TimestampUtc)
                        .ThenByDescending(entry => entry.Id)
                        .ToArray(),
                    loaded.Errors);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                string message = exception is ActivityLogSizeException
                    ? "Activity log exceeds the configured size limit."
                    : "Activity log is unavailable.";
                return new ActivityLogLoadResult(
                    [],
                    [message]);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static ActivityLogEntry CreateEntry(
        ActivityOperationType operationType,
        ActivityStatus status,
        string summary,
        IEnumerable<string>? affectedPaths = null,
        string? snapshotPath = null,
        string? rollbackPath = null,
        DateTimeOffset? timestampUtc = null)
    {
        return NormalizeEntry(new ActivityLogEntry(
            Guid.NewGuid(),
            timestampUtc ?? DateTimeOffset.UtcNow,
            operationType,
            status,
            summary,
            affectedPaths?.ToArray() ?? [],
            snapshotPath,
            rollbackPath));
    }

    private async Task<ActivityLogLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_logPath))
        {
            return new ActivityLogLoadResult([], []);
        }

        FileInfo metadata = new(_logPath);
        if (metadata.Length > _maxBytes)
        {
            throw new ActivityLogSizeException();
        }

        List<ActivityLogEntry> entries = [];
        List<string> errors = [];
        await using FileStream stream = new(
            _logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > _maxBytes)
        {
            throw new ActivityLogSizeException();
        }
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int lineNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lineNumber++;
            if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
            {
                AddReadError(errors, lineNumber, "line is too large");
                continue;
            }

            if (!TryDeserialize(line, out ActivityLogEntry? entry, out string? error))
            {
                AddReadError(errors, lineNumber, error ?? "invalid record");
                continue;
            }

            entries.Add(entry!);
            if (entries.Count > _maxEntries * 2)
            {
                entries = TrimToLimits(entries);
            }
        }

        return new ActivityLogLoadResult(TrimToLimits(entries), errors);
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<ActivityLogEntry> entries,
        CancellationToken cancellationToken)
    {
        EnsureSafeLogLocation(createDirectory: true);
        string directory = Path.GetDirectoryName(_logPath)!;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_logPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (StreamWriter writer = new(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                16_384,
                leaveOpen: true))
            {
                writer.NewLine = "\n";
                long bytes = 0;
                foreach (ActivityLogEntry entry in entries)
                {
                    string line = JsonSerializer.Serialize(ToDocument(entry), JsonOptions);
                    long lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                    if (lineBytes > MaxLineBytes || bytes + lineBytes > _maxBytes)
                    {
                        throw new InvalidOperationException(
                            "The activity record exceeds the configured activity log size limit.");
                    }

                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    bytes += lineBytes;
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureSafeLogLocation(createDirectory: true);
            File.Move(temporaryPath, _logPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private List<ActivityLogEntry> TrimToLimits(IEnumerable<ActivityLogEntry> source)
    {
        List<ActivityLogEntry> entries = source.ToList();
        while (entries.Count > _maxEntries)
        {
            entries.RemoveAt(0);
        }

        long bytes = entries.Sum(GetSerializedByteCount);
        while (entries.Count > 0 && bytes > _maxBytes)
        {
            bytes -= GetSerializedByteCount(entries[0]);
            entries.RemoveAt(0);
        }

        return entries;
    }

    private static long GetSerializedByteCount(ActivityLogEntry entry)
    {
        string line = JsonSerializer.Serialize(ToDocument(entry), JsonOptions);
        return Encoding.UTF8.GetByteCount(line) + 1;
    }

    private static ActivityLogEntry NormalizeEntry(ActivityLogEntry entry)
    {
        if (entry.Id == Guid.Empty)
        {
            throw new ArgumentException("Activity record ID cannot be empty.", nameof(entry));
        }

        if (!Enum.IsDefined(entry.OperationType))
        {
            throw new ArgumentException("Activity operation type is invalid.", nameof(entry));
        }

        if (!Enum.IsDefined(entry.Status))
        {
            throw new ArgumentException("Activity status is invalid.", nameof(entry));
        }

        string normalizedSummary = NormalizeText(entry.Summary, MaxSummaryLength, "summary");
        string[] paths = (entry.AffectedPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path, "affected path"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length > MaxAffectedPathCount)
        {
            throw new ArgumentException(
                $"An activity record may contain at most {MaxAffectedPathCount} affected paths.",
                nameof(entry));
        }

        string? snapshotPath = NormalizeOptionalPath(entry.SnapshotPath, "snapshot path");
        string? rollbackPath = NormalizeOptionalPath(entry.RollbackPath, "rollback path");
        return entry with
        {
            TimestampUtc = entry.TimestampUtc.ToUniversalTime(),
            Summary = normalizedSummary,
            AffectedPaths = paths,
            SnapshotPath = snapshotPath,
            RollbackPath = rollbackPath,
        };
    }

    private static string NormalizeText(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Activity {fieldName} cannot be empty.", fieldName);
        }

        string sanitized = RedactSensitiveText(value.Trim());
        if (sanitized.Length > maxLength)
        {
            throw new ArgumentException(
                $"Activity {fieldName} exceeds the {maxLength}-character limit.",
                fieldName);
        }

        if (sanitized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Activity {fieldName} contains control characters.",
                fieldName);
        }

        return sanitized;
    }

    private static string? NormalizeOptionalPath(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizePath(value, fieldName);

    private static string NormalizePath(string value, string fieldName)
    {
        string sanitized = NormalizeText(value, MaxPathLength, fieldName);
        if (Uri.TryCreate(sanitized, UriKind.Absolute, out Uri? uri)
            && !uri.IsFile)
        {
            throw new ArgumentException(
                $"Activity {fieldName} must be a local path, not a URI.",
                fieldName);
        }

        if (!Path.IsPathRooted(sanitized))
        {
            throw new ArgumentException(
                $"Activity {fieldName} must be an absolute path.",
                fieldName);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sanitized);
            EnsureNoReparsePoints(fullPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new ArgumentException(
                $"Activity {fieldName} points through an unavailable or linked path.",
                fieldName,
                exception);
        }

        return fullPath;
    }

    private static string RedactSensitiveText(string value)
    {
        string sanitized = AuthorizationHeaderRegex.Replace(
            value,
            static match => $"{match.Groups["name"].Value}<redacted>");
        sanitized = SensitiveAssignmentRegex.Replace(
            sanitized,
            static match => $"{match.Groups["name"].Value}=<redacted>");
        sanitized = SensitiveCommandArgumentRegex.Replace(
            sanitized,
            static match => $"{match.Groups["name"].Value} <redacted>");
        sanitized = BearerTokenRegex.Replace(sanitized, "Bearer <redacted>");
        sanitized = UriCredentialRegex.Replace(
            sanitized,
            static match => $"{match.Groups["scheme"].Value}<redacted>@");
        return SensitiveQueryRegex.Replace(
            sanitized,
            static match => $"{match.Groups["prefix"].Value}<redacted>");
    }

    private static bool TryDeserialize(
        string line,
        out ActivityLogEntry? entry,
        out string? error)
    {
        entry = null;
        error = null;
        ActivityLogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ActivityLogDocument>(line, JsonOptions);
        }
        catch (JsonException)
        {
            error = "invalid JSON";
            return false;
        }

        if (document is null || document.SchemaVersion != CurrentSchemaVersion)
        {
            error = "unsupported schema";
            return false;
        }

        if (!Guid.TryParse(document.Id, out Guid id)
            || !Enum.TryParse(document.OperationType, ignoreCase: true, out ActivityOperationType operationType)
            || !Enum.IsDefined(operationType)
            || !Enum.TryParse(document.Status, ignoreCase: true, out ActivityStatus status)
            || !Enum.IsDefined(status)
            || string.IsNullOrWhiteSpace(document.Summary))
        {
            error = "missing or invalid fields";
            return false;
        }

        try
        {
            entry = NormalizeEntry(new ActivityLogEntry(
                id,
                document.TimestampUtc,
                operationType,
                status,
                document.Summary,
                document.AffectedPaths ?? [],
                document.SnapshotPath,
                document.RollbackPath));
            return true;
        }
        catch (ArgumentException)
        {
            error = "unsafe or invalid fields";
            return false;
        }
    }

    private static ActivityLogDocument ToDocument(ActivityLogEntry entry) => new(
        CurrentSchemaVersion,
        entry.Id.ToString("D", CultureInfo.InvariantCulture),
        entry.TimestampUtc,
        entry.OperationType.ToString(),
        entry.Status.ToString(),
        entry.Summary,
        entry.AffectedPaths.ToList(),
        entry.SnapshotPath,
        entry.RollbackPath);

    private void EnsureSafeLogLocation(bool createDirectory)
    {
        EnsureNoReparsePoints(_managedRoot);
        string directory = Path.GetDirectoryName(_logPath)!;
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
        }

        EnsureNoReparsePoints(directory);
        if (TryGetAttributes(_logPath, out FileAttributes attributes)
            && attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The activity log cannot be a reparse point.");
        }
    }

    private async Task<InterprocessLock> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken)
    {
        string lockPath = _logPath + ".lock";
        EnsureChildPath(_managedRoot, lockPath, "activity log lock");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            InterprocessLockTimeoutMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(Path.GetDirectoryName(lockPath)!);
            if (TryGetAttributes(lockPath, out FileAttributes attributes)
                && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The activity log lock cannot be a reparse point.");
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                if (stream.Length == 0)
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }

                return new InterprocessLock(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                stream?.Dispose();
                await Task.Delay(InterprocessLockRetryMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    private static void EnsureChildPath(string root, string candidate, string description)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {description} must remain inside the managed root.",
                nameof(candidate));
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The path has no filesystem root.");
        string current = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (current.Length == 0)
        {
            current = root;
        }

        if (TryGetAttributes(root, out FileAttributes rootAttributes)
            && rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The path traverses a reparse point.");
        }

        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out FileAttributes attributes))
            {
                break;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The path traverses a reparse point.");
            }
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void AddReadError(ICollection<string> errors, int lineNumber, string reason)
    {
        if (errors.Count < 128)
        {
            errors.Add($"Skipped activity record at line {lineNumber}: {reason}.");
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ActivityLogSizeException : IOException
    {
    }

    private sealed class InterprocessLock : IDisposable
    {
        private readonly FileStream _stream;

        public InterprocessLock(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    private sealed record ActivityLogDocument(
        int SchemaVersion,
        string? Id,
        DateTimeOffset TimestampUtc,
        string? OperationType,
        string? Status,
        string? Summary,
        List<string>? AffectedPaths,
        string? SnapshotPath,
        string? RollbackPath);
}
