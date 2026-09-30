using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AutoEnvPlus.Core.Tests;

public sealed class McpServerContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AutoEnvPlus-McpServer-{Guid.NewGuid():N}");

    private static string CliExecutable => Path.Combine(AppContext.BaseDirectory, "autoenvplus.exe");

    [Fact]
    public async Task McpServer_Initialize_CompletesHandshake()
    {
        using McpSession session = new();
        JsonElement response = await session.RequestAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"contract-tests","version":"1.0"}}}
            """);

        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, response.GetProperty("id").GetInt32());
        JsonElement result = response.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.TryGetProperty("capabilities", out JsonElement capabilities));
        Assert.True(capabilities.TryGetProperty("tools", out _));
        JsonElement serverInfo = result.GetProperty("serverInfo");
        Assert.Equal("autoenvplus", serverInfo.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task McpServer_ToolsList_ExposesContractTools()
    {
        using McpSession session = new();
        await session.RequestAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"contract-tests","version":"1.0"}}}
            """);
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        JsonElement tools = response.GetProperty("result").GetProperty("tools");
        Assert.Equal(6, tools.GetArrayLength());
        HashSet<string> names = new(
            tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString() ?? string.Empty),
            StringComparer.Ordinal);
        Assert.Superset(
            new HashSet<string>(
            [
                "doctor",
                "list_runtimes",
                "catalog",
                "provider_list",
                "which",
                "cli",
            ],
            StringComparer.Ordinal),
            names);
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
        }
    }

    [Fact]
    public async Task McpServer_DoctorTool_ReturnsDiagnosticReport()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement payload = await session.CallToolAsync(
            "doctor",
            $$"""{"root":"{{JsonEscape(_root)}}"}""");

        Assert.True(payload.GetProperty("exitCode").GetInt32() is 0 or 2);
        using JsonDocument report = JsonDocument.Parse(payload.GetProperty("stdout").GetString()!);
        Assert.True(report.RootElement.TryGetProperty("Path", out _));
        Assert.True(report.RootElement.TryGetProperty("IsHealthy", out _));
    }

    [Fact]
    public async Task McpServer_ListRuntimesTool_ReturnsManagedRegistry()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement payload = await session.CallToolAsync(
            "list_runtimes",
            $$"""{"managed":true,"root":"{{JsonEscape(_root)}}"}""");

        Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
        using JsonDocument registry = JsonDocument.Parse(payload.GetProperty("stdout").GetString()!);
        Assert.Equal(JsonValueKind.Array, registry.RootElement.ValueKind);
    }

    [Fact]
    public async Task McpServer_ProviderListTool_ReturnsProviders()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement payload = await session.CallToolAsync(
            "provider_list",
            $$"""{"root":"{{JsonEscape(_root)}}"}""");

        Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
        using JsonDocument providers = JsonDocument.Parse(payload.GetProperty("stdout").GetString()!);
        Assert.True(providers.RootElement.TryGetProperty("Providers", out JsonElement list));
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
    }

    [Fact]
    public async Task McpServer_CliTool_RunsArbitraryCommand()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement payload = await session.CallToolAsync(
            "cli",
            $$"""{"args":["provider","list","--json","--root","{{JsonEscape(_root)}}"]}""");

        Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
        using JsonDocument providers = JsonDocument.Parse(payload.GetProperty("stdout").GetString()!);
        Assert.True(providers.RootElement.TryGetProperty("Providers", out _));
    }

    [Fact]
    public async Task McpServer_CliTool_RejectsNestedMcpServer()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"cli","arguments":{"args":["mcp"]}}}""");

        JsonElement error = response.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("nested MCP server", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServer_CliTool_ValidatesArguments()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"cli","arguments":{"args":[]}}}""");

        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task McpServer_UnknownTool_ReturnsInvalidParams()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"does-not-exist","arguments":{}}}""");

        JsonElement error = response.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("Unknown tool", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServer_UnknownMethod_ReturnsMethodNotFound()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":12,"method":"resources/templates/list"}""");

        JsonElement error = response.GetProperty("error");
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task McpServer_Ping_ReturnsEmptyResult()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":13,"method":"ping"}""");

        Assert.True(response.TryGetProperty("result", out _));
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task McpServer_MalformedLine_ReturnsParseError()
    {
        using McpSession session = new();
        await session.WriteLineAsync("{ this is not json");
        JsonElement response = await session.ReadResponseAsync();

        JsonElement error = response.GetProperty("error");
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task McpServer_InitializedNotification_ProducesNoResponse()
    {
        using McpSession session = new();
        await session.InitializeAsync();
        await session.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        JsonElement response = await session.RequestAsync(
            """{"jsonrpc":"2.0","id":14,"method":"ping"}""");

        Assert.Equal(14, response.GetProperty("id").GetInt32());
        Assert.True(response.TryGetProperty("result", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string JsonEscape(string value) => JsonSerializer.Serialize(value)[1..^1];

    private sealed class McpSession : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;

        public McpSession()
        {
            Assert.True(File.Exists(CliExecutable), $"CLI was not copied to test output: {CliExecutable}");
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CliExecutable,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            _process.StartInfo.ArgumentList.Add("mcp");
            _process.Start();
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
        }

        public async Task InitializeAsync()
        {
            JsonElement response = await RequestAsync("""
                {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"contract-tests","version":"1.0"}}}
                """);
            Assert.True(response.TryGetProperty("result", out _));
            await WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        }

        public async Task<JsonElement> CallToolAsync(string toolName, string argumentsJson)
        {
            string request =
                $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argumentsJson}}}}}""";
            JsonElement response = await RequestAsync(request);
            Assert.True(response.TryGetProperty("result", out JsonElement result));
            Assert.False(result.GetProperty("isError").GetBoolean());
            JsonElement content = result.GetProperty("content");
            Assert.Equal(1, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            using JsonDocument payload = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
            return payload.RootElement.Clone();
        }

        public async Task<JsonElement> RequestAsync(string requestJson)
        {
            await WriteLineAsync(requestJson);
            return await ReadResponseAsync();
        }

        public Task WriteLineAsync(string line) => _stdin.WriteLineAsync(line);

        public async Task<JsonElement> ReadResponseAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
            string? line = await _stdout.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(line), "The MCP server closed its output before responding.");
            return JsonDocument.Parse(line!).RootElement.Clone();
        }

        public void Dispose()
        {
            try
            {
                _stdin.Close();
            }
            catch (ObjectDisposedException)
            {
                // The server may have exited already.
            }

            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.Dispose();
        }
    }
}
