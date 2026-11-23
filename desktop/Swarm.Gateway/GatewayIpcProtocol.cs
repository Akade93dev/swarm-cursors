using System.Text.Json;

namespace Swarm.Gateway;

public static class GatewayIpcProtocol
{
    public const string PipeName = "SwarmDesktop.V1";
    /// <summary>取消等控制消息的独立管道：主 IPC 串行 accept，动作执行期间无法接收第二条连接。</summary>
    public const string ControlPipeName = "SwarmDesktop.Control.V1";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(65);

    /// <summary>
    /// 识别桌面端在最终响应前插入的进度行。进度行没有 ok 字段，必须带本次请求的 id；
    /// 其余行一律交给 <see cref="ParseResponse"/>，避免把畸形输入误当成进度而吞掉真正的错误。
    /// </summary>
    public static bool TryParseProgress(string json, string expectedId, out DesktopProgress progress)
    {
        progress = null!;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("ok", out _)) return false;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != expectedId) return false;
            if (!root.TryGetProperty("progress", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
            if (!payload.TryGetProperty("value", out var value) || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            var message = payload.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            progress = new DesktopProgress(Math.Clamp(number, 0, 1), message);
            return true;
        }
    }

    public static DesktopIpcResponse ParseResponse(string json, string expectedId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != expectedId)
        {
            throw new InvalidDataException("Desktop IPC response id does not match the request id.");
        }

        if (!root.TryGetProperty("ok", out var ok) || (ok.ValueKind != JsonValueKind.True && ok.ValueKind != JsonValueKind.False))
        {
            throw new InvalidDataException("Desktop IPC response is missing a boolean ok field.");
        }

        if (ok.GetBoolean())
        {
            var result = root.TryGetProperty("result", out var value)
                ? value.Clone()
                : JsonSerializer.SerializeToElement<object?>(null);
            return DesktopIpcResponse.Success(result);
        }

        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Desktop IPC error response is missing its error object.");
        }

        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        if (code is null || message is null)
        {
            throw new InvalidDataException("Desktop IPC error response requires code and message strings.");
        }

        var reason = error.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString()
            : null;
        return DesktopIpcResponse.Failure(new DesktopIpcError(code, message, reason));
    }
}

public sealed record DesktopIpcError(string Code, string Message, string? Reason = null);

/// <summary>桌面端在最终响应前上报的阶段进度；value 为 0..1，message 为阶段标识（capture/uia/execute/verify 等）。</summary>
public sealed record DesktopProgress(double Value, string? Message);

public sealed record DesktopIpcResponse(bool Ok, JsonElement Result, DesktopIpcError? Error)
{
    public static DesktopIpcResponse Success(JsonElement result) => new(true, result, null);

    public static DesktopIpcResponse Failure(DesktopIpcError error) => new(false, default, error);
}
