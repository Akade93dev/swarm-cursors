using System.Text.Json;

namespace Swarm.Gateway;

public static class GatewayIpcProtocol
{
    public const string PipeName = "SwarmDesktop.V1";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(65);

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

public sealed record DesktopIpcResponse(bool Ok, JsonElement Result, DesktopIpcError? Error)
{
    public static DesktopIpcResponse Success(JsonElement result) => new(true, result, null);

    public static DesktopIpcResponse Failure(DesktopIpcError error) => new(false, default, error);
}
