namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 等宽柱(K 线实体、成交量柱)取几个设备像素宽。位置不用管:图层照旧交 scale 投影出的 DIP 中心 ± 半宽,
    /// 宽度是整数设备像素时,渲染器的像素中心规则排出来每根都是这个宽度,同一中心上的竖线(影线、十字线)只要奇偶相同就落在正中。
    /// </summary>
    internal static class ColumnWidth
    {
        // 单格 × k 刚好是整数时浮点误差可能落在整数下方一点,floor 会少算一列
        private const double Epsilon = 1e-4;

        /// <summary>画笔线宽在设备像素上占几列(0 = 没有线)。跟渲染器 <see cref="PixelSnap.HalfPx"/> 同口径。</summary>
        public static int LinePixels(HevoPen? pen, double k)
        {
            if (pen == null || pen.Thickness <= 0) return 0;
            return Math.Max(1, (int)Math.Ceiling(PixelSnap.DeviceThickness(pen.Thickness, k) - Epsilon));
        }

        /// <summary>
        /// 柱宽(设备像素):在 [理想 − 1, 理想 + 1] 里取跟中线同奇偶、离理想值最近的整数,理想 = 单格 × 比例 × k。
        /// 比例 &lt; 1(留了空隙)时不超过 floor(单格 × k) − 1,取整不会把空隙吃掉、相邻两根贴在一起;冲突时空隙优先。
        /// <paramref name="linePixels"/> = 0(没有中线,成交量柱)按 1px 中线取奇偶:跟默认 1px 影线 / 十字线同轴。
        /// </summary>
        public static int Of(double slotDip, double ratio, double k, int linePixels)
        {
            double ideal = slotDip * ratio * k;
            int max = int.MaxValue;
            if (ratio < 1)
            {
                int pitch = (int)Math.Floor(slotDip * k + Epsilon);
                max = pitch >= 2 ? pitch - 1 : 1;
            }
            int lo = Math.Max(1, (int)Math.Ceiling(ideal - 1 - Epsilon));
            int hi = Math.Min(max, (int)Math.Floor(ideal + 1 + Epsilon));
            if (lo > hi) return Math.Clamp((int)Math.Round(ideal), 1, max);

            int parity = (linePixels == 0 ? 1 : linePixels) & 1;
            int best = -1;
            double bestErr = double.MaxValue;
            for (int w = lo; w <= hi; w++)
            {
                if ((w & 1) != parity) continue;
                double err = Math.Abs(w - ideal);
                if (err < bestErr) { best = w; bestErr = err; }
            }
            return best > 0 ? best : Math.Clamp((int)Math.Round(ideal), lo, hi);
        }

        /// <summary>十字星等扁实体的最小高度(设备像素):1 DIP 按线宽规则换算,125% / 150% 下都是 1px。</summary>
        public static int MinHeight(double k) => Math.Max(1, (int)PixelSnap.DeviceThickness(1.0, k));
    }
}
