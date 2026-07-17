using System.Text;
using System.Text.Json;

namespace AutoEnvPlus.Core.Shell;

public sealed record PowerShellIntegrationPlan(
    string ProfilePath,
    bool ProfileExisted,
    string Before,
    string After,
    string ModulePath,
    string? ModuleBefore,
    string ModuleContent,
    int ExistingProfileBlockCount)
{
    public bool ProfileChanged => !Before.Equals(After, StringComparison.Ordinal)
        || !ProfileExisted;

    public bool ModuleChanged => !ModuleContent.Equals(ModuleBefore, StringComparison.Ordinal);

    public bool Changed => ProfileChanged || ModuleChanged;
}

public sealed record PowerShellProfileSnapshot(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string ProfilePath,
    bool ProfileExisted,
    string Before,
    string After);

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Encoding FileEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private readonly string _managedRoot;
    private readonly string _autoEnvPlusExecutable;
    private readonly IReadOnlyList<string> _autoEnvPlusPrefixArguments;

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
        string before = profileExisted ? File.ReadAllText(fullProfilePath) : string.Empty;
        string moduleContent = BuildModuleContent();
        string? moduleBefore = File.Exists(ModulePath) ? File.ReadAllText(ModulePath) : null;
        (string withoutBlocks, int blockCount) = RemoveManagedBlocks(before);
        string after = AppendManagedBlock(
            withoutBlocks,
            BuildProfileBlock(ModulePath),
            DetectNewLine(before));

        return new PowerShellIntegrationPlan(
            fullProfilePath,
            profileExisted,
            before,
            after,
            ModulePath,
            moduleBefore,
            moduleContent,
            blockCount);
    }

    public async Task<PowerShellIntegrationResult> ApplyAsync(
        PowerShellIntegrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsurePlanTargetsManagedModule(plan);

        bool profileExistsNow = File.Exists(plan.ProfilePath);
        string profileNow = profileExistsNow
            ? await File.ReadAllTextAsync(plan.ProfilePath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        if (profileExistsNow != plan.ProfileExisted
            || !profileNow.Equals(plan.Before, StringComparison.Ordinal))
        {
            return Failure(
                plan,
                "The PowerShell Profile changed after the preview was created; refresh and review the new plan.");
        }

        string? moduleNow = File.Exists(plan.ModulePath)
            ? await File.ReadAllTextAsync(plan.ModulePath, cancellationToken).ConfigureAwait(false)
            : null;
        if (!string.Equals(moduleNow, plan.ModuleBefore, StringComparison.Ordinal))
        {
            return Failure(
                plan,
                "The AutoEnvPlus PowerShell module changed after the preview was created; refresh the plan.");
        }

        string? snapshotPath = null;
        try
        {
            if (plan.ModuleChanged)
            {
                await WriteFileAtomicallyAsync(
                    plan.ModulePath,
                    plan.ModuleContent,
                    cancellationToken).ConfigureAwait(false);
            }

            if (plan.ProfileChanged)
            {
                PowerShellProfileSnapshot snapshot = new(
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    plan.ProfilePath,
                    plan.ProfileExisted,
                    plan.Before,
                    plan.After);
                string snapshotDirectory = GetSnapshotDirectory();
                snapshotPath = Path.Combine(snapshotDirectory, snapshot.Id + ".json");
                await WriteFileAtomicallyAsync(
                    snapshotPath,
                    JsonSerializer.Serialize(snapshot, JsonOptions),
                    cancellationToken,
                    overwrite: false).ConfigureAwait(false);
                await WriteFileAtomicallyAsync(
                    plan.ProfilePath,
                    plan.After,
                    cancellationToken).ConfigureAwait(false);
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
            or JsonException)
        {
            return new PowerShellIntegrationResult(
                false,
                false,
                plan.ModulePath,
                snapshotPath,
                exception.Message);
        }
    }

    public async Task<PowerShellIntegrationResult> RollbackAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        string fullSnapshotPath = Path.GetFullPath(snapshotPath);
        try
        {
            EnsureChildPath(GetSnapshotDirectory(), fullSnapshotPath);
            if (!File.Exists(fullSnapshotPath))
            {
                return new PowerShellIntegrationResult(
                    false,
                    false,
                    ModulePath,
                    fullSnapshotPath,
                    "The PowerShell Profile snapshot does not exist.");
            }

            PowerShellProfileSnapshot? snapshot = JsonSerializer.Deserialize<PowerShellProfileSnapshot>(
                await File.ReadAllTextAsync(fullSnapshotPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            string expectedId = Path.GetFileNameWithoutExtension(fullSnapshotPath);
            if (snapshot is null
                || !Guid.TryParseExact(snapshot.Id, "N", out _)
                || !snapshot.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(snapshot.ProfilePath)
                || !Path.IsPathFullyQualified(snapshot.ProfilePath))
            {
                return new PowerShellIntegrationResult(
                    false,
                    false,
                    ModulePath,
                    fullSnapshotPath,
                    "The PowerShell Profile snapshot is invalid.");
            }

            bool profileExistsNow = File.Exists(snapshot.ProfilePath);
            string profileNow = profileExistsNow
                ? await File.ReadAllTextAsync(snapshot.ProfilePath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            if (!profileExistsNow || !profileNow.Equals(snapshot.After, StringComparison.Ordinal))
            {
                return new PowerShellIntegrationResult(
                    false,
                    false,
                    ModulePath,
                    fullSnapshotPath,
                    "The PowerShell Profile changed after this snapshot; automatic rollback would overwrite newer changes.");
            }

            if (snapshot.ProfileExisted)
            {
                await WriteFileAtomicallyAsync(
                    snapshot.ProfilePath,
                    snapshot.Before,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Delete(snapshot.ProfilePath);
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
            or JsonException
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

    private void EnsurePlanTargetsManagedModule(PowerShellIntegrationPlan plan)
    {
        if (!Path.GetFullPath(plan.ModulePath).Equals(ModulePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The PowerShell module plan must target the AutoEnvPlus managed shell directory.",
                nameof(plan));
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

    private static async Task WriteFileAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken,
        bool overwrite = true)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The target file does not have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                FileEncoding,
                cancellationToken).ConfigureAwait(false);
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

    private static void EnsureChildPath(string root, string candidate)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidate);
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The PowerShell Profile snapshot must remain inside the AutoEnvPlus state directory.");
        }
    }

    private sealed record ProfileLine(string Text, string LineEnding);
}
