using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.Core.Settings;

namespace AutoEnvPlus.Core.Tests;

public sealed class ApplicationSettingsPresentationPolicyTests
{
    [Theory]
    [InlineData((int)StartupDestination.Overview, "dashboard")]
    [InlineData((int)StartupDestination.Languages, "languages")]
    [InlineData((int)StartupDestination.Projects, "projects")]
    [InlineData((int)StartupDestination.Diagnostics, "doctor")]
    public void GetStartupNavigationTag_MapsEverySupportedDestination(
        int destination,
        string expected)
    {
        Assert.Equal(
            expected,
            ApplicationSettingsPresentationPolicy.GetStartupNavigationTag(
                (StartupDestination)destination));
    }

    [Theory]
    [InlineData((int)ApplicationThemePreference.System, (int)RequestedElementTheme.Default)]
    [InlineData((int)ApplicationThemePreference.Light, (int)RequestedElementTheme.Light)]
    [InlineData((int)ApplicationThemePreference.Dark, (int)RequestedElementTheme.Dark)]
    public void GetRequestedTheme_MapsEverySupportedPreference(int preference, int expected)
    {
        Assert.Equal(
            (RequestedElementTheme)expected,
            ApplicationSettingsPresentationPolicy.GetRequestedTheme(
                (ApplicationThemePreference)preference));
    }

    [Fact]
    public void GetPagePadding_CompactDensityUsesSmallerStableInsets()
    {
        PagePaddingMetrics comfortable = ApplicationSettingsPresentationPolicy.GetPagePadding(
            InterfaceDensity.Comfortable);
        PagePaddingMetrics compact = ApplicationSettingsPresentationPolicy.GetPagePadding(
            InterfaceDensity.Compact);

        Assert.Equal(new PagePaddingMetrics(32, 24, 32, 32), comfortable);
        Assert.Equal(new PagePaddingMetrics(20, 16, 20, 20), compact);
        Assert.True(compact.Left < comfortable.Left);
        Assert.True(compact.Top < comfortable.Top);
    }
}
