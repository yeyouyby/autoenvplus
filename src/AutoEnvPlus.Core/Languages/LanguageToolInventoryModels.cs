using System.Security.Cryptography;
using System.Text;

namespace AutoEnvPlus.Core.Languages;

public sealed record LanguageToolInventoryEntry(
    string ToolId,
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<string> DetectedCommands);

public sealed record LanguageToolInventorySnapshot(
    DateTimeOffset CapturedAtUtc,
    string CatalogFingerprint,
    IReadOnlyList<LanguageToolInventoryEntry> Tools)
{
    public const int MaximumToolCount = 512;
    public const int MaximumCommandsPerTool = 64;
    public const int MaximumIdentifierLength = 128;

    public bool IsCompatibleWith(LanguageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return CatalogFingerprint.Equals(
            ComputeCatalogFingerprint(catalog),
            StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> GetDetectedCommands(LanguageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> commands = new(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageToolInventoryEntry entry in Tools)
        {
            if (!catalog.TryGetTool(entry.ToolId, out LanguageToolDefinition? tool))
            {
                continue;
            }

            foreach (string command in entry.DetectedCommands)
            {
                if (tool!.DiscoveryCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
                {
                    commands.Add(command);
                }
            }
        }

        return commands;
    }

    public void Validate()
    {
        if (CapturedAtUtc == default || CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException("The language tool inventory timestamp is invalid.");
        }

        if (CatalogFingerprint.Length != 64 || CatalogFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The language tool inventory fingerprint is invalid.");
        }

        if (Tools.Count > MaximumToolCount)
        {
            throw new InvalidDataException("The language tool inventory contains too many tools.");
        }

        HashSet<string> toolIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageToolInventoryEntry entry in Tools)
        {
            ValidateIdentifier(entry.ToolId, "tool ID");
            if (entry.ScannedAtUtc == default
                || entry.ScannedAtUtc > CapturedAtUtc.AddMinutes(5))
            {
                throw new InvalidDataException(
                    "A language tool inventory scan timestamp is invalid.");
            }

            if (!toolIds.Add(entry.ToolId))
            {
                throw new InvalidDataException("Language tool inventory IDs must be unique.");
            }

            if (entry.DetectedCommands.Count > MaximumCommandsPerTool)
            {
                throw new InvalidDataException("A language tool inventory entry contains too many commands.");
            }

            HashSet<string> commands = new(StringComparer.OrdinalIgnoreCase);
            foreach (string command in entry.DetectedCommands)
            {
                ValidateIdentifier(command, "command");
                if (!commands.Add(command))
                {
                    throw new InvalidDataException("Detected commands must be unique within a tool.");
                }
            }
        }
    }

    public static LanguageToolInventorySnapshot Merge(
        LanguageCatalog catalog,
        LanguageToolInventorySnapshot? current,
        LanguageToolInventorySnapshot update)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(update);
        current?.Validate();
        update.Validate();

        Dictionary<string, LanguageToolInventoryEntry> entries = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (LanguageToolInventoryEntry entry in current?.Tools ?? [])
        {
            if (catalog.TryGetTool(entry.ToolId, out LanguageToolDefinition? tool))
            {
                entries[tool!.Id] = NormalizeEntry(tool, entry);
            }
        }

        foreach (LanguageToolInventoryEntry entry in update.Tools)
        {
            if (!catalog.TryGetTool(entry.ToolId, out LanguageToolDefinition? tool))
            {
                continue;
            }

            LanguageToolInventoryEntry normalized = NormalizeEntry(tool!, entry);
            if (!entries.TryGetValue(tool!.Id, out LanguageToolInventoryEntry? existing)
                || normalized.ScannedAtUtc >= existing.ScannedAtUtc)
            {
                entries[tool.Id] = normalized;
            }
        }

        LanguageToolInventorySnapshot merged = new(
            current is null || update.CapturedAtUtc >= current.CapturedAtUtc
                ? update.CapturedAtUtc
                : current.CapturedAtUtc,
            ComputeCatalogFingerprint(catalog),
            entries.Values.OrderBy(entry => entry.ToolId, StringComparer.Ordinal).ToArray());
        merged.Validate();
        return merged;
    }

    public static string ComputeCatalogFingerprint(LanguageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        StringBuilder canonical = new();
        foreach (LanguageToolDefinition tool in catalog.Tools.OrderBy(
            item => item.Id,
            StringComparer.Ordinal))
        {
            Append(canonical, tool.Id);
            canonical.Append(tool.Capabilities.Discover ? '1' : '0');
            foreach (string command in tool.DiscoveryCommands.Order(StringComparer.Ordinal))
            {
                Append(canonical, command);
            }

            canonical.AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string value) =>
        target.Append(value.Length).Append(':').Append(value).Append(';');

    private static LanguageToolInventoryEntry NormalizeEntry(
        LanguageToolDefinition tool,
        LanguageToolInventoryEntry entry) => new(
            tool.Id,
            entry.ScannedAtUtc,
            entry.DetectedCommands
                .Where(command => tool.DiscoveryCommands.Contains(
                    command,
                    StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static void ValidateIdentifier(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumIdentifierLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"The language tool inventory {description} is invalid.");
        }
    }
}
