using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AutoEnvPlus.Cli;

/// <summary>
/// Minimal stdio MCP (Model Context Protocol) server that exposes the
/// AutoEnvPlus CLI as callable tools. Every tool call spawns the real CLI
/// executable and returns its exit code, stdout, and stderr, so each MCP
/// invocation exercises the shipped binary end to end. The generic
/// <c>cli</c> tool forwards arbitrary CLI arguments, which makes every CLI
/// feature reachable from MCP clients.
/// </summary>
internal static class AutoEnvPlusMcpServer
{
    private const string DefaultProtocolVersion = "2025-06-18";
    private const int ParseErrorCode = -32700;
    private const int InvalidRequestCode = -32600;
    private const int MethodNotFoundCode = -32601;
    private const int InvalidParamsCode = -32602;
    private const int InternalErrorCode = -32603;

    private static readonly string[] SupportedProtocolVersions =
    [
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
    ];

    private static readonly string[] CatalogRuntimeKinds =
    [
        "python",
        "node",
        "java",
        "dotnet",
        "msvc",
        "llvm",
        "mingw",
        "cmake",
        "ninja",
    ];

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // MCP stdio framing is one JSON-RPC message per line, UTF-8 without a
        // BOM. Nothing else may ever be written to stdout in this mode.
        using StreamWriter stdout = new(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        using StreamReader stdin = new(Console.OpenStandardInput(), Encoding.UTF8);

        while (true)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await HandleLineAsync(stdout, line, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task HandleLineAsync(
        StreamWriter stdout,
        string line,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            await WriteResponseAsync(
                stdout,
                CreateError(null, ParseErrorCode, "Parse error")).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                await HandleBatchAsync(stdout, root, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteResponseAsync(
                    stdout,
                    CreateError(null, InvalidRequestCode, "Invalid Request")).ConfigureAwait(false);
                return;
            }

            object? response = await BuildResponseAsync(root, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await WriteResponseAsync(stdout, response).ConfigureAwait(false);
            }
        }
    }

    private static async Task HandleBatchAsync(
        StreamWriter stdout,
        JsonElement batch,
        CancellationToken cancellationToken)
    {
        List<object> responses = [];
        foreach (JsonElement item in batch.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                responses.Add(CreateError(null, InvalidRequestCode, "Invalid Request"));
                continue;
            }

            object? response = await BuildResponseAsync(item, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                responses.Add(response);
            }
        }

        if (responses.Count > 0)
        {
            await WriteResponseAsync(stdout, responses).ConfigureAwait(false);
        }
    }

    private static async Task<object?> BuildResponseAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        bool hasId = request.TryGetProperty("id", out JsonElement idElement);
        string? method = request.TryGetProperty("method", out JsonElement methodElement)
            && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;

        if (!hasId)
        {
            // Notifications (notifications/initialized, notifications/cancelled)
            // never receive a response.
            return null;
        }

        JsonElement? id = idElement.ValueKind
            is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null
                ? idElement
                : null;

        if (string.IsNullOrWhiteSpace(method))
        {
            return CreateError(id, InvalidRequestCode, "Invalid Request");
        }

        JsonElement parameters =
            request.TryGetProperty("params", out JsonElement parameterElement)
            && parameterElement.ValueKind == JsonValueKind.Object
                ? parameterElement
                : default;

        switch (method)
        {
            case "initialize":
                return CreateInitializeResult(id, parameters);
            case "ping":
                return new { jsonrpc = "2.0", id, result = new { } };
            case "tools/list":
                return new { jsonrpc = "2.0", id, result = new { tools = CreateToolDescriptors() } };
            case "resources/list":
                return new { jsonrpc = "2.0", id, result = new { resources = Array.Empty<object>() } };
            case "prompts/list":
                return new { jsonrpc = "2.0", id, result = new { prompts = Array.Empty<object>() } };
            case "tools/call":
                try
                {
                    object result = await CallToolAsync(parameters, cancellationToken).ConfigureAwait(false);
                    return new { jsonrpc = "2.0", id, result };
                }
                catch (McpProtocolException exception)
                {
                    return CreateError(id, exception.Code, exception.Message);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return CreateError(id, InternalErrorCode, exception.Message);
                }
            default:
                return CreateError(id, MethodNotFoundCode, $"Method not found: {method}");
        }
    }

    private static object CreateInitializeResult(JsonElement? id, JsonElement parameters)
    {
        string requestedVersion =
            parameters.TryGetProperty("protocolVersion", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString() ?? string.Empty
                : string.Empty;
        string protocolVersion = SupportedProtocolVersions.Contains(requestedVersion)
            ? requestedVersion
            : DefaultProtocolVersion;
        string serverVersion = typeof(AutoEnvPlusMcpServer).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        return new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                protocolVersion,
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "autoenvplus", version = serverVersion },
            },
        };
    }

    private static object CreateError(JsonElement? id, int code, string message) => new
    {
        jsonrpc = "2.0",
        id,
        error = new { code, message },
    };

    private static async Task WriteResponseAsync(StreamWriter stdout, object response)
    {
        await stdout.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
    }

    private static object[] CreateToolDescriptors() =>
    [
        new
        {
            name = "doctor",
            description = "Run the AutoEnvPlus environment diagnosis and return the JSON report. "
                + "Exit code 2 means issues were found; the report is still returned.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    root = new
                    {
                        type = "string",
                        description = "Managed root directory; defaults to the per-user managed root.",
                    },
                },
                additionalProperties = false,
            },
        },
        new
        {
            name = "list_runtimes",
            description = "List runtimes discovered on PATH, or the AutoEnvPlus managed runtime registry.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    managed = new
                    {
                        type = "boolean",
                        description = "List the managed registry instead of PATH discovery.",
                    },
                    root = new
                    {
                        type = "string",
                        description = "Managed root directory; defaults to the per-user managed root.",
                    },
                },
                additionalProperties = false,
            },
        },
        new
        {
            name = "catalog",
            description = "Query a runtime provider's release catalog. Requires network access to the provider.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = CatalogRuntimeKinds },
                    provider = new
                    {
                        type = "string",
                        description = "Provider id such as python-org or plugin:<id>.",
                    },
                    feature = new { type = "string", description = "Feature filter such as java-major." },
                    lts = new { type = "boolean", description = "Restrict to long-term support releases." },
                    arch = new { type = "string", @enum = new[] { "x64", "x86", "arm64" } },
                    limit = new { type = "integer", description = "Maximum releases to return (1-100)." },
                    asset = new { type = "string", description = "Exact version whose assets should be listed." },
                    root = new
                    {
                        type = "string",
                        description = "Managed root directory; defaults to the per-user managed root.",
                    },
                },
                required = new[] { "kind" },
                additionalProperties = false,
            },
        },
        new
        {
            name = "provider_list",
            description = "List runtime providers (built-in and installed plugins).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = CatalogRuntimeKinds },
                    pluginsOnly = new { type = "boolean", description = "List only installed plugins." },
                    root = new
                    {
                        type = "string",
                        description = "Managed root directory; defaults to the per-user managed root.",
                    },
                },
                additionalProperties = false,
            },
        },
        new
        {
            name = "which",
            description = "Resolve which managed runtime a project or the global selection would use.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = CatalogRuntimeKinds },
                    project = new { type = "string", description = "Project directory to resolve for." },
                    runtimeId = new { type = "string", description = "Exact managed runtime id." },
                    provider = new { type = "string", description = "Exact provider id." },
                    root = new
                    {
                        type = "string",
                        description = "Managed root directory; defaults to the per-user managed root.",
                    },
                },
                required = new[] { "kind" },
                additionalProperties = false,
            },
        },
        new
        {
            name = "cli",
            description = "Run any AutoEnvPlus CLI command and return its exit code, stdout, and stderr. "
                + "This exposes every CLI feature: install, uninstall, use, exec, tool, network, download, "
                + "provider, plugin, shim, shell, storage, toolchain, project, and resolve. "
                + "Mutating commands follow the CLI's own safety rules (for example --yes confirmations).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    args = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Arguments passed to autoenvplus, for example [\"storage\",\"list\",\"--json\"].",
                    },
                },
                required = new[] { "args" },
                additionalProperties = false,
            },
        },
    ];

    private static async Task<object> CallToolAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new McpProtocolException(
                InvalidParamsCode,
                "The tools/call request requires an object with a tool name.");
        }

        string? toolName =
            parameters.TryGetProperty("name", out JsonElement nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new McpProtocolException(InvalidParamsCode, "The tools/call request is missing a tool name.");
        }

        JsonElement arguments =
            parameters.TryGetProperty("arguments", out JsonElement argumentElement)
            && argumentElement.ValueKind == JsonValueKind.Object
                ? argumentElement
                : default;

        List<string> cliArguments = toolName switch
        {
            "doctor" => BuildDoctorArguments(arguments),
            "list_runtimes" => BuildListArguments(arguments),
            "catalog" => BuildCatalogArguments(arguments),
            "provider_list" => BuildProviderListArguments(arguments),
            "which" => BuildWhichArguments(arguments),
            "cli" => BuildCliArguments(arguments),
            _ => throw new McpProtocolException(InvalidParamsCode, $"Unknown tool: {toolName}"),
        };

        return await ExecuteCliAsync(cliArguments, cancellationToken).ConfigureAwait(false);
    }

    private static List<string> BuildDoctorArguments(JsonElement arguments)
    {
        List<string> cliArguments = ["doctor", "--json"];
        AppendRoot(cliArguments, arguments);
        return cliArguments;
    }

    private static List<string> BuildListArguments(JsonElement arguments)
    {
        List<string> cliArguments = ["list"];
        if (GetBooleanArgument(arguments, "managed") is true)
        {
            cliArguments.Add("--managed");
        }

        cliArguments.Add("--json");
        AppendRoot(cliArguments, arguments);
        return cliArguments;
    }

    private static List<string> BuildCatalogArguments(JsonElement arguments)
    {
        string? kind = GetStringArgument(arguments, "kind");
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new McpProtocolException(InvalidParamsCode, "The catalog tool requires a kind.");
        }

        List<string> cliArguments = ["catalog", kind];
        AppendOptionWithValue(cliArguments, arguments, "provider", "--provider");
        AppendOptionWithValue(cliArguments, arguments, "feature", "--feature");
        if (GetBooleanArgument(arguments, "lts") is true)
        {
            cliArguments.Add("--lts");
        }

        AppendOptionWithValue(cliArguments, arguments, "arch", "--arch");
        AppendOptionWithValue(cliArguments, arguments, "asset", "--asset");
        if (GetIntArgument(arguments, "limit") is int limit)
        {
            cliArguments.Add("--limit");
            cliArguments.Add(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        cliArguments.Add("--json");
        AppendRoot(cliArguments, arguments);
        return cliArguments;
    }

    private static List<string> BuildProviderListArguments(JsonElement arguments)
    {
        bool pluginsOnly = GetBooleanArgument(arguments, "pluginsOnly") is true;
        List<string> cliArguments = [pluginsOnly ? "plugin" : "provider", "list"];
        AppendOptionWithValue(cliArguments, arguments, "kind", "--kind");
        cliArguments.Add("--json");
        AppendRoot(cliArguments, arguments);
        return cliArguments;
    }

    private static List<string> BuildWhichArguments(JsonElement arguments)
    {
        string? kind = GetStringArgument(arguments, "kind");
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new McpProtocolException(InvalidParamsCode, "The which tool requires a kind.");
        }

        List<string> cliArguments = ["which", kind];
        AppendOptionWithValue(cliArguments, arguments, "runtimeId", "--runtime-id");
        AppendOptionWithValue(cliArguments, arguments, "provider", "--provider");
        AppendOptionWithValue(cliArguments, arguments, "project", "--project");
        AppendRoot(cliArguments, arguments);
        return cliArguments;
    }

    private static List<string> BuildCliArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("args", out JsonElement argsElement)
            || argsElement.ValueKind != JsonValueKind.Array)
        {
            throw new McpProtocolException(
                InvalidParamsCode,
                "The cli tool requires an args array of CLI arguments.");
        }

        List<string> cliArguments = [];
        foreach (JsonElement item in argsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { } argument)
            {
                throw new McpProtocolException(
                    InvalidParamsCode,
                    "Every entry in the cli tool args array must be a string.");
            }

            cliArguments.Add(argument);
        }

        if (cliArguments.Count == 0)
        {
            throw new McpProtocolException(InvalidParamsCode, "The cli tool requires at least one argument.");
        }

        if (cliArguments[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpProtocolException(
                InvalidParamsCode,
                "The cli tool cannot start a nested MCP server.");
        }

        return cliArguments;
    }

    private static async Task<object> ExecuteCliAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new McpProtocolException(
                InternalErrorCode,
                "The AutoEnvPlus CLI executable could not be resolved.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new McpProtocolException(InternalErrorCode, "Unable to start the AutoEnvPlus CLI process.");
        process.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new
        {
            content = new object[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(
                        new { exitCode = process.ExitCode, stdout, stderr }),
                },
            },
            isError = false,
        };
    }

    private static void AppendRoot(List<string> cliArguments, JsonElement arguments)
    {
        AppendOptionWithValue(cliArguments, arguments, "root", "--root");
    }

    private static void AppendOptionWithValue(
        List<string> cliArguments,
        JsonElement arguments,
        string propertyName,
        string optionName)
    {
        string? value = GetStringArgument(arguments, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            cliArguments.Add(optionName);
            cliArguments.Add(value);
        }
    }

    private static string? GetStringArgument(JsonElement arguments, string propertyName) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBooleanArgument(JsonElement arguments, string propertyName) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? GetIntArgument(JsonElement arguments, string propertyName) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private sealed class McpProtocolException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
