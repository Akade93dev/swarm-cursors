namespace Swarm.Core.Semantics;

/// <summary>
/// 观察元素 ID：<c>g{generation}:{path}</c>。把观察代数嵌进 ID，动作执行时才能区分
/// “本次观察的元素”和“上一次观察的同路径元素”——后者必须显式失败，而不是静默命中新元素。
/// </summary>
public static class ElementId
{
    private const char Prefix = 'g';
    private const char Separator = ':';

    public static string Create(long generation, string path) => $"g{generation}:{path}";

    /// <summary>解析 ID 携带的观察代数；没有代数前缀的 ID（外部手写/旧数据）返回 false。</summary>
    public static bool TryParseGeneration(string? id, out long generation)
    {
        generation = 0;
        if (string.IsNullOrEmpty(id) || id[0] != Prefix) return false;
        var separator = id.IndexOf(Separator);
        if (separator < 2) return false;
        return long.TryParse(
            id.AsSpan(1, separator - 1),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out generation);
    }

    /// <summary>去掉代数前缀后的稳定路径；用于跨观察记忆（如敏感元素打码）。</summary>
    public static string Stable(string id) =>
        TryParseGeneration(id, out _) ? id[(id.IndexOf(Separator) + 1)..] : id;
}
