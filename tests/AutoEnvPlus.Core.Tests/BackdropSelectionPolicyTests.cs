using AutoEnvPlus.App.Appearance;
using AutoEnvPlus.Core.Settings;

namespace AutoEnvPlus.Core.Tests;

public sealed class BackdropSelectionPolicyTests
{
    [Theory]
    [InlineData((int)BackdropPreference.Solid, true, true, (int)BackdropSelection.SolidPreferred)]
    [InlineData((int)BackdropPreference.Mica, true, true, (int)BackdropSelection.Mica)]
    [InlineData((int)BackdropPreference.Mica, false, true, (int)BackdropSelection.DesktopAcrylic)]
    [InlineData((int)BackdropPreference.Acrylic, true, true, (int)BackdropSelection.DesktopAcrylic)]
    [InlineData((int)BackdropPreference.Acrylic, true, false, (int)BackdropSelection.Mica)]
    public void Select_HonorsMaterialPreferenceWithCompatibleFallback(
        int preference,
        bool micaSupported,
        bool acrylicSupported,
        int expected)
    {
        BackdropSelection actual = BackdropSelectionPolicy.Select(
            highContrast: false,
            transparencyEffectsEnabled: true,
            micaSupported: micaSupported,
            desktopAcrylicSupported: acrylicSupported,
            preference: (BackdropPreference)preference);

        Assert.Equal((BackdropSelection)expected, actual);
    }

    [Fact]
    public void Select_HighContrastOverridesMaterialPreference()
    {
        BackdropSelection actual = BackdropSelectionPolicy.Select(
            highContrast: true,
            transparencyEffectsEnabled: true,
            micaSupported: true,
            desktopAcrylicSupported: true,
            preference: BackdropPreference.Mica);

        Assert.Equal(BackdropSelection.SolidHighContrast, actual);
    }

    [Theory]
    [InlineData(true, true, true, true, (int)BackdropSelection.SolidHighContrast)]
    [InlineData(false, false, true, true, (int)BackdropSelection.SolidTransparencyDisabled)]
    [InlineData(false, true, true, true, (int)BackdropSelection.Mica)]
    [InlineData(false, true, false, true, (int)BackdropSelection.DesktopAcrylic)]
    [InlineData(false, true, false, false, (int)BackdropSelection.SolidUnsupported)]
    public void Select_UsesAccessibilityThenBestSupportedMaterial(
        bool highContrast,
        bool transparencyEffectsEnabled,
        bool micaSupported,
        bool desktopAcrylicSupported,
        int expected)
    {
        BackdropSelection actual = BackdropSelectionPolicy.Select(
            highContrast,
            transparencyEffectsEnabled,
            micaSupported,
            desktopAcrylicSupported);

        Assert.Equal((BackdropSelection)expected, actual);
    }
}
