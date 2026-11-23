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
    private readonly Func<string, Task>? _notify;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

    /// <param name="notify">写出服务器主动通知（例如 notifications/progress）；为 null 时静默丢弃。</param>
    public McpGateway(IDesktopGatewayTransport transport, Func<string, Task>? notify = null)
    {
        _transport = transport;
        _notify = notify;
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
                    case "notifications/cancelled":
                        await CancelRequest(root);
                        return null;
                    case "initialize": {
                        var negotiated = NegotiateInitialProtocol(root, out var modernRequested);
                        if (modernRequested && negotiated == FallbackProtocolVersion)
                        {
                            // modern 客户端显式声明了不支持的版本 → -32022 (预协商失败).
                            // legacy params.protocolVersion 顶层永远走 fallback.
                            return hasId
                                ? RpcError(id, -32022, "Unsupported protocol version",
                                    $"Requested version not in [{string.Join(", ", SupportedProtocolVersions)}].")
                                : null;
                        }
                        return hasId ? RpcResult(id, InitializeResult(negotiated)) : null;
                    }
                    case "server/discover":
                        return hasId ? RpcResult(id, DiscoverResult()) : null;
                    case "ping":
                        return hasId ? RpcResult(id, new { }) : null;
                    case "tools/list":
                        return hasId ? RpcResult(id, new { tools = ToolDefinitions() }) : null;
                    case "tools/call":
                        return await CallToolWithCancellation(hasId ? id : null, root, cancellationToken);
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

    /// <summary>
    /// 把请求登记到在途表，使 notifications/cancelled 能按 id 取消；
    /// 取消后按 MCP 约定返回 -32800，而不是伪装成工具错误。
    /// </summary>
    private async Task<string?> CallToolWithCancellation(object? id, JsonElement root, CancellationToken cancellationToken)
    {
        var key = RequestKey(root);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (key is not null) _inFlight[key] = requestCancellation;
        try
        {
            var result = await CallTool(root, requestCancellation.Token);
            return id is null ? null : RpcResult(id, result);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return id is null ? null : RpcError(id, -32800, "Request cancelled");
        }
        finally
        {
            if (key is not null) _inFlight.TryRemove(key, out _);
        }
    }

    private async Task CancelRequest(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("requestId", out var requestId))
        {
            return;
        }
        var key = IdKey(requestId);
        if (key is null || !_inFlight.TryGetValue(key, out var cancellation)) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 请求刚结束并被释放，取消已无意义。
            return;
        }
        // 主 IPC 是串行 accept，取消必须走独立控制管道；失败也只影响动手时机，不影响向客户端返回 cancelled。
        try
        {
            await _transport.CancelAsync();
        }
        catch (Exception exception)
        {
            FileLog.Write("gateway", $"cancel forwarding failed: {exception.Message}");
        }
    }

    private static string? RequestKey(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id)) return null;
        return IdKey(id);
    }

    private static string? IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => $"s:{id.GetString()}",
        JsonValueKind.Number => $"r:{id.GetRawText()}",
        _ => null,
    };

    /// <summary>按 MCP 约定从 params._meta.progressToken 取进度令牌；字符串与数字都允许，其它类型忽略。</summary>
    private static JsonElement? ProgressToken(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("progressToken", out var token))
        {
            return null;
        }
        return token.ValueKind is JsonValueKind.String or JsonValueKind.Number ? token.Clone() : null;
    }

    /// <summary>通知不带 id；progressToken 原样回显（字符串/数字保持原类型），total 固定为 1。</summary>
    private static string ProgressNotification(JsonElement token, DesktopProgress progress)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["progressToken"] = token,
            ["progress"] = progress.Value,
            ["total"] = 1,
        };
        if (!string.IsNullOrEmpty(progress.Message)) parameters["message"] = progress.Message;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = parameters,
        });
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
            // 只有客户端在 _meta.progressToken 里显式要求时才发进度通知；没有 token 就彻底不发，避免凭空造进度。
            var progressToken = ProgressToken(root);
            Func<DesktopProgress, Task>? progress = progressToken is not null && _notify is not null
                ? update => _notify(ProgressNotification(progressToken.Value, update))
                : null;
            var protocolVersion = NegotiatedProtocolVersion(root);
            var response = await _transport.SendAsync(name, JsonSerializer.SerializeToElement(parameters), cancellationToken, progress);
            return response.Ok ? ToolSuccess(response.Result, protocolVersion) : ToolFailure(response.Error!, protocolVersion);
        }
        catch (DesktopUnavailableException exception)
        {
            FileLog.Write("gateway", $"desktop unavailable: {exception.Message}");
            return ToolFailure(new DesktopIpcError("desktop_unavailable", exception.Message), null);
        }
        catch (OperationCanceledException)
        {
            // 取消属于协议层语义，交给 CallToolWithCancellation 统一转成 -32800。
            throw;
        }
        catch (Exception exception)
        {
            FileLog.Write("gateway", $"{name} transport error: {exception.GetType().Name}: {exception.Message}");
            return ToolFailure(new DesktopIpcError("desktop_transport_error", exception.Message), null);
        }
    }

    private static object ToolSuccess(JsonElement result, string? protocolVersion)
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
            }
        }

        // 先按 legacy 形状构造（structuredContent + content），再按协议版本包装 resultType + _meta。
        var (legacyStructured, legacyContent) = BuildLegacySuccessShape(result);
        if (protocolVersion != ModernProtocolVersion)
        {
            return new { structuredContent = legacyStructured, content = legacyContent };
        }

        // MRTR: success 形状 = legacy + resultType + _meta 回显 + confirmToken 提升为 input_required
        if (TryExtractConfirmToken(result, out var confirmToken, out var actionLabel))
        {
            return new
            {
                _meta = new { protocolVersion = ModernProtocolVersion, requestId = (string?)null, timestamp = DateTimeOffset.UtcNow },
                resultType = "input_required",
                confirmToken,
                actionLabel,
                structuredContent = ExtractStructuredContent(result),
                input = new
                {
                    type = "confirm_required",
                    confirmToken,
                    prompt = $"Confirm {actionLabel}. Pass confirmToken back with the same action.",
                    expiresInMs = 5_000,
                },
                // P1-P 补充: ttlMs 让客户端知道结果有效窗口; cacheScope 声明结果可缓存粒度.
                ttlMs = 120_000,
                cacheScope = "per_task",
            };
        }

        return new
        {
            _meta = new { protocolVersion = ModernProtocolVersion, requestId = (string?)null, timestamp = DateTimeOffset.UtcNow },
            resultType = "success",
            structuredContent = ExtractStructuredContent(result),
            content = legacyContent,
            ttlMs = 120_000,
            cacheScope = "per_observe",
        };
    }

    private static object ToolFailure(DesktopIpcError error, string? protocolVersion)
    {
        var legacy = new
        {
            structuredContent = new { error = new { code = error.Code, reason = error.Reason, message = error.Message } },
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { error = new { code = error.Code, reason = error.Reason, message = error.Message } }) } },
            isError = true,
        };
        if (protocolVersion != ModernProtocolVersion) return legacy;

        // MRTR: blocked 形状 = error code + structuredContent
        var isPolicyDenied = error.Code == "policy_denied" || error.Code.EndsWith("_denied", StringComparison.Ordinal);
        return new
        {
            _meta = new { protocolVersion = ModernProtocolVersion, requestId = (string?)null, timestamp = DateTimeOffset.UtcNow },
            resultType = isPolicyDenied ? "blocked" : "success",
            structuredContent = legacy.structuredContent,
            content = legacy.content,
            isError = legacy.isError,
            ttlMs = 0,                    // blocked 结果不缓存
            cacheScope = "never",
        };
    }

    private static (object structuredContent, object content) BuildLegacySuccessShape(JsonElement result)
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
                return (
                    JsonSerializer.SerializeToElement(root),
                    new object[]
                    {
                        new { type = "text", text = root.ToJsonString() },
                        new { type = "image", mimeType, data },
                    }
                );
            }
        }
        return (result.Clone(), new[] { new { type = "text", text = result.GetRawText() } });
    }

    private static bool TryExtractConfirmToken(JsonElement result, out string token, out string actionLabel)
    {
        token = null!;
        actionLabel = null!;
        if (result.ValueKind != JsonValueKind.Object) return false;
        if (!result.TryGetProperty("confirmToken", out var ct) || ct.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(ct.GetString())) return false;
        token = ct.GetString()!;
        actionLabel = result.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(label.GetString())
            ? label.GetString()!
            : "the action";
        return true;
    }

    private static JsonElement ExtractStructuredContent(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("observation", out var observation) && observation.ValueKind == JsonValueKind.Object)
            return observation.Clone();
        return result.Clone();
    }

    /// <summary>
    /// 从 params._meta.protocolVersion 读取协商协议版本；
    /// modern 客户端显式声明 "2025-06-18" 时返回 MRTR 形状，否则返回 null（legacy 默认）。
    /// </summary
    private static string? NegotiatedProtocolVersion(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!meta.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String) return null;
        var requested = version.GetString();
        return requested is not null && Array.IndexOf(SupportedProtocolVersions, requested) >= 0 ? requested : null;
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

    private static readonly string[] SupportedProtocolVersions = { "2024-11-05", "2025-06-18" };
    private const string FallbackProtocolVersion = "2024-11-05";
    private const string ModernProtocolVersion = "2025-06-18";

    /// <summary>
    /// 从 initialize params.protocolVersion 协商协议版本.
    /// 返回 supported 版本或 fallback.
    /// modernRequested=true 表示 params._meta.protocolVersion 显式声明了 (仅 modern 客户端会带),
    /// 这时 initialize switch 会在不支持时返回 -32022.
    /// params 顶层 protocolVersion 属于 legacy 容错, 永远 fallback (不触发 -32022).
    /// </summary>
    private static string NegotiateInitialProtocol(JsonElement root, out bool modernRequested)
    {
        modernRequested = false;
        // modern 客户端: params._meta.protocolVersion (2025-06-18 MRTR 约定)
        if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("protocolVersion", out var metaVer) && metaVer.ValueKind == JsonValueKind.String)
        {
            var mv = metaVer.GetString();
            if (mv is not null && Array.IndexOf(SupportedProtocolVersions, mv) >= 0) return mv;
            modernRequested = true;
            return FallbackProtocolVersion;
        }
        // legacy: params.protocolVersion (顶层), 不支持就静默 fallback
        var requested = root.TryGetProperty("params", out parameters) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("protocolVersion", out var version) &&
            version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        if (requested is null) return FallbackProtocolVersion;
        return Array.IndexOf(SupportedProtocolVersions, requested) >= 0
            ? requested
            : FallbackProtocolVersion;
    }

    private static object InitializeResult(string negotiated)
    {
        return new
        {
            protocolVersion = negotiated,
            capabilities = new
            {
                tools = new { listChanged = false },
                governance = new
                {
                    policy = true,
                    confirmToken = true,
                    redactions = new[] { "password", "credential", "email", "id", "bank", "phone" },
                    blockedResult = true,
                    inputRequiredResult = true,
                },
            },
            serverInfo = new { name = "swarm-desktop", version = "1.0.0", protocolVersions = SupportedProtocolVersions },
            instructions = "Call task_begin first to obtain local user authorization. Coordinates are window-client CSS pixels returned by observe; elementId takes precedence. Screenshots are returned on task_begin and on explicit observe requests. The desktop never steals foreground: if an action returns needs_foreground, call focus first. Esc stops when the target is focused; Ctrl+Alt+Shift+Esc always stops.",
        };
    }

    /// <summary>
    /// 2025-06-18 新增的 MCP discover 端点：声明完整能力矩阵，
    /// 让客户端在 initialize 之前就知道支持哪些 MRTR 功能。
    /// </summary
    private static object DiscoverResult() => new
    {
        protocolVersions = SupportedProtocolVersions,
        defaultProtocolVersion = FallbackProtocolVersion,
        capabilities = new
        {
            tools = new { listChanged = false },
            governance = new
            {
                policy = true,
                confirmToken = true,
                redactions = new[] { "password", "credential", "email", "id", "bank", "phone" },
                blockedResult = true,
                inputRequiredResult = true,
            },
        },
        serverInfo = new { name = "swarm-desktop", version = "1.0.0" },
        endpoints = new[] { "initialize", "tools/list", "tools/call", "server/discover" },
    };

    private static object[] ToolDefinitions() => new object[]
    {
        Tool("task_begin", "Begin a desktop task and obtain local user authorization; this must succeed before other tools are used. Pass policy to restrict processes/titles, enforce read-only, cap actions and duration, deny or confirm cross-app actions, and redact sensitive regions.", new { targetProcess = StringSchema("Optional target process name."), targetTitle = StringSchema("Optional target window title."), policy = PolicySchema() }, Array.Empty<string>(), new ToolAnnotations(ReadOnly: false, Destructive: false, Idempotent: false, OpenWorld: true), ObservationOutputSchema()),
        Tool("observe", "Observe the authorized window and return elements, window-client CSS geometry, occlusion state, and target capabilities. If capabilities.messageReliable is false (Chromium/Electron), actions on a non-foreground window return needs_foreground with reason app_ignores_message; call focus first. Set includeScreenshot to receive a PNG.", new { token = StringSchema("Task token returned by task_begin."), includeScreenshot = BooleanSchema("Return a PNG screenshot scaled to at most 1600 px.") }, new[] { "token" }, new ToolAnnotations(ReadOnly: true, Destructive: false, Idempotent: true, OpenWorld: true), ObservationOutputSchema()),
        Tool("click", "Click in the authorized task. elementId takes precedence over coordinates; x and y are window-client CSS coordinates returned by observe. If the target ignores window messages (see observe.capabilities), call focus first. Set observe to refresh the view in the same call.", new { token = StringSchema("Task token."), elementId = StringSchema("Preferred observed element id."), x = NumberSchema("Window-client CSS x coordinate."), y = NumberSchema("Window-client CSS y coordinate."), label = StringSchema("Optional click label."), verify = StringSchema("Set pixels to confirm the click with a stable before/after pixel diff of the element or click region."), observe = BooleanSchema("Return a fresh observation after the click."), confirmToken = StringSchema("One-time token returned by needs_confirmation for this exact action.") }, new[] { "token" }, new ToolAnnotations(ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: true), ActionOutputSchema()),
        Tool("type_text", "Type text into the authorized task, preferably at elementId. Set sensitive for secrets. If the target ignores window messages (see observe.capabilities), call focus first.", new { token = StringSchema("Task token."), text = StringSchema("Text to type."), sensitive = BooleanSchema("Whether the text is sensitive."), elementId = StringSchema("Preferred observed element id."), verify = StringSchema("Set pixels to confirm typing with a stable before/after pixel diff; requires elementId."), observe = BooleanSchema("Return a fresh observation after typing."), confirmToken = StringSchema("One-time token returned by needs_confirmation for this exact action.") }, new[] { "token", "text" }, new ToolAnnotations(ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: true), ActionOutputSchema()),
        Tool("press_key", "Press a key or key chord in the authorized task. If the target ignores window messages (see observe.capabilities), call focus first.", new { token = StringSchema("Task token."), key = StringSchema("Key or key chord."), verify = StringSchema("Set pixels to confirm the key with a stable before/after pixel diff of the client area."), observe = BooleanSchema("Return a fresh observation after the key."), confirmToken = StringSchema("One-time token returned by needs_confirmation for this exact action.") }, new[] { "token", "key" }, new ToolAnnotations(ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: true), ActionOutputSchema()),
        Tool("scroll", "Scroll at window-client CSS coordinates x and y by delta in the authorized task. If the target ignores window messages (see observe.capabilities), call focus first.", new { token = StringSchema("Task token."), x = NumberSchema("Window-client CSS x coordinate."), y = NumberSchema("Window-client CSS y coordinate."), delta = NumberSchema("Scroll delta."), verify = StringSchema("Set pixels to confirm scrolling with a stable before/after pixel diff of the client area."), observe = BooleanSchema("Return a fresh observation after scrolling."), confirmToken = StringSchema("One-time token returned by needs_confirmation for this exact action.") }, new[] { "token", "x", "y", "delta" }, new ToolAnnotations(ReadOnly: false, Destructive: false, Idempotent: false, OpenWorld: true), ActionOutputSchema()),
        Tool("focus", "Explicitly bring the authorized target window to the foreground. Only needed when an action returns needs_foreground.", new { token = StringSchema("Task token."), observe = BooleanSchema("Return a fresh observation after focusing."), confirmToken = StringSchema("One-time token returned by needs_confirmation for this exact action.") }, new[] { "token" }, new ToolAnnotations(ReadOnly: false, Destructive: false, Idempotent: true, OpenWorld: true), ActionOutputSchema()),
        Tool("task_end", "End the authorized desktop task normally.", new { token = StringSchema("Task token.") }, new[] { "token" }, new ToolAnnotations(ReadOnly: false, Destructive: false, Idempotent: true, OpenWorld: false), EndOutputSchema()),
        Tool("task_stop", "Stop the authorized desktop task immediately. The local user can press Esc for emergency stop.", new { token = StringSchema("Task token.") }, new[] { "token" }, new ToolAnnotations(ReadOnly: false, Destructive: false, Idempotent: true, OpenWorld: false), EndOutputSchema()),
    };

    private readonly record struct ToolAnnotations(bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld);

    private static object Tool(string name, string description, object properties, string[] required, ToolAnnotations annotations, object outputSchema) => new
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
        outputSchema,
        annotations = new
        {
            readOnlyHint = annotations.ReadOnly,
            destructiveHint = annotations.Destructive,
            idempotentHint = annotations.Idempotent,
            openWorldHint = annotations.OpenWorld,
        },
    };

    // 结构化结果只声明顶层键，不设 required 并允许追加字段：错误结果里的 error 对象、
    // 以及截图被移到 content 后缺失的 screenshot 都不会造成校验失败。
    private static object ObservationOutputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object?>
        {
            ["token"] = NullableStringSchema(),
            ["target"] = OpenObjectSchema(),
            ["viewport"] = OpenObjectSchema(),
            ["capabilities"] = OpenObjectSchema(),
            ["window"] = OpenObjectSchema(),
            ["generation"] = new { type = "integer" },
            ["elements"] = new { type = "array", items = OpenObjectSchema() },
            ["redactions"] = new { type = "array", items = OpenObjectSchema() },
            ["screenshot"] = OpenObjectSchema(),
            ["occluded"] = new { type = "boolean" },
            ["captureBackend"] = new { type = "string" },
            ["frameAgeMs"] = new { type = new[] { "number", "null" } },
            ["warning"] = NullableStringSchema(),
        },
        additionalProperties = true,
    };

    private static object ActionOutputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object?>
        {
            ["executed"] = new { type = "boolean" },
            ["actionId"] = new { type = "string" },
            ["tier"] = new { type = "string" },
            ["verified"] = new { type = "boolean" },
            ["evidence"] = NullableStringSchema(),
            ["note"] = NullableStringSchema(),
            ["foregroundUnchanged"] = new { type = "boolean" },
            ["foregroundRestored"] = new { type = "boolean" },
            ["status"] = new { type = "string" },
            ["confirmToken"] = new { type = "string" },
            ["summary"] = new { type = "string" },
            ["expiresInMs"] = new { type = "integer" },
            ["observation"] = OpenObjectSchema(),
        },
        additionalProperties = true,
    };

    private static object EndOutputSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object?>
        {
            ["ok"] = new { type = "boolean" },
            ["stopped"] = new { type = "boolean" },
        },
        additionalProperties = true,
    };

    private static object PolicySchema() => new
    {
        type = "object",
        description = "Optional governance policy. Defaults are conservative: cross-app actions are denied, destructive/external actions require a confirmToken, and sensitive regions are masked.",
        properties = new Dictionary<string, object?>
        {
            ["allowedProcesses"] = StringArraySchema("Process names permitted for this task."),
            ["allowedTitles"] = StringArraySchema("Window title fragments permitted for this task."),
            ["readOnly"] = BooleanSchema("Deny every action that changes the target."),
            ["maxActions"] = new { type = "integer", minimum = 1, maximum = 10_000, description = "Maximum executed actions." },
            ["maxDurationMs"] = new { type = "integer", minimum = 1_000, maximum = 86_400_000, description = "Maximum task duration in milliseconds." },
            ["allowCrossApp"] = BooleanSchema("Allow actions on elements owned by other processes."),
            ["requireConfirmFor"] = StringArraySchema("Action categories that require a confirmToken: submit, delete, send, purchase, cross_app. An empty array disables confirmation."),
            ["redaction"] = new
            {
                type = "object",
                properties = new Dictionary<string, object?>
                {
                    ["mode"] = new { type = "string", @enum = new[] { "mask", "metadata", "off" }, description = "mask redacts text and pixels; metadata redacts text only; off disables redaction." },
                    ["categories"] = StringArraySchema("Sensitive categories to redact: password, credential, email, id, bank, phone. Empty means all."),
                },
                additionalProperties = false,
            },
        },
        additionalProperties = false,
    };

    private static object StringArraySchema(string description) =>
        new { type = "array", items = new { type = "string" }, description };

    private static object NullableStringSchema() => new { type = new[] { "string", "null" } };

    private static object OpenObjectSchema() => new { type = "object", additionalProperties = true };

    private static object StringSchema(string description) => new { type = "string", description };

    private static object NumberSchema(string description) => new { type = "number", description };

    private static object BooleanSchema(string description) => new { type = "boolean", description };

    private sealed class InvalidToolParamsException : Exception
    {
        public InvalidToolParamsException(string message) : base(message) { }
    }
}
