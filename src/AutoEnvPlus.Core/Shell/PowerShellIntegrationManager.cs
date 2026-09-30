using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Shell;

public sealed record PowerShellIntegrationPlan(
    string ProfilePath,
    bool ProfileExisted,
    string Before,
    string After,
    string ModulePath,
    string? ModuleBefore,
    string ModuleContent,
    int ExistingProfileBlockCount,
    byte[] ProfileBeforeBytes,
    byte[] ProfileAfterBytes,
    byte[]? ModuleBeforeBytes,
    byte[] ModuleContentBytes)
{
    public bool ProfileChanged => !ProfileExisted
        || !ProfileBeforeBytes.AsSpan().SequenceEqual(ProfileAfterBytes);

    public bool ModuleChanged => ModuleBeforeBytes is null
        || !ModuleBeforeBytes.AsSpan().SequenceEqual(ModuleContentBytes);

    public bool Changed => ProfileChanged || ModuleChanged;
}

public sealed record PowerShellProfileSnapshot(
    int SchemaVersion,
    string? Id,
    DateTimeOffset CreatedAtUtc,
    string? ProfilePath,
    bool ProfileExisted,
    byte[]? BeforeBytes,
    byte[]? AfterBytes,
    string? BeforeSha256,
    string? AfterSha256,
    string? ModulePath,
    string? Before,
    string? After);

public sealed record PowerShellRollbackPreview(
    bool Success,
    string SnapshotPath,
    string ProfilePath,
    bool DeletesProfile,
    string? Error);

public sealed record PowerShellIntegrationResult(
    bool Success,
    bool Changed,
    string ModulePath,
    string? SnapshotPath,
    string? Error);

public sealed class PowerShellIntegrationManager
{
    public const string BeginMarker = "# >>> AutoEnvPlus PowerShell integration >>>";
    public const string EndMarker = "# <<< AutoEnvPlus PowerShell integration <<<";

    private const int CurrentSnapshotSchemaVersion = 2;
    private const int MaximumProfileBytes = 2 * 1024 * 1024;
    private const int MaximumModuleBytes = 1024 * 1024;
    private const int MaximumSnapshotBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding StrictUtf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding StrictUtf8WithBom = new(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);

    private readonly string _managedRoot;
    private readonly string _autoEnvPlusExecutable;
    private readonly IReadOnlyList<string> _autoEnvPlusPrefixArguments;
    private readonly Func<string, CancellationToken, Task>? _snapshotCommittedObserver;

    public PowerShellIntegrationManager(
        string managedRoot,
        string autoEnvPlusExecutable,
        IReadOnlyList<string>? autoEnvPlusPrefixArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(autoEnvPlusExecutable);
        _managedRoot = Path.GetFullPath(managedRoot);
        _autoEnvPlusExecutable = Path.GetFullPath(autoEnvPlusExecutable);
        _autoEnvPlusPrefixArguments = autoEnvPlusPrefixArguments?.ToArray() ?? [];
    }

    internal PowerShellIntegrationManager(
        string managedRoot,
        string autoEnvPlusExecutable,
        IReadOnlyList<string>? autoEnvPlusPrefixArguments,
        Func<string, CancellationToken, Task> snapshotCommittedObserver)
        : this(managedRoot, autoEnvPlusExecutable, autoEnvPlusPrefixArguments)
    {
        _snapshotCommittedObserver = snapshotCommittedObserver
            ?? throw new ArgumentNullException(nameof(snapshotCommittedObserver));
    }

    public string ModulePath => Path.Combine(
        _managedRoot,
        "shell",
        "powershell",
        "AutoEnvPlus.PowerShell.psm1");

    public static string GetDefaultWindowsPowerShellProfilePath()
    {
        string documents = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "Documents");
        }

        return Path.Combine(
            documents,
            "WindowsPowerShell",
            "Microsoft.PowerShell_profile.ps1");
    }

    public PowerShellIntegrationPlan PlanInstall(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        if (!File.Exists(_autoEnvPlusExecutable))
        {
            throw new FileNotFoundException(
                "The AutoEnvPlus CLI executable does not exist.",
                _autoEnvPlusExecutable);
        }

        string fullProfilePath = Path.GetFullPath(profilePath);
        bool profileExisted = File.Exists(fullProfilePath);
        TextFileContent profileBefore = profileExisted
            ? ReadTextFile(fullProfilePath, MaximumProfileBytes, "PowerShell Profile")
            : TextFileContent.NewUtf8Bom();
        string moduleContent = BuildModuleContent();
        TextFileContent? moduleBefore = File.Exists(ModulePath)
            ? ReadTextFile(ModulePath, MaximumModuleBytes, "AutoEnvPlus PowerShell module")
            : null;
        (string withoutBlocks, int blockCount) = RemoveManagedBlocks(profileBefore.Text);
        string after = AppendManagedBlock(
            withoutBlocks,
            BuildProfileBlock(ModulePath),
            DetectNewLine(profileBefore.Text));
        byte[] profileAfterBytes = profileBefore.Encode(after);
        byte[] moduleContentBytes = EncodeUtf8Bom(moduleContent);

        return new PowerShellIntegrationPlan(
            fullProfilePath,
            profileExisted,
            profileBefore.Text,
            after,
            ModulePath,
            moduleBefore?.Text,
            moduleContent,
            blockCount,
            profileBefore.Bytes,
            profileAfterBytes,
            moduleBefore?.Bytes,
            moduleContentBytes);
    }

    public async Task<PowerShellIntegrationResult> ApplyAsync(
        PowerShellIntegrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);

        bool profileExistsNow = File.Exists(plan.ProfilePath);
        byte[] profileNow = profileExistsNow
            ? await ReadFileBytesAsync(
                plan.ProfilePath,
                MaximumProfileBytes,
                "PowerShell Profile",
                cancellationToken).ConfigureAwait(false)
            : [];
        if (profileExistsNow != plan.ProfileExisted
            || !profileNow.AsSpan().SequenceEqual(plan.ProfileBeforeBytes))
        {
            return Failure(
                plan,
                "The PowerShell Profile changed after the preview was created; refresh and review the new plan.");
        }

        bool moduleExistsNow = File.Exists(plan.ModulePath);
        byte[]? moduleNow = moduleExistsNow
            ? await ReadFileBytesAsync(
                plan.ModulePath,
                MaximumModuleBytes,
                "AutoEnvPlus PowerShell module",
                cancellationToken).ConfigureAwait(false)
            : null;
        if (moduleExistsNow != (plan.ModuleBeforeBytes is not null)
            || !BytesEqual(moduleNow, plan.ModuleBeforeBytes))
        {
            return Failure(
                plan,
                "The AutoEnvPlus PowerShell module changed after the preview was created; refresh the plan.");
        }

        string? snapshotPath = null;
        byte[]? snapshotBytes = null;
        bool snapshotCommitted = false;
        bool moduleCommitted = false;
        try
        {
            if (plan.ModuleChanged)
            {
                await WriteFileAtomicallyAsync(
                    plan.ModulePath,
                    plan.ModuleContentBytes,
                    cancellationToken,
                    expectation: new ExpectedFileState(
                        plan.ModuleBeforeBytes is not null,
                        plan.ModuleBeforeBytes ?? [],
                        MaximumModuleBytes,
                        "The AutoEnvPlus PowerShell module changed after the preview was created; refresh the plan."),
                    requireNoReparsePath: true)
                    .ConfigureAwait(false);
                moduleCommitted = true;
            }

            if (plan.ProfileChanged)
            {
                PowerShellProfileSnapshot snapshot = new(
                    CurrentSnapshotSchemaVersion,
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    plan.ProfilePath,
                    plan.ProfileExisted,
                    plan.ProfileBeforeBytes,
                    plan.ProfileAfterBytes,
                    ComputeSha256(plan.ProfileBeforeBytes),
                    ComputeSha256(plan.ProfileAfterBytes),
                    plan.ModulePath,
                    null,
                    null);
                string snapshotDirectory = GetSnapshotDirectory();
                snapshotPath = Path.Combine(snapshotDirectory, snapshot.Id! + ".json");
                snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                if (snapshotBytes.Length > MaximumSnapshotBytes)
                {
                    throw new InvalidDataException(
                        "The PowerShell Profile snapshot exceeds the supported size limit.");
                }

                await WriteFileAtomicallyAsync(
                    snapshotPath,
                    snapshotBytes,
                    cancellationToken,
                    overwrite: false,
                    requireNoReparsePath: true).ConfigureAwait(false);
                snapshotCommitted = true;
                if (_snapshotCommittedObserver is not null)
                {
                    await _snapshotCommittedObserver(snapshotPath, cancellationToken)
                        .ConfigureAwait(false);
                }

                await WriteFileAtomicallyAsync(
                    plan.ProfilePath,
                    plan.ProfileAfterBytes,
                    cancellationToken,
                    expectation: new ExpectedFileState(
                        plan.ProfileExisted,
                        plan.ProfileBeforeBytes,
                        MaximumProfileBytes,
                        "The PowerShell Profile changed after the preview was created; refresh and review the new plan."))
                    .ConfigureAwait(false);
            }

            return new PowerShellIntegrationResult(
                true,
                plan.Changed,
                plan.ModulePath,
                snapshotPath,
                null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or JsonException
            or ArgumentException
            or NotSupportedException
            or OperationCanceledException)
        {
            string? compensationError = await CompensateFailedApplyAsync(
                plan,
                moduleCommitted,
                snapshotCommitted ? snapshotPath : null,
                snapshotCommitted ? snapshotBytes : null).ConfigureAwait(false);
            if (!snapshotCommitted || compensationError is null)
            {
                snapshotPath = null;
            }

            if (exception is OperationCanceledException && compensationError is null)
            {
                throw;
            }

            string error = compensationError is null
                ? exception.Message
                : $"{exception.Message} Automatic transaction rollback was incomplete: {compensationError}";
            return new PowerShellIntegrationResult(
                false,
                false,
                plan.ModulePath,
                snapshotPath,
                error);
        }
    }

    private static async Task<string?> CompensateFailedApplyAsync(
        PowerShellIntegrationPlan plan,
        bool moduleCommitted,
        string? snapshotPath,
        byte[]? snapshotBytes)
    {
        List<string> errors = [];
        if (snapshotPath is not null && snapshotBytes is not null)
        {
            try
            {
                ManagedPathSafety.EnsureNoReparsePointInPath(snapshotPath);
                EnsureFileMatches(
                    snapshotPath,
                    new ExpectedFileState(
                        true,
                        snapshotBytes,
                        MaximumSnapshotBytes,
                        "The newly-created PowerShell Profile snapshot changed before rollback."));
                File.Delete(snapshotPath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException
                or ArgumentException
                or NotSupportedException)
            {
                errors.Add($"snapshot cleanup failed: {exception.Message}");
            }
        }

        if (moduleCommitted)
        {
            try
            {
                ExpectedFileState installedModule = new(
                    true,
                    plan.ModuleContentBytes,
                    MaximumModuleBytes,
                    "The PowerShell module changed before transaction rollback.");
                if (plan.ModuleBeforeBytes is not null)
                {
                    await WriteFileAtomicallyAsync(
                        plan.ModulePath,
                        plan.ModuleBeforeBytes,
                        CancellationToken.None,
                        expectation: installedModule,
                        requireNoReparsePath: true).ConfigureAwait(false);
                }
                else
                {
                    ManagedPathSafety.EnsureNoReparsePointInPath(plan.ModulePath);
                    EnsureFileMatches(plan.ModulePath, installedModule);
                    File.Delete(plan.ModulePath);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException
                or ArgumentException
                or NotSupportedException)
            {
                errors.Add($"module restoration failed: {exception.Message}");
            }
        }

        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    public async Task<PowerShellRollbackPreview> PreviewRollbackAsync(
        string snapshotPath,
        string expectedProfilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfilePath);
        string fullSnapshotPath = Path.GetFullPath(snapshotPath);
        string fullExpectedProfilePath = Path.GetFullPath(expectedProfilePath);
        RollbackPreparationResult prepared = await PrepareRollbackAsync(
            fullSnapshotPath,
            fullExpectedProfilePath,
            cancellationToken).ConfigureAwait(false);
        return prepared.Preparation is { } preparation
            ? new PowerShellRollbackPreview(
                true,
                fullSnapshotPath,
                preparation.ProfilePath,
                !preparation.ProfileExisted,
                null)
            : new PowerShellRollbackPreview(
                false,
                fullSnapshotPath,
                fullExpectedProfilePath,
                false,
                prepared.Error);
    }

    public async Task<PowerShellIntegrationResult> RollbackAsync(
        string snapshotPath,
        string expectedProfilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfilePath);
        string fullSnapshotPath = Path.GetFullPath(snapshotPath);
        string fullExpectedProfilePath = Path.GetFullPath(expectedProfilePath);
        RollbackPreparationResult prepared = await PrepareRollbackAsync(
            fullSnapshotPath,
            fullExpectedProfilePath,
            cancellationToken).ConfigureAwait(false);
        if (prepared.Preparation is not { } preparation)
        {
            return new PowerShellIntegrationResult(
                false,
                false,
                ModulePath,
                fullSnapshotPath,
                prepared.Error);
        }

        try
        {
            ExpectedFileState expectation = new(
                true,
                preparation.AfterBytes,
                MaximumProfileBytes,
                "The PowerShell Profile changed after this snapshot; automatic rollback would overwrite newer changes.");
            if (preparation.ProfileExisted)
            {
                await WriteFileAtomicallyAsync(
                    preparation.ProfilePath,
                    preparation.BeforeBytes,
                    cancellationToken,
                    expectation: expectation).ConfigureAwait(false);
            }
            else
            {
                EnsureFileMatches(preparation.ProfilePath, expectation);
                File.Delete(preparation.ProfilePath);
            }

            return new PowerShellIntegrationResult(
                true,
                true,
                ModulePath,
                fullSnapshotPath,
                null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new PowerShellIntegrationResult(
                false,
                false,
                ModulePath,
                fullSnapshotPath,
                exception.Message);
        }
    }

    private string BuildModuleContent()
    {
        string executable = ToPowerShellSingleQuotedLiteral(_autoEnvPlusExecutable);
        string managedRoot = ToPowerShellSingleQuotedLiteral(_managedRoot);
        string shimDirectory = ToPowerShellSingleQuotedLiteral(
            Path.Combine(_managedRoot, "shims"));
        string prefixArguments = _autoEnvPlusPrefixArguments.Count == 0
            ? "@()"
            : "@(" + string.Join(
                ", ",
                _autoEnvPlusPrefixArguments.Select(ToPowerShellSingleQuotedLiteral)) + ")";

        return string.Join(
            "\r\n",
            "Set-StrictMode -Version Latest",
            string.Empty,
            $"$script:AutoEnvPlusExecutable = {executable}",
            $"$script:AutoEnvPlusPrefixArguments = {prefixArguments}",
            $"$script:AutoEnvPlusManagedRoot = {managedRoot}",
            $"$script:AutoEnvPlusShimDirectory = {shimDirectory}",
            "$script:AutoEnvPlusRuntimeVariables = @{",
            "    python = 'AUTOENVPLUS_PYTHON_VERSION'",
            "    node = 'AUTOENVPLUS_NODE_VERSION'",
            "    java = 'AUTOENVPLUS_JAVA_VERSION'",
            "    dotnet = 'AUTOENVPLUS_DOTNET_VERSION'",
            "    msvc = 'AUTOENVPLUS_MSVC_VERSION'",
            "    llvm = 'AUTOENVPLUS_LLVM_VERSION'",
            "    mingw = 'AUTOENVPLUS_MINGW_VERSION'",
            "    cmake = 'AUTOENVPLUS_CMAKE_VERSION'",
            "    ninja = 'AUTOENVPLUS_NINJA_VERSION'",
            "}",
            "$script:AutoEnvPlusRuntimeIdVariables = @{",
            "    python = 'AUTOENVPLUS_PYTHON_RUNTIME_ID'",
            "    node = 'AUTOENVPLUS_NODE_RUNTIME_ID'",
            "    java = 'AUTOENVPLUS_JAVA_RUNTIME_ID'",
            "    dotnet = 'AUTOENVPLUS_DOTNET_RUNTIME_ID'",
            "    msvc = 'AUTOENVPLUS_MSVC_RUNTIME_ID'",
            "    llvm = 'AUTOENVPLUS_LLVM_RUNTIME_ID'",
            "    mingw = 'AUTOENVPLUS_MINGW_RUNTIME_ID'",
            "    cmake = 'AUTOENVPLUS_CMAKE_RUNTIME_ID'",
            "    ninja = 'AUTOENVPLUS_NINJA_RUNTIME_ID'",
            "}",
            "$script:AutoEnvPlusRuntimeProviderVariables = @{",
            "    python = 'AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID'",
            "    node = 'AUTOENVPLUS_NODE_RUNTIME_PROVIDER_ID'",
            "    java = 'AUTOENVPLUS_JAVA_RUNTIME_PROVIDER_ID'",
            "    dotnet = 'AUTOENVPLUS_DOTNET_RUNTIME_PROVIDER_ID'",
            "    msvc = 'AUTOENVPLUS_MSVC_RUNTIME_PROVIDER_ID'",
            "    llvm = 'AUTOENVPLUS_LLVM_RUNTIME_PROVIDER_ID'",
            "    mingw = 'AUTOENVPLUS_MINGW_RUNTIME_PROVIDER_ID'",
            "    cmake = 'AUTOENVPLUS_CMAKE_RUNTIME_PROVIDER_ID'",
            "    ninja = 'AUTOENVPLUS_NINJA_RUNTIME_PROVIDER_ID'",
            "}",
            string.Empty,
            "if (Test-Path -LiteralPath $script:AutoEnvPlusShimDirectory -PathType Container) {",
            "    $normalizedShim = $script:AutoEnvPlusShimDirectory.TrimEnd('\\', '/')",
            "    $containsShim = @($env:PATH -split ';') | Where-Object {",
            "        $_.Trim().TrimEnd('\\', '/') -ieq $normalizedShim",
            "    }",
            "    if (-not $containsShim) {",
            "        $env:PATH = $script:AutoEnvPlusShimDirectory + ';' + $env:PATH",
            "    }",
            "}",
            string.Empty,
            "function Use-AutoEnvPlusRuntime {",
            "    [CmdletBinding()]",
            "    param(",
            "        [Parameter(Mandatory, Position = 0)]",
            "        [ValidateSet('python', 'node', 'java', 'dotnet', 'msvc', 'llvm', 'mingw', 'cmake', 'ninja')]",
            "        [string] $Runtime,",
            string.Empty,
            "        [Parameter(Mandatory, Position = 1)]",
            "        [ValidateNotNullOrEmpty()]",
            "        [string] $Selector,",
            string.Empty,
            "        [Parameter()]",
            "        [ValidateNotNullOrEmpty()]",
            "        [string] $RuntimeId,",
            string.Empty,
            "        [Parameter()]",
            "        [ValidateNotNullOrEmpty()]",
            "        [string] $ProviderId",
            "    )",
            string.Empty,
            "    if ($ProviderId -and -not $RuntimeId) {",
            "        throw 'ProviderId requires RuntimeId for an exact AutoEnvPlus session pin.'",
            "    }",
            string.Empty,
            "    $runtimeKey = $Runtime.ToLowerInvariant()",
            "    $variableName = $script:AutoEnvPlusRuntimeVariables[$runtimeKey]",
            "    $runtimeIdVariableName = $script:AutoEnvPlusRuntimeIdVariables[$runtimeKey]",
            "    $providerVariableName = $script:AutoEnvPlusRuntimeProviderVariables[$runtimeKey]",
            "    $previousValue = [System.Environment]::GetEnvironmentVariable(",
            "        $variableName,",
            "        [System.EnvironmentVariableTarget]::Process)",
            "    $previousRuntimeId = [System.Environment]::GetEnvironmentVariable(",
            "        $runtimeIdVariableName,",
            "        [System.EnvironmentVariableTarget]::Process)",
            "    $previousProviderId = [System.Environment]::GetEnvironmentVariable(",
            "        $providerVariableName,",
            "        [System.EnvironmentVariableTarget]::Process)",
            "    [System.Environment]::SetEnvironmentVariable(",
            "        $runtimeIdVariableName,",
            "        $RuntimeId,",
            "        [System.EnvironmentVariableTarget]::Process)",
            "    [System.Environment]::SetEnvironmentVariable(",
            "        $providerVariableName,",
            "        $ProviderId,",
            "        [System.EnvironmentVariableTarget]::Process)",
            "    [System.Environment]::SetEnvironmentVariable(",
            "        $variableName,",
            "        $Selector,",
            "        [System.EnvironmentVariableTarget]::Process)",
            string.Empty,
            "    try {",
            "        $autoEnvPlusArguments = @($script:AutoEnvPlusPrefixArguments)",
            "        $autoEnvPlusArguments += @('which', $runtimeKey, '--root', $script:AutoEnvPlusManagedRoot)",
            "        & $script:AutoEnvPlusExecutable @autoEnvPlusArguments",
            "        if ($LASTEXITCODE -ne 0) {",
            "            throw \"AutoEnvPlus could not resolve $Runtime selector '$Selector'.\"",
            "        }",
            "    }",
            "    catch {",
            "        [System.Environment]::SetEnvironmentVariable(",
            "            $variableName,",
            "            $previousValue,",
            "            [System.EnvironmentVariableTarget]::Process)",
            "        [System.Environment]::SetEnvironmentVariable(",
            "            $runtimeIdVariableName,",
            "            $previousRuntimeId,",
            "            [System.EnvironmentVariableTarget]::Process)",
            "        [System.Environment]::SetEnvironmentVariable(",
            "            $providerVariableName,",
            "            $previousProviderId,",
            "            [System.EnvironmentVariableTarget]::Process)",
            "        throw",
            "    }",
            "}",
            string.Empty,
            "function Clear-AutoEnvPlusRuntime {",
            "    [CmdletBinding()]",
            "    param(",
            "        [Parameter(Position = 0)]",
            "        [ValidateSet('python', 'node', 'java', 'dotnet', 'msvc', 'llvm', 'mingw', 'cmake', 'ninja')]",
            "        [string] $Runtime",
            "    )",
            string.Empty,
            "    $runtimeKeys = if ($PSBoundParameters.ContainsKey('Runtime')) {",
            "        @($Runtime.ToLowerInvariant())",
            "    }",
            "    else {",
            "        @($script:AutoEnvPlusRuntimeVariables.Keys)",
            "    }",
            string.Empty,
            "    foreach ($runtimeKey in $runtimeKeys) {",
            "        [System.Environment]::SetEnvironmentVariable(",
            "            $script:AutoEnvPlusRuntimeVariables[$runtimeKey],",
            "            $null,",
            "            [System.EnvironmentVariableTarget]::Process)",
            "        [System.Environment]::SetEnvironmentVariable(",
            "            $script:AutoEnvPlusRuntimeIdVariables[$runtimeKey],",
            "            $null,",
            "            [System.EnvironmentVariableTarget]::Process)",
            "        [System.Environment]::SetEnvironmentVariable(",
            "            $script:AutoEnvPlusRuntimeProviderVariables[$runtimeKey],",
            "            $null,",
            "            [System.EnvironmentVariableTarget]::Process)",
            "    }",
            "}",
            string.Empty,
            "Export-ModuleMember -Function Use-AutoEnvPlusRuntime, Clear-AutoEnvPlusRuntime",
            string.Empty);
    }

    private static string BuildProfileBlock(string modulePath)
    {
        string quotedModulePath = ToPowerShellSingleQuotedLiteral(modulePath);
        return string.Join(
            "\n",
            BeginMarker,
            $"$__autoEnvPlusModulePath = {quotedModulePath}",
            "if (Test-Path -LiteralPath $__autoEnvPlusModulePath -PathType Leaf) {",
            "    Import-Module -Name $__autoEnvPlusModulePath -Global -Force",
            "}",
            "Remove-Variable -Name __autoEnvPlusModulePath -ErrorAction SilentlyContinue",
            EndMarker);
    }

    private static (string Content, int BlockCount) RemoveManagedBlocks(string content)
    {
        IReadOnlyList<ProfileLine> lines = ParseLines(content);
        StringBuilder result = new(content.Length);
        bool insideManagedBlock = false;
        int blockCount = 0;
        foreach (ProfileLine line in lines)
        {
            string trimmed = line.Text.Trim();
            if (trimmed.Equals(BeginMarker, StringComparison.Ordinal))
            {
                if (insideManagedBlock)
                {
                    throw new InvalidDataException(
                        "The PowerShell Profile contains nested AutoEnvPlus integration markers.");
                }

                insideManagedBlock = true;
                blockCount++;
                continue;
            }

            if (trimmed.Equals(EndMarker, StringComparison.Ordinal))
            {
                if (!insideManagedBlock)
                {
                    throw new InvalidDataException(
                        "The PowerShell Profile contains an unmatched AutoEnvPlus end marker.");
                }

                insideManagedBlock = false;
                continue;
            }

            if (!insideManagedBlock)
            {
                result.Append(line.Text);
                result.Append(line.LineEnding);
            }
        }

        if (insideManagedBlock)
        {
            throw new InvalidDataException(
                "The PowerShell Profile contains an unmatched AutoEnvPlus begin marker.");
        }

        return (result.ToString(), blockCount);
    }

    private static string AppendManagedBlock(
        string content,
        string block,
        string? detectedNewLine)
    {
        string newLine = detectedNewLine ?? System.Environment.NewLine;
        StringBuilder result = new(content.Length + block.Length + (newLine.Length * 8));
        result.Append(content);
        if (result.Length > 0 && !EndsWithNewLine(result))
        {
            result.Append(newLine);
        }

        result.Append(block.Replace("\n", newLine, StringComparison.Ordinal));
        result.Append(newLine);
        return result.ToString();
    }

    private static IReadOnlyList<ProfileLine> ParseLines(string content)
    {
        List<ProfileLine> lines = [];
        int lineStart = 0;
        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] is not ('\r' or '\n'))
            {
                continue;
            }

            int endingLength = content[index] == '\r'
                && index + 1 < content.Length
                && content[index + 1] == '\n'
                    ? 2
                    : 1;
            lines.Add(new ProfileLine(
                content[lineStart..index],
                content.Substring(index, endingLength)));
            index += endingLength - 1;
            lineStart = index + 1;
        }

        if (lineStart < content.Length)
        {
            lines.Add(new ProfileLine(content[lineStart..], string.Empty));
        }

        return lines;
    }

    private static string? DetectNewLine(string content)
    {
        int lineFeed = content.IndexOf('\n', StringComparison.Ordinal);
        if (lineFeed >= 0)
        {
            return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
        }

        return content.Contains('\r', StringComparison.Ordinal) ? "\r" : null;
    }

    private static bool EndsWithNewLine(StringBuilder value) => value[^1] is '\r' or '\n';

    private static string ToPowerShellSingleQuotedLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private void ValidatePlan(PowerShellIntegrationPlan plan)
    {
        if (!Path.GetFullPath(plan.ModulePath).Equals(ModulePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The PowerShell module plan must target the AutoEnvPlus managed shell directory.",
                nameof(plan));
        }

        if (!Path.IsPathFullyQualified(plan.ProfilePath)
            || plan.ProfileBeforeBytes.Length > MaximumProfileBytes
            || plan.ProfileAfterBytes.Length > MaximumProfileBytes
            || plan.ModuleContentBytes.Length > MaximumModuleBytes
            || plan.ModuleBeforeBytes is { Length: > MaximumModuleBytes })
        {
            throw new ArgumentException("The PowerShell integration plan is invalid.", nameof(plan));
        }

        TextFileContent profileBefore;
        if (plan.ProfileExisted)
        {
            profileBefore = DecodeTextBytes(plan.ProfileBeforeBytes, "PowerShell Profile");
        }
        else
        {
            if (plan.ProfileBeforeBytes.Length != 0 || plan.Before.Length != 0)
            {
                throw new ArgumentException(
                    "A new PowerShell Profile plan cannot contain previous file bytes.",
                    nameof(plan));
            }

            profileBefore = TextFileContent.NewUtf8Bom();
        }

        if (!profileBefore.Text.Equals(plan.Before, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The PowerShell Profile preview does not match its original bytes.",
                nameof(plan));
        }

        (string withoutBlocks, _) = RemoveManagedBlocks(profileBefore.Text);
        string expectedAfter = AppendManagedBlock(
            withoutBlocks,
            BuildProfileBlock(ModulePath),
            DetectNewLine(profileBefore.Text));
        byte[] expectedAfterBytes = profileBefore.Encode(expectedAfter);
        if (!expectedAfter.Equals(plan.After, StringComparison.Ordinal)
            || !expectedAfterBytes.AsSpan().SequenceEqual(plan.ProfileAfterBytes))
        {
            throw new ArgumentException(
                "The PowerShell Profile plan does not describe a valid AutoEnvPlus transformation.",
                nameof(plan));
        }

        string expectedModuleContent = BuildModuleContent();
        byte[] expectedModuleBytes = EncodeUtf8Bom(expectedModuleContent);
        if (!expectedModuleContent.Equals(plan.ModuleContent, StringComparison.Ordinal)
            || !expectedModuleBytes.AsSpan().SequenceEqual(plan.ModuleContentBytes))
        {
            throw new ArgumentException(
                "The PowerShell module plan does not contain the current AutoEnvPlus module.",
                nameof(plan));
        }

        if (plan.ModuleBeforeBytes is null)
        {
            if (plan.ModuleBefore is not null)
            {
                throw new ArgumentException(
                    "The PowerShell module preview is inconsistent with its file state.",
                    nameof(plan));
            }
        }
        else
        {
            TextFileContent moduleBefore = DecodeTextBytes(
                plan.ModuleBeforeBytes,
                "AutoEnvPlus PowerShell module");
            if (!moduleBefore.Text.Equals(plan.ModuleBefore, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The PowerShell module preview does not match its original bytes.",
                    nameof(plan));
            }
        }
    }

    private string GetSnapshotDirectory() => Path.Combine(
        _managedRoot,
        "state",
        "powershell-profile-snapshots");

    private static PowerShellIntegrationResult Failure(
        PowerShellIntegrationPlan plan,
        string error) => new(
            false,
            false,
            plan.ModulePath,
            null,
            error);

    private async Task<RollbackPreparationResult> PrepareRollbackAsync(
        string fullSnapshotPath,
        string fullExpectedProfilePath,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureSnapshotPath(fullSnapshotPath);
            if (!File.Exists(fullSnapshotPath))
            {
                return new RollbackPreparationResult(
                    null,
                    "The PowerShell Profile snapshot does not exist.");
            }

            byte[] serialized = await ReadFileBytesAsync(
                fullSnapshotPath,
                MaximumSnapshotBytes,
                "PowerShell Profile snapshot",
                cancellationToken,
                requireNonEmpty: true,
                requireNoReparsePath: true).ConfigureAwait(false);
            ValidateSnapshotJsonShape(serialized);
            PowerShellProfileSnapshot? snapshot = JsonSerializer.Deserialize<PowerShellProfileSnapshot>(
                serialized,
                JsonOptions);
            string? validationError = ValidateSnapshot(
                fullSnapshotPath,
                fullExpectedProfilePath,
                snapshot,
                out RollbackPreparation? preparation);
            if (validationError is not null)
            {
                return new RollbackPreparationResult(null, validationError);
            }

            if (!File.Exists(preparation!.ProfilePath))
            {
                return new RollbackPreparationResult(
                    null,
                    "The PowerShell Profile changed after this snapshot; automatic rollback would overwrite newer changes.");
            }

            byte[] current = await ReadFileBytesAsync(
                preparation.ProfilePath,
                MaximumProfileBytes,
                "PowerShell Profile",
                cancellationToken).ConfigureAwait(false);
            if (!ComputeSha256(current).Equals(preparation.AfterSha256, StringComparison.OrdinalIgnoreCase)
                || !current.AsSpan().SequenceEqual(preparation.AfterBytes))
            {
                return new RollbackPreparationResult(
                    null,
                    "The PowerShell Profile changed after this snapshot; automatic rollback would overwrite newer changes.");
            }

            return new RollbackPreparationResult(preparation, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return new RollbackPreparationResult(
                null,
                $"The PowerShell Profile snapshot could not be used: {exception.Message}");
        }
    }

    private string? ValidateSnapshot(
        string fullSnapshotPath,
        string fullExpectedProfilePath,
        PowerShellProfileSnapshot? snapshot,
        out RollbackPreparation? preparation)
    {
        preparation = null;
        string expectedId = Path.GetFileNameWithoutExtension(fullSnapshotPath);
        if (snapshot is null
            || snapshot.SchemaVersion is not 0 and not CurrentSnapshotSchemaVersion
            || !Guid.TryParseExact(snapshot.Id, "N", out _)
            || !string.Equals(snapshot.Id, expectedId, StringComparison.OrdinalIgnoreCase)
            || snapshot.CreatedAtUtc == default
            || string.IsNullOrWhiteSpace(snapshot.ProfilePath)
            || !Path.IsPathFullyQualified(snapshot.ProfilePath))
        {
            return "The PowerShell Profile snapshot is invalid.";
        }

        string canonicalProfilePath = Path.GetFullPath(snapshot.ProfilePath);
        if (!canonicalProfilePath.Equals(snapshot.ProfilePath, StringComparison.OrdinalIgnoreCase)
            || !canonicalProfilePath.Equals(fullExpectedProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            return "The PowerShell Profile snapshot is not bound to the explicitly selected Profile.";
        }

        return snapshot.SchemaVersion == 0
            ? ValidateLegacySnapshot(snapshot, canonicalProfilePath, out preparation)
            : ValidateCurrentSnapshot(snapshot, canonicalProfilePath, out preparation);
    }

    private string? ValidateCurrentSnapshot(
        PowerShellProfileSnapshot snapshot,
        string canonicalProfilePath,
        out RollbackPreparation? preparation)
    {
        preparation = null;
        if (snapshot.BeforeBytes is null
            || snapshot.AfterBytes is null
            || string.IsNullOrWhiteSpace(snapshot.BeforeSha256)
            || string.IsNullOrWhiteSpace(snapshot.AfterSha256)
            || snapshot.BeforeBytes.Length > MaximumProfileBytes
            || snapshot.AfterBytes.Length > MaximumProfileBytes
            || (!snapshot.ProfileExisted && snapshot.BeforeBytes.Length != 0)
            || !ComputeSha256(snapshot.BeforeBytes).Equals(
                snapshot.BeforeSha256,
                StringComparison.OrdinalIgnoreCase)
            || !ComputeSha256(snapshot.AfterBytes).Equals(
                snapshot.AfterSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The PowerShell Profile snapshot byte hashes are invalid.";
        }

        TextFileContent before = snapshot.ProfileExisted
            ? DecodeTextBytes(snapshot.BeforeBytes, "PowerShell Profile snapshot before image")
            : TextFileContent.NewUtf8Bom();
        TextFileContent after = DecodeTextBytes(
            snapshot.AfterBytes,
            "PowerShell Profile snapshot after image");
        string? modulePathError = ResolveSnapshotModulePath(
            snapshot.ModulePath,
            after.Text,
            out string? snapshotModulePath);
        if (modulePathError is not null)
        {
            return modulePathError;
        }

        (string withoutBlocks, _) = RemoveManagedBlocks(before.Text);
        string expectedAfterText = AppendManagedBlock(
            withoutBlocks,
            BuildProfileBlock(snapshotModulePath!),
            DetectNewLine(before.Text));
        byte[] expectedAfterBytes = before.Encode(expectedAfterText);
        if (!expectedAfterBytes.AsSpan().SequenceEqual(snapshot.AfterBytes))
        {
            return "The PowerShell Profile snapshot does not describe a valid AutoEnvPlus transformation.";
        }

        preparation = new RollbackPreparation(
            canonicalProfilePath,
            snapshot.ProfileExisted,
            snapshot.BeforeBytes,
            snapshot.AfterBytes,
            snapshot.AfterSha256);
        return null;
    }

    private string? ValidateLegacySnapshot(
        PowerShellProfileSnapshot snapshot,
        string canonicalProfilePath,
        out RollbackPreparation? preparation)
    {
        preparation = null;
        if (snapshot.Before is null
            || snapshot.After is null
            || snapshot.BeforeBytes is not null
            || snapshot.AfterBytes is not null
            || snapshot.BeforeSha256 is not null
            || snapshot.AfterSha256 is not null
            || snapshot.ModulePath is not null
            || (!snapshot.ProfileExisted && snapshot.Before.Length != 0))
        {
            return "The legacy PowerShell Profile snapshot is invalid.";
        }

        byte[] beforeBytes = StrictUtf8NoBom.GetBytes(snapshot.Before);
        byte[] afterBytes = StrictUtf8NoBom.GetBytes(snapshot.After);
        if (beforeBytes.Length > MaximumProfileBytes
            || afterBytes.Length > MaximumProfileBytes)
        {
            return "The legacy PowerShell Profile snapshot exceeds the supported Profile size limit.";
        }

        string? modulePathError = ResolveSnapshotModulePath(
            null,
            snapshot.After,
            out string? snapshotModulePath);
        if (modulePathError is not null)
        {
            return modulePathError;
        }

        (string withoutBlocks, _) = RemoveManagedBlocks(snapshot.Before);
        string expectedAfter = AppendManagedBlock(
            withoutBlocks,
            BuildProfileBlock(snapshotModulePath!),
            DetectNewLine(snapshot.Before));
        if (!expectedAfter.Equals(snapshot.After, StringComparison.Ordinal))
        {
            return "The legacy PowerShell Profile snapshot does not describe a valid AutoEnvPlus transformation.";
        }

        preparation = new RollbackPreparation(
            canonicalProfilePath,
            snapshot.ProfileExisted,
            beforeBytes,
            afterBytes,
            ComputeSha256(afterBytes));
        return null;
    }

    private string? ResolveSnapshotModulePath(
        string? declaredModulePath,
        string after,
        out string? snapshotModulePath)
    {
        snapshotModulePath = declaredModulePath;
        if (snapshotModulePath is null
            && !TryExtractManagedModulePath(after, out snapshotModulePath))
        {
            return "The PowerShell Profile snapshot does not contain a valid AutoEnvPlus module path.";
        }

        if (string.IsNullOrWhiteSpace(snapshotModulePath)
            || !Path.IsPathFullyQualified(snapshotModulePath))
        {
            return "The PowerShell Profile snapshot contains an invalid AutoEnvPlus module path.";
        }

        string canonicalSnapshotModulePath = Path.GetFullPath(snapshotModulePath);
        if (!canonicalSnapshotModulePath.Equals(
                snapshotModulePath,
                StringComparison.OrdinalIgnoreCase)
            || !canonicalSnapshotModulePath.Equals(
                Path.GetFullPath(ModulePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return "The PowerShell Profile snapshot is not bound to the AutoEnvPlus managed module.";
        }

        return null;
    }

    private static bool TryExtractManagedModulePath(
        string content,
        out string? modulePath)
    {
        const string assignmentPrefix = "$__autoEnvPlusModulePath = ";
        modulePath = null;
        IReadOnlyList<ProfileLine> lines = ParseLines(content);
        for (int index = 0; index < lines.Count; index++)
        {
            if (!lines[index].Text.Equals(BeginMarker, StringComparison.Ordinal))
            {
                continue;
            }

            if (modulePath is not null
                || index + 1 >= lines.Count
                || !lines[index + 1].Text.StartsWith(
                    assignmentPrefix,
                    StringComparison.Ordinal)
                || !TryParsePowerShellSingleQuotedLiteral(
                    lines[index + 1].Text[assignmentPrefix.Length..],
                    out modulePath))
            {
                modulePath = null;
                return false;
            }
        }

        return modulePath is not null;
    }

    private static bool TryParsePowerShellSingleQuotedLiteral(
        string literal,
        out string? value)
    {
        value = null;
        if (literal.Length < 2
            || literal[0] != '\''
            || literal[^1] != '\'')
        {
            return false;
        }

        StringBuilder result = new(literal.Length - 2);
        for (int index = 1; index < literal.Length - 1; index++)
        {
            char character = literal[index];
            if (character != '\'')
            {
                result.Append(character);
                continue;
            }

            if (index + 1 >= literal.Length - 1
                || literal[index + 1] != '\'')
            {
                return false;
            }

            result.Append('\'');
            index++;
        }

        value = result.ToString();
        return true;
    }

    private static void ValidateSnapshotJsonShape(byte[] serialized)
    {
        using JsonDocument document = JsonDocument.Parse(
            serialized,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The PowerShell Profile snapshot must be a JSON object.");
        }

        HashSet<string> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The PowerShell Profile snapshot contains a duplicate '{property.Name}' property.");
            }
        }

        string[] legacyProperties =
        [
            "id",
            "createdAtUtc",
            "profilePath",
            "profileExisted",
            "before",
            "after",
        ];
        if (!properties.Contains("schemaVersion"))
        {
            if (!properties.SetEquals(legacyProperties))
            {
                throw new InvalidDataException(
                    "The legacy PowerShell Profile snapshot has an invalid property set.");
            }

            return;
        }

        JsonElement schemaVersion = document.RootElement.GetProperty("schemaVersion");
        if (schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out int version)
            || version != CurrentSnapshotSchemaVersion)
        {
            throw new InvalidDataException("The PowerShell Profile snapshot schema is not supported.");
        }

        string[] requiredProperties =
        [
            "schemaVersion",
            "id",
            "createdAtUtc",
            "profilePath",
            "profileExisted",
            "beforeBytes",
            "afterBytes",
            "beforeSha256",
            "afterSha256",
        ];
        HashSet<string> allowedProperties = new(
            requiredProperties.Append("modulePath"),
            StringComparer.Ordinal);
        if (requiredProperties.Any(property => !properties.Contains(property))
            || properties.Any(property => !allowedProperties.Contains(property)))
        {
            throw new InvalidDataException(
                "The PowerShell Profile snapshot has an invalid property set.");
        }
    }

    private void EnsureSnapshotPath(string fullSnapshotPath)
    {
        string snapshotDirectory = Path.GetFullPath(GetSnapshotDirectory());
        string? parent = Path.GetDirectoryName(fullSnapshotPath);
        if (parent is null
            || !Path.GetFullPath(parent).Equals(snapshotDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(fullSnapshotPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The PowerShell Profile snapshot must be a direct .json child of the AutoEnvPlus snapshot directory.");
        }

        ManagedPathSafety.EnsureNoReparsePointInPath(fullSnapshotPath);
    }

    private static async Task WriteFileAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken,
        bool overwrite = true,
        ExpectedFileState? expectation = null,
        bool requireNoReparsePath = false)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The target file does not have a parent directory.");
        }

        if (requireNoReparsePath)
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(directory);
        }

        Directory.CreateDirectory(directory);
        if (requireNoReparsePath)
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(directory);
            ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (requireNoReparsePath)
            {
                ManagedPathSafety.EnsureNoReparsePointInPath(directory);
                ManagedPathSafety.EnsureNoReparsePointInPath(temporaryPath);
                ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
            }

            if (expectation is not null)
            {
                EnsureFileMatches(fullPath, expectation);
            }

            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureFileMatches(string path, ExpectedFileState expectation)
    {
        bool exists = File.Exists(path);
        if (exists != expectation.Exists)
        {
            throw new InvalidOperationException(expectation.ChangedMessage);
        }

        if (exists)
        {
            byte[] current = ReadFileBytes(path, expectation.MaximumBytes, "expected target file");
            if (!current.AsSpan().SequenceEqual(expectation.Bytes))
            {
                throw new InvalidOperationException(expectation.ChangedMessage);
            }
        }
    }

    private static TextFileContent ReadTextFile(
        string path,
        long maximumBytes,
        string description) => DecodeTextBytes(
            ReadFileBytes(path, maximumBytes, description),
            description);

    private static byte[] ReadFileBytes(
        string path,
        long maximumBytes,
        string description,
        bool requireNonEmpty = false,
        bool requireNoReparsePath = false)
    {
        string fullPath = Path.GetFullPath(path);
        if (requireNoReparsePath)
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        }

        EnsureOrdinaryFile(fullPath, description);
        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.SequentialScan);
        if ((requireNonEmpty && stream.Length == 0)
            || stream.Length < 0
            || stream.Length > maximumBytes
            || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException($"The {description} has an invalid size.");
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (requireNoReparsePath)
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        }

        EnsureOrdinaryFile(fullPath, description);
        return bytes;
    }

    private static async Task<byte[]> ReadFileBytesAsync(
        string path,
        long maximumBytes,
        string description,
        CancellationToken cancellationToken,
        bool requireNonEmpty = false,
        bool requireNoReparsePath = false)
    {
        string fullPath = Path.GetFullPath(path);
        if (requireNoReparsePath)
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        }

        EnsureOrdinaryFile(fullPath, description);
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if ((requireNonEmpty && stream.Length == 0)
            || stream.Length < 0
            || stream.Length > maximumBytes
            || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException($"The {description} has an invalid size.");
        }

        byte[] bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (requireNoReparsePath)
        {
            ManagedPathSafety.EnsureNoReparsePointInPath(fullPath);
        }

        EnsureOrdinaryFile(fullPath, description);
        return bytes;
    }

    private static void EnsureOrdinaryFile(string path, string description)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory
            | FileAttributes.Device
            | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"The {description} must be an ordinary file.");
        }
    }

    private static TextFileContent DecodeTextBytes(byte[] bytes, string description)
    {
        (Encoding Encoding, byte[] Preamble) format = DetectEncoding(bytes, description);
        try
        {
            string text = format.Encoding.GetString(bytes, format.Preamble.Length, bytes.Length - format.Preamble.Length);
            return new TextFileContent(bytes.ToArray(), text, format.Encoding, format.Preamble);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The {description} uses an unsupported or invalid text encoding.", exception);
        }
    }

    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(
        byte[] bytes,
        string description)
    {
        if (StartsWith(bytes, [0x00, 0x00, 0xFE, 0xFF]))
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true),
                [0x00, 0x00, 0xFE, 0xFF]);
        }

        if (StartsWith(bytes, [0xFF, 0xFE, 0x00, 0x00]))
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true),
                [0xFF, 0xFE, 0x00, 0x00]);
        }

        if (StartsWith(bytes, [0xEF, 0xBB, 0xBF]))
        {
            return (StrictUtf8NoBom, [0xEF, 0xBB, 0xBF]);
        }

        if (StartsWith(bytes, [0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                [0xFF, 0xFE]);
        }

        if (StartsWith(bytes, [0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                [0xFE, 0xFF]);
        }

        try
        {
            _ = StrictUtf8NoBom.GetString(bytes);
            return (StrictUtf8NoBom, []);
        }
        catch (DecoderFallbackException) when (OperatingSystem.IsWindows())
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try
            {
                Encoding ansi = Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                _ = ansi.GetString(bytes);
                return (ansi, []);
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or DecoderFallbackException)
            {
                throw new InvalidDataException(
                    $"The {description} uses an unsupported or invalid text encoding.",
                    exception);
            }
        }
    }

    private static byte[] EncodeUtf8Bom(string content)
    {
        byte[] payload = StrictUtf8WithBom.GetBytes(content);
        byte[] preamble = StrictUtf8WithBom.GetPreamble();
        byte[] bytes = new byte[preamble.Length + payload.Length];
        preamble.CopyTo(bytes, 0);
        payload.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static bool StartsWith(byte[] value, byte[] prefix) =>
        value.AsSpan().StartsWith(prefix);

    private static bool BytesEqual(byte[]? left, byte[]? right) =>
        left is null
            ? right is null
            : right is not null && left.AsSpan().SequenceEqual(right);

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record TextFileContent(
        byte[] Bytes,
        string Text,
        Encoding Encoding,
        byte[] Preamble)
    {
        public static TextFileContent NewUtf8Bom() => new(
            [],
            string.Empty,
            StrictUtf8NoBom,
            StrictUtf8WithBom.GetPreamble());

        public byte[] Encode(string content)
        {
            byte[] payload = Encoding.GetBytes(content);
            byte[] bytes = new byte[Preamble.Length + payload.Length];
            Preamble.CopyTo(bytes, 0);
            payload.CopyTo(bytes, Preamble.Length);
            return bytes;
        }
    }

    private sealed record ExpectedFileState(
        bool Exists,
        byte[] Bytes,
        long MaximumBytes,
        string ChangedMessage);

    private sealed record RollbackPreparation(
        string ProfilePath,
        bool ProfileExisted,
        byte[] BeforeBytes,
        byte[] AfterBytes,
        string AfterSha256);

    private sealed record RollbackPreparationResult(
        RollbackPreparation? Preparation,
        string? Error);

    private sealed record ProfileLine(string Text, string LineEnding);
}
