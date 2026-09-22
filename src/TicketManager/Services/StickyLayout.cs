namespace TicketManager.Services;

/// <summary>树顶部“钉住浮层”里各级表头的位置计算。抽成纯函数，便于单独验证（tools/StickyProbe）。</summary>
public static class StickyLayout
{
    /// <summary>
    /// 算每一级的上滑量（0 = 正常钉在槽位里，等于行高 = 已完全滑出、被槽位裁没）。
    /// </summary>
    /// <param name="heights">自外向内各级的行高（客户 / 产品 / 线索）。</param>
    /// <param name="boundaries">各级子树结束处：紧随其后的同级（或更外层）行的表头顶；
    /// <see cref="double.PositiveInfinity"/> 表示后面没有同级内容了。</param>
    /// <remarks>
    /// 规则：某级的内容全部滚进钉住区（子树末尾越过本行槽位下沿）后，这一级就在自己槽位里向上卷出，
    /// 滑出距离正好等于自身高度，滑完即被槽位裁掉；而且自内向外串行——内层滑完才轮到外层，
    /// 这样看到的就是三级（线索）→ 二级（产品）→ 一级（客户）依次卷出，而不是几行一起塌掉。
    /// </remarks>
    public static double[] ComputeSlides(IReadOnlyList<double> heights, IReadOnlyList<double> boundaries)
    {
        int n = heights.Count;
        var slides = new double[n];
        if (n == 0) return slides;

        double stackH = 0;
        for (int i = 0; i < n; i++) stackH += heights[i];

        // 时钟取最内层：它的子树末尾一到视口顶部，下一段内容就接上来了（钉住链也随之切换）
        double bottomSlot = stackH;
        double clock = double.IsPositiveInfinity(boundaries[n - 1])
            ? 0
            : Math.Max(0, bottomSlot - boundaries[n - 1]);

        double slotBottom = 0;
        for (int i = 0; i < n; i++)
        {
            double h = heights[i];
            slotBottom += h;
            double belowSum = stackH - slotBottom;                 // 更内层各行高度之和（要先滑完）
            double need = double.IsPositiveInfinity(boundaries[i])
                ? 0
                : Math.Max(0, slotBottom - boundaries[i]);         // 本行内容滚进钉住区多少
            slides[i] = Math.Clamp(Math.Min(need, clock - belowSum), 0, h);
        }
        return slides;
    }
}
