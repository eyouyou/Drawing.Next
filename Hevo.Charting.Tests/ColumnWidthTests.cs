using System;
using Hevo.Charting.Renderers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 像素规则与柱宽:
    /// ① 横竖线跟填充矩形是同一条像素中心规则——中心相同、宽度相同时覆盖同一批像素(影线 / 十字线跟 K 线实体同列的根据);
    /// ② 柱宽贴近配置、有空隙时不被取整吃掉、跟中线同奇偶;③ 十字星最小高度在 125% / 150% 下也是 1px。
    /// </summary>
    public sealed class ColumnWidthTests
    {
        // 线的覆盖列 [first, first + n):中心 = Vertex,向两边各展开 n/2
        private static int LineFirstColumn(double x, int n, float k)
        {
            float center = PixelSnap.Vertex(x, PixelSnap.HalfPx(n), k) * k;
            return (int)Math.Round(center - n / 2.0);
        }

        private static int RectFirstColumn(double x, int n, float k)
        {
            var r = PixelSnap.FillRect(new HevoRect((float)(x - n / (2.0 * k)), 0, (float)(n / (double)k), 1), k);
            return (int)Math.Round(r.X * k);
        }

        [Theory]
        [InlineData(1.0f)]
        [InlineData(1.25f)]
        [InlineData(1.5f)]
        public void Line_And_Rect_SameCenterSameWidth_CoverSamePixels(float k)
        {
            foreach (int n in new[] { 1, 2, 3, 5 })
            for (double x = 10; x < 14; x += 0.0137)
                Assert.True(LineFirstColumn(x, n, k) == RectFirstColumn(x, n, k), $"x={x:F4} n={n} k={k}");
        }

        [Fact]
        public void LineEnds_FollowRectEdges()
        {
            // 竖线两端 = 同一段 y 的填充矩形上下沿
            for (float y = 3; y < 5; y += 0.013f)
            {
                PixelSnap.SnapEndpoints(new HevoPoint(7.3f, y), new HevoPoint(7.3f, y + 9.6f), 0.5f, 1.25f, out var a, out var b);
                var r = PixelSnap.FillRect(new HevoRect(7f, y, 1f, 9.6f), 1.25f);
                Assert.Equal(r.Y, a.Y, 4);
                Assert.Equal(r.Bottom, b.Y, 4);
            }
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        [InlineData(2.0)]
        public void Width_NearIdeal_KeepsGap_MatchesLineParity(double k)
        {
            foreach (double ratio in new[] { 0.8, 0.6, 0.95 })
            foreach (int line in new[] { 0, 1, 2, 3 })
            for (double slot = 2.0 / k; slot < 40; slot += 0.013)
            {
                int w = ColumnWidth.Of(slot, ratio, k, line);
                double ideal = slot * ratio * k;
                int pitch = (int)Math.Floor(slot * k + 1e-4);
                int max = pitch >= 2 ? pitch - 1 : 1;
                string at = $"slot={slot:F3} k={k} ratio={ratio} line={line} → {w}";

                Assert.True(w >= 1 && w <= max, at);
                if (Math.Ceiling(ideal - 1 - 1e-4) <= max) Assert.True(Math.Abs(w - ideal) <= 1 + 1e-9, at);
                int parity = (line == 0 ? 1 : line) & 1;
                int lo = Math.Max(1, (int)Math.Ceiling(ideal - 1 - 1e-4));
                int hi = Math.Min(max, (int)Math.Floor(ideal + 1 + 1e-4));
                bool feasible = false;
                for (int c = lo; c <= hi; c++) feasible |= (c & 1) == parity;
                if (feasible) Assert.True((w & 1) == parity, at);
            }
        }

        [Fact]
        public void Width_Examples()
        {
            // 8 DIP 一格、BodyPadding 0.2:理想 6.4px,1px 影线取 7(不往小压成 5),2px 影线取 6
            Assert.Equal(7, ColumnWidth.Of(8, 0.8, 1.0, 1));
            Assert.Equal(6, ColumnWidth.Of(8, 0.8, 1.0, 2));
            // 每格 2px:1px 实体 + 1px 空隙
            Assert.Equal(1, ColumnWidth.Of(2, 0.8, 1.0, 1));
            // 每格 3px:理想 2.4,空隙要求 ≤ 2,同奇偶的 1 偏差超过 1px → 空隙优先取 2
            Assert.Equal(2, ColumnWidth.Of(3, 0.8, 1.0, 1));
            // 比例 = 1(不留空隙):不强加空隙,3px 一格就是 3px
            Assert.Equal(3, ColumnWidth.Of(3, 1.0, 1.0, 1));
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void MinHeight_IsOneDevicePixel(double k) => Assert.Equal(1, ColumnWidth.MinHeight(k));
    }
}
