using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AutoEnvPlus.Core.Storage;

public sealed record MavenSettingsReadResult(
    string SettingsPath,
    bool Exists,
    string? Content,
    string? LocalRepository,
    string? Error);

public sealed record MavenSettingsMutation(
    string SettingsPath,
    bool Existed,
    string? Before,
    string After);

public sealed partial class MavenSettingsXmlService
{
    private const string SettingsNamespace = "http://maven.apache.org/SETTINGS/1.0.0";

    public MavenSettingsReadResult Read(
        string settingsPath,
        CacheEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(environment);
        string fullPath = Path.GetFullPath(settingsPath);
        if (!File.Exists(fullPath))
        {
            return new MavenSettingsReadResult(fullPath, false, null, null, null);
        }

        try
        {
            string content = File.ReadAllText(fullPath);
            XDocument document = Parse(content);
            XElement root = RequireSettingsRoot(document);
            IReadOnlyList<XElement> repositories = FindLocalRepositories(root);
            if (repositories.Count > 1)
            {
                throw new InvalidDataException(
                    "Maven settings.xml contains more than one localRepository element.");
            }

            string? configured = repositories.SingleOrDefault()?.Value.Trim();
            if (string.IsNullOrWhiteSpace(configured))
            {
                return new MavenSettingsReadResult(fullPath, true, content, null, null);
            }

            string resolved = ResolvePath(configured, fullPath, environment);
            return new MavenSettingsReadResult(fullPath, true, content, resolved, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or XmlException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new MavenSettingsReadResult(fullPath, true, null, null, exception.Message);
        }
    }

    public MavenSettingsMutation CreateMutation(
        string settingsPath,
        string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullPath = Path.GetFullPath(settingsPath);
        string destination = Path.GetFullPath(destinationPath);
        bool existed = File.Exists(fullPath);
        string? before = existed ? File.ReadAllText(fullPath) : null;
        XDocument document = existed
            ? Parse(before!)
            : CreateDocument();
        XElement root = RequireSettingsRoot(document);
        IReadOnlyList<XElement> repositories = FindLocalRepositories(root);
        if (repositories.Count > 1)
        {
            throw new InvalidDataException(
                "Maven settings.xml contains more than one localRepository element.");
        }

        XElement? repository = repositories.SingleOrDefault();
        if (repository is null)
        {
            repository = new XElement(root.Name.Namespace + "localRepository", destination);
            InsertFirstElement(root, repository, DetectNewLine(before));
        }
        else
        {
            repository.Value = destination;
        }

        return new MavenSettingsMutation(
            fullPath,
            existed,
            before,
            Serialize(document, DetectNewLine(before)));
    }

    public async Task WriteAtomicallyAsync(
        string settingsPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(content);
        string fullPath = Path.GetFullPath(settingsPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Maven settings.xml requires a parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static XDocument Parse(string content)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        };
        using StringReader input = new(content);
        using XmlReader reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }

    private static XDocument CreateDocument()
    {
        XNamespace settings = SettingsNamespace;
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(settings + "settings"));
    }

    private static XElement RequireSettingsRoot(XDocument document)
    {
        XElement root = document.Root
            ?? throw new InvalidDataException("Maven settings.xml does not contain a root element.");
        if (!root.Name.LocalName.Equals("settings", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Maven settings.xml root element must be settings.");
        }

        return root;
    }

    private static IReadOnlyList<XElement> FindLocalRepositories(XElement root) => root
        .Elements()
        .Where(element => element.Name.LocalName.Equals(
            "localRepository",
            StringComparison.Ordinal))
        .ToArray();

    private static string ResolvePath(
        string configured,
        string settingsPath,
        CacheEnvironment environment)
    {
        string expanded = MavenExpressionRegex().Replace(configured, match =>
        {
            string name = match.Groups[1].Value;
            if (name.Equals("user.home", StringComparison.OrdinalIgnoreCase))
            {
                return environment.UserProfile;
            }

            if (name.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
            {
                string? value = environment.GetVariable(name[4..]);
                return value ?? match.Value;
            }

            return match.Value;
        });
        expanded = WindowsEnvironmentRegex().Replace(expanded, match =>
            environment.GetVariable(match.Groups[1].Value) ?? match.Value);
        if (expanded.Contains("${", StringComparison.Ordinal)
            || WindowsEnvironmentRegex().IsMatch(expanded))
        {
            throw new InvalidDataException(
                $"Maven localRepository contains an unresolved expression: {configured}");
        }

        if (expanded.Equals("~", StringComparison.Ordinal)
            || expanded.StartsWith("~\\", StringComparison.Ordinal)
            || expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                environment.UserProfile,
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        expanded = expanded.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new InvalidDataException(
                $"Maven localRepository must resolve to an absolute path: {configured}");
        }

        _ = settingsPath;
        return Path.GetFullPath(expanded);
    }

    private static void InsertFirstElement(
        XElement root,
        XElement element,
        string newLine)
    {
        string indent = DetectIndent(root) ?? "  ";
        XNode? first = root.Nodes().FirstOrDefault();
        if (first is XText text && string.IsNullOrWhiteSpace(text.Value))
        {
            first.AddAfterSelf(element, new XText(newLine + indent));
            return;
        }

        root.AddFirst(
            new XText(newLine + indent),
            element,
            new XText(newLine));
    }

    private static string? DetectIndent(XElement root)
    {
        foreach (XText text in root.Nodes().OfType<XText>())
        {
            int newline = Math.Max(
                text.Value.LastIndexOf('\n'),
                text.Value.LastIndexOf('\r'));
            if (newline >= 0 && newline + 1 < text.Value.Length)
            {
                string indent = text.Value[(newline + 1)..];
                if (indent.All(char.IsWhiteSpace))
                {
                    return indent;
                }
            }
        }

        return null;
    }

    private static string Serialize(XDocument document, string newLine)
    {
        using MemoryStream output = new();
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            NewLineChars = newLine,
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = document.Declaration is null,
        };
        using (XmlWriter writer = XmlWriter.Create(output, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string DetectNewLine(string? content)
    {
        if (content is null)
        {
            return "\r\n";
        }

        int lineFeed = content.IndexOf('\n', StringComparison.Ordinal);
        return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex MavenExpressionRegex();

    [GeneratedRegex("%([^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsEnvironmentRegex();
}
