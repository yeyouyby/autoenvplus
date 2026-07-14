using AutoEnvPlus.Core.Runtimes;
using AutoEnvPlus.Core.State;

namespace AutoEnvPlus.Core.Tests;

public sealed class GlobalRuntimeProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoEnvPlus-Profile-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAsync_PreservesOtherSelectionsAcrossInstances()
    {
        GlobalRuntimeProfileStore first = new(_root);
        await first.SetAsync(RuntimeKind.Python, VersionSelector.Parse("3.13"));
        await first.SetAsync(RuntimeKind.NodeJs, VersionSelector.Parse("24-lts"));

        RuntimeProfile loaded = await new GlobalRuntimeProfileStore(_root).LoadAsync();

        Assert.Equal(VersionSelector.Parse("3.13"), loaded.Selections[RuntimeKind.Python]);
        Assert.Equal(VersionSelector.Parse("24-lts"), loaded.Selections[RuntimeKind.NodeJs]);
    }

    [Fact]
    public async Task LoadAsync_MissingProfileReturnsEmpty()
    {
        RuntimeProfile loaded = await new GlobalRuntimeProfileStore(_root).LoadAsync();

        Assert.Empty(loaded.Selections);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
