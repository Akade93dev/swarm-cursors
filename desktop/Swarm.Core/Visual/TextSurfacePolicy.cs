namespace Swarm.Core.Visual;

/// <summary>
/// 决定是否给重建文字铺一层与所在表面同色的不透明底色。
/// <para>
/// 透明覆盖层会把文字强制降级为灰度抗锯齿（字形整体偏重，DirectWrite 不允许在透明中间位图上用 ClearType），
/// 而且整屏截图底座里的原文会从重建文字下方透出，形成双绘加粗。给文字一个不透明同色底色，
/// 既能让 Chromium 重新启用 ClearType（子像素）渲染，又能盖住底座里的原文——一举解决两个主因。
/// </para>
/// <para>
/// 但底色只在“表面色可信”时铺：输入框有真实绘制的表面，普通文字落在窗口客户区/控件表面内时主色可信；
/// 壁纸、渐变这类非均匀表面上铺同色块会留下可见矩形，因此不铺，保留现状。
/// </para>
/// </summary>
public static class TextSurfacePolicy
{
    /// <param name="isEdit">是否为输入框/文档控件（有真实绘制的表面）。</param>
    /// <param name="hasWindowOwner">是否归属某个真实窗口（落在窗口客户区/控件表面内）。</param>
    public static bool ShouldPaint(bool isEdit, bool hasWindowOwner) => isEdit || hasWindowOwner;
}
