using System.Text.RegularExpressions;
using Swarm.Core.Semantics;

namespace Swarm.Core.Governance;

public sealed record RedactionHit(string ElementId, string Category, string Risk, bool Necessary, RectInt Bounds);

/// <summary>
/// 敏感区域识别：控件元数据（密码框、名称敏感词）优先，其次对值做策略正则。
/// 只读取控件自身属性，不依赖屏幕文字，避免被界面内容诱导。
/// </summary>
public static partial class RedactionDetector
{
    public const string Password = "password";
    public const string Credential = "credential";
    public const string Email = "email";
    public const string IdentityNumber = "id";
    public const string BankCard = "bank";
    public const string Phone = "phone";

    public static readonly IReadOnlyList<string> AllCategories =
        new[] { Password, Credential, Email, IdentityNumber, BankCard, Phone };

    private static readonly string[] CredentialWords =
    {
        "password", "passwd", "pwd", "secret", "token", "credential", "api key", "apikey",
        "cvv", "pin", "otp", "密码", "口令", "密钥", "令牌", "验证码", "动态码"
    };

    public static IReadOnlyList<RedactionHit> Detect(RedactionOptions options, IEnumerable<UiaElementInfo> elements)
    {
        if (options.IsOff) return Array.Empty<RedactionHit>();
        var hits = new List<RedactionHit>();
        foreach (var element in elements)
        {
            var match = Classify(element);
            if (match is null || !options.Allows(match.Value.Category)) continue;
            hits.Add(new RedactionHit(element.Id, match.Value.Category, match.Value.Risk, false, element.Bounds));
        }
        return hits;
    }

    private static (string Category, string Risk)? Classify(UiaElementInfo element)
    {
        if (element.IsPassword) return (Password, "high");
        if (ContainsCredentialWord(element.Name)) return (Credential, "high");

        var value = element.Value;
        if (string.IsNullOrEmpty(value)) return null;
        if (IdentityPattern().IsMatch(value)) return (IdentityNumber, "high");
        if (BankCardPattern().IsMatch(value)) return (BankCard, "high");
        if (PhonePattern().IsMatch(value)) return (Phone, "medium");
        if (EmailPattern().IsMatch(value)) return (Email, "medium");
        return null;
    }

    private static bool ContainsCredentialWord(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var word in CredentialWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 18 位居民身份证：地区码 + 合法出生日期 + 顺序码 + 校验位。
    [GeneratedRegex(@"(?<!\d)[1-9]\d{5}(?:18|19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    // 16–19 位银行卡号，允许空格或短横线分组。
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){15,18}\d(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex BankCardPattern();

    [GeneratedRegex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
