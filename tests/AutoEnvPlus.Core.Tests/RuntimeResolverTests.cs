using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class RuntimeResolverTests
{
    private static readonly RuntimeInstallation[] Installations =
    [
        RuntimeInstallation.Create("py311", RuntimeKind.Python, "3.11.9"),
        RuntimeInstallation.Create("py312", RuntimeKind.Python, "3.12.8"),
        RuntimeInstallation.Create("py313", RuntimeKind.Python, "3.13.5"),
    ];

    [Fact]
    public void Resolve_UsesSessionBeforeProjectAndGlobal()
    {
        RuntimeResolutionContext context = new(
            Session: Profile("3.11"),
            Project: Profile("3.12"),
            Global: Profile("3.13"));

        RuntimeResolutionResult result = new RuntimeResolver().Resolve(
            RuntimeKind.Python,
            context,
            Installations);

        Assert.True(result.Success);
        Assert.Equal(ResolutionScope.Session, result.Scope);
        Assert.Equal(RuntimeVersion.Parse("3.11.9"), result.Installation!.Version);
    }

    [Fact]
    public void Resolve_MissingProjectPinDoesNotSilentlyUseGlobal()
    {
        RuntimeResolutionContext context = new(
            Project: Profile("3.10"),
            Global: Profile("3.13"));

        RuntimeResolutionResult result = new RuntimeResolver().Resolve(
            RuntimeKind.Python,
            context,
            Installations);

        Assert.False(result.Success);
        Assert.Equal(ResolutionScope.Project, result.Scope);
    }

    [Fact]
    public void Resolve_AutomaticSelectsHighestStableVersion()
    {
        RuntimeInstallation[] installations =
        [
            .. Installations,
            RuntimeInstallation.Create("preview", RuntimeKind.Python, "3.14.0-rc.1"),
        ];

        RuntimeResolutionResult result = new RuntimeResolver().Resolve(
            RuntimeKind.Python,
            new RuntimeResolutionContext(),
            installations);

        Assert.Equal(RuntimeVersion.Parse("3.13.5"), result.Installation!.Version);
    }

    private static RuntimeProfile Profile(string selector) => new(
        new Dictionary<RuntimeKind, VersionSelector>
        {
            [RuntimeKind.Python] = VersionSelector.Parse(selector),
        });
}
