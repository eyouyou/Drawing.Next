using System.Runtime.CompilerServices;

namespace Hevo.Charting.Renderers
{
    // 顶点对齐 helper：让 stroke 中心精确落在像素整数 / 半像素位置。
    // 配合 IsAntialias=true 的 stroke paint，AA scanline 算法对 H/V 线给出精确 1 像素覆盖，
    // 对斜线段给出平滑边缘。业务侧不可见——所有像素算术只在 renderer 命令分发循环里发生。
    internal static class PixelSnap
    {
        // 把 stroke 中心放在使其覆盖最少像素的位置：
        // ceil(thickness) 为奇 → 中心落在像素中心 (+0.5)，AA scanline 把单像素覆盖成 100%；
        // ceil(thickness) 为偶 → 中心落在像素边界 (+0)，AA scanline 把整数像素行覆盖成 100%。
        // 关键：必须用 Ceiling 而不是 (int) 截断，否则 thickness=0.5 等 sub-pixel 线宽会被算成偶数。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HalfPx(double thickness)
        {
            if (thickness <= 0) return 0f;
            int n = (int)Math.Ceiling(thickness);
            return (n % 2 != 0) ? 0.5f : 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Vertex(double v, float halfPx)
            => MathF.Round((float)v) + halfPx;

        // 单线段端点 snap：H/V 时**非对称** snap，斜线段两轴都 .5。
        //
        // 为什么 H/V 不能两轴都 .5：1px 水平线 (0,10)→(600,10) 若 snap 成 (0.5, 10.5)→(600.5, 10.5)，
        // Skia stroke 几何 X∈[0.5, 600.5]，AA scanline 给端点列各 50% 覆盖率。淡色 grid 在低对比背景下
        // 端点几乎不可见 → 视觉上"线没画全"。
        //
        // 正解：parallel 轴（线方向）走整数 round，端点像素列 100% 覆盖；perpendicular 轴走 .5 snap，
        // stroke 中心落像素中心 → 整条线全长 100% 覆盖、无端点羽化。
        public static void SnapEndpoints(HevoPoint p1, HevoPoint p2, float halfPx,
            out HevoPoint a, out HevoPoint b)
        {
            // 水平
            if (p1.Y == p2.Y)
            {
                float y = MathF.Round(p1.Y) + halfPx;
                a = new HevoPoint(MathF.Round(p1.X), y);
                b = new HevoPoint(MathF.Round(p2.X), y);
                return;
            }
            // 垂直
            if (p1.X == p2.X)
            {
                float x = MathF.Round(p1.X) + halfPx;
                a = new HevoPoint(x, MathF.Round(p1.Y));
                b = new HevoPoint(x, MathF.Round(p2.Y));
                return;
            }
            // 斜线段：两轴都 .5（标准 vertex snap）
            a = new HevoPoint(Vertex(p1.X, halfPx), Vertex(p1.Y, halfPx));
            b = new HevoPoint(Vertex(p2.X, halfPx), Vertex(p2.Y, halfPx));
        }

        // 填充矩形 / 裁剪框四边取整到**设备像素**边界。全图抗锯齿,小数边会变成半透明的边(K 线实体两侧发虚),
        // 取整后边缘全是实心像素。规则用像素中心 ceil(v·k - 0.5) / k(k = PixelsPerDip):覆盖的设备像素跟
        // 位图后端(RasterSurface.FillRect,无抗锯齿、像素中心采样)画原矩形完全一样。
        // 不用 MathF.Round:它对 .5 取偶,会跟像素中心规则差一列。
        public static HevoRect FillRect(HevoRect r, double pixelsPerDip)
        {
            float k = pixelsPerDip > 0 ? (float)pixelsPerDip : 1f;
            float left = CenterEdge(r.X, k);
            float top = CenterEdge(r.Y, k);
            float right = CenterEdge(r.Right, k);
            float bottom = CenterEdge(r.Bottom, k);
            return new HevoRect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float CenterEdge(float v, float k) => MathF.Ceiling(v * k - 0.5f) / k;

        // ==========================================
        // 设备像素版(k = PixelsPerDip)。规则同 Skia 后端:先乘 k 在设备像素上取整,再除回 DIP。
        // k = 1 时与上面的 DIP 版逐位相同。全图抗锯齿下靠它保证横竖线 / 矩形描边在任意 DPI 都落在整像素上不发虚。
        // ==========================================

        /// <summary>
        /// 线宽换算到设备像素。整数 DIP 线宽取整到整数设备像素(四舍、.5 舍去、至少 1px):
        /// 1px 线在 125% / 150% 下仍是 1 个设备像素,不会变成 1.25 / 1.5px 横跨两行发虚。小数线宽按比例缩放。
        /// </summary>
        public static double DeviceThickness(double t, double k)
        {
            if (k == 1.0 || k <= 0 || t <= 0) return t;
            double d = t * k;
            if (t == Math.Floor(t)) d = Math.Max(1.0, Math.Ceiling(d - 0.5));
            return d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Vertex(double v, float halfPx, float k)
            => k == 1f ? Vertex(v, halfPx) : (MathF.Round((float)v * k) + halfPx) / k;

        public static void SnapEndpoints(HevoPoint p1, HevoPoint p2, float halfPx, float k,
            out HevoPoint a, out HevoPoint b)
        {
            if (k == 1f) { SnapEndpoints(p1, p2, halfPx, out a, out b); return; }
            if (p1.Y == p2.Y)
            {
                float y = Vertex(p1.Y, halfPx, k);
                a = new HevoPoint(Vertex(p1.X, 0f, k), y);
                b = new HevoPoint(Vertex(p2.X, 0f, k), y);
                return;
            }
            if (p1.X == p2.X)
            {
                float x = Vertex(p1.X, halfPx, k);
                a = new HevoPoint(x, Vertex(p1.Y, 0f, k));
                b = new HevoPoint(x, Vertex(p2.Y, 0f, k));
                return;
            }
            a = new HevoPoint(Vertex(p1.X, halfPx, k), Vertex(p1.Y, halfPx, k));
            b = new HevoPoint(Vertex(p2.X, halfPx, k), Vertex(p2.Y, halfPx, k));
        }

        /// <summary>外边界先取整到设备像素,再向内收半个(设备)线宽。deviceThickness 用 <see cref="DeviceThickness"/> 换算后的值。</summary>
        public static HevoRect InsideStroke(HevoRect r, double deviceThickness, float k)
        {
            if (k == 1f) return InsideStroke(r, deviceThickness);
            float left = MathF.Round(r.X * k);
            float top = MathF.Round(r.Y * k);
            float right = MathF.Round(r.Right * k);
            float bottom = MathF.Round(r.Bottom * k);
            float h = (float)(deviceThickness / 2.0);
            return new HevoRect(
                (left + h) / k,
                (top + h) / k,
                Math.Max(0f, right - left - 2f * h) / k,
                Math.Max(0f, bottom - top - 2f * h) / k);
        }

        // stroke 中心从外边界向内收缩 halfT，等价于其它 GFX 的 "inside stroke alignment"。
        // 关键：必须先把外边界 round 到设备像素整数（layout 的浮点 rect 直接 +halfT 会让
        // stroke 中心落在浮点位置，AA 横跨 2 像素羽化）。round 之后再 inside-shrink，
        // 奇数 thickness → 中心在半像素 (X.5)；偶数 thickness → 中心在整像素 (X.0)。
        public static HevoRect InsideStroke(HevoRect r, double thickness)
        {
            float left = MathF.Round(r.X);
            float top = MathF.Round(r.Y);
            float right = MathF.Round(r.Right);
            float bottom = MathF.Round(r.Bottom);
            float h = (float)(thickness / 2.0);
            return new HevoRect(
                left + h,
                top + h,
                Math.Max(0f, right - left - 2f * h),
                Math.Max(0f, bottom - top - 2f * h));
        }
    }
}
