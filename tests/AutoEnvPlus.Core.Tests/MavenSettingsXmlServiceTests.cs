using AutoEnvPlus.Core.Storage;

namespace AutoEnvPlus.Core.Tests;

public sealed class MavenSettingsXmlServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Maven-{Guid.NewGuid():N}");

    [Fact]
    public void Read_ResolvesSupportedExpressions()
    {
        string profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        string settings = Path.Combine(_root, "settings.xml");
        File.WriteAllText(
            settings,
            "<settings><localRepository>${env.MAVEN_CACHE}\\repo</localRepository></settings>");
        CacheEnvironment environment = new(
            Path.Combine(_root, "local"),
            profile,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["MAVEN_CACHE"] = Path.Combine(_root, "cache root"),
            });

        MavenSettingsReadResult result = new MavenSettingsXmlService().Read(settings, environment);

        Assert.Null(result.Error);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "cache root", "repo")),
            result.LocalRepository);
    }

    [Fact]
    public void Read_RejectsDtdAndUnresolvedExpressions()
    {
        string profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        CacheEnvironment environment = new(
            Path.Combine(_root, "local"),
            profile,
            new Dictionary<string, string?>());
        string dtdSettings = Path.Combine(_root, "dtd.xml");
        File.WriteAllText(
            dtdSettings,
            "<!DOCTYPE settings [<!ENTITY xxe SYSTEM 'file:///windows/win.ini'>]><settings><localRepository>&xxe;</localRepository></settings>");
        string unresolvedSettings = Path.Combine(_root, "unresolved.xml");
        File.WriteAllText(
            unresolvedSettings,
            "<settings><localRepository>${unknown.value}</localRepository></settings>");

        MavenSettingsReadResult dtd = new MavenSettingsXmlService().Read(
            dtdSettings,
            environment);
        MavenSettingsReadResult unresolved = new MavenSettingsXmlService().Read(
            unresolvedSettings,
            environment);

        Assert.NotNull(dtd.Error);
        Assert.NotNull(unresolved.Error);
    }

    [Fact]
    public void CreateMutation_PreservesUnknownNodesAndCreatesNamespacedRepository()
    {
        string settings = Path.Combine(_root, ".m2", "settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        string before = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\">\r\n"
            + "  <mirrors><mirror><id>company</id></mirror></mirrors>\r\n"
            + "</settings>";
        File.WriteAllText(settings, before);
        string destination = Path.Combine(_root, "new repository");

        MavenSettingsMutation mutation = new MavenSettingsXmlService().CreateMutation(
            settings,
            destination);

        Assert.True(mutation.Existed);
        Assert.Equal(before, mutation.Before);
        Assert.Contains("<localRepository>", mutation.After, StringComparison.Ordinal);
        Assert.Contains(destination, mutation.After, StringComparison.Ordinal);
        Assert.Contains("<mirrors>", mutation.After, StringComparison.Ordinal);
        Assert.Contains("company", mutation.After, StringComparison.Ordinal);
        Assert.Contains("xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"", mutation.After);
    }

    [Fact]
    public void CreateMutation_RejectsDuplicateRepositories()
    {
        string settings = Path.Combine(_root, "settings.xml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            settings,
            "<settings><localRepository>C:\\one</localRepository><localRepository>C:\\two</localRepository></settings>");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new MavenSettingsXmlService().CreateMutation(
                settings,
                Path.Combine(_root, "new")));

        Assert.Contains("more than one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
