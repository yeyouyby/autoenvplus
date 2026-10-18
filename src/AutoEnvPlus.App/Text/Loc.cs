using System.Globalization;

namespace AutoEnvPlus.App.Text;

/// <summary>
/// Resolves localized UI strings from the application's resource map
/// (Strings/<language>/Resources.resw, compiled into the app PRI).
/// XAML elements bind through x:Uid; code-behind calls <see cref="Get"/> with
/// the same "<Scope>.<Element>" key prefix. Missing keys fall back
/// to the default language (zh-CN) inside MRT Core.
/// </summary>
public static class Loc
{
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? _loader;

    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader Loader =>
        _loader ??= new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();

    /// <summary>Returns the localized string for the given resource key.</summary>
    /// <remarks>
    /// Keys use the same dotted convention as the .resw table and x:Uid
    /// (e.g. "Settings.Language.Option.System"). MRT Core stores those keys
    /// as slash-separated paths (the RESW indexer converts dots to slashes)
    /// and ResourceLoader.GetString neither converts dots nor looks outside
    /// the "Resources" subtree, so the dots are translated here once for
    /// every caller.
    /// </remarks>
    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string value = Loader.GetString(key.Replace('.', '/'));
        return value;
    }

    /// <summary>
    /// Returns the localized string for the given key with placeholders
    /// ({0}, {1}, ...) filled from <paramref name="args"/> using the current
    /// UI culture.
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
