using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Hevo.Charting.Renderers.Raster;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DrawPolygon / DrawBand:
    /// ① 录制(命令形状、引用不拷贝);
    /// ② 图形拆分(NaN 断带、上下穿越分段、穿越点位置);
    /// ③ 像素:WPF 矢量 vs 位图后端逐像素对比,横竖边跟填充矩形同一取整、任意 DPI 无半透明边,
    ///    断带处没有多 figure 幻线,分段两色各在各的位置;位图后端上屏 0 分配。
    /// </summary>
    public sealed class FillDrawingTests
    {
        private readonly ITestOutputHelper _out;
        public FillDrawingTests(ITestOutputHelper output) => _out = output;

        private static readonly HevoSolidBrush GreenBrush = new(HevoColor.FromRgb(0, 160, 0));
        private static readonly HevoSolidBrush RedBrush = new(HevoColors.Red);
        private static readonly HevoPen NavyPen = new(new HevoSolidBrush(HevoColors.Navy), 1);

        // ====================================================================
        // ① 录制
        // ====================================================================

        private sealed class Recorder : IDrawingSink
        {
            public readonly List<DrawCmd> Cmds = new();
            public void Push(DrawCmd cmd) => Cmds.Add(cmd);
        }

        [Fact]
        public void Record_PolygonAndBand_KeepReferences()
        {
            var sink = new Recorder();
            var pts = new List<HevoPoint> { new(0, 0), new(10, 0), new(5, 8) };
            var band = new HevoBand();
            band.Add(0, 1, 5);
            band.Add(10, 2, 6);
            sink.DrawPolygon(GreenBrush, NavyPen, pts);
            sink.DrawBand(GreenBrush, null, band, RedBrush);

            Assert.Equal(2, sink.Cmds.Count);
            Assert.Equal(DrawOp.DrawPolygon, sink.Cmds[0].Op);
            Assert.Same(pts, sink.Cmds[0].RefData);
            Assert.Same(GreenBrush, sink.Cmds[0].Brush);
            Assert.Same(NavyPen, sink.Cmds[0].Pen);
            Assert.Equal(DrawOp.DrawBand, sink.Cmds[1].Op);
            Assert.Same(band, sink.Cmds[1].RefData);
            Assert.Same(RedBrush, band.CrossedBrush);
            Assert.Equal(2, band.Count);

            // 已有指令的枚举值不能因为加了填充指令而变
            Assert.Equal(16, (int)DrawOp.Pop);
            Assert.Equal(17, (int)DrawOp.DrawPolygon);
            Assert.Equal(18, (int)DrawOp.DrawBand);
        }

        // ====================================================================
        // ② 图形拆分
        // ====================================================================

        private struct Collect : IFigureWriter
        {
            public List<List<HevoPoint>> Figures;
            public void Begin(HevoPoint p) => Figures.Add(new List<HevoPoint> { p });
            public void LineTo(HevoPoint p) => Figures[Figures.Count - 1].Add(p);
            public void End() { }
        }

        private static List<List<HevoPoint>> Band(HevoBand b, int sign)
        {
            var w = new Collect { Figures = new() };
            FillGeometry.Band(b.Upper.ToArray(), b.Lower.ToArray(), sign, ref w);
            return w.Figures;
        }

        [Fact]
        public void Band_OneFigurePerRun_NaNBreaks_IsolatedSampleSkipped()
        {
            var b = new HevoBand();
            for (int i = 0; i < 4; i++) b.Add(i * 10, 10, 20);  // 0..3
            b.AddGap();                                          // 4
            b.Add(50, 10, 20);                                   // 5:孤立样本,围不出面积
            b.Add(60, float.NaN, 20);                            // 6:只有一边是 NaN 也断
            for (int i = 7; i < 10; i++) b.Add(i * 10, 10, 20);  // 7..9

            var figs = Band(b, 0);
            Assert.Equal(2, figs.Count);
            // 上边正向 + 下边反向
            Assert.Equal(new HevoPoint[] { new HevoPoint(0, 10), new(10, 10), new(20, 10), new(30, 10), new(30, 20), new(20, 20), new(10, 20), new(0, 20) }, figs[0]);
            Assert.Equal(6, figs[1].Count);
            Assert.Equal(new HevoPoint(70, 10), figs[1][0]);

            var edges = new Collect { Figures = new() };
            FillGeometry.BandEdges(b.Upper.ToArray(), b.Lower.ToArray(), ref edges);
            Assert.Equal(4, edges.Figures.Count); // 两段 × 上下各一条
        }

        [Fact]
        public void Band_SplitsAtCrossing_SharedInterpolatedPoint()
        {
            // 上边 10 → 30,下边恒 20:x = 5 处穿越
            var b = new HevoBand();
            b.Add(0, 10, 20);
            b.Add(10, 30, 20);
            b.Add(20, 30, 20);

            var above = Band(b, +1);
            var crossed = Band(b, -1);
            Assert.Single(above);
            Assert.Single(crossed);
            Assert.Equal(new HevoPoint[] { new HevoPoint(0, 10), new(5, 20), new(0, 20) }, above[0]);
            Assert.Equal(new HevoPoint[] { new HevoPoint(5, 20), new(10, 30), new(20, 30), new(20, 20), new(10, 20) }, crossed[0]);
            Assert.Single(Band(b, 0)); // 不分段时还是一整个图形
        }

        [Fact]
        public void Band_CrossingExactlyOnSample_SharesTheSample()
        {
            var b = new HevoBand();
            b.Add(0, 10, 20);
            b.Add(10, 20, 20); // 恰好重合
            b.Add(20, 30, 20);
            var above = Band(b, +1);
            var crossed = Band(b, -1);
            Assert.Equal(new HevoPoint[] { new HevoPoint(0, 10), new(10, 20), new(10, 20), new(0, 20) }, above[0]);
            Assert.Equal(new HevoPoint[] { new HevoPoint(10, 20), new(20, 30), new(20, 20), new(10, 20) }, crossed[0]);
        }

        [Fact]
        public void Polygon_SkipsNonFinite_NeedsThreeVertices()
        {
            var w = new Collect { Figures = new() };
            Assert.True(FillGeometry.Polygon(new HevoPoint[] { new HevoPoint(0, 0), new(float.NaN, 1), new(10, 0), new(5, 5) }, ref w));
            Assert.Equal(3, w.Figures[0].Count);
            Assert.False(FillGeometry.Polygon(new HevoPoint[] { new HevoPoint(0, 0), new(float.PositiveInfinity, 1), new(10, 0) }, ref w));
            Assert.Single(w.Figures);
        }

        // ====================================================================
        // ③ 像素
        // ====================================================================

        private sealed class ScriptLayer : ChartLayer
        {
            private readonly Action<IDrawingSink> _draw;
            public ScriptLayer(Action<IDrawingSink> draw) { _draw = draw; Level = ChartLayerType.Main; Name = "script"; }
            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink) => _draw(drawSink);
        }

        private sealed class NoData : IVisualData
        {
            public static readonly NoData Instance = new();
            public T? Get<T>() where T : class => null;
            public void Publish<T>(T snapshot) where T : class { }
        }

        private const int W = 240, H = 160;

        private static ChartCell NewCell()
        {
            var cell = new ChartCell
            {
            }.WithShell();
            cell.Measure(new Size(W, H));
            cell.Arrange(new Rect(0, 0, W, H));
            cell.UpdateLayout();
            Assert.True(cell.DrawingCanvas.ActualWidth > 0, "测试用 ChartCell 没有完成布局");
            return cell;
        }

        private static uint[] Capture(Visual v)
        {
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            var px = new int[W * H];
            rtb.CopyPixels(px, W * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        /// <summary>同一套指令在真 ChartCell 里分别走 WPF 矢量和位图后端截图。</summary>
        private static (uint[] Wpf, uint[] Bmp) RenderBoth(Action<IDrawingSink> draw)
        {
            var cell = NewCell();
            var layer = new ScriptLayer(draw);
            cell.AddUnmanagedLayer(layer);
            layer.MarkDirty();
            layer.Update(NoData.Instance);
            cell.Invalidate();
            var wpf = Capture(cell);
            layer.Mode = RenderMode.Bitmap;
            cell.Invalidate();
            Assert.True(layer.Wpf().BitmapSurface is { IsMounted: true });
            return (wpf, Capture(cell));
        }

        /// <summary>离屏按 DPI k 画一次 WPF 矢量路径(同 StaticCacheAliasingTests)。</summary>
        private static uint[] RenderAt(Action<IDrawingSink> scene, double k)
        {
            var layer = new ScriptLayer(scene);
            layer.MarkDirty();
            layer.Update(NoData.Instance);
            layer.SwapBuffer();
            var provider = new WpfRenderProvider();
            provider.UpdateDpi(k);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) ((LayerBuffer)layer.Buffer!).Execute(provider, dc);
            int pw = (int)Math.Ceiling(W * k), ph = (int)Math.Ceiling(H * k);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new int[pw * ph];
            rtb.CopyPixels(px, pw * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        private static bool IsPartial(uint p) { uint a = p >> 24; return a != 0 && a != 0xFF; }

        private static int CountPartial(uint[] px) { int n = 0; foreach (var p in px) if (IsPartial(p)) n++; return n; }

        private static int CountDiff(uint[] a, uint[] b, bool skipEdges, out int covered)
        {
            int diff = 0;
            covered = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (skipEdges && NearPartial(a, i)) continue;
                if (a[i] != 0 || b[i] != 0) covered++;
                if (a[i] != b[i]) diff++;
            }
            return diff;
        }

        private static bool NearPartial(uint[] px, int i)
        {
            int x = i % W, y = i / W;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && nx < W && ny >= 0 && ny < H && IsPartial(px[ny * W + nx])) return true;
                }
            return false;
        }

        private static uint Px(uint[] px, int x, int y) => px[y * W + x];

        private static readonly uint OpaqueGreen = 0xFF00A000;
        private static readonly uint OpaqueRed = 0xFFFF0000;

        // 横竖边的多边形 / 水平带子,小数坐标。带子中间断开一段。
        private static readonly List<HevoPoint> RectPolygon = new()
        {
            new(20.3f, 10.6f), new(90.7f, 10.6f), new(90.7f, 30.2f), new(20.3f, 30.2f),
        };

        private static readonly List<HevoPoint> LPolygon = new()
        {
            new(150.4f, 8.5f), new(170.6f, 8.5f), new(170.6f, 25.5f), new(210.2f, 25.5f), new(210.2f, 38.7f), new(150.4f, 38.7f),
        };

        private static HevoBand GapBand()
        {
            var b = new HevoBand();
            for (int i = 0; i <= 22; i++)
            {
                float x = 10.4f + i * 10;
                if (i >= 10 && i <= 12) b.AddGap();          // 110.4 / 120.4 / 130.4 处断开
                else b.Add(x, 60.3f, 140.6f);
            }
            return b;
        }

        private static void AxisAlignedFillScene(IDrawingSink d)
        {
            d.DrawPolygon(GreenBrush, null, RectPolygon);
            d.DrawPolygon(RedBrush, null, LPolygon);
            d.DrawBand(GreenBrush, null, GapBand());
        }

        // 同样的形状用填充矩形画:填充多边形 / 带子的横竖边必须跟它逐像素一致
        private static void SameShapesAsRectangles(IDrawingSink d)
        {
            d.DrawRectangle(GreenBrush, null, new HevoRect(20.3f, 10.6f, 70.4f, 19.6f));
            d.DrawRectangle(RedBrush, null, new HevoRect(150.4f, 8.5f, 20.2f, 30.2f));
            d.DrawRectangle(RedBrush, null, new HevoRect(170.6f, 25.5f, 39.6f, 13.2f));
            d.DrawRectangle(GreenBrush, null, new HevoRect(10.4f, 60.3f, 90f, 80.3f));
            d.DrawRectangle(GreenBrush, null, new HevoRect(140.4f, 60.3f, 90f, 80.3f));
        }

        [Fact]
        public void AxisAlignedFills_MatchFillRect_AndBitmapBackend() => IncrementalTestKit.RunOnSta(() =>
        {
            var (wpf, bmp) = RenderBoth(AxisAlignedFillScene);
            var (rectWpf, _) = RenderBoth(SameShapesAsRectangles);
            Assert.Equal(0, CountPartial(wpf));
            int diffBmp = CountDiff(wpf, bmp, skipEdges: false, out int covered);
            int diffRect = CountDiff(wpf, rectWpf, skipEdges: false, out _);
            _out.WriteLine($"轴对齐填充:覆盖 {covered},跟位图后端不同 {diffBmp},跟填充矩形不同 {diffRect}");            Assert.True(covered > 15000, "场景没画出来");
            Assert.Equal(0, diffBmp);
            Assert.Equal(0, diffRect);

            // 断带处(两个后端)一个像素都没有:多 figure 的 StreamGeometry 不能在图形之间连出幻线
            for (int y = 0; y < H; y++)
                for (int x = 101; x < 140; x++)
                {
                    Assert.Equal(0u, Px(wpf, x, y));
                    Assert.Equal(0u, Px(bmp, x, y));
                }
        });

        [Theory]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void AxisAlignedFills_HaveNoPartialPixels_AtAnyDpi(double k) => IncrementalTestKit.RunOnSta(() =>
        {
            var px = RenderAt(d =>
            {
                AxisAlignedFillScene(d);
                d.DrawPolygon(null, new HevoPen(new HevoSolidBrush(HevoColors.Blue), 2), RectPolygon);
            }, k);
            int solid = 0;
            foreach (var p in px) if (p >> 24 == 0xFF) solid++;
            _out.WriteLine($"k={k}:实心 {solid},半透明 {CountPartial(px)}");
            Assert.True(solid > 15000 * k * k * 0.9, "场景没画出来");
            Assert.Equal(0, CountPartial(px));
        });

        private static HevoBand SineBand()
        {
            // 上边一个周期的正弦,下边恒 80:前半周期上边在上(绿),后半周期穿越到下面(红)
            var b = new HevoBand();
            for (int i = 0; i <= 110; i++)
            {
                float x = 10 + i * 2;
                b.Add(x, 80 - 50 * MathF.Sin((x - 10) / 220f * MathF.PI * 2), 80);
            }
            return b;
        }

        [Fact]
        public void CrossedBand_TwoColorsOnEachSide_CloseToWpf() => IncrementalTestKit.RunOnSta(() =>
        {
            var arrow = new List<HevoPoint> { new(150, 20), new(200, 45), new(150, 70), new(162, 45) };
            var (wpf, bmp) = RenderBoth(d =>
            {
                d.DrawBand(GreenBrush, null, SineBand(), RedBrush);
                d.DrawPolygon(RedBrush, NavyPen, arrow);
            });

            foreach (var px in new[] { wpf, bmp })
            {
                Assert.Equal(OpaqueGreen, Px(px, 65, 60));  // 波峰下方:上边在上 → 绿
                Assert.Equal(0u, Px(px, 65, 100));
                Assert.Equal(OpaqueRed, Px(px, 175, 100));  // 波谷:穿越 → 红
                Assert.Equal(0u, Px(px, 175, 60));
                Assert.Equal(OpaqueRed, Px(px, 170, 45));   // 实心箭头内部
            }
            Assert.True(CountPartial(wpf) > 50, "斜边应有抗锯齿");
            int diff = CountDiff(wpf, bmp, skipEdges: true, out int covered);
            double ratio = (double)diff / Math.Max(1, covered);
            _out.WriteLine($"分段带子 + 箭头:确定像素 {covered},不同 {diff}({ratio:P2})");
            Assert.True(covered > 4000, "场景没画出来");
            Assert.True(ratio < 0.02, $"跟 WPF 差异 {ratio:P1} 超过 2%");
        });

        [Fact]
        public void BandStroke_DrawsOnlyUpperAndLowerEdges() => IncrementalTestKit.RunOnSta(() =>
        {
            var b = new HevoBand();
            b.Add(20.2f, 30.4f, 90.6f);
            b.Add(200.7f, 30.4f, 90.6f);
            var (wpf, bmp) = RenderBoth(d => d.DrawBand(null, NavyPen, b));
            // 边的顶点同 DrawPolyline(对称 Vertex 对齐),线端那一列两个后端取舍不同(同折线),只比中段
            int covered = 0;
            for (int y = 0; y < H; y++)
                for (int x = 25; x < 195; x++)
                {
                    Assert.Equal(Px(wpf, x, y), Px(bmp, x, y));
                    if (Px(wpf, x, y) != 0) covered++;
                }
            Assert.Equal(2 * 170, covered);
            uint navy = 0xFF000080;
            Assert.Equal(navy, Px(wpf, 100, 30));
            Assert.Equal(navy, Px(wpf, 100, 90));
            Assert.Equal(0u, Px(wpf, 20, 60));   // 两端不封口
            Assert.Equal(0u, Px(wpf, 100, 60));  // 没有填充
        });

        [Fact]
        public void BitmapBackend_BandAndPolygon_DoNotAllocate() => IncrementalTestKit.RunOnSta(() =>
        {
            var buffer = new DrawingBuffer();
            var band = SineBand();
            var arrow = new List<HevoPoint> { new(150, 20), new(200, 45), new(150, 70), new(162, 45) };
            buffer.DrawBand(GreenBrush, NavyPen, band, RedBrush);
            buffer.DrawPolygon(RedBrush, NavyPen, arrow);

            var px = new uint[W * H];
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var surface = new RasterSurface();
                surface.Bind(pin.AddrOfPinnedObject(), W, H, W * 4, 1f);
                var renderer = new RasterDrawingRenderer();
                renderer.Render(buffer, surface); // 暖机:画刷 / 画笔缓存、边表扩容
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5; i++) renderer.Render(buffer, surface);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                surface.Unbind();
                _out.WriteLine($"位图后端 5 帧分配 {allocated} 字节");
                Assert.Equal(0, allocated);
            }
            finally { pin.Free(); }
        });
    }
}
