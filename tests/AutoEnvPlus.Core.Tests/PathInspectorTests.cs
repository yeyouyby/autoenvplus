using AutoEnvPlus.Core.Environment;

namespace AutoEnvPlus.Core.Tests;

public sealed class PathInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-{Guid.NewGuid():N}");

    public PathInspectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Inspect_FindsMissingAndDuplicateEntries()
    {
        string existing = Directory.CreateDirectory(Path.Combine(_root, "existing")).FullName;
        string missing = Path.Combine(_root, "missing");
        string processPath = string.Join(';', existing, missing, existing + Path.DirectorySeparatorChar);

        PathInspectionReport report = new PathInspector().Inspect(processPath, existing, string.Empty);

        Assert.Equal(1, report.MissingCount);
        Assert.Equal(1, report.DuplicateCount);
        Assert.Equal(PathEntryScope.User, report.Entries[0].Scope);
    }

    [Fact]
    public void Inspect_FindsCommandShadowingInPathOrder()
    {
        string first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        File.WriteAllText(Path.Combine(first, "python.exe"), string.Empty);
        File.WriteAllText(Path.Combine(second, "python.exe"), string.Empty);

        PathInspectionReport report = new PathInspector().Inspect(
            string.Join(';', first, second),
            string.Empty,
            string.Empty,
            ["python"]);

        CommandConflict conflict = Assert.Single(report.Conflicts);
        Assert.Equal("python", conflict.Command);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Equal(0, conflict.Candidates[0].PathIndex);
    }

    [Fact]
    public void Inspect_UsesNormalizedPathExtOrderWithinEachDirectory()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "extensions")).FullName;
        string com = Path.Combine(directory, "python.com");
        string exe = Path.Combine(directory, "python.exe");
        File.WriteAllText(com, string.Empty);
        File.WriteAllText(exe, string.Empty);

        PathInspectionReport report = new PathInspector("com;.COM;.eXe").Inspect(
            directory,
            string.Empty,
            string.Empty,
            ["python"]);

        CommandResolution resolution = Assert.Single(report.CommandResolutions);
        Assert.Equal(com, resolution.Winner!.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal([com, exe], resolution.Candidates.Select(candidate => candidate.ExecutablePath),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_FallsBackToWindowsExecutableOrderWhenPathExtHasNoSafeEntries()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "fallback-extensions")).FullName;
        string com = Path.Combine(directory, "node.com");
        string exe = Path.Combine(directory, "node.exe");
        File.WriteAllText(com, string.Empty);
        File.WriteAllText(exe, string.Empty);

        PathInspectionReport report = new PathInspector(" ; ../bad ; . ;").Inspect(
            directory,
            string.Empty,
            string.Empty,
            ["node"]);

        CommandResolution resolution = Assert.Single(report.CommandResolutions);
        Assert.Equal(com, resolution.Winner!.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, resolution.Candidates.Count);
    }

    [Fact]
    public void MergePathValues_PreservesProcessPrecedenceAndAddsPersistedEntries()
    {
        string process = Directory.CreateDirectory(Path.Combine(_root, "process")).FullName;
        string user = Directory.CreateDirectory(Path.Combine(_root, "user")).FullName;
        string machine = Directory.CreateDirectory(Path.Combine(_root, "machine")).FullName;
        File.WriteAllText(Path.Combine(process, "gcc.exe"), string.Empty);
        File.WriteAllText(Path.Combine(user, "gcc.exe"), string.Empty);
        File.WriteAllText(Path.Combine(machine, "gcc.exe"), string.Empty);

        PathInspectionReport report = new PathInspector().InspectMerged(
            process,
            $"{process}{Path.DirectorySeparatorChar};{user}",
            machine,
            ["gcc"]);

        Assert.Equal([process, user, machine], report.Entries.Select(entry => entry.ExpandedValue));
        CommandResolution resolution = Assert.Single(report.CommandResolutions);
        Assert.Equal(Path.Combine(process, "gcc.exe"), resolution.Winner!.ExecutablePath);
        Assert.Equal(3, resolution.Candidates.Count);
        Assert.Equal(0, report.DuplicateCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
