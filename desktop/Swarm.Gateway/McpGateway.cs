using System.Text.Json;
using System.Text.Json.Nodes;
using Swarm.Core.Diagnostics;

namespace Swarm.Gateway;

public sealed class McpGateway
{
    private static readonly HashSet<string> ToolNames = new(StringComparer.Ordinal)
    {
        "task_begin", "observe", "click", "type_text", "press_key", "scroll", "focus", "task_end", "task_stop",
    };

    private readonly IDesktopGatewayTransport _transport;

    public McpGateway(IDesktopGatewayTransport transport)
    {
        _transport = transport;
    }

    public async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            return RpcError(null, -32700, "Parse error", exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var hasId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out _);
            var id = hasId ? (object)root.GetProperty("id") : null;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                return hasId ? RpcError(id, -32600, "Invalid Request") : null;
            }

            var method = methodElement.GetString()!;
            try
            {
                switch (method)
                {
                    case "notifications/initialized":
                        return null;
                    case "initialize":
                        return hasId ? RpcResult(id, InitializeResult()) : null;
                    case "ping":
                        return hasId ? RpcResult(id, new { }) : null;
                    case "tools/list":
                        return hasId ? RpcResult(id, new { tools = ToolDefinitions() }) : null;
                    case "tools/call":
                        var result = await CallTool(root, cancellationToken);
                        return hasId ? RpcResult(id, result) : null;
                    default:
                        return hasId ? RpcError(id, -32601, "Method not found") : null;
                }
            }
            catch (InvalidToolParamsException exception)
            {
                return hasId ? RpcError(id, -32602, "Invalid params", exception.Message) : null;
            }
            catch (Exception exception)
            {
                return hasId ? RpcError(id, -32603, "Internal error", exception.Message) : null;
            }
        }
    }

    private async Task<object> CallTool(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("params", out var call) || call.ValueKind != JsonValueKind.Object ||
            !call.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidToolParamsException("tools/call requires a tool name.");
        }

        var name = nameElement.GetString()!;
        if (!ToolNames.Contains(name))
        {
            throw new InvalidToolParamsException($"Unknown tool: {name}");
        }

        JsonElement arguments;
        if (call.TryGetProperty("arguments", out var suppliedArguments))
        {
            if (suppliedArguments.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidToolParamsException("Tool arguments must be a JSON object.");
            }
            arguments = suppliedArguments;
        }
        else
        {
            arguments = JsonSerializer.SerializeToElement(new { });
        }

        var parameters = new Dictionary<string, JsonElement>();
        foreach (var property in arguments.EnumerateObject())
        {
            parameters[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.Value.Clone();
        }

        try
        {
            var response = await _transport.SendAsync(name, JsonSerializer.SerializeToElement(parameters), cancellationToken);
            return response.Ok ? ToolSuccess(response.Result) : ToolFailure(response.Error!);
        }
        catch (DesktopUnavailableException exception)
        {
            FileLog.Write("gateway", $"desktop unavailable: {exception.Message}");
            return ToolFailure(new DesktopIpcError("desktop_unavailable", exception.Message));
        }
        catch (Exception exception)
        {
            FileLog.Write("gateway", $"{name} transport error: {exception.GetType().Name}: {exception.Message}");
            return ToolFailure(new DesktopIpcError("desktop_transport_error", exception.Message));
        }
    }

    private static object ToolSuccess(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            var root = JsonNode.Parse(result.GetRawText())!.AsObject();
            var observation = root["observation"] is JsonObject nested ? nested : root;
            if (observation["screenshot"] is JsonObject screenshot &&
                screenshot["mimeType"] is JsonValue mimeValue && mimeValue.TryGetValue<string>(out var mimeType) &&
                screenshot["data"] is JsonValue dataValue && dataValue.TryGetValue<string>(out var data) &&
                mimeType.StartsWith("image/", StringComparison.Ordinal) && data.Length > 0)
            {
                observation.Remove("screenshot");
                return new
                {
                    structuredContent = JsonSerializer.SerializeToElement(root),
                    content = new object[]
                    {
                        new { type = "text", text = root.ToJsonString() },
                        new { type = "image", mimeType, data },
                    },
                };
            }
        }

        return new
        {
            structuredContent = result,
            content = new[] { new { type = "text", text = result.GetRawText() } },
        };
    }

    private static object ToolFailure(DesktopIpcError error)
    {
        var structuredContent = new
        {
            error = new { code = error.Code, reason = error.Reason, message = error.Message },
        };
        return new
        {
            structuredContent,
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(structuredContent) } },
            isError = true,
        };
    }

    private static string RpcResult(object? id, object result) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    });

    private static string RpcError(object? id, int code, string message, string? data = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = data is null ? new { code, message } : new { code, message, data },
    });

    private static object InitializeResult() => new
    {
        protocolVersion = "2024-11-05",
        capabilities = new { tools = new { listChanged = false } },
        serverInfo = new { name = "swarm-desktop", version = "1.0.0" },
        instructions = "Call task_begin first to obtain local user authorization. Coordinates are window-client CSS pixels returned by observe; elementId takes precedence. Screenshots are returned on task_begin and on explicit observe requests. The desktop never steals foreground: if an action returns needs_foreground, call focus first. Esc stops when the target is focused; Ctrl+Alt+Shift+Esc always stops.",
    };

    private static object[] ToolDefinitions() => new object[]
    {
        Tool("task_begin", "Begin a desktop task and obtain local user authorization; this must succeed before other tools are used.", new { targetProcess = StringSchema("Optional target process name."), targetTitle = StringSchema("Optional target window title.") }, Array.Empty<string>()),
        Tool("observe", "Observe the authorized window and return elements, window-client CSS geometry, occlusion state, and target capabilities. If capabilities.messageReliable is false (Chromium/Electron), actions on a non-foreground window return needs_foreground with reason app_ignores_message; call focus first. Set includeScreenshot to receive a PNG.", new { token = StringSchema("Task token returned by task_begin."), includeScreenshot = BooleanSchema("Return a PNG screenshot scaled to at most 1600 px.") }, new[] { "token" }),
        Tool("click", "Click in the authorized task. elementId takes precedence over coordinates; x and y are window-client CSS coordinates returned by observe. If the target ignores window messages (see observe.capabilities), call focus first. Set observe to refresh the view in the same call.", new { token = StringSchema("Task token."), elementId = StringSchema("Preferred observed element id."), x = NumberSchema("Window-client CSS x coordinate."), y = NumberSchema("Window-client CSS y coordinate."), label = StringSchema("Optional click label."), verify = StringSchema("Set pixels to confirm the click with a stable before/after pixel diff of the element or click region."), observe = BooleanSchema("Return a fresh observation after the click.") }, new[] { "token" }),
        Tool("type_text", "Type text into the authorized task, preferably at elementId. Set sensitive for secrets. If the target ignores window messages (see observe.capabilities), call focus first.", new { token = StringSchema("Task token."), text = StringSchema("Text to type."), sensitive = BooleanSchema("Whether the text is sensitive."), elementId = StringSchema("Preferred observed element id."), verify = StringSchema("Set pixels to confirm typing with a stable before/after pixel diff; requires elementId."), observe = BooleanSchema("Return a fresh observation after typing.") }, new[] { "token", "text" }),
        Tool("press_key", "Press a key or key chord in the authorized task. If the target ignores window messages (see observe.capabilities), call focus first.", new { token = StringSchema("Task token."), key = StringSchema("Key or key chord."), verify = StringSchema("Set pixels to confirm the key with a stable before/after pixel diff of the client area."), observe = BooleanSchema("Return a fresh observation after the key.") }, new[] { "token", "key" }),
        Tool("scroll", "Scroll at window-client CSS coordinates x and y by delta in the authorized task. If the target ignores window messages (see observe.capabilities), call focus first.", new { token = StringSchema("Task token."), x = NumberSchema("Window-client CSS x coordinate."), y = NumberSchema("Window-client CSS y coordinate."), delta = NumberSchema("Scroll delta."), verify = StringSchema("Set pixels to confirm scrolling with a stable before/after pixel diff of the client area."), observe = BooleanSchema("Return a fresh observation after scrolling.") }, new[] { "token", "x", "y", "delta" }),
        Tool("focus", "Explicitly bring the authorized target window to the foreground. Only needed when an action returns needs_foreground.", new { token = StringSchema("Task token."), observe = BooleanSchema("Return a fresh observation after focusing.") }, new[] { "token" }),
        Tool("task_end", "End the authorized desktop task normally.", new { token = StringSchema("Task token.") }, new[] { "token" }),
        Tool("task_stop", "Stop the authorized desktop task immediately. The local user can press Esc for emergency stop.", new { token = StringSchema("Task token.") }, new[] { "token" }),
    };

    private static object Tool(string name, string description, object properties, string[] required) => new
    {
        name,
        description,
        inputSchema = new
        {
            type = "object",
            properties,
            required,
            additionalProperties = false,
        },
    };

    private static object StringSchema(string description) => new { type = "string", description };

    private static object NumberSchema(string description) => new { type = "number", description };

    private static object BooleanSchema(string description) => new { type = "boolean", description };

    private sealed class InvalidToolParamsException : Exception
    {
        public InvalidToolParamsException(string message) : base(message) { }
    }
}
