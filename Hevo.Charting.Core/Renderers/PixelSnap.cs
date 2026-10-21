using System.Runtime.CompilerServices;

namespace Hevo.Charting.Renderers
{
    // 顶点对齐 helper：让 stroke 中心精确落在像素整数 / 半像素位置。
    // 配合 IsAntialias=true 的 stroke paint，AA scanline 算法对 H/V 线给出精确 1 像素覆盖，
    // 对斜线段给出平滑边缘。业务侧不可见——所有像素算术只在 renderer 命令分发循环里发生。
    //
    // 横平竖直的图元只有一条覆盖规则:每条边四舍五入到最近的像素边界 floor(v + 0.5)
    // (= 像素中心落在 (a, b] 内就涂,恰好压在中心上的边归右 / 下;位图后端的光栅规则同此)。
    // 宽 n 的竖线 = 矩形 [x − n/2, x + n/2],跟填充矩形用同一个取整。
    // 平局归右 / 下:100% 下整数坐标上的 1px 线(鼠标位置、布局边界)落在 [x, x+1) 这一列,跟以前一样。
    // 以前竖线按 round(x) + half 落列,奇数线宽时跟填充矩形差半格:同一个中心上的 1px 影线 / 十字线
    // 和奇数宽 K 线实体有一半小数位置会错开一列。
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

        // 线中心:覆盖范围 [v − n/2, v + n/2] 两边四舍五入后的中点。奇数线宽(halfPx = 0.5)= floor(v) + 0.5,
        // 偶数线宽(halfPx = 0)= floor(v + 0.5);只跟奇偶有关,跟 n 本身无关
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Vertex(double v, float halfPx)
            => MathF.Floor((float)v + 0.5f - halfPx) + halfPx;

        // 线端(沿线方向)同填充矩形的边
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float End(float v) => MathF.Floor(v + 0.5f);

        // 单线段端点 snap：H/V 时**非对称** snap，斜线段两轴都 .5。
        //
        // 为什么 H/V 不能两轴都 .5：1px 水平线 (0,10)→(600,10) 若 snap 成 (0.5, 10.5)→(600.5, 10.5)，
        // Skia stroke 几何 X∈[0.5, 600.5]，AA scanline 给端点列各 50% 覆盖率。淡色 grid 在低对比背景下
        // 端点几乎不可见 → 视觉上"线没画全"。
        //
        // 正解：parallel 轴（线方向）取整到像素边界，端点像素列 100% 覆盖；perpendicular 轴按线宽奇偶落在像素中心 / 边界，
        // stroke 中心落像素中心 → 整条线全长 100% 覆盖、无端点羽化。
        public static void SnapEndpoints(HevoPoint p1, HevoPoint p2, float halfPx,
            out HevoPoint a, out HevoPoint b)
        {
            // 水平
            if (p1.Y == p2.Y)
            {
                float y = Vertex(p1.Y, halfPx);
                a = new HevoPoint(End(p1.X), y);
                b = new HevoPoint(End(p2.X), y);
                return;
            }
            // 垂直
            if (p1.X == p2.X)
            {
                float x = Vertex(p1.X, halfPx);
                a = new HevoPoint(x, End(p1.Y));
                b = new HevoPoint(x, End(p2.Y));
                return;
            }
            // 斜线段：两轴都 .5（标准 vertex snap）
            a = new HevoPoint(Vertex(p1.X, halfPx), Vertex(p1.Y, halfPx));
            b = new HevoPoint(Vertex(p2.X, halfPx), Vertex(p2.Y, halfPx));
        }

        // 填充矩形 / 裁剪框四边取整到**设备像素**边界。全图抗锯齿,小数边会变成半透明的边(K 线实体两侧发虚),
        // 取整后边缘全是实心像素。规则 floor(v·k + 0.5) / k(k = PixelsPerDip):覆盖的设备像素跟
        // 位图后端(RasterSurface.FillRect,无抗锯齿)画原矩形完全一样。
        // 不用 MathF.Round:它对 .5 取偶,平局时一会儿归左一会儿归右。
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
        private static float CenterEdge(float v, float k) => MathF.Floor(v * k + 0.5f) / k;

        /// <summary>
        /// 填充多边形 / 带子的顶点:跟 <see cref="FillRect"/> 的边同一条取整(设备像素边界)。
        /// 横平竖直的边因此跟填充矩形、位图后端逐像素一致;斜边顶点最多挪半个设备像素,仍由抗锯齿画平滑。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float FillVertex(float v, float k) => MathF.Floor(v * k + 0.5f) / k;

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
            => k == 1f ? Vertex(v, halfPx) : (MathF.Floor((float)v * k + 0.5f - halfPx) + halfPx) / k;

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
            float left = MathF.Floor(r.X * k + 0.5f);
            float top = MathF.Floor(r.Y * k + 0.5f);
            float right = MathF.Floor(r.Right * k + 0.5f);
            float bottom = MathF.Floor(r.Bottom * k + 0.5f);
            float h = (float)(deviceThickness / 2.0);
            return new HevoRect(
                (left + h) / k,
                (top + h) / k,
                Math.Max(0f, right - left - 2f * h) / k,
                Math.Max(0f, bottom - top - 2f * h) / k);
        }

        // stroke 中心从外边界向内收缩 halfT，等价于其它 GFX 的 "inside stroke alignment"。
        // 关键：必须先把外边界取整到设备像素整数(同填充矩形的 floor(v + 0.5))（layout 的浮点 rect 直接 +halfT 会让
        // stroke 中心落在浮点位置，AA 横跨 2 像素羽化）。round 之后再 inside-shrink，
        // 奇数 thickness → 中心在半像素 (X.5)；偶数 thickness → 中心在整像素 (X.0)。
        public static HevoRect InsideStroke(HevoRect r, double thickness)
        {
            float left = End(r.X);
            float top = End(r.Y);
            float right = End(r.Right);
            float bottom = End(r.Bottom);
            float h = (float)(thickness / 2.0);
            return new HevoRect(
                left + h,
                top + h,
                Math.Max(0f, right - left - 2f * h),
                Math.Max(0f, bottom - top - 2f * h));
        }
    }
}
