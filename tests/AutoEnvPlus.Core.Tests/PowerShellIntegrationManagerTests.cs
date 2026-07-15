using System.Text.Json;
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
        Assert.Contains(
            "[ValidateSet('python', 'node', 'java', 'dotnet')]",
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

        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        PowerShellIntegrationResult rollback = await manager.RollbackAsync(applied.SnapshotPath!);

        Assert.True(applied.Success);
        Assert.True(File.Exists(applied.SnapshotPath));
        Assert.True(rollback.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(profile));
    }

    [Fact]
    public async Task RollbackAsync_RemovesProfileThatDidNotExistBeforeInstall()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string profile = Path.Combine(_root, "new-profile.ps1");

        PowerShellIntegrationResult applied = await manager.ApplyAsync(manager.PlanInstall(profile));
        PowerShellIntegrationResult rollback = await manager.RollbackAsync(applied.SnapshotPath!);

        Assert.True(applied.Success);
        Assert.True(rollback.Success);
        Assert.False(File.Exists(profile));
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
    public async Task RollbackAsync_RejectsOutsideAndMalformedSnapshots()
    {
        PowerShellIntegrationManager manager = CreateManager();
        string outside = Path.Combine(_root, "outside.json");
        await File.WriteAllTextAsync(outside, "{}");

        PowerShellIntegrationResult outsideResult = await manager.RollbackAsync(outside);

        Assert.False(outsideResult.Success);
        Assert.Contains("inside the AutoEnvPlus state directory", outsideResult.Error, StringComparison.Ordinal);

        string snapshotDirectory = Path.Combine(
            _root,
            "managed",
            "state",
            "powershell-profile-snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        string malformed = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(malformed, "{ definitely not json }");

        PowerShellIntegrationResult malformedResult = await manager.RollbackAsync(malformed);

        Assert.False(malformedResult.Success);
        Assert.NotNull(malformedResult.Error);
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

        PowerShellIntegrationResult editedSnapshot = await manager.RollbackAsync(applied.SnapshotPath!);

        Assert.False(editedSnapshot.Success);
        Assert.Contains("invalid", editedSnapshot.Error, StringComparison.OrdinalIgnoreCase);

        string newerProfile = Path.Combine(_root, "newer-profile.ps1");
        await File.WriteAllTextAsync(newerProfile, "# another original\n");
        PowerShellIntegrationResult reapplied = await manager.ApplyAsync(
            manager.PlanInstall(newerProfile));
        Assert.True(reapplied.Success);
        Assert.NotNull(reapplied.SnapshotPath);
        await File.AppendAllTextAsync(newerProfile, "# newer user change\n");

        PowerShellIntegrationResult newerChange = await manager.RollbackAsync(reapplied.SnapshotPath!);

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
