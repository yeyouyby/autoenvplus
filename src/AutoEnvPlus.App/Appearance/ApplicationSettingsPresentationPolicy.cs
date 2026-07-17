using AutoEnvPlus.Core.Settings;

namespace AutoEnvPlus.App.Appearance;

internal enum RequestedElementTheme
{
    Default,
    Light,
    Dark,
}

internal readonly record struct PagePaddingMetrics(
    double Left,
    double Top,
    double Right,
    double Bottom);

internal static class ApplicationSettingsPresentationPolicy
{
    public static string GetStartupNavigationTag(StartupDestination destination) => destination switch
    {
        StartupDestination.Overview => "dashboard",
        StartupDestination.Languages => "languages",
        StartupDestination.Projects => "projects",
        StartupDestination.Diagnostics => "doctor",
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    public static RequestedElementTheme GetRequestedTheme(
        ApplicationThemePreference preference)
    {
        return preference switch
        {
            ApplicationThemePreference.System => RequestedElementTheme.Default,
            ApplicationThemePreference.Light => RequestedElementTheme.Light,
            ApplicationThemePreference.Dark => RequestedElementTheme.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
    }

    public static PagePaddingMetrics GetPagePadding(InterfaceDensity density) => density switch
    {
        InterfaceDensity.Comfortable => new PagePaddingMetrics(32, 24, 32, 32),
        InterfaceDensity.Compact => new PagePaddingMetrics(20, 16, 20, 20),
        _ => throw new ArgumentOutOfRangeException(nameof(density)),
    };
}
