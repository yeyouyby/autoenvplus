using AutoEnvPlus.Core.Environment;

namespace AutoEnvPlus.Core.Tests;

public sealed class ManagedRootResolverTests
{
    [Fact]
    public void TryResolve_PrefersExplicitRootOverEnvironmentAndDefault()
    {
        string explicitRoot = Path.Combine(Path.GetTempPath(), "explicit", "..", "selected");

        bool resolved = ManagedRootResolver.TryResolve(
            explicitRoot,
            Path.Combine(Path.GetTempPath(), "environment"),
            Path.Combine(Path.GetTempPath(), "local"),
            out string? managedRoot,
            out string? error);

        Assert.True(resolved, error);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "selected")), managedRoot);
    }

    [Fact]
    public void TryResolve_UsesEnvironmentBeforeLocalApplicationData()
    {
        string environmentRoot = Path.Combine(Path.GetTempPath(), "env", "..", "managed");

        bool resolved = ManagedRootResolver.TryResolve(
            null,
            environmentRoot,
            Path.Combine(Path.GetTempPath(), "local"),
            out string? managedRoot,
            out string? error);

        Assert.True(resolved, error);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "managed")), managedRoot);
    }

    [Fact]
    public void TryResolve_DefaultsToLocalApplicationDataAndAppendsDirectoryName()
    {
        string localApplicationData = Path.Combine(Path.GetTempPath(), "local", "..", "profile");

        bool resolved = ManagedRootResolver.TryResolve(
            null,
            null,
            localApplicationData,
            out string? managedRoot,
            out string? error);

        Assert.True(resolved, error);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "profile", ManagedRootResolver.DefaultDirectoryName)),
            managedRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\root")]
    public void TryResolve_RejectsInvalidExplicitRoot(string explicitRoot)
    {
        bool resolved = ManagedRootResolver.TryResolve(
            explicitRoot,
            Path.Combine(Path.GetTempPath(), "environment"),
            Path.Combine(Path.GetTempPath(), "local"),
            out string? managedRoot,
            out string? error);

        Assert.False(resolved);
        Assert.Null(managedRoot);
        Assert.Contains("managed root", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_RejectsEmptyConfiguredEnvironmentInsteadOfFallingBack()
    {
        bool resolved = ManagedRootResolver.TryResolve(
            null,
            "   ",
            Path.Combine(Path.GetTempPath(), "local"),
            out string? managedRoot,
            out string? error);

        Assert.False(resolved);
        Assert.Null(managedRoot);
        Assert.Contains("environment variable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalize_ProducesAbsolutePathWithoutTrailingSeparator()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "one", "..", "two")
            + Path.DirectorySeparatorChar;

        bool normalized = ManagedRootResolver.TryNormalize(
            candidate,
            out string? normalizedPath,
            out string? error);

        Assert.True(normalized, error);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "two")), normalizedPath);
    }

    [Fact]
    public void TryNormalize_RejectsDriveRoot()
    {
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        bool normalized = ManagedRootResolver.TryNormalize(
            driveRoot,
            out string? normalizedPath,
            out string? error);

        Assert.False(normalized);
        Assert.Null(normalizedPath);
        Assert.Contains("drive", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalize_RejectsNetworkShareRoot()
    {
        bool normalized = ManagedRootResolver.TryNormalize(
            @"\\server\share\",
            out string? normalizedPath,
            out string? error);

        Assert.False(normalized);
        Assert.Null(normalizedPath);
        Assert.Contains("root", error, StringComparison.OrdinalIgnoreCase);
    }
}
