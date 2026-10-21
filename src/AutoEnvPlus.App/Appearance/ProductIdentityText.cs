using AutoEnvPlus.App.Text;

namespace AutoEnvPlus.App.Appearance;

/// <summary>
/// Formats the structured product identity for display. Kept out of
/// <see cref="ProductIdentityPresentation"/> because that record is
/// compiled into the core test project, which has no access to the
/// resource loader; all user-visible identity strings resolve through
/// <see cref="Text.Loc"/> here instead.
/// </summary>
internal static class ProductIdentityText
{
    public static string DisplayVersion(ProductIdentityPresentation identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.IsPreview
            ? Loc.Format("Identity.Version.Preview", identity.ProductVersion)
            : Loc.Format("Identity.Version.Stable", identity.ProductVersion);
    }

    public static string WindowTitle(ProductIdentityPresentation identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Loc.Format("Identity.Window.Title", DisplayVersion(identity));
    }
}
