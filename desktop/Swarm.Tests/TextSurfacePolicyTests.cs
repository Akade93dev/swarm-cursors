using Swarm.Core.Visual;

namespace Swarm.Tests;

/// <summary>
/// 给重建文字铺不透明同色底色的门槛：只在表面色可信时铺，避免壁纸/渐变上出现可见色块。
/// </summary>
public class TextSurfacePolicyTests
{
    [Theory]
    [InlineData(true, true, true)]   // 输入框落在窗口内：铺
    [InlineData(true, false, true)]  // 输入框即使无窗口归属也有真实表面：铺
    [InlineData(false, true, true)]  // 窗口内的普通文字：铺
    [InlineData(false, false, false)]// 壁纸/桌面文字：不铺，避免色块
    public void ShouldPaint_按控件类型与窗口归属决定(bool isEdit, bool hasWindowOwner, bool expected)
    {
        Assert.Equal(expected, TextSurfacePolicy.ShouldPaint(isEdit, hasWindowOwner));
    }
}
