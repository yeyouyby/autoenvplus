using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoEnvPlus.Core.Shell;

namespace AutoEnvPlus.Core.Tests;

public sealed class PowerShellIntegrationManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-PowerShell-{Guid.NewGuid():N}");

    [Fact]
    public void PlanInstall_GeneratesSessionCommandsAndSafelyQuotesPaths()
    {
        string managedRoot = Directory.CreateDirectory(
            Path.Combine(_root, "managed root's files")).FullName;
        string executable = CreateExecutable(Path.Combine(_root, "CLI files", "autoenvplus's cli.exe"));
        string assembly = Path.Combine(_root, "CLI files", "autoenvplus's cli.dll");
        PowerShellIntegrationManager manager = new(managedRoot, executable, [assembly]);
        string profile = Path.Combine(_root, "profile.ps1");

        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);

        Assert.Contains("function Use-AutoEnvPlusRuntime", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("function Clear-AutoEnvPlusRuntime", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_PYTHON_VERSION", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_NODE_VERSION", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_JAVA_VERSION", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_DOTNET_VERSION", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_PYTHON_RUNTIME_ID", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_NODE_RUNTIME_ID", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("AUTOENVPLUS_JAVA_RUNTIME_PROVIDER_ID", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("$previousRuntimeId", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("$previousProviderId", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateSet('python', 'node', 'java', 'dotnet', 'msvc', 'llvm', 'mingw', 'cmake', 'ninja')]",
            plan.ModuleContent,
            StringComparison.Ordinal);
        Assert.Contains("autoenvplus''s cli.exe'", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("autoenvplus''s cli.dll'", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("managed root''s files'", plan.ModuleContent, StringComparison.Ordinal);
        Assert.Contains("Import-Module", plan.After, StringComparison.Ordinal);
        Assert.Contains("managed root''s files", plan.After, StringComparison.Ordinal);
        Assert.True(plan.ProfileChanged);
        Assert.True(plan.ModuleChanged);
    }

    [Fact]
    public async Task GeneratedModule_ExactPinsRestoreOnFailureAndClearOnSelectorSuccess()
    {
        string managedRoot = Directory.CreateDirectory(Path.Combine(_root, "managed-session")).FullName;
        string fakeCli = Path.Combine(_root, "fake-autoenvplus.cmd");
        await File.WriteAllTextAsync(
            fakeCli,
            "@echo off\r\n"
            + "if /I \"%AUTOENVPLUS_PYTHON_VERSION%\"==\"fail\" exit /b 7\r\n"
            + "if defined AUTOENVPLUS_PYTHON_RUNTIME_ID if /I not \"%AUTOENVPLUS_PYTHON_RUNTIME_ID%\"==\"chosen-id\" exit /b 8\r\n"
            + "if defined AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID if /I not \"%AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID%\"==\"chosen-provider\" exit /b 9\r\n"
            + "exit /b 0\r\n");
        PowerShellIntegrationPlan plan = new PowerShellIntegrationManager(
            managedRoot,
            fakeCli).PlanInstall(Path.Combine(_root, "unused-profile.ps1"));
        Directory.CreateDirectory(Path.GetDirectoryName(plan.ModulePath)!);
        await File.WriteAllTextAsync(plan.ModulePath, plan.ModuleContent);
        string modulePath = plan.ModulePath.Replace("'", "''", StringComparison.Ordinal);
        string script = $$"""
            Import-Module -Name '{{modulePath}}' -Force -ErrorAction Stop
            $env:AUTOENVPLUS_PYTHON_VERSION = 'old'
            $env:AUTOENVPLUS_PYTHON_RUNTIME_ID = 'old-id'
            $env:AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID = 'old-provider'
            try {
                Use-AutoEnvPlusRuntime python fail -RuntimeId chosen-id -ProviderId chosen-provider
            } catch { }
            Write-Output ('FAIL=' + $env:AUTOENVPLUS_PYTHON_VERSION + '|' + $env:AUTOENVPLUS_PYTHON_RUNTIME_ID + '|' + $env:AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID)
            Use-AutoEnvPlusRuntime python ok
            Write-Output ('SUCCESS=' + $env:AUTOENVPLUS_PYTHON_VERSION + '|' + $env:AUTOENVPLUS_PYTHON_RUNTIME_ID + '|' + $env:AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID)
            Use-AutoEnvPlusRuntime python ok -RuntimeId chosen-id -ProviderId chosen-provider
            Write-Output ('PIN=' + $env:AUTOENVPLUS_PYTHON_VERSION + '|' + $env:AUTOENVPLUS_PYTHON_RUNTIME_ID + '|' + $env:AUTOENVPLUS_PYTHON_RUNTIME_PROVIDER_ID)
            """;
        string powershell = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = powershell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            script,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string output = await standardOutput;
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("FAIL=old|old-id|old-provider", output, StringComparison.Ordinal);
        Assert.Contains("SUCCESS=ok||", output, StringComparison.Ordinal);
        Assert.Contains("PIN=ok|chosen-id|chosen-provider", output, StringComparison.Ordinal);
        Assert.True(
            string.IsNullOrWhiteSpace(await standardError),
            $"PowerShell integration wrote an unexpected error: {await standardError}");
    }

    [Fact]
    public async Task ApplyAsync_IsIdempotentAndPreservesExistingProfileContent()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "Documents", "profile.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        const string existing = "Set-Alias ll Get-ChildItem\r\n# user content\r\n";
        await File.WriteAllTextAsync(profile, existing);

        PowerShellIntegrationResult first = await manager.ApplyAsync(manager.PlanInstall(profile));
        PowerShellIntegrationPlan secondPlan = manager.PlanInstall(profile);
        PowerShellIntegrationResult second = await manager.ApplyAsync(secondPlan);

        Assert.True(first.Success);
        Assert.True(first.Changed);
        Assert.NotNull(first.SnapshotPath);
        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Null(second.SnapshotPath);
        Assert.False(secondPlan.ProfileChanged);
        Assert.False(secondPlan.ModuleChanged);
        string installed = await File.ReadAllTextAsync(profile);
        Assert.StartsWith(existing, installed, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(installed, PowerShellIntegrationManager.BeginMarker));
        Assert.Equal(1, CountOccurrences(installed, PowerShellIntegrationManager.EndMarker));
    }

    [Fact]
    public async Task ApplyAsync_CleansDuplicateManagedBlocksWithoutRemovingUserContent()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "duplicate-profile.ps1");
        string managedBlock = string.Join(
            "\n",
            PowerShellIntegrationManager.BeginMarker,
            "# stale generated content",
            PowerShellIntegrationManager.EndMarker,
            string.Empty);
        await File.WriteAllTextAsync(
            profile,
            "# before\n" + managedBlock + "Write-Host 'keep me'\n" + managedBlock + "# after\n");

        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);
        PowerShellIntegrationResult result = await manager.ApplyAsync(plan);

        Assert.Equal(2, plan.ExistingProfileBlockCount);
        Assert.True(result.Success);
        string installed = await File.ReadAllTextAsync(profile);
        Assert.Contains("# before", installed, StringComparison.Ordinal);
        Assert.Contains("Write-Host 'keep me'", installed, StringComparison.Ordinal);
        Assert.Contains("# after", installed, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(installed, PowerShellIntegrationManager.BeginMarker));
        Assert.DoesNotContain("stale generated content", installed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAndRollback_RestoresProfileExactly()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "rollback-profile.ps1");
        const string original = "function prompt { 'custom> ' }\n";
        await File.WriteAllTextAsync(profile, original);
        byte[] originalBytes = await File.ReadAllBytesAsync(profile);

        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        using JsonDocument snapshotDocument = JsonDocument.Parse(
            await File.ReadAllBytesAsync(applied.SnapshotPath!));
        PowerShellIntegrationResult rollback = await manager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.True(applied.Success);
        Assert.True(File.Exists(applied.SnapshotPath));
        Assert.Equal(2, snapshotDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            manager.ModulePath,
            snapshotDocument.RootElement.GetProperty("modulePath").GetString());
        Assert.True(rollback.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(profile));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profile));
    }

    [Fact]
    public async Task RollbackAsync_AcceptsStrictLegacySnapshotAndRestoresUtf8WithoutBom()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "legacy-profile.ps1");
        const string original = "# legacy original\r\n";
        await File.WriteAllTextAsync(profile, original, new UTF8Encoding(false));
        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);
        await File.WriteAllTextAsync(profile, plan.After, new UTF8Encoding(false));
        string snapshotPath = await WriteLegacySnapshotAsync(
            manager,
            profile,
            profileExisted: true,
            plan.Before,
            plan.After);

        PowerShellIntegrationResult result = await manager.RollbackAsync(
            snapshotPath,
            profile);

        Assert.True(result.Success);
        byte[] restored = await File.ReadAllBytesAsync(profile);
        Assert.False(HasUtf8Bom(restored));
        Assert.Equal(new UTF8Encoding(false).GetBytes(original), restored);
    }

    [Fact]
    public async Task RollbackAsync_RejectsTamperedLegacyTransformation()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "legacy-tampered-profile.ps1");
        await File.WriteAllTextAsync(profile, "# original\n", new UTF8Encoding(false));
        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);
        string tamperedAfter = plan.After + "# injected after managed block\n";
        await File.WriteAllTextAsync(profile, tamperedAfter, new UTF8Encoding(false));
        string snapshotPath = await WriteLegacySnapshotAsync(
            manager,
            profile,
            profileExisted: true,
            plan.Before,
            tamperedAfter);

        PowerShellIntegrationResult result = await manager.RollbackAsync(
            snapshotPath,
            profile);

        Assert.False(result.Success);
        Assert.Contains("valid AutoEnvPlus transformation", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tamperedAfter, await File.ReadAllTextAsync(profile));
    }

    [Fact]
    public async Task RollbackAsync_BindsLegacySnapshotToExplicitProfile()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string selectedProfile = Path.Combine(_root, "selected-legacy-profile.ps1");
        string snapshotProfile = Path.Combine(_root, "snapshot-legacy-profile.ps1");
        await File.WriteAllTextAsync(snapshotProfile, "# original\n", new UTF8Encoding(false));
        PowerShellIntegrationPlan plan = manager.PlanInstall(snapshotProfile);
        await File.WriteAllTextAsync(snapshotProfile, plan.After, new UTF8Encoding(false));
        string snapshotPath = await WriteLegacySnapshotAsync(
            manager,
            snapshotProfile,
            profileExisted: true,
            plan.Before,
            plan.After);

        PowerShellIntegrationResult result = await manager.RollbackAsync(
            snapshotPath,
            selectedProfile);

        Assert.False(result.Success);
        Assert.Contains("explicitly selected Profile", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(plan.After, await File.ReadAllTextAsync(snapshotProfile));
    }

    [Fact]
    public async Task RollbackAsync_RejectsUnknownLegacySnapshotProperty()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "legacy-unknown-property.ps1");
        await File.WriteAllTextAsync(profile, "# original\n", new UTF8Encoding(false));
        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);
        await File.WriteAllTextAsync(profile, plan.After, new UTF8Encoding(false));
        string snapshotPath = await WriteLegacySnapshotAsync(
            manager,
            profile,
            profileExisted: true,
            plan.Before,
            plan.After);
        JsonObject snapshot = JsonNode.Parse(
            await File.ReadAllTextAsync(snapshotPath))!.AsObject();
        snapshot["unexpected"] = true;
        await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString());

        PowerShellIntegrationResult result = await manager.RollbackAsync(
            snapshotPath,
            profile);

        Assert.False(result.Success);
        Assert.Contains("invalid property set", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(plan.After, await File.ReadAllTextAsync(profile));
    }

    [Fact]
    public async Task RollbackAsync_LegacyV2SnapshotWithoutModulePathAcceptsEquivalentPathCasing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string managedRoot = Path.Combine(_root, "managed-case-root");
        string executable = CreateExecutable(Path.Combine(_root, "cli-case", "autoenvplus.exe"));
        PowerShellIntegrationManager originalManager = new(managedRoot, executable);
        string profile = Path.Combine(_root, "case-profile.ps1");
        await File.WriteAllTextAsync(profile, "# original\n");
        PowerShellIntegrationResult applied = await originalManager.ApplyAsync(
            originalManager.PlanInstall(profile));
        JsonObject snapshot = JsonNode.Parse(
            await File.ReadAllTextAsync(applied.SnapshotPath!))!.AsObject();
        Assert.True(snapshot.Remove("modulePath"));
        await File.WriteAllTextAsync(applied.SnapshotPath!, snapshot.ToJsonString());
        string differentlyCasedRoot = managedRoot.ToUpperInvariant();
        Assert.NotEqual(managedRoot, differentlyCasedRoot);
        PowerShellIntegrationManager differentlyCasedManager = new(
            differentlyCasedRoot,
            executable.ToUpperInvariant());

        PowerShellIntegrationResult result = await differentlyCasedManager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.True(result.Success);
        Assert.Equal("# original\n", await File.ReadAllTextAsync(profile));
    }

    [Fact]
    public async Task RollbackAsync_RemovesProfileThatDidNotExistBeforeInstall()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "new-profile.ps1");

        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        byte[] installedProfile = await File.ReadAllBytesAsync(profile);
        byte[] installedModule = await File.ReadAllBytesAsync(manager.ModulePath);
        PowerShellRollbackPreview preview = await manager.PreviewRollbackAsync(
            applied.SnapshotPath!,
            profile);
        PowerShellIntegrationResult rollback = await manager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.True(applied.Success);
        Assert.True(HasUtf8Bom(installedProfile));
        Assert.True(HasUtf8Bom(installedModule));
        Assert.True(preview.Success);
        Assert.Equal(Path.GetFullPath(profile), preview.ProfilePath);
        Assert.True(preview.DeletesProfile);
        Assert.True(rollback.Success);
        Assert.False(File.Exists(profile));
    }

    [Fact]
    public async Task ApplyAndRollback_PreservesUtf16LeProfileBytesExactly()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "utf16-profile.ps1");
        Encoding utf16Le = new UnicodeEncoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidBytes: true);
        byte[] original = WithPreamble(
            utf16Le,
            "function prompt { '自定义> ' }\r\n");
        await File.WriteAllBytesAsync(profile, original);

        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));

        Assert.True(applied.Success);
        byte[] installed = await File.ReadAllBytesAsync(profile);
        Assert.True(installed.AsSpan().StartsWith(utf16Le.GetPreamble()));
        Assert.Contains(
            PowerShellIntegrationManager.BeginMarker,
            utf16Le.GetString(installed, utf16Le.GetPreamble().Length, installed.Length - utf16Le.GetPreamble().Length),
            StringComparison.Ordinal);

        PowerShellIntegrationResult rollback = await manager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.True(rollback.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(profile));
    }

    [Fact]
    public async Task RollbackAsync_BindsSnapshotToExplicitProfileAndBlocksArbitraryDelete()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "created-profile.ps1");
        string victim = Path.Combine(_root, "victim.ps1");
        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        byte[] installed = await File.ReadAllBytesAsync(profile);
        await File.WriteAllBytesAsync(victim, installed);

        JsonObject snapshot = JsonNode.Parse(
            await File.ReadAllTextAsync(applied.SnapshotPath!))!.AsObject();
        snapshot["profilePath"] = Path.GetFullPath(victim);
        await File.WriteAllTextAsync(applied.SnapshotPath!, snapshot.ToJsonString());

        PowerShellIntegrationResult result = await manager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.False(result.Success);
        Assert.Contains("explicitly selected Profile", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(installed, await File.ReadAllBytesAsync(victim));
        Assert.True(File.Exists(profile));
    }

    [Fact]
    public async Task RollbackAsync_RejectsInvalidTransformationEvenWhenEditedHashMatches()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "transformation-profile.ps1");
        await File.WriteAllTextAsync(profile, "# original\n");
        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        JsonObject snapshot = JsonNode.Parse(
            await File.ReadAllTextAsync(applied.SnapshotPath!))!.AsObject();
        byte[] attackerBefore = Encoding.UTF8.GetBytes("# attacker content\n");
        snapshot["beforeBytes"] = Convert.ToBase64String(attackerBefore);
        snapshot["beforeSha256"] = Convert.ToHexString(SHA256.HashData(attackerBefore));
        await File.WriteAllTextAsync(applied.SnapshotPath!, snapshot.ToJsonString());

        PowerShellIntegrationResult result = await manager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.False(result.Success);
        Assert.Contains("valid AutoEnvPlus transformation", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            PowerShellIntegrationManager.BeginMarker,
            await File.ReadAllTextAsync(profile),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_RefusesConcurrentProfileChanges()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "concurrent-profile.ps1");
        await File.WriteAllTextAsync(profile, "# original\n");
        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);
        await File.WriteAllTextAsync(profile, "# changed elsewhere\n");

        PowerShellIntegrationResult result = await manager.ApplyAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("changed after the preview", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# changed elsewhere\n", await File.ReadAllTextAsync(profile));
    }

    [Fact]
    public async Task ApplyAsync_ProfileCommitFailureRestoresModuleAndRemovesSnapshot()
    {
        PowerShellIntegrationManager manager = CreateManager();
        Directory.CreateDirectory(Path.GetDirectoryName(manager.ModulePath)!);
        byte[] originalModule = Encoding.UTF8.GetBytes("# previous managed module\n");
        await File.WriteAllBytesAsync(manager.ModulePath, originalModule);

        string blockingFile = Path.Combine(_root, "profile-parent-is-a-file");
        await File.WriteAllTextAsync(blockingFile, "blocks directory creation");
        string profile = Path.Combine(blockingFile, "profile.ps1");

        PowerShellIntegrationResult result = await manager.ApplyAsync(
            manager.PlanInstall(profile));

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Null(result.SnapshotPath);
        Assert.DoesNotContain("rollback was incomplete", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalModule, await File.ReadAllBytesAsync(manager.ModulePath));
        Assert.False(File.Exists(profile));

        string snapshotDirectory = Path.Combine(
            _root,
            "managed",
            "state",
            "powershell-profile-snapshots");
        Assert.Empty(Directory.EnumerateFiles(snapshotDirectory, "*.json"));
    }

    [Fact]
    public async Task ApplyAsync_InvalidProfileStateAfterSnapshotRestoresModuleAndSnapshot()
    {
        string managedRoot = Path.Combine(_root, "managed-invalid-data");
        string executable = CreateExecutable(
            Path.Combine(_root, "cli-invalid-data", "autoenvplus.exe"));
        string profile = Path.Combine(_root, "invalid-data-profile.ps1");
        PowerShellIntegrationManager manager = new(
            managedRoot,
            executable,
            autoEnvPlusPrefixArguments: null,
            async (_, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(
                    profile,
                    new byte[(2 * 1024 * 1024) + 1],
                    cancellationToken);
            });
        Directory.CreateDirectory(Path.GetDirectoryName(manager.ModulePath)!);
        byte[] originalModule = Encoding.UTF8.GetBytes("# previous managed module\n");
        await File.WriteAllBytesAsync(manager.ModulePath, originalModule);
        await File.WriteAllTextAsync(profile, "# original\n");
        PowerShellIntegrationPlan plan = manager.PlanInstall(profile);

        PowerShellIntegrationResult result = await manager.ApplyAsync(plan);

        Assert.False(result.Success);
        Assert.Null(result.SnapshotPath);
        Assert.Contains("invalid size", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalModule, await File.ReadAllBytesAsync(manager.ModulePath));
        Assert.Equal((2 * 1024 * 1024) + 1, new FileInfo(profile).Length);
        string snapshotDirectory = Path.Combine(
            managedRoot,
            "state",
            "powershell-profile-snapshots");
        Assert.Empty(Directory.EnumerateFiles(snapshotDirectory, "*.json"));
    }

    [Fact]
    public async Task RollbackAsync_RejectsOutsideAndMalformedSnapshots()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string outside = Path.Combine(_root, "outside.json");
        await File.WriteAllTextAsync(outside, "{}");

        PowerShellIntegrationResult outsideResult = await manager.RollbackAsync(
            outside,
            Path.Combine(_root, "profile.ps1"));

        Assert.False(outsideResult.Success);
        Assert.Contains("direct .json child", outsideResult.Error, StringComparison.OrdinalIgnoreCase);

        string snapshotDirectory = Path.Combine(
            _root,
            "managed",
            "state",
            "powershell-profile-snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        string malformed = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(malformed, "{ definitely not json }");

        PowerShellIntegrationResult malformedResult = await manager.RollbackAsync(
            malformed,
            Path.Combine(_root, "profile.ps1"));

        Assert.False(malformedResult.Success);
        Assert.NotNull(malformedResult.Error);
    }

    [Fact]
    public async Task RollbackAsync_RejectsNestedOversizedAndReparseSnapshots()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string expectedProfile = Path.Combine(_root, "profile.ps1");
        string snapshotDirectory = Path.Combine(
            _root,
            "managed",
            "state",
            "powershell-profile-snapshots");
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(snapshotDirectory, "nested")).FullName;
        string nested = Path.Combine(nestedDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(nested, "{}");

        PowerShellIntegrationResult nestedResult = await manager.RollbackAsync(
            nested,
            expectedProfile);

        Assert.False(nestedResult.Success);
        Assert.Contains("direct .json child", nestedResult.Error, StringComparison.OrdinalIgnoreCase);

        string oversized = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(oversized, new byte[(8 * 1024 * 1024) + 1]);
        PowerShellIntegrationResult oversizedResult = await manager.RollbackAsync(
            oversized,
            expectedProfile);

        Assert.False(oversizedResult.Success);
        Assert.Contains("invalid size", oversizedResult.Error, StringComparison.OrdinalIgnoreCase);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string external = Path.Combine(_root, "external-snapshot.json");
        await File.WriteAllTextAsync(external, "{}");
        string linked = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            try
            {
                File.CreateSymbolicLink(linked, external);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            PowerShellIntegrationResult linkedResult = await manager.RollbackAsync(
                linked,
                expectedProfile);

            Assert.False(linkedResult.Success);
            Assert.Contains("reparse", linkedResult.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(linked))
            {
                File.Delete(linked);
            }
        }
    }

    [Fact]
    public async Task RollbackAsync_RejectsEditedSnapshotIdentityAndNewerProfileChanges()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "edited-profile.ps1");
        await File.WriteAllTextAsync(profile, "# original\n");
        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        string snapshotJson = await File.ReadAllTextAsync(applied.SnapshotPath!);
        using JsonDocument document = JsonDocument.Parse(snapshotJson);
        string editedJson = snapshotJson.Replace(
            document.RootElement.GetProperty("id").GetString()!,
            Guid.NewGuid().ToString("N"),
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(applied.SnapshotPath!, editedJson);

        PowerShellIntegrationResult editedSnapshot = await manager.RollbackAsync(
            applied.SnapshotPath!,
            profile);

        Assert.False(editedSnapshot.Success);
        Assert.Contains("invalid", editedSnapshot.Error, StringComparison.OrdinalIgnoreCase);

        string newerProfile = Path.Combine(_root, "newer-profile.ps1");
        await File.WriteAllTextAsync(newerProfile, "# another original\n");
        PowerShellIntegrationResult reapplied = await manager.ApplyAsync(
            manager.PlanInstall(newerProfile));
        Assert.True(reapplied.Success);
        Assert.NotNull(reapplied.SnapshotPath);
        await File.AppendAllTextAsync(newerProfile, "# newer user change\n");

        PowerShellIntegrationResult newerChange = await manager.RollbackAsync(
            reapplied.SnapshotPath!,
            newerProfile);

        Assert.False(newerChange.Success);
        Assert.Contains("newer changes", newerChange.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanInstall_RejectsUnmatchedManagedMarkers()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "invalid-profile.ps1");
        File.WriteAllText(profile, PowerShellIntegrationManager.BeginMarker + "\n# incomplete\n");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => manager.PlanInstall(profile));

        Assert.Contains("unmatched", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PowerShellIntegrationManager CreateManager()
    {
        string executable = CreateExecutable(Path.Combine(_root, "cli", "autoenvplus.exe"));
        return new PowerShellIntegrationManager(Path.Combine(_root, "managed"), executable);
    }

    private static string CreateExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test executable placeholder");
        return path;
    }

    private static async Task<string> WriteLegacySnapshotAsync(
        PowerShellIntegrationManager manager,
        string profilePath,
        bool profileExisted,
        string before,
        string after)
    {
        string id = Guid.NewGuid().ToString("N");
        string snapshotDirectory = Path.Combine(
            Path.GetDirectoryName(
                Path.GetDirectoryName(
                    Path.GetDirectoryName(manager.ModulePath)!)!)!,
            "state",
            "powershell-profile-snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        string snapshotPath = Path.Combine(snapshotDirectory, id + ".json");
        string json = JsonSerializer.Serialize(new
        {
            id,
            createdAtUtc = DateTimeOffset.UtcNow,
            profilePath = Path.GetFullPath(profilePath),
            profileExisted,
            before,
            after,
        });
        await File.WriteAllTextAsync(snapshotPath, json, new UTF8Encoding(false));
        return snapshotPath;
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });

    private static byte[] WithPreamble(Encoding encoding, string content)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] payload = encoding.GetBytes(content);
        byte[] bytes = new byte[preamble.Length + payload.Length];
        preamble.CopyTo(bytes, 0);
        payload.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
