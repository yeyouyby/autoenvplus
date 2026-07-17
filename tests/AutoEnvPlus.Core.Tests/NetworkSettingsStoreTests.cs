using System.Text.Json;
using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.Core.Tests;

public sealed class NetworkSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-Network-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_MissingFileReturnsDefaultSettings()
    {
        NetworkSettingsStore store = new(_root);

        NetworkSettingsLoadResult result = await store.LoadAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Settings);
        Assert.Empty(result.Settings.Tools!);
        Assert.Empty(result.Settings.Global!.NoProxy!);
        Assert.Equal(
            Path.Combine(_root, "state", "network-settings.json"),
            store.SettingsPath);
        Assert.False(File.Exists(store.SettingsPath));
    }

    [Theory]
    [InlineData(NetworkToolIds.RuntimePython)]
    [InlineData(NetworkToolIds.RuntimeNode)]
    [InlineData(NetworkToolIds.RuntimeJava)]
    [InlineData(NetworkToolIds.RuntimeDotNet)]
    [InlineData(NetworkToolIds.RuntimeCpp)]
    [InlineData(NetworkToolIds.Downloads)]
    [InlineData(NetworkToolIds.Pip)]
    [InlineData(NetworkToolIds.Npm)]
    [InlineData(NetworkToolIds.Pnpm)]
    [InlineData(NetworkToolIds.Yarn)]
    [InlineData(NetworkToolIds.NuGet)]
    [InlineData(NetworkToolIds.Maven)]
    [InlineData(NetworkToolIds.Gradle)]
    public void ToolWhitelist_ContainsRequiredRuntimeAndPackageTools(string toolId)
    {
        Assert.True(NetworkToolIds.IsSupported(toolId));
        Assert.Contains(toolId, NetworkToolIds.All, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsGlobalDefaultsAndToolOverrides()
    {
        NetworkSettingsStore store = new(_root);
        NetworkSettings settings = new(
            new GlobalNetworkSettings(
                "http://proxy.example:8080",
                "https://secure-proxy.example:8443",
                ["localhost", ".example.test", "LOCALHOST"],
                "https://mirror.example/packages"),
            new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Pip] = new(
                    HttpsProxy: NetworkEndpointOverride.Disabled,
                    Mirror: NetworkEndpointOverride.Custom(
                        "https://pypi.example/simple")),
                [NetworkToolIds.Npm] = new(
                    HttpProxy: NetworkEndpointOverride.Custom(
                        "http://npm-proxy.example:3128")),
            });

        NetworkSettingsSaveResult saved = await store.SaveAsync(settings);
        NetworkSettingsLoadResult loaded = await new NetworkSettingsStore(_root).LoadAsync();

        Assert.True(saved.Success);
        Assert.True(loaded.Success);
        Assert.Empty(saved.Errors);
        Assert.Empty(loaded.Errors);
        Assert.Equal("http://proxy.example:8080/", loaded.Settings!.Global!.HttpProxy);
        Assert.Equal(
            "https://secure-proxy.example:8443/",
            loaded.Settings.Global.HttpsProxy);
        Assert.Equal(
            ["localhost", ".example.test"],
            loaded.Settings.Global.NoProxy);
        Assert.Equal(
            "https://mirror.example/packages",
            loaded.Settings.Global.Mirror);

        ToolNetworkSettings pip = loaded.Settings.Tools![NetworkToolIds.Pip];
        Assert.Equal(NetworkEndpointOverrideMode.Inherit, pip.HttpProxy!.Mode);
        Assert.Equal(NetworkEndpointOverrideMode.Disabled, pip.HttpsProxy!.Mode);
        Assert.Equal(NetworkEndpointOverrideMode.Custom, pip.Mirror!.Mode);
        Assert.Equal("https://pypi.example/simple", pip.Mirror.Value);

        string json = await File.ReadAllTextAsync(store.SettingsPath);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            NetworkSettingsStore.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "disabled",
            document.RootElement
                .GetProperty("tools")
                .GetProperty(NetworkToolIds.Pip)
                .GetProperty("httpsProxy")
                .GetProperty("mode")
                .GetString());
        Assert.False(document.RootElement
            .GetProperty("tools")
            .GetProperty(NetworkToolIds.Pip)
            .TryGetProperty("httpProxy", out _));
    }

    [Fact]
    public void Resolve_EmptyToolOverrideInheritsEveryGlobalEndpoint()
    {
        NetworkSettings settings = new(
            new GlobalNetworkSettings(
                "http://proxy.example:8080",
                "https://secure-proxy.example:8443",
                ["localhost", "10.0.0.0/8"],
                "https://mirror.example/packages"),
            new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Pip] = new(),
            });

        NetworkSettingsResolutionResult result = NetworkSettingsResolver.Resolve(
            settings,
            "PIP");

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(NetworkToolIds.Pip, result.EffectiveSettings!.ToolId);
        Assert.Equal("http://proxy.example:8080/", result.EffectiveSettings.HttpProxy!.AbsoluteUri);
        Assert.Equal(
            "https://secure-proxy.example:8443/",
            result.EffectiveSettings.HttpsProxy!.AbsoluteUri);
        Assert.Equal(
            "https://mirror.example/packages",
            result.EffectiveSettings.Mirror!.AbsoluteUri);
        Assert.Equal(["localhost", "10.0.0.0/8"], result.EffectiveSettings.NoProxy);
    }

    [Fact]
    public async Task Resolve_DisabledOverrideTurnsOffGlobalProxyAndMirror()
    {
        NetworkSettingsStore store = new(_root);
        NetworkSettings settings = new(
            new GlobalNetworkSettings(
                "http://proxy.example:8080",
                "https://secure-proxy.example:8443",
                ["localhost"],
                "https://mirror.example/packages"),
            new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Npm] = new(
                    NetworkEndpointOverride.Disabled,
                    NetworkEndpointOverride.Disabled,
                    NetworkEndpointOverride.Disabled),
            });
        Assert.True((await store.SaveAsync(settings)).Success);
        NetworkSettings loaded = (await store.LoadAsync()).Settings!;

        NetworkSettingsResolutionResult result = NetworkSettingsResolver.Resolve(
            loaded,
            NetworkToolIds.Npm);

        Assert.True(result.Success);
        Assert.Null(result.EffectiveSettings!.HttpProxy);
        Assert.Null(result.EffectiveSettings.HttpsProxy);
        Assert.Null(result.EffectiveSettings.Mirror);
        Assert.Equal(["localhost"], result.EffectiveSettings.NoProxy);
    }

    [Fact]
    public void Resolve_CustomOverrideReplacesTheSelectedGlobalEndpoint()
    {
        NetworkSettings settings = new(
            new GlobalNetworkSettings(
                "http://global-proxy.example:8080",
                Mirror: "https://global-mirror.example/packages"),
            new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Pip] = new(
                    HttpProxy: NetworkEndpointOverride.Custom(
                        "https://tool-proxy.example:8443"),
                    Mirror: NetworkEndpointOverride.Custom(
                        "https://tool-mirror.example/simple")),
            });

        NetworkSettingsResolutionResult result = NetworkSettingsResolver.Resolve(
            settings,
            NetworkToolIds.Pip);

        Assert.True(result.Success);
        Assert.Equal(
            "https://tool-proxy.example:8443/",
            result.EffectiveSettings!.HttpProxy!.AbsoluteUri);
        Assert.Equal(
            "https://tool-mirror.example/simple",
            result.EffectiveSettings.Mirror!.AbsoluteUri);
    }

    [Theory]
    [InlineData("ftp://proxy.example", NetworkSettingsErrorCode.InvalidProxyUri)]
    [InlineData("proxy.example:8080", NetworkSettingsErrorCode.InvalidProxyUri)]
    [InlineData("https://proxy.example/#section", NetworkSettingsErrorCode.InvalidProxyUri)]
    [InlineData("https://proxy.example/?token=secret", NetworkSettingsErrorCode.InvalidProxyUri)]
    public async Task SaveAsync_RejectsInvalidProxyUri(
        string proxy,
        NetworkSettingsErrorCode expectedCode)
    {
        NetworkSettingsStore store = new(_root);
        NetworkSettings settings = new(new GlobalNetworkSettings(HttpProxy: proxy));

        NetworkSettingsSaveResult result = await store.SaveAsync(settings);

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal("global.httpProxy", error.Path);
        Assert.False(File.Exists(store.SettingsPath));
    }

    [Theory]
    [InlineData("http://mirror.example/simple")]
    [InlineData("http://localhost:8080/simple")]
    [InlineData("/relative/simple")]
    [InlineData("https://mirror.example/simple#fragment")]
    [InlineData("https://mirror.example/simple?token=secret")]
    public async Task SaveAsync_RejectsMirrorThatIsNotAbsoluteCredentialFreeHttps(
        string mirror)
    {
        NetworkSettingsStore store = new(_root);
        NetworkSettings settings = new(new GlobalNetworkSettings(Mirror: mirror));

        NetworkSettingsSaveResult result = await store.SaveAsync(settings);

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.InvalidMirrorUri, error.Code);
        Assert.Equal("global.mirror", error.Path);
    }

    [Fact]
    public async Task SaveAsync_RejectsCredentialsWithoutLeakingThemInDiagnostics()
    {
        const string User = "private-user";
        const string Password = "top-secret-password";
        NetworkEndpointOverride endpoint = NetworkEndpointOverride.Custom(
            $"https://{User}:{Password}@proxy.example:8443");
        ToolNetworkSettings tool = new(HttpProxy: endpoint);
        NetworkSettings settings = new(
            Tools: new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Pip] = tool,
            });

        NetworkSettingsSaveResult result = await new NetworkSettingsStore(_root).SaveAsync(settings);
        string diagnostics = string.Join(
            System.Environment.NewLine,
            endpoint.ToString(),
            tool.ToString(),
            settings.ToString(),
            result.ToString(),
            string.Join(System.Environment.NewLine, result.Errors));

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.InvalidProxyUri, error.Code);
        Assert.Contains("Windows integrated credentials", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(User, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, diagnostics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NetworkEndpointOverrideMode.Custom, null)]
    [InlineData(NetworkEndpointOverrideMode.Custom, "   ")]
    [InlineData(NetworkEndpointOverrideMode.Disabled, "https://proxy.example")]
    [InlineData(NetworkEndpointOverrideMode.Inherit, "https://proxy.example")]
    public async Task SaveAsync_RejectsInconsistentOverride(
        NetworkEndpointOverrideMode mode,
        string? value)
    {
        NetworkSettings settings = new(
            Tools: new Dictionary<string, ToolNetworkSettings>
            {
                [NetworkToolIds.Gradle] = new(
                    HttpProxy: new NetworkEndpointOverride(mode, value)),
            });

        NetworkSettingsSaveResult result = await new NetworkSettingsStore(_root).SaveAsync(settings);

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.InvalidOverride, error.Code);
        Assert.Equal("tools.gradle.httpProxy", error.Path);
    }

    [Fact]
    public async Task SaveAsync_RejectsUnknownToolAndUnsafeNoProxyEntry()
    {
        NetworkSettings settings = new(
            new GlobalNetworkSettings(NoProxy: ["localhost,example.test"]),
            new Dictionary<string, ToolNetworkSettings>
            {
                ["unknown-package-manager"] = new(),
            });

        NetworkSettingsSaveResult result = await new NetworkSettingsStore(_root).SaveAsync(settings);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Code == NetworkSettingsErrorCode.UnsupportedTool);
        Assert.Contains(result.Errors, error =>
            error.Code == NetworkSettingsErrorCode.InvalidNoProxyEntry);
        Assert.False(File.Exists(Path.Combine(_root, "state", "network-settings.json")));
    }

    [Fact]
    public async Task LoadAsync_MalformedJsonReturnsStructuredErrorWithoutSensitiveContent()
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"schemaVersion\":1,\"global\":{\"httpProxy\":\"https://user:secret@proxy.example\"");

        NetworkSettingsLoadResult result = await store.LoadAsync();
        string diagnostics = result.ToString();

        Assert.False(result.Success);
        Assert.Null(result.Settings);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.MalformedJson, error.Code);
        Assert.DoesNotContain("user", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"global\":{},\"tools\":{},\"unexpected\":true}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{\"unexpected\":true},\"tools\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{},\"tools\":{\"pip\":{\"unexpected\":true}}}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{},\"tools\":{\"pip\":{\"mirror\":{\"mode\":\"disabled\",\"unexpected\":true}}}}")]
    public async Task LoadAsync_RejectsUnknownJsonFields(string json)
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(store.SettingsPath, json);

        NetworkSettingsLoadResult result = await store.LoadAsync();

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.InvalidDocument, error.Code);
        Assert.Contains("unsupported field", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"global\":{},\"tools\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{\"mirror\":null,\"mirror\":null},\"tools\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{},\"tools\":{\"pip\":{\"mirror\":null,\"mirror\":null}}}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{},\"tools\":{\"pip\":{\"mirror\":{\"mode\":\"disabled\",\"mode\":\"disabled\"}}}}")]
    [InlineData("{\"schemaVersion\":1,\"global\":{},\"tools\":{\"pip\":{},\"PIP\":{}}}")]
    public async Task LoadAsync_RejectsDuplicateJsonFields(string json)
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(store.SettingsPath, json);

        NetworkSettingsLoadResult result = await store.LoadAsync();

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.InvalidDocument, error.Code);
        Assert.True(
            error.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_RejectsFileOverMaximumSizeBeforeParsing()
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await using (FileStream stream = new(
            store.SettingsPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.SetLength(NetworkSettingsStore.MaximumSettingsBytes + 1L);
        }

        NetworkSettingsLoadResult result = await store.LoadAsync();

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.DocumentTooLarge, error.Code);
    }

    [Fact]
    public async Task LoadAsync_FutureSchemaReturnsStructuredError()
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"schemaVersion\":999,\"global\":{},\"tools\":{}}");

        NetworkSettingsLoadResult result = await store.LoadAsync();

        Assert.False(result.Success);
        Assert.Null(result.Settings);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.UnsupportedSchema, error.Code);
        Assert.Equal("schemaVersion", error.Path);
    }

    [Fact]
    public async Task LoadAsync_NullToolOverrideReturnsStructuredError()
    {
        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        await File.WriteAllTextAsync(
            store.SettingsPath,
            "{\"schemaVersion\":1,\"global\":{},\"tools\":{\"pip\":null}}");

        NetworkSettingsLoadResult result = await store.LoadAsync();

        Assert.False(result.Success);
        NetworkSettingsError error = Assert.Single(result.Errors);
        Assert.Equal(NetworkSettingsErrorCode.InvalidDocument, error.Code);
        Assert.Equal("tools.pip", error.Path);
    }

    [Fact]
    public async Task SaveAsync_InvalidReplacementLeavesPreviousDocumentUntouched()
    {
        NetworkSettingsStore store = new(_root);
        NetworkSettings original = new(
            new GlobalNetworkSettings(Mirror: "https://original.example/simple"));
        Assert.True((await store.SaveAsync(original)).Success);
        byte[] before = await File.ReadAllBytesAsync(store.SettingsPath);

        NetworkSettingsSaveResult replacement = await store.SaveAsync(
            new NetworkSettings(
                new GlobalNetworkSettings(Mirror: "http://invalid.example/simple")));
        byte[] after = await File.ReadAllBytesAsync(store.SettingsPath);

        Assert.False(replacement.Success);
        Assert.Equal(before, after);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(store.SettingsPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentStoresProduceOneCompleteAtomicDocument()
    {
        NetworkSettingsStore[] stores = Enumerable.Range(0, 2)
            .Select(_ => new NetworkSettingsStore(_root))
            .ToArray();
        Task<NetworkSettingsSaveResult>[] writes = Enumerable.Range(0, 24)
            .Select(index => stores[index % stores.Length].SaveAsync(
                new NetworkSettings(
                    new GlobalNetworkSettings(
                        Mirror: $"https://mirror{index}.example/simple"),
                    new Dictionary<string, ToolNetworkSettings>
                    {
                        [NetworkToolIds.Pip] = new(
                            Mirror: NetworkEndpointOverride.Custom(
                                $"https://pypi{index}.example/simple")),
                    })))
            .ToArray();

        NetworkSettingsSaveResult[] results = await Task.WhenAll(writes);
        NetworkSettingsLoadResult loaded = await stores[0].LoadAsync();
        string json = await File.ReadAllTextAsync(stores[0].SettingsPath);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.True(loaded.Success);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            NetworkSettingsStore.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.StartsWith(
            "https://mirror",
            loaded.Settings!.Global!.Mirror,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(stores[0].SettingsPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task SaveAsync_TwoStoreInstancesWaitForTheInterprocessLock()
    {
        NetworkSettingsStore first = new(_root);
        NetworkSettingsStore second = new(_root);
        Assert.True((await first.LoadAsync()).Success);
        string lockPath = Path.Combine(_root, "state", "network-settings.lock");
        Task<NetworkSettingsSaveResult> pendingSave;

        using (FileStream heldLock = new(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            pendingSave = second.SaveAsync(
                new NetworkSettings(
                    new GlobalNetworkSettings(
                        Mirror: "https://locked.example/simple")));
            await Task.Delay(150);
            Assert.False(pendingSave.IsCompleted);
        }

        NetworkSettingsSaveResult result = await pendingSave;
        Assert.True(result.Success);
        Assert.Equal(
            "https://locked.example/simple",
            (await first.LoadAsync()).Settings!.Global!.Mirror);
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointStateDirectoryWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string externalState = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Network-State-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(externalState);
        string stateLink = Path.Combine(_root, "state");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(stateLink, externalState);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            NetworkSettingsLoadResult result = await new NetworkSettingsStore(_root).LoadAsync();

            Assert.False(result.Success);
            Assert.Equal(
                NetworkSettingsErrorCode.UnsafePath,
                Assert.Single(result.Errors).Code);
        }
        finally
        {
            if (Directory.Exists(stateLink))
            {
                Directory.Delete(stateLink);
            }

            if (Directory.Exists(externalState))
            {
                Directory.Delete(externalState, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsReparsePointSettingsFileWhenSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        NetworkSettingsStore store = new(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        string externalSettings = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"AutoEnvPlus-Network-Settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            externalSettings,
            "{\"schemaVersion\":1,\"global\":{},\"tools\":{}}");
        try
        {
            try
            {
                File.CreateSymbolicLink(store.SettingsPath, externalSettings);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            NetworkSettingsLoadResult result = await store.LoadAsync();

            Assert.False(result.Success);
            Assert.Equal(
                NetworkSettingsErrorCode.UnsafePath,
                Assert.Single(result.Errors).Code);
        }
        finally
        {
            if (File.Exists(store.SettingsPath))
            {
                File.Delete(store.SettingsPath);
            }

            if (File.Exists(externalSettings))
            {
                File.Delete(externalSettings);
            }
        }
    }

    [Fact]
    public void Constructor_RejectsSettingsPathOutsideManagedRoot()
    {
        string outside = Path.Combine(
            Path.GetDirectoryName(_root)!,
            $"outside-{Guid.NewGuid():N}.json");

        Assert.Throws<ArgumentException>(() => new NetworkSettingsStore(_root, outside));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
