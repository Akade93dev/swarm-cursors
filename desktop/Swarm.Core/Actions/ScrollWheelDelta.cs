namespace Swarm.Core.Actions;

/// <summary>滚轮增量规整：必须是有限数且绝对值不超过 1200，避免极端值在输入层发生 int 溢出。</summary>
public static class ScrollWheelDelta
{
    public const int MaxMagnitude = 1200;

    public static bool TryNormalize(double value, out int delta)
    {
        delta = 0;
        if (!double.IsFinite(value) || Math.Abs(value) > MaxMagnitude) return false;
        delta = (int)Math.Round(value);
        return true;
    }
}
