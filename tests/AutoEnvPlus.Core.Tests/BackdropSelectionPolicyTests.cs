using AutoEnvPlus.App.Appearance;

namespace AutoEnvPlus.Core.Tests;

public sealed class BackdropSelectionPolicyTests
{
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
