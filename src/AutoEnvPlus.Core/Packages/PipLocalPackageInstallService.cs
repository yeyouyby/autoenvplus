using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AutoEnvPlus.Core.Downloads;
using AutoEnvPlus.Core.Networking;
using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Packages;

public sealed class PipLocalPackageInstallService
{
    private const int MaximumEnvironmentNameLength = 64;
    private const int FileBufferSize = 81_920;
    private static readonly Regex EnvironmentNamePattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] PipNetworkEnvironmentNames =
    [
        "HTTP_PROXY",
        "http_proxy",
        "HTTPS_PROXY",
        "https_proxy",
        "NO_PROXY",
        "no_proxy",
        "ALL_PROXY",
        "all_proxy",
        "PIP_INDEX_URL",
        "PIP_EXTRA_INDEX_URL",
        "PIP_FIND_LINKS",
        "PIP_NO_INDEX",
        "PIP_PROXY",
        "PIP_TRUSTED_HOST",
    ];

    private readonly string _managedRootWithSeparator;
    private readonly IPipLocalPackageProcessRunner _processRunner;

    public PipLocalPackageInstallService(
        string managedRoot,
        IPipLocalPackageProcessRunner? processRunner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ManagedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
        if (string.IsNullOrWhiteSpace(ManagedRoot))
        {
            throw new ArgumentException("The managed root is invalid.", nameof(managedRoot));
        }

        _managedRootWithSeparator = Path.EndsInDirectorySeparator(ManagedRoot)
            ? ManagedRoot
            : ManagedRoot + Path.DirectorySeparatorChar;
        LibraryRoot = Path.Combine(ManagedRoot, "downloads", "library");
        EnvironmentsRoot = Path.Combine(ManagedRoot, "environments", "python");
        TemporaryRoot = Path.Combine(ManagedRoot, "temporary", "pip");
        PipCacheRoot = Path.Combine(ManagedRoot, "caches", "pip");
        _processRunner = processRunner ?? new PipLocalPackageProcessRunner();
    }

    public string ManagedRoot { get; }

    public string LibraryRoot { get; }

    public string EnvironmentsRoot { get; }

    public string TemporaryRoot { get; }

    public string PipCacheRoot { get; }

    public async Task<PipLocalPackageInstallPlan> CreatePlanAsync(
        PipLocalPackageInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvironmentName(request.EnvironmentName);
        if (!Enum.IsDefined(request.SourceMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.SourceMode,
                "The pip package source mode is unsupported.");
        }

        EnsureManagedRootIsUsable();
        ManagedRuntimeEntry runtime = CloneAndValidateRuntime(request.Runtime);
        EffectiveNetworkSettings? networkSettings = CloneAndValidateNetworkSettings(
            request.NetworkSettings);
        PipLocalPackageFileSnapshot runtimeSnapshot = await CaptureRegularFileSnapshotAsync(
            runtime.ExecutablePath,
            "managed Python executable",
            cancellationToken).ConfigureAwait(false);
        string wheelPath = ValidateWheelPath(request.WheelPath);
        PipLocalPackageFileSnapshot wheelSnapshot = await CaptureRegularFileSnapshotAsync(
            wheelPath,
            "managed wheel",
            cancellationToken).ConfigureAwait(false);
        ManagedDownloadRecordedIdentity? recordedIdentity = new ManagedDownloadLibrary(LibraryRoot)
            .GetRecordedIdentity(Path.GetFileName(wheelPath));
        if (recordedIdentity is not null
            && (recordedIdentity.SizeBytes != wheelSnapshot.Length
                || !recordedIdentity.ContentSha256.Equals(
                    wheelSnapshot.Sha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The wheel bytes no longer match the download-library identity recorded at import or download.");
        }

        string environmentRoot = Path.Combine(EnvironmentsRoot, request.EnvironmentName);
        EnsureStrictChildPath(EnvironmentsRoot, environmentRoot, "Python environment root");
        EnsurePathHasNoReparsePoints(environmentRoot, allowMissingTail: true);
        string environmentExecutable = Path.Combine(environmentRoot, "Scripts", "python.exe");
        EnsureStrictChildPath(environmentRoot, environmentExecutable, "virtual environment executable");

        bool environmentExists = TryGetAttributes(environmentRoot, out FileAttributes environmentAttributes);
        PipLocalPackageFileSnapshot? environmentSnapshot = null;
        if (environmentExists)
        {
            if ((environmentAttributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    "The selected virtual environment path is occupied by a file.");
            }

            environmentSnapshot = await CaptureRegularFileSnapshotAsync(
                environmentExecutable,
                "virtual environment Python executable",
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyDictionary<string, string?> environment = BuildEnvironment(
            networkSettings,
            request.SourceMode);
        PipLocalPackageInstallCommand createCommand = new(
            runtime.ExecutablePath,
            AsReadOnly(["-m", "venv", environmentRoot]),
            ManagedRoot,
            environment);
        List<string> installArguments = ["-m", "pip", "install"];
        if (request.SourceMode == PipPackageSourceMode.OfflineManagedLibrary)
        {
            installArguments.Add("--no-index");
            installArguments.Add("--find-links");
            installArguments.Add(LibraryRoot);
            installArguments.Add("--no-deps");
        }

        installArguments.Add(wheelPath);
        PipLocalPackageInstallCommand installCommand = new(
            environmentExecutable,
            AsReadOnly(installArguments),
            ManagedRoot,
            environment);
        PipLocalPackageInstallPlan plan = new(
            ManagedRoot,
            LibraryRoot,
            request.EnvironmentName,
            environmentRoot,
            wheelPath,
            request.SourceMode,
            runtime,
            networkSettings,
            !environmentExists,
            runtimeSnapshot,
            wheelSnapshot,
            environmentSnapshot,
            createCommand,
            installCommand,
            string.Empty);
        return plan with { IntegritySha256 = ComputePlanIntegritySha256(plan) };
    }

    public async Task<PipLocalPackageInstallResult> ExecuteAsync(
        PipLocalPackageInstallPlan plan,
        IProgress<PipLocalPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<PipLocalPackageInstallStageResult> stages = [];
        bool environmentCreated = false;
        PipLocalPackageInstallStage currentStage = PipLocalPackageInstallStage.Validating;
        PipLocalPackageInstallPlan expected;
        try
        {
            progress?.Report(new PipLocalPackageInstallProgress(
                currentStage,
                "Revalidating the reviewed installation plan."));
            expected = await RevalidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            EnsureOperationalDirectory(TemporaryRoot, "pip temporary directory");
            EnsureOperationalDirectory(PipCacheRoot, "pip cache directory");
            stages.Add(new PipLocalPackageInstallStageResult(
                currentStage,
                PipLocalPackageInstallStageStatus.Succeeded));

            currentStage = PipLocalPackageInstallStage.CreatingEnvironment;
            if (expected.RequiresEnvironmentCreation)
            {
                progress?.Report(new PipLocalPackageInstallProgress(
                    currentStage,
                    "Creating the managed Python virtual environment."));
                PipLocalPackageProcessResult createResult = await _processRunner.RunAsync(
                    expected.CreateEnvironmentCommand,
                    cancellationToken).ConfigureAwait(false);
                if (!createResult.Success)
                {
                    string error = DescribeProcessFailure(
                        "Python virtual environment creation failed",
                        createResult);
                    stages.Add(ProcessStageResult(currentStage, createResult, error));
                    return Failure(
                        environmentCreated,
                        currentStage,
                        stages,
                        error);
                }

                environmentCreated = true;
                stages.Add(ProcessStageResult(currentStage, createResult, error: null));
            }
            else
            {
                stages.Add(new PipLocalPackageInstallStageResult(
                    currentStage,
                    PipLocalPackageInstallStageStatus.Skipped));
            }

            currentStage = PipLocalPackageInstallStage.InstallingPackage;
            await RevalidateInputsBeforeInstallAsync(expected, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new PipLocalPackageInstallProgress(
                currentStage,
                "Installing the reviewed wheel into the managed environment."));
            PipLocalPackageProcessResult installResult = await _processRunner.RunAsync(
                expected.InstallPackageCommand,
                cancellationToken).ConfigureAwait(false);
            if (!installResult.Success)
            {
                string error = DescribeProcessFailure("pip installation failed", installResult);
                stages.Add(ProcessStageResult(currentStage, installResult, error));
                return Failure(
                    environmentCreated,
                    currentStage,
                    stages,
                    error);
            }

            stages.Add(ProcessStageResult(currentStage, installResult, error: null));
            currentStage = PipLocalPackageInstallStage.Completed;
            stages.Add(new PipLocalPackageInstallStageResult(
                currentStage,
                PipLocalPackageInstallStageStatus.Succeeded));
            progress?.Report(new PipLocalPackageInstallProgress(
                currentStage,
                "The local wheel installation completed."));
            return new PipLocalPackageInstallResult(
                true,
                environmentCreated,
                true,
                currentStage,
                Array.AsReadOnly(stages.ToArray()),
                PipLocalPackageRollbackBehavior.NotTransactional,
                null);
        }
        catch (OperationCanceledException)
        {
            stages.Add(new PipLocalPackageInstallStageResult(
                currentStage,
                PipLocalPackageInstallStageStatus.Cancelled,
                Error: "The local wheel installation was cancelled."));
            progress?.Report(new PipLocalPackageInstallProgress(
                PipLocalPackageInstallStage.Cancelled,
                "The local wheel installation was cancelled."));
            return new PipLocalPackageInstallResult(
                false,
                environmentCreated,
                false,
                PipLocalPackageInstallStage.Cancelled,
                Array.AsReadOnly(stages.ToArray()),
                PipLocalPackageRollbackBehavior.NotTransactional,
                "The operation was cancelled. No transactional rollback was attempted.");
        }
        catch (Exception exception) when (IsValidationOrIoFailure(exception))
        {
            stages.Add(new PipLocalPackageInstallStageResult(
                currentStage,
                PipLocalPackageInstallStageStatus.Failed,
                Error: exception.Message));
            return Failure(
                environmentCreated,
                currentStage,
                stages,
                exception.Message);
        }
    }

    private async Task<PipLocalPackageInstallPlan> RevalidatePlanAsync(
        PipLocalPackageInstallPlan plan,
        CancellationToken cancellationToken)
    {
        if (!PathEquals(plan.ManagedRoot, ManagedRoot)
            || !PathEquals(plan.LibraryRoot, LibraryRoot))
        {
            throw new InvalidDataException(
                "The installation plan belongs to a different managed root or download library.");
        }

        string actualIntegrity = ComputePlanIntegritySha256(plan);
        if (!HashesEqual(actualIntegrity, plan.IntegritySha256))
        {
            throw new InvalidDataException("The reviewed pip installation plan was modified.");
        }

        PipLocalPackageInstallPlan expected = await CreatePlanAsync(
            new PipLocalPackageInstallRequest(
                plan.Runtime,
                plan.WheelPath,
                plan.EnvironmentName,
                plan.SourceMode,
                plan.NetworkSettings),
            cancellationToken).ConfigureAwait(false);
        if (!HashesEqual(expected.IntegritySha256, plan.IntegritySha256))
        {
            throw new InvalidDataException(
                "The runtime, wheel, environment, or reviewed pip command changed after planning.");
        }

        return expected;
    }

    private async Task RevalidateInputsBeforeInstallAsync(
        PipLocalPackageInstallPlan plan,
        CancellationToken cancellationToken)
    {
        PipLocalPackageFileSnapshot runtimeSnapshot = await CaptureRegularFileSnapshotAsync(
            plan.Runtime.ExecutablePath,
            "managed Python executable",
            cancellationToken).ConfigureAwait(false);
        EnsureSnapshotMatches(
            plan.RuntimeExecutableSnapshot,
            runtimeSnapshot,
            "The managed Python executable changed after planning.");
        PipLocalPackageFileSnapshot wheelSnapshot = await CaptureRegularFileSnapshotAsync(
            plan.WheelPath,
            "managed wheel",
            cancellationToken).ConfigureAwait(false);
        EnsureSnapshotMatches(
            plan.WheelSnapshot,
            wheelSnapshot,
            "The wheel changed after planning.");

        PipLocalPackageFileSnapshot environmentSnapshot = await CaptureRegularFileSnapshotAsync(
            plan.InstallPackageCommand.ExecutablePath,
            "virtual environment Python executable",
            cancellationToken).ConfigureAwait(false);
        if (!plan.RequiresEnvironmentCreation)
        {
            EnsureSnapshotMatches(
                plan.EnvironmentExecutableSnapshot!,
                environmentSnapshot,
                "The virtual environment Python executable changed after planning.");
        }
    }

    private ManagedRuntimeEntry CloneAndValidateRuntime(ManagedRuntimeEntry runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Kind != RuntimeKind.Python)
        {
            throw new ArgumentException(
                "A local wheel can only be installed with a managed Python runtime.",
                nameof(runtime));
        }

        string installRoot = Path.GetFullPath(runtime.InstallRoot);
        EnsureStrictChildPath(ManagedRoot, installRoot, "managed Python install root");
        if (Path.IsPathRooted(runtime.ExecutableRelativePath)
            || HasTraversalSegment(runtime.ExecutableRelativePath))
        {
            throw new ArgumentException(
                "The managed Python executable must use a safe relative path.",
                nameof(runtime));
        }

        string executablePath = Path.GetFullPath(Path.Combine(
            installRoot,
            runtime.ExecutableRelativePath));
        EnsureStrictChildPath(installRoot, executablePath, "managed Python executable");
        EnsureStrictChildPath(ManagedRoot, executablePath, "managed Python executable");
        EnsurePathHasNoReparsePoints(executablePath, allowMissingTail: false);

        IReadOnlyCollection<string>? channels = runtime.Channels is null
            ? null
            : Array.AsReadOnly(runtime.Channels.ToArray());
        return runtime with
        {
            InstallRoot = installRoot,
            Channels = channels,
        };
    }

    private string ValidateWheelPath(string wheelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wheelPath);
        string fullPath = Path.GetFullPath(wheelPath);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !PathEquals(parent, LibraryRoot))
        {
            throw new ArgumentException(
                "The wheel must be a top-level file in the managed downloads library.",
                nameof(wheelPath));
        }

        if (!Path.GetExtension(fullPath).Equals(".whl", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .whl files can be installed by pip.", nameof(wheelPath));
        }

        EnsurePathHasNoReparsePoints(fullPath, allowMissingTail: false);
        return fullPath;
    }

    private EffectiveNetworkSettings? CloneAndValidateNetworkSettings(
        EffectiveNetworkSettings? settings)
    {
        if (settings is null)
        {
            return null;
        }

        if (!settings.ToolId.Equals(NetworkToolIds.Pip, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Effective network settings for local wheel installation must target pip.",
                nameof(settings));
        }

        ValidateEndpoint(settings.HttpProxy, "HTTP proxy", requireHttps: false);
        ValidateEndpoint(settings.HttpsProxy, "HTTPS proxy", requireHttps: false);
        ValidateEndpoint(settings.Mirror, "pip mirror", requireHttps: true);
        if (settings.NoProxy is null)
        {
            throw new ArgumentException("The effective no-proxy list cannot be null.", nameof(settings));
        }

        string[] noProxy = settings.NoProxy.ToArray();
        foreach (string entry in noProxy)
        {
            if (string.IsNullOrWhiteSpace(entry)
                || entry.Any(character => char.IsControl(character)))
            {
                throw new ArgumentException(
                    "The effective no-proxy list contains an invalid entry.",
                    nameof(settings));
            }
        }

        return new EffectiveNetworkSettings(
            NetworkToolIds.Pip,
            CloneUri(settings.HttpProxy),
            CloneUri(settings.HttpsProxy),
            Array.AsReadOnly(noProxy),
            CloneUri(settings.Mirror));
    }

    private IReadOnlyDictionary<string, string?> BuildEnvironment(
        EffectiveNetworkSettings? networkSettings,
        PipPackageSourceMode sourceMode)
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = TemporaryRoot,
            ["TMP"] = TemporaryRoot,
            ["PIP_CACHE_DIR"] = PipCacheRoot,
            ["PIP_CONFIG_FILE"] = "NUL",
            ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
            ["PIP_NO_INPUT"] = "1",
        };

        // A null effective setting means "use the reviewed direct/default pip path", not
        // "inherit whatever proxy or index happened to launch AutoEnvPlus".  Keep explicit
        // removals in the process plan so both offline and configured-network operations are
        // deterministic and ALL_PROXY cannot bypass the modeled HTTP(S) policy.
        foreach (string name in PipNetworkEnvironmentNames)
        {
            environment[name] = null;
        }

        if (networkSettings is not null)
        {
            ToolNetworkEnvironment.Apply(environment, "pip", networkSettings);
            foreach (string name in PipNetworkEnvironmentNames)
            {
                environment.TryAdd(name, null);
            }
        }

        if (sourceMode == PipPackageSourceMode.OfflineManagedLibrary)
        {
            environment["PIP_NO_INDEX"] = "1";
            environment["PIP_FIND_LINKS"] = LibraryRoot;
        }

        return new ReadOnlyDictionary<string, string?>(environment);
    }

    private async Task<PipLocalPackageFileSnapshot> CaptureRegularFileSnapshotAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        EnsureStrictChildPath(ManagedRoot, fullPath, description);
        EnsurePathHasNoReparsePoints(fullPath, allowMissingTail: false);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"The {description} was not found.", fullPath);
        }

        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {description} must be a regular file and cannot be a reparse point.");
        }

        FileInfo before = new(fullPath);
        before.Refresh();
        long length = before.Length;
        long lastWriteTicks = before.LastWriteTimeUtc.Ticks;
        byte[] hash;
        await using (FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length != length)
            {
                throw new IOException($"The {description} changed while it was being reviewed.");
            }

            hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        FileInfo after = new(fullPath);
        after.Refresh();
        if (after.Length != length || after.LastWriteTimeUtc.Ticks != lastWriteTicks)
        {
            throw new IOException($"The {description} changed while it was being reviewed.");
        }

        return new PipLocalPackageFileSnapshot(
            fullPath,
            length,
            lastWriteTicks,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private void EnsureManagedRootIsUsable()
    {
        if (!Directory.Exists(ManagedRoot))
        {
            throw new DirectoryNotFoundException(
                $"The managed root '{ManagedRoot}' does not exist.");
        }

        EnsurePathHasNoReparsePoints(ManagedRoot, allowMissingTail: false);
        EnsureStrictChildPath(ManagedRoot, LibraryRoot, "managed downloads library");
        EnsurePathHasNoReparsePoints(LibraryRoot, allowMissingTail: false);
    }

    private void EnsureOperationalDirectory(string path, string description)
    {
        EnsureStrictChildPath(ManagedRoot, path, description);
        EnsurePathHasNoReparsePoints(path, allowMissingTail: true);
        Directory.CreateDirectory(path);
        EnsurePathHasNoReparsePoints(path, allowMissingTail: false);
    }

    private void EnsurePathHasNoReparsePoints(string path, bool allowMissingTail)
    {
        string fullPath = Path.GetFullPath(path);
        if (!PathEquals(fullPath, ManagedRoot))
        {
            EnsureStrictChildPath(ManagedRoot, fullPath, "managed path");
        }

        if (!TryGetAttributes(ManagedRoot, out FileAttributes rootAttributes))
        {
            throw new DirectoryNotFoundException(
                $"The managed root '{ManagedRoot}' does not exist.");
        }

        if ((rootAttributes & FileAttributes.Directory) == 0
            || (rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The managed root must be a regular directory and cannot be a reparse point.");
        }

        if (PathEquals(fullPath, ManagedRoot))
        {
            return;
        }

        string relative = Path.GetRelativePath(ManagedRoot, fullPath);
        string current = ManagedRoot;
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!TryGetAttributes(current, out FileAttributes attributes))
            {
                if (allowMissingTail)
                {
                    return;
                }

                throw new FileNotFoundException(
                    "A required managed path does not exist.",
                    current);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Managed path '{current}' cannot be a reparse point.");
            }

            if (index < segments.Length - 1
                && (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    $"Managed path component '{current}' is not a directory.");
            }
        }
    }

    private void EnsureStrictChildPath(string root, string candidate, string description)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        string prefix = PathEquals(fullRoot, ManagedRoot)
            ? _managedRootWithSeparator
            : Path.EndsInDirectorySeparator(fullRoot)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {description} must remain inside '{fullRoot}'.");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool HasTraversalSegment(string path)
    {
        string[] segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment is "." or "..");
    }

    private static void ValidateEnvironmentName(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (environmentName.Length > MaximumEnvironmentNameLength
            || !EnvironmentNamePattern.IsMatch(environmentName)
            || environmentName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Environment names must use 1-64 ASCII letters, digits, periods, underscores, or hyphens, with an alphanumeric first and last character.",
                nameof(environmentName));
        }

        string deviceStem = environmentName.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(deviceStem))
        {
            throw new ArgumentException(
                "The environment name is reserved by Windows.",
                nameof(environmentName));
        }
    }

    private static void ValidateEndpoint(Uri? endpoint, string description, bool requireHttps)
    {
        if (endpoint is null)
        {
            return;
        }

        bool validScheme = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (!requireHttps
                && endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
        if (!endpoint.IsAbsoluteUri
            || !validScheme
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                $"The effective {description} must be an absolute {(requireHttps ? "HTTPS" : "HTTP(S)")} URI without embedded credentials, a query, or a fragment.");
        }
    }

    private static Uri? CloneUri(Uri? value) => value is null ? null : new Uri(value.AbsoluteUri);

    private static IReadOnlyList<string> AsReadOnly(IEnumerable<string> values) =>
        Array.AsReadOnly(values.ToArray());

    private static bool PathEquals(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void EnsureSnapshotMatches(
        PipLocalPackageFileSnapshot expected,
        PipLocalPackageFileSnapshot actual,
        string error)
    {
        if (!PathEquals(expected.Path, actual.Path)
            || expected.Length != actual.Length
            || expected.LastWriteTimeUtcTicks != actual.LastWriteTimeUtcTicks
            || !HashesEqual(expected.Sha256, actual.Sha256))
        {
            throw new InvalidDataException(error);
        }
    }

    private static PipLocalPackageInstallStageResult ProcessStageResult(
        PipLocalPackageInstallStage stage,
        PipLocalPackageProcessResult process,
        string? error) => new(
            stage,
            error is null
                ? PipLocalPackageInstallStageStatus.Succeeded
                : PipLocalPackageInstallStageStatus.Failed,
            process.ExitCode,
            process.StandardOutput,
            process.StandardError,
            error,
            process.StandardOutputTruncated,
            process.StandardErrorTruncated);

    private static string DescribeProcessFailure(
        string prefix,
        PipLocalPackageProcessResult process)
    {
        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return $"{prefix}: {process.StartError}";
        }

        return $"{prefix} with exit code {process.ExitCode}. No transactional rollback was attempted.";
    }

    private static PipLocalPackageInstallResult Failure(
        bool environmentCreated,
        PipLocalPackageInstallStage finalStage,
        List<PipLocalPackageInstallStageResult> stages,
        string error) => new(
            false,
            environmentCreated,
            false,
            finalStage,
            Array.AsReadOnly(stages.ToArray()),
            PipLocalPackageRollbackBehavior.NotTransactional,
            error);

    private static bool IsValidationOrIoFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or CryptographicException
            or NotSupportedException;

    private static string ComputePlanIntegritySha256(PipLocalPackageInstallPlan plan)
    {
        using MemoryStream memory = new();
        using (BinaryWriter writer = new(memory, Encoding.UTF8, leaveOpen: true))
        {
            Write(writer, plan.ManagedRoot);
            Write(writer, plan.LibraryRoot);
            Write(writer, plan.EnvironmentName);
            Write(writer, plan.EnvironmentRoot);
            Write(writer, plan.WheelPath);
            writer.Write((int)plan.SourceMode);
            WriteRuntime(writer, plan.Runtime);
            WriteNetworkSettings(writer, plan.NetworkSettings);
            writer.Write(plan.RequiresEnvironmentCreation);
            WriteSnapshot(writer, plan.RuntimeExecutableSnapshot);
            WriteSnapshot(writer, plan.WheelSnapshot);
            writer.Write(plan.EnvironmentExecutableSnapshot is not null);
            if (plan.EnvironmentExecutableSnapshot is not null)
            {
                WriteSnapshot(writer, plan.EnvironmentExecutableSnapshot);
            }

            WriteCommand(writer, plan.CreateEnvironmentCommand);
            WriteCommand(writer, plan.InstallPackageCommand);
        }

        return Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant();
    }

    private static void WriteRuntime(BinaryWriter writer, ManagedRuntimeEntry runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Write(writer, runtime.Id);
        Write(writer, runtime.ProviderId);
        writer.Write((int)runtime.Kind);
        Write(writer, runtime.Version.ToString());
        writer.Write((int)runtime.Architecture);
        Write(writer, runtime.InstallRoot);
        Write(writer, runtime.ExecutableRelativePath);
        Write(writer, runtime.PackageHash);
        writer.Write(runtime.InstalledAtUtc.UtcDateTime.Ticks);
        writer.Write((int)runtime.PackageHashAlgorithm);
        string[] channels = runtime.Channels?
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? [];
        writer.Write(channels.Length);
        foreach (string channel in channels)
        {
            Write(writer, channel);
        }
    }

    private static void WriteNetworkSettings(
        BinaryWriter writer,
        EffectiveNetworkSettings? settings)
    {
        writer.Write(settings is not null);
        if (settings is null)
        {
            return;
        }

        Write(writer, settings.ToolId);
        Write(writer, settings.HttpProxy?.AbsoluteUri);
        Write(writer, settings.HttpsProxy?.AbsoluteUri);
        Write(writer, settings.Mirror?.AbsoluteUri);
        string[] noProxy = settings.NoProxy?.ToArray()
            ?? throw new InvalidDataException("The plan contains a null no-proxy list.");
        writer.Write(noProxy.Length);
        foreach (string entry in noProxy)
        {
            Write(writer, entry);
        }
    }

    private static void WriteSnapshot(BinaryWriter writer, PipLocalPackageFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Write(writer, snapshot.Path);
        writer.Write(snapshot.Length);
        writer.Write(snapshot.LastWriteTimeUtcTicks);
        Write(writer, snapshot.Sha256);
    }

    private static void WriteCommand(BinaryWriter writer, PipLocalPackageInstallCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Write(writer, command.ExecutablePath);
        Write(writer, command.WorkingDirectory);
        writer.Write(command.ArgumentList.Count);
        foreach (string argument in command.ArgumentList)
        {
            Write(writer, argument);
        }

        KeyValuePair<string, string?>[] environment = command.Environment
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        writer.Write(environment.Length);
        foreach ((string name, string? value) in environment)
        {
            Write(writer, name);
            Write(writer, value);
        }
    }

    private static void Write(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static bool HashesEqual(string? left, string? right)
    {
        if (left is null || right is null || left.Length != 64 || right.Length != 64)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
