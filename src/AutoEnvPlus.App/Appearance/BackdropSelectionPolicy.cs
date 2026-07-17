using AutoEnvPlus.Core.Settings;

namespace AutoEnvPlus.App.Appearance;

internal enum BackdropSelection
{
    Mica,
    DesktopAcrylic,
    SolidHighContrast,
    SolidTransparencyDisabled,
    SolidPreferred,
    SolidUnsupported,
}

internal static class BackdropSelectionPolicy
{
    public static BackdropSelection Select(
        bool highContrast,
        bool transparencyEffectsEnabled,
        bool micaSupported,
        bool desktopAcrylicSupported,
        BackdropPreference preference = BackdropPreference.Automatic)
    {
        if (highContrast)
        {
            return BackdropSelection.SolidHighContrast;
        }

        if (!transparencyEffectsEnabled)
        {
            return BackdropSelection.SolidTransparencyDisabled;
        }

        if (preference == BackdropPreference.Solid)
        {
            return BackdropSelection.SolidPreferred;
        }

        if (preference == BackdropPreference.Mica)
        {
            return micaSupported
                ? BackdropSelection.Mica
                : desktopAcrylicSupported
                    ? BackdropSelection.DesktopAcrylic
                    : BackdropSelection.SolidUnsupported;
        }

        if (preference == BackdropPreference.Acrylic)
        {
            return desktopAcrylicSupported
                ? BackdropSelection.DesktopAcrylic
                : micaSupported
                    ? BackdropSelection.Mica
                    : BackdropSelection.SolidUnsupported;
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
