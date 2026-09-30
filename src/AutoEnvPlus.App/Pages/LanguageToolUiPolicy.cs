using AutoEnvPlus.Core.Languages;

namespace AutoEnvPlus.App.Pages;

internal enum ToolManagementKind
{
    None,
    OfficialArchive,
    WinGet,
}

internal static class LanguageToolUiPolicy
{
    public static ToolManagementKind GetManagementKind(LanguageToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.Capabilities.Install)
        {
            return ToolManagementKind.None;
        }

        ToolProviderAdapterKind[] adapterKinds = tool.Providers
            .Select(provider => LanguageToolProviderProfile.Create(tool, provider).AdapterKind)
            .ToArray();
        if (adapterKinds.Contains(ToolProviderAdapterKind.ManagedArchive))
        {
            return ToolManagementKind.OfficialArchive;
        }

        return adapterKinds.Contains(ToolProviderAdapterKind.WinGet)
            ? ToolManagementKind.WinGet
            : ToolManagementKind.None;
    }

    public static Uri GetLicenseReferenceUri(LanguageToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        string? spdxId = tool.License
            .Split([' ', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value is not "AND" and not "OR" and not "WITH");
        return spdxId is null || spdxId.StartsWith("LicenseRef-", StringComparison.Ordinal)
            ? tool.Homepage
            : new Uri($"https://spdx.org/licenses/{Uri.EscapeDataString(spdxId)}.html");
    }
}
