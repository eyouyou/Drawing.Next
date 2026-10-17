using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.Renderers;
using Hevo.Charting.Renderers.Raster;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// K 线 + 成交量的设备像素几何:从离屏截图里量,
    /// 每根实体宽度相同且贴近 BodyPadding(误差 ≤ 1px)、相邻至少隔 1px、影线在实体正中(奇偶对不上时固定偏半像素)、
    /// 成交量柱跟 K 线同中心、十字星高度不随亚像素位置变、没有半透明边。100% / 125% / 150%,单格 ≈3 / 8 / 20 DIP,影线 1 / 2px。
    /// 100% 下位图后端跟矢量路径逐像素一致(两者现在是同一条像素中心规则)。
    /// </summary>
    public sealed class CandleColumnPixelTests
    {
        private readonly ITestOutputHelper _out;
        public CandleColumnPixelTests(ITestOutputHelper output) => _out = output;

        internal const int Count = 40;
        internal const float CandleH = 100, VolumeH = 40, DojiH = 40;
        internal static readonly RealRange PriceView = new(80, 130);
        internal static readonly RealRange XView = new(-0.37, Count - 0.37);
        internal const double BarRatio = 0.6;

        // 单格取 N + 0.3 DIP:整数单格在 100% 下每根小数位相同,看不出逐根跳变
        internal static float PlotWidth(int cellTarget) => (float)((cellTarget + 0.3) * Count);

        internal static double CellDip(float plotWidth)
            => plotWidth * Math.Abs(ScaleStrategyTrait.CandleMode.DomainScale.Normalize(1, XView) - ScaleStrategyTrait.CandleMode.DomainScale.Normalize(0, XView));

        internal static CandleStyle Style(double wick)
            => CandleStyle.CreateSolid(Color.FromRgb(234, 71, 109), Color.FromRgb(38, 166, 154), Color.FromRgb(128, 128, 128), wickWidth: wick);

        [Theory]
        [InlineData(1.0, 3, 1.0)] [InlineData(1.0, 8, 1.0)] [InlineData(1.0, 20, 1.0)]
        [InlineData(1.0, 3, 2.0)] [InlineData(1.0, 8, 2.0)] [InlineData(1.0, 20, 2.0)]
        [InlineData(1.25, 3, 1.0)] [InlineData(1.25, 8, 1.0)] [InlineData(1.25, 20, 1.0)]
        [InlineData(1.25, 3, 2.0)] [InlineData(1.25, 8, 2.0)] [InlineData(1.25, 20, 2.0)]
        [InlineData(1.5, 3, 1.0)] [InlineData(1.5, 8, 1.0)] [InlineData(1.5, 20, 1.0)]
        [InlineData(1.5, 3, 2.0)] [InlineData(1.5, 8, 2.0)] [InlineData(1.5, 20, 2.0)]
        public void Columns_StableWidth_CenteredWick_AlignedVolume(double k, int cellTarget, double wick) => IncrementalTestKit.RunOnSta(() =>
        {
            float w = PlotWidth(cellTarget);
            var style = Style(wick);
            var candle = RenderCandles(w, CandleH, k, style, doji: false, raster: false, out int pw, out _);
            var volume = RenderVolume(w, k, raster: false, out _, out _);
            var doji = RenderCandles(w, DojiH, k, style, doji: true, raster: false, out _, out int dojiH);
            var m = Measure(candle, volume, doji, pw, dojiH, k);

            double cellPx = CellDip(w) * k;
            double ideal = cellPx * (1 - style.BodyPadding);
            int linePx = ColumnWidth.LinePixels(style.WickPen, k);
            _out.WriteLine($"k={k} cell={cellPx:F2}px 期望实体 {ideal:F2}:实体 [{Join(m.Body)}] 影线偏移 [{Join(m.WickOffset)}] 间隙 [{Join(m.Gap)}] "
                + $"柱 [{Join(m.Bar)}] 柱偏移 [{Join(m.BarOffset)}] 十字星 [{Join(m.Doji)}] 半透明 {m.Semi}");

            // 可见的每一根都单独量到(粘连会把两根量成一段,根数就少了)
            Assert.InRange(m.Body.Count, Count - 3, Count);
            int body = Assert.Single(m.Body.Distinct().ToList());
            Assert.InRange(body, ideal - 1, ideal + 1);
            Assert.All(m.Gap, g => Assert.True(g >= 1, $"相邻实体间隙 {g}px"));

            // 影线:奇偶一致时每根都在正中;奇偶不同(2px 影线配奇数宽,或格子太小)时偏半像素,方向随小数位置
            double wickOff = (body & 1) == (linePx & 1) ? 0.0 : 0.5;
            Assert.All(m.WickOffset, o => Assert.Equal(wickOff, Math.Abs(o)));

            // 成交量柱跟 K 线同中心(柱优先奇数宽,跟 1px 影线 / 十字线同轴)
            int bar = Assert.Single(m.Bar.Distinct().ToList());
            Assert.InRange(bar, cellPx * BarRatio - 1, cellPx * BarRatio + 1);
            double barOff = (body & 1) == (bar & 1) ? 0.0 : 0.5;
            Assert.All(m.BarOffset, o => Assert.Equal(barOff, Math.Abs(o)));

            Assert.All(m.Doji, h => Assert.Equal(ColumnWidth.MinHeight(k), h));
            Assert.Equal(0, m.Semi);
        });

        // 位图后端本来就是像素中心规则;矢量路径的线改成同一条规则后,两者墨迹逐像素相同。
        // 只测 100%:位图后端在 125% / 150% 下先按 DIP 取整再放大(已知局限,K 线 / 成交量是 Software 图层,不走位图)
        [Theory]
        [InlineData(1.0, 3)]
        [InlineData(1.0, 8)]
        [InlineData(1.0, 20)]
        public void Raster_MatchesVector(double k, int cellTarget) => IncrementalTestKit.RunOnSta(() =>
        {
            float w = PlotWidth(cellTarget);
            var style = Style(1.0);
            var vec = RenderCandles(w, CandleH, k, style, doji: false, raster: false, out int pw, out int ph);
            var ras = RenderCandles(w, CandleH, k, style, doji: false, raster: true, out _, out _);
            var vecBar = RenderVolume(w, k, raster: false, out _, out int bh);
            var rasBar = RenderVolume(w, k, raster: true, out _, out _);
            int diff = 0;
            for (int i = 0; i < pw * ph; i++) if (Ink(vec[i]) != Ink(ras[i])) diff++;
            for (int i = 0; i < pw * bh; i++) if (Ink(vecBar[i]) != Ink(rasBar[i])) diff++;
            _out.WriteLine($"k={k} cell≈{cellTarget}:位图与矢量墨迹不同的像素 {diff}");
            Assert.Equal(0, diff);
        });

        // 前半段每格 1 份、后半段每格 2 份宽的 scale(模拟按真实时间轴 / 不等间距的自定义 scale)
        private sealed class TwoZoneScale : IScale
        {
            private readonly double _split;
            public TwoZoneScale(double split) { _split = split - 0.5; }
            private double U(double v) => v < _split ? v : _split + 2 * (v - _split);
            private double InvU(double u) => u < _split ? u : _split + (u - _split) / 2;
            public double Normalize(double value, RealRange range)
            {
                double a = U(range.Min - 0.5), b = U(range.Max + 0.5);
                return (U(value) - a) / (b - a);
            }
            public double Denormalize(double normalValue, RealRange range)
            {
                double a = U(range.Min - 0.5), b = U(range.Max + 0.5);
                return InvU(a + normalValue * (b - a));
            }
        }

        // 格宽逐根问 scale:不等间距的自定义 scale 下,K 线实体与成交量柱按各自那一格取宽,图层不用改
        [Fact]
        public void NonUniformScale_WidthFollowsEachSlot() => IncrementalTestKit.RunOnSta(() =>
        {
            const double k = 1.0;
            var scale = new ScaleStrategyTrait(new TwoZoneScale(Count / 2), new LinearScale());
            float w = 600;
            var style = Style(1.0);
            var candle = RenderCandles(w, CandleH, k, style, doji: false, raster: false, out int pw, out _, scale);
            var volume = RenderVolume(w, k, raster: false, out _, out _, scale);
            var doji = RenderCandles(w, DojiH, k, style, doji: true, raster: false, out _, out int dojiH, scale);
            var m = Measure(candle, volume, doji, pw, dojiH, k);

            var area = new HevoRect(0, 0, w, CandleH);
            double narrow = CoordinateExtensions.ProjectDomainSlotWidth(area, XView, scale, 0);
            double wide = CoordinateExtensions.ProjectDomainSlotWidth(area, XView, scale, Count - 1);
            int line = ColumnWidth.LinePixels(style.WickPen, k);
            var expectBodies = new[] { ColumnWidth.Of(narrow, 1 - style.BodyPadding, k, line), ColumnWidth.Of(wide, 1 - style.BodyPadding, k, line) };
            var expectBars = new[] { ColumnWidth.Of(narrow, BarRatio, k, 0), ColumnWidth.Of(wide, BarRatio, k, 0) };
            _out.WriteLine($"格宽 {narrow:F2} / {wide:F2}:实体 [{Join(m.Body)}] 期望 [{string.Join(",", expectBodies)}],柱 [{Join(m.Bar)}] 期望 [{string.Join(",", expectBars)}]");
            Assert.Equal(expectBodies, m.Body.Distinct().OrderBy(x => x).ToArray());
            Assert.Equal(expectBars, m.Bar.Distinct().OrderBy(x => x).ToArray());
            Assert.All(m.WickOffset, o => Assert.Equal(0.0, o));
        });

        private static string Join(IEnumerable<double> v) => string.Join(",", v.Distinct().OrderBy(x => x));
        private static string Join(IEnumerable<int> v) => string.Join(",", v.Distinct().OrderBy(x => x));

        // ── 渲染 ───────────────────────────────────────────────────────────────

        internal static uint[] RenderCandles(float w, float h, double k, CandleStyle style, bool doji, bool raster, out int pw, out int ph, ScaleStrategyTrait? scale = null)
        {
            var o = new double[Count]; var hi = new double[Count]; var lo = new double[Count]; var c = new double[Count];
            for (int i = 0; i < Count; i++)
            {
                if (doji)
                {
                    // 开 = 收,价格的小数位逐根变化,十字星落在不同的亚像素位置
                    o[i] = c[i] = 100 + (i % 7) * 0.137;
                    hi[i] = o[i] + 8; lo[i] = o[i] - 8;
                }
                else { o[i] = 100; c[i] = 110; hi[i] = 120; lo[i] = 90; }
            }
            var layer = new CandleLayer { PixelsPerDip = k };
            return Render(layer, w, h, k, PriceView, raster, ctx =>
            {
                ctx.For(layer).PublishData(new CandleData(0, o, hi, lo, c));
                ctx.For(layer).PublishData(style);
            }, out pw, out ph, scale);
        }

        internal static uint[] RenderVolume(float w, double k, bool raster, out int pw, out int ph, ScaleStrategyTrait? scale = null)
        {
            var v = Enumerable.Repeat(1.0, Count).ToArray();
            var layer = new BarLayer { PixelsPerDip = k };
            return Render(layer, w, VolumeH, k, new RealRange(0, 1.25), raster, ctx =>
                ctx.For(layer).PublishData(new BarDataTrait(v, new HevoSolidBrush(Color.FromRgb(70, 110, 200)), BarRatio)), out pw, out ph, scale);
        }

        private static uint[] Render(ChartLayer layer, float w, float h, double k, RealRange yView, bool raster, Action<RenderContext> publish, out int pw, out int ph, ScaleStrategyTrait? scale = null)
        {
            using (var ctx = IncrementalTestKit.NewRenderContext())
            {
                var g = ctx.Shared();
                g.PublishData(new PlotAreaTrait(new HevoRect(0, 0, w, h)));
                g.PublishData(new XAxisTrait(XView));
                g.PublishData(new YAxisTrait(yView));
                g.PublishData(scale ?? ScaleStrategyTrait.CandleMode);
                publish(ctx);
                ctx.SubmitSync(new IChartLayer[] { layer });
                using var frame = ctx.PrepareTasks(new IChartLayer[] { layer });
                foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
            }
            layer.SwapBuffer();
            pw = (int)Math.Ceiling(w * k);
            ph = (int)Math.Ceiling(h * k);
            var u = new uint[pw * ph];
            if (raster)
            {
                var pin = GCHandle.Alloc(u, GCHandleType.Pinned);
                try
                {
                    var surface = new RasterSurface();
                    surface.Bind(pin.AddrOfPinnedObject(), pw, ph, pw * 4, (float)k);
                    new RasterDrawingRenderer().Render(((LayerBuffer)layer.Buffer!).Drawing, surface);
                    surface.Unbind();
                }
                finally { pin.Free(); }
                return u;
            }
            var provider = new WpfRenderProvider();
            provider.UpdateDpi(k);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) ((LayerBuffer)layer.Buffer!).Execute(provider, dc);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new int[pw * ph];
            rtb.CopyPixels(px, pw * 4, 0);
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        // ── 量测 ───────────────────────────────────────────────────────────────

        internal sealed class Metrics
        {
            public readonly List<int> Body = new(), Gap = new(), Bar = new(), Doji = new();
            public readonly List<double> WickOffset = new(), BarOffset = new();
            public int Semi;
        }

        internal static bool Ink(uint p) => (p >> 24) != 0;
        // 实体红与影线灰的区分:红通道明显高于绿通道
        private static bool Red(uint p) => Ink(p) && ((p >> 16) & 0xFF) > ((p >> 8) & 0xFF) + 60;

        private static int Row(double value, RealRange view, float h, double k) => (int)((h - (value - view.Min) / view.Span * h) * k);

        private static List<(int Start, int Width)> Runs(uint[] px, int pw, int row)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int x = 0; x <= pw; x++)
            {
                bool on = x < pw && Ink(px[row * pw + x]);
                if (on && start < 0) start = x;
                if (!on && start >= 0) { runs.Add((start, x - start)); start = -1; }
            }
            return runs;
        }

        internal static Metrics Measure(uint[] candle, uint[] volume, uint[] doji, int pw, int dojiH, double k)
        {
            var m = new Metrics();
            // 实体行(收开之间)、影线行(实体上方)、成交量柱中段
            var bodies = Runs(candle, pw, Row(105, PriceView, CandleH, k));
            var wicks = Runs(candle, pw, Row(115, PriceView, CandleH, k));
            var bars = Runs(volume, pw, (int)(VolumeH * 0.6 * k));
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                double center = b.Start + b.Width / 2.0;
                m.Body.Add(b.Width);
                if (i > 0) m.Gap.Add(b.Start - (bodies[i - 1].Start + bodies[i - 1].Width));
                var wk = wicks.OrderBy(r => Math.Abs(r.Start + r.Width / 2.0 - center)).FirstOrDefault();
                if (wk.Width > 0) m.WickOffset.Add(wk.Start + wk.Width / 2.0 - center);
                var bar = bars.OrderBy(r => Math.Abs(r.Start + r.Width / 2.0 - center)).FirstOrDefault();
                if (bar.Width > 0) { m.Bar.Add(bar.Width); m.BarOffset.Add(bar.Start + bar.Width / 2.0 - center); }
                // 十字星:实体所在列范围里含红色像素的行数
                int rows = 0;
                for (int y = 0; y < dojiH; y++)
                {
                    bool red = false;
                    for (int x = b.Start; x < b.Start + b.Width && !red; x++) red = Red(doji[y * pw + x]);
                    if (red) rows++;
                }
                m.Doji.Add(rows);
            }
            foreach (var arr in new[] { candle, volume, doji })
                foreach (var p in arr) { uint a = p >> 24; if (a != 0 && a != 255) m.Semi++; }
            return m;
        }
    }
}
