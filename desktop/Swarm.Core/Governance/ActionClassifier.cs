using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Swarm.Core.Governance;

/// <summary>把动作标签/按键映射到需要确认的类别；屏幕文本不参与判定。</summary>
public static class ActionClassifier
{
    public const string Delete = "delete";
    public const string Send = "send";
    public const string Submit = "submit";
    public const string Purchase = "purchase";

    private static readonly string[] DeleteWords = { "delete", "remove", "trash", "erase", "删除", "移除", "清空", "回收站" };
    private static readonly string[] PurchaseWords = { "pay", "purchase", "buy", "checkout", "购买", "支付", "付款", "下单", "结算", "转账" };
    private static readonly string[] SendWords = { "send", "发送", "发出", "寄送", "投递" };
    private static readonly string[] SubmitWords = { "submit", "提交" };

    public static string? Classify(string? label, string? key = null)
    {
        var text = $"{label} {key}".Trim();
        if (text.Length == 0) return null;
        if (ContainsAny(text, DeleteWords)) return Delete;
        if (ContainsAny(text, PurchaseWords)) return Purchase;
        if (ContainsAny(text, SendWords)) return Send;
        if (ContainsAny(text, SubmitWords)) return Submit;
        return null;
    }

    private static bool ContainsAny(string text, string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>动作指纹：绑定 token 与具体动作，忽略 confirmToken/token 以免重试时变化。</summary>
public static class ActionFingerprint
{
    public static string Compute(string method, JsonElement parameters)
    {
        var builder = new StringBuilder(method);
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parameters.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (IsIgnored(property.Name)) continue;
                builder.Append('\n').Append(property.Name).Append('=').Append(property.Value.GetRawText());
            }
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsIgnored(string name) =>
        string.Equals(name, "confirmToken", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "token", StringComparison.OrdinalIgnoreCase);
}
