namespace AutoEnvPlus.Core.Settings;

public enum StartupDestination
{
    Overview,
    Languages,
    Projects,
    Diagnostics,
}

public enum OverviewRefreshPolicy
{
    CachedOnly,
    Quick,
    Full,
}

public enum LanguageVisibilityPolicy
{
    TopTenAndDetected,
    EnabledOnly,
    AllBuiltIn,
}

public enum ApplicationThemePreference
{
    System,
    Light,
    Dark,
}

public enum BackdropPreference
{
    Automatic,
    Mica,
    Acrylic,
    Solid,
}

public enum InterfaceDensity
{
    Comfortable,
    Compact,
}

public enum CatalogUpdatePolicy
{
    Manual,
    Daily,
    Weekly,
}

public sealed record AutoEnvPlusApplicationSettings(
    StartupDestination StartupDestination,
    OverviewRefreshPolicy OverviewRefreshPolicy,
    int DeepScanIntervalHours,
    LanguageVisibilityPolicy LanguageVisibilityPolicy,
    int DefaultDownloadConnections,
    long DefaultDownloadMaximumBytes,
    ApplicationThemePreference Theme,
    BackdropPreference Backdrop,
    InterfaceDensity Density,
    bool ShellAutoActivation,
    bool UseManagedShims,
    CatalogUpdatePolicy CatalogUpdatePolicy,
    int LogRetentionDays,
    bool RequireDestructiveActionConfirmation,
    bool ShowExperimentalTools)
{
    public const int MinimumDeepScanIntervalHours = 1;
    public const int MaximumDeepScanIntervalHours = 8_760;
    public const long MinimumDownloadMaximumBytes = 1 * 1024 * 1024;
    public const long MaximumDownloadMaximumBytes = 1L * 1024 * 1024 * 1024 * 1024;
    public const int MinimumLogRetentionDays = 1;
    public const int MaximumLogRetentionDays = 365;

    public static AutoEnvPlusApplicationSettings Default { get; } = new(
        StartupDestination.Overview,
        OverviewRefreshPolicy.CachedOnly,
        DeepScanIntervalHours: 168,
        LanguageVisibilityPolicy.TopTenAndDetected,
        DefaultDownloadConnections: 8,
        DefaultDownloadMaximumBytes: 8L * 1024 * 1024 * 1024,
        ApplicationThemePreference.System,
        BackdropPreference.Automatic,
        InterfaceDensity.Comfortable,
        ShellAutoActivation: true,
        UseManagedShims: true,
        CatalogUpdatePolicy.Weekly,
        LogRetentionDays: 30,
        RequireDestructiveActionConfirmation: true,
        ShowExperimentalTools: false);

    public void Validate()
    {
        if (!Enum.IsDefined(StartupDestination)
            || !Enum.IsDefined(OverviewRefreshPolicy)
            || !Enum.IsDefined(LanguageVisibilityPolicy)
            || !Enum.IsDefined(Theme)
            || !Enum.IsDefined(Backdrop)
            || !Enum.IsDefined(Density)
            || !Enum.IsDefined(CatalogUpdatePolicy))
        {
            throw new InvalidDataException("Application settings contain an unsupported option.");
        }

        if (DeepScanIntervalHours is < MinimumDeepScanIntervalHours
            or > MaximumDeepScanIntervalHours)
        {
            throw new InvalidDataException(
                $"Deep scan interval must be between {MinimumDeepScanIntervalHours} and {MaximumDeepScanIntervalHours} hours.");
        }

        if (DefaultDownloadConnections is not (1 or 2 or 4 or 8 or 16))
        {
            throw new InvalidDataException(
                "Default download connections must be one of 1, 2, 4, 8, or 16.");
        }

        if (DefaultDownloadMaximumBytes is < MinimumDownloadMaximumBytes
            or > MaximumDownloadMaximumBytes)
        {
            throw new InvalidDataException(
                "Default download maximum is outside the supported range.");
        }

        if (LogRetentionDays is < MinimumLogRetentionDays or > MaximumLogRetentionDays)
        {
            throw new InvalidDataException(
                $"Log retention must be between {MinimumLogRetentionDays} and {MaximumLogRetentionDays} days.");
        }
    }
}
