namespace AutoEnvPlus.App.Appearance;

internal enum BackdropSelection
{
    Mica,
    DesktopAcrylic,
    SolidHighContrast,
    SolidTransparencyDisabled,
    SolidUnsupported,
}

internal static class BackdropSelectionPolicy
{
    public static BackdropSelection Select(
        bool highContrast,
        bool transparencyEffectsEnabled,
        bool micaSupported,
        bool desktopAcrylicSupported)
    {
        if (highContrast)
        {
            return BackdropSelection.SolidHighContrast;
        }

        if (!transparencyEffectsEnabled)
        {
            return BackdropSelection.SolidTransparencyDisabled;
        }

        if (micaSupported)
        {
            return BackdropSelection.Mica;
        }

        return desktopAcrylicSupported
            ? BackdropSelection.DesktopAcrylic
            : BackdropSelection.SolidUnsupported;
    }
}
