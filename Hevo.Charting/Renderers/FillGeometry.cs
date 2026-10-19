using System.Runtime.CompilerServices;

namespace Hevo.Charting.Renderers
{
    /// <summary>后端各自的图形输出(StreamGeometryContext / 扫描线边表 / SKPath)。用 struct 泛型调用,不装箱、不分配。</summary>
    internal interface IFigureWriter
    {
        void Begin(HevoPoint p);
        void LineTo(HevoPoint p);
        void End();
    }

    /// <summary>
    /// DrawPolygon / DrawBand 的图形拆分,三个后端共用一份:哪些点连成哪个闭合图形、NaN 在哪里断开、
    /// 上下穿越点算在哪。坐标保持 DIP 原样,像素对齐由各后端的 writer 做(规则不同:WPF / Skia 按设备像素
    /// 取整顶点,位图后端靠像素中心采样)。
    /// </summary>
    internal static class FillGeometry
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Finite(HevoPoint p) => float.IsFinite(p.X) && float.IsFinite(p.Y);

        /// <summary>多边形:跳过非有限顶点,有效顶点不足 3 个不出图形。返回是否输出。</summary>
        public static bool Polygon<T>(ReadOnlySpan<HevoPoint> pts, ref T w) where T : struct, IFigureWriter
        {
            int valid = 0;
            for (int i = 0; i < pts.Length; i++) if (Finite(pts[i])) valid++;
            if (valid < 3) return false;
            bool begun = false;
            for (int i = 0; i < pts.Length; i++)
            {
                if (!Finite(pts[i])) continue;
                if (!begun) { w.Begin(pts[i]); begun = true; }
                else w.LineTo(pts[i]);
            }
            w.End();
            return true;
        }

        /// <summary>
        /// 带状填充的闭合图形:每段连续有效样本(上下都有限)一个图形 = 上边正向 + 下边反向。
        /// <paramref name="sign"/> = 0 不分段;+1 只出"上边在上"(屏幕 y 上 &lt; 下)的部分;−1 只出穿越后的部分。
        /// 分段时在相邻样本间按线性插值求穿越点,两侧图形共用这个点。
        /// </summary>
        public static void Band<T>(ReadOnlySpan<HevoPoint> up, ReadOnlySpan<HevoPoint> lo, int sign, ref T w) where T : struct, IFigureWriter
        {
            int n = Math.Min(up.Length, lo.Length);
            int i = 0;
            while (i < n)
            {
                while (i < n && !(Finite(up[i]) && Finite(lo[i]))) i++;
                int a = i;
                while (i < n && Finite(up[i]) && Finite(lo[i])) i++;
                int b = i - 1;
                if (b <= a) continue; // 单个孤立样本围不出面积
                if (sign == 0) Emit(up, lo, a, b, false, default, false, default, ref w);
                else Run(up, lo, a, b, sign, ref w);
            }
        }

        /// <summary>带子的上下两条边(描边用):跟填充同样按有效样本断开,每段上下各一条开放折线。</summary>
        public static void BandEdges<T>(ReadOnlySpan<HevoPoint> up, ReadOnlySpan<HevoPoint> lo, ref T w) where T : struct, IFigureWriter
        {
            int n = Math.Min(up.Length, lo.Length);
            int i = 0;
            while (i < n)
            {
                while (i < n && !(Finite(up[i]) && Finite(lo[i]))) i++;
                int a = i;
                while (i < n && Finite(up[i]) && Finite(lo[i])) i++;
                int b = i - 1;
                if (b <= a) continue;
                w.Begin(up[a]);
                for (int k = a + 1; k <= b; k++) w.LineTo(up[k]);
                w.End();
                w.Begin(lo[a]);
                for (int k = a + 1; k <= b; k++) w.LineTo(lo[k]);
                w.End();
            }
        }

        // 屏幕坐标:下边 y − 上边 y > 0 = 上边在上
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Side(HevoPoint u, HevoPoint l) { float d = l.Y - u.Y; return d > 0 ? 1 : d < 0 ? -1 : 0; }

        private static void Run<T>(ReadOnlySpan<HevoPoint> up, ReadOnlySpan<HevoPoint> lo, int a, int b, int sign, ref T w) where T : struct, IFigureWriter
        {
            int first = a;
            bool hasStart = false;
            HevoPoint start = default;
            int side = Side(up[a], lo[a]);
            for (int i = a; i < b; i++)
            {
                int next = Side(up[i + 1], lo[i + 1]);
                // 重合的样本(差 = 0)并进当前段;段的方向由第一个非零样本定
                if (side == 0) { side = next; continue; }
                if (next == 0 || next == side) continue;

                float d0 = lo[i].Y - up[i].Y;
                if (d0 == 0)
                {
                    // 正好在样本 i 上相交:两段共用样本 i,不另加穿越点
                    if (side == sign) Emit(up, lo, first, i, hasStart, start, false, default, ref w);
                    first = i;
                    hasStart = false;
                }
                else
                {
                    float d1 = lo[i + 1].Y - up[i + 1].Y;
                    float t = d0 / (d0 - d1);
                    // 上下两条线段在 t 处 y 相同;x 两边可能不同(上下边 x 不对齐时),取中点
                    var c = new HevoPoint(
                        0.5f * (Lerp(up[i].X, up[i + 1].X, t) + Lerp(lo[i].X, lo[i + 1].X, t)),
                        0.5f * (Lerp(up[i].Y, up[i + 1].Y, t) + Lerp(lo[i].Y, lo[i + 1].Y, t)));
                    if (side == sign) Emit(up, lo, first, i, hasStart, start, true, c, ref w);
                    first = i + 1;
                    hasStart = true;
                    start = c;
                }
                side = next;
            }
            if (side == sign) Emit(up, lo, first, b, hasStart, start, false, default, ref w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // 图形 = [起点穿越点] 上边 first..last [终点穿越点] 下边 last..first,闭合回起点
        private static void Emit<T>(ReadOnlySpan<HevoPoint> up, ReadOnlySpan<HevoPoint> lo, int first, int last,
            bool hasStart, HevoPoint start, bool hasEnd, HevoPoint end, ref T w) where T : struct, IFigureWriter
        {
            int k = first;
            if (hasStart) w.Begin(start);
            else w.Begin(up[k++]);
            for (; k <= last; k++) w.LineTo(up[k]);
            if (hasEnd) w.LineTo(end);
            for (k = last; k >= first; k--) w.LineTo(lo[k]);
            w.End();
        }
    }
}
