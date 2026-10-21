using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// K 线样式变体(空心实体 / OHLC 美国线 / 影线分涨跌色)录出的指令与几何;
    /// 默认样式录出的指令跟加这些字段之前逐条一致(指纹在改动前的代码上录下)。
    /// </summary>
    public sealed class CandleStyleVariantTests
    {
        private readonly ITestOutputHelper _out;
        public CandleStyleVariantTests(ITestOutputHelper output) => _out = output;

        private const float W = 300, H = 120;
        private static readonly RealRange YView = new(80, 130);

        private static readonly HevoColor Up = HevoColor.FromRgb(0xE5, 0x2E, 0x3D), Down = HevoColor.FromRgb(0x2B, 0xAB, 0x60);

        // ── 录制 ───────────────────────────────────────────────────────────────

        internal sealed class Recording
        {
            public readonly List<DrawCmd> Commands = new();
            public IEnumerable<DrawCmd> Of(DrawOp op) => Commands.Where(c => c.Op == op);
        }

        private static Recording Record(CandleStyle? style, double[] o, double[] h, double[] l, double[] c, RealRange xView, double k)
        {
            var rec = new Recording();
            IncrementalTestKit.RunOnSta(() =>
            {
                var layer = new CandleLayer { PixelsPerDip = k };
                using (var ctx = IncrementalTestKit.NewRenderContext())
                {
                    var g = ctx.Shared();
                    g.PublishData(new PlotAreaTrait(new HevoRect(0, 0, W, H)));
                    g.PublishData(new XAxisTrait(xView));
                    g.PublishData(new YAxisTrait(YView));
                    g.PublishData(ScaleStrategyTrait.CandleMode);
                    ctx.For(layer).PublishData(new CandleData(0, o, h, l, c));
                    if (style != null) ctx.For(layer).PublishData(style);
                    ctx.SubmitSync(new IChartLayer[] { layer });
                    using var frame = ctx.PrepareTasks(new IChartLayer[] { layer });
                    foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
                }
                layer.SwapBuffer();
                foreach (var cmd in ((LayerBuffer)layer.Buffer!).Drawing.Commands)
                {
                    // 图层的列表跨帧复用,拷一份再交出去
                    object? data = cmd.RefData switch
                    {
                        List<HevoPoint> pts => pts.ToList(),
                        IList<HevoRect> rs => rs.ToList(),
                        _ => cmd.RefData,
                    };
                    rec.Commands.Add(new DrawCmd(cmd.Op, cmd.Pen, cmd.Brush, cmd.Payload, data));
                }
            });
            return rec;
        }

        // 涨跌混合 + 每 5 根一个十字星(开 = 收)
        private static (double[] O, double[] H, double[] L, double[] C) Bars(int n, int seed = 11)
        {
            var rnd = new Random(seed);
            double[] o = new double[n], h = new double[n], l = new double[n], c = new double[n];
            double p = 105;
            for (int i = 0; i < n; i++)
            {
                o[i] = p;
                c[i] = p = i % 5 == 4 ? p : Math.Clamp(p + (rnd.NextDouble() - 0.5) * 6, 88, 122);
                h[i] = Math.Max(o[i], c[i]) + rnd.NextDouble() * 3;
                l[i] = Math.Min(o[i], c[i]) - rnd.NextDouble() * 3;
            }
            return (o, h, l, c);
        }

        private static string Fingerprint(Recording rec)
        {
            var sb = new StringBuilder();
            foreach (var cmd in rec.Commands)
            {
                sb.Append(cmd.Op).Append('|').Append(Describe(cmd.Brush)).Append('|');
                if (cmd.Pen != null) sb.Append(Describe(cmd.Pen.Brush)).Append('@').Append(cmd.Pen.Thickness.ToString("R", CultureInfo.InvariantCulture));
                sb.Append('|').Append(F(cmd.Payload.RectArea.X)).Append(',').Append(F(cmd.Payload.RectArea.Y)).Append(',')
                  .Append(F(cmd.Payload.RectArea.Width)).Append(',').Append(F(cmd.Payload.RectArea.Height)).Append('|');
                if (cmd.RefData is List<HevoPoint> pts) foreach (var p in pts) sb.Append(F(p.X)).Append(',').Append(F(p.Y)).Append(';');
                if (cmd.RefData is List<HevoRect> rs) foreach (var r in rs) sb.Append(F(r.X)).Append(',').Append(F(r.Y)).Append(',').Append(F(r.Width)).Append(',').Append(F(r.Height)).Append(';');
                sb.Append('\n');
            }
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16);
        }

        private static string Describe(IHevoBrush? b) => b is HevoSolidBrush s ? s.Color.ToString() : b?.GetType().Name ?? "null";
        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        // ── 回归:默认样式 ──────────────────────────────────────────────────────

        // 指纹录自加样式字段之前的 CandleLayer。逐根(三种 DPI,含十字星)与 LOD 两条路径,
        // 不发样式(用 Default)与显式发一个 CreateSolid 都要一致
        [Theory]
        [InlineData(37, 1.0, false, "B1E154289D13EB5A")]
        [InlineData(37, 1.25, false, "3A16B06F7F8B1C65")]
        [InlineData(37, 1.5, true, "2B40CFADAE79B847")]
        [InlineData(2000, 1.0, false, "16B9CD2602EC5140")]
        [InlineData(2000, 1.25, true, "74CB0D4CE27A94E2")]
        public void DefaultStyle_RecordingUnchanged(int count, double k, bool explicitStyle, string expected)
        {
            var (o, h, l, c) = Bars(count);
            var style = explicitStyle ? CandleStyle.CreateSolid(Up, Down, HevoColors.Gray, wickWidth: 2.0) : null;
            var fp = Fingerprint(Record(style, o, h, l, c, new RealRange(-0.37, count - 0.37), k));
            _out.WriteLine($"count={count} k={k} explicit={explicitStyle}:{fp}");
            Assert.Equal(expected, fp);
        }

        // ── 指令:分涨跌影线 / 空心 / 美国线 ─────────────────────────────────────

        private static HevoPen Pen(HevoColor c, double t = 1.0) => new(new HevoSolidBrush(c), t);

        private static readonly CandleStyle Base = CandleStyle.CreateSolid(Up, Down, HevoColors.Gray);

        private static List<HevoPoint> Points(Recording r, HevoPen pen)
            => r.Of(DrawOp.DrawLineSegments).Where(c => ReferenceEquals(c.Pen, pen)).SelectMany(c => (List<HevoPoint>)c.RefData!).ToList();

        private static List<HevoRect> Rects(Recording r, IHevoBrush? brush, HevoPen? pen)
            => r.Of(DrawOp.DrawRectangles).Where(c => ReferenceEquals(c.Brush, brush) && ReferenceEquals(c.Pen, pen))
                .SelectMany(c => (List<HevoRect>)c.RefData!).ToList();

        private static double Slot(int count) => CoordinateExtensions.ProjectDomainSlotWidth(
            new HevoRect(0, 0, W, H), new RealRange(-0.37, count - 0.37), ScaleStrategyTrait.CandleMode, 0);

        // 影线分涨跌色(FinNex 实心):几何跟默认样式完全一样,只是影线按涨跌拆成两条指令
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        public void SplitWickPens_SameGeometry_ColoredByDirection(double k)
        {
            var (o, h, l, c) = Bars(37);
            var view = new RealRange(-0.37, 36.63);
            var upPen = Pen(Up); var downPen = Pen(Down);
            var plain = Record(Base, o, h, l, c, view, k);
            var split = Record(Base with { UpWickPen = upPen, DownWickPen = downPen }, o, h, l, c, view, k);

            Assert.Empty(Points(split, Base.WickPen));
            var upPts = Points(split, upPen); var downPts = Points(split, downPen);
            Assert.NotEmpty(upPts); Assert.NotEmpty(downPts);
            Assert.Equal(Points(plain, Base.WickPen).OrderBy(p => p.X).ThenBy(p => p.Y), upPts.Concat(downPts).OrderBy(p => p.X).ThenBy(p => p.Y));
            Assert.Equal(Rects(plain, Base.UpBrush, null), Rects(split, Base.UpBrush, null));
            Assert.Equal(Rects(plain, Base.DownBrush, null), Rects(split, Base.DownBrush, null));
            // 涨影线都在涨实体中心上
            var upCenters = Rects(split, Base.UpBrush, null).Select(r => r.X + r.Width / 2).ToList();
            Assert.All(upPts, p => Assert.Contains(upCenters, x => Math.Abs(x - p.X) < 1e-3));
        }

        // 涨跌影线粗细不同:实体宽度各按各的影线奇偶取,影线仍在正中
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void SplitWickPens_DifferentWidths_ParityPerDirection(double k)
        {
            var (o, h, l, c) = Bars(21);
            var style = Base with { UpWickPen = Pen(Up, 2.0), DownWickPen = Pen(Down, 1.0) };
            var rec = Record(style, o, h, l, c, new RealRange(-0.37, 20.63), k);
            double slot = Slot(21);
            int upLine = ColumnWidth.LinePixels(style.UpWickPen, k), downLine = ColumnWidth.LinePixels(style.DownWickPen, k);
            int upW = ColumnWidth.Of(slot, 0.8, k, upLine);
            int downW = ColumnWidth.Of(slot, 0.8, k, downLine);
            _out.WriteLine($"k={k} slot={slot:F3} 涨线 {upLine}px 实体 {upW}px,跌线 {downLine}px 实体 {downW}px");
            Assert.Equal(upLine & 1, upW & 1); Assert.Equal(downLine & 1, downW & 1);
            Assert.All(Rects(rec, style.UpBrush, null), r => Assert.Equal(upW, r.Width * k, 3));
            Assert.All(Rects(rec, style.DownBrush, null), r => Assert.Equal(downW, r.Width * k, 3));
        }

        // 空心:描边笔画实体矩形(跟实心同一个矩形),影线分上下两段、端点就是实体上下边,不进实体
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public void Hollow_RecordsOutlineRects_WicksStopAtBody(double k)
        {
            var (o, h, l, c) = Bars(37);
            var view = new RealRange(-0.37, 36.63);
            var upPen = Pen(Up);
            var filled = Record(Base with { UpWickPen = upPen }, o, h, l, c, view, k);
            var hollow = Record(Base with { UpWickPen = upPen, UpBody = CandleBodyKind.Hollow }, o, h, l, c, view, k);

            var bodies = Rects(filled, Base.UpBrush, null);
            var outlines = Rects(hollow, null, upPen);       // 描边笔缺省 = 涨影线笔
            var flat = Rects(hollow, upPen.Brush, null);     // 十字星:放不下内部,直接用描边色填
            Assert.Empty(Rects(hollow, Base.UpBrush, null));
            Assert.NotEmpty(outlines); Assert.NotEmpty(flat);
            Assert.Equal(bodies.OrderBy(r => r.X), outlines.Concat(flat).OrderBy(r => r.X));
            Assert.All(flat, r => Assert.True(r.Height * k < 3));
            // 跌线没改,照旧实心
            Assert.Equal(Rects(filled, Base.DownBrush, null), Rects(hollow, Base.DownBrush, null));

            var wicks = Points(hollow, upPen);
            for (int i = 0; i < wicks.Count; i += 2)
            {
                var a = wicks[i]; var b = wicks[i + 1];
                var body = bodies.Single(r => Math.Abs(r.X + r.Width / 2 - a.X) < 1e-3);
                Assert.True(Math.Max(a.Y, b.Y) <= body.Y || Math.Min(a.Y, b.Y) >= body.Bottom, $"影线 {a.Y}..{b.Y} 穿过实体 {body.Y}..{body.Bottom}");
            }
        }

        // 空心描边笔:显式配了用配的;都没配就是涨 / 跌色的 1px 线
        [Fact]
        public void Hollow_OutlinePenFallbacks()
        {
            var (o, h, l, c) = Bars(37);
            var view = new RealRange(-0.37, 36.63);
            var outline = Pen(HevoColors.White, 1.0);
            var rec = Record(Base with { UpBody = CandleBodyKind.Hollow, DownBody = CandleBodyKind.Hollow, UpOutlinePen = outline }, o, h, l, c, view, 1.0);
            var strokes = rec.Of(DrawOp.DrawRectangles).Where(x => x.Pen != null).ToList();
            Assert.Equal(2, strokes.Count);
            Assert.Same(outline, strokes[0].Pen);
            Assert.Same(Base.DownBrush, strokes[1].Pen!.Brush);
            Assert.Equal(1.0, strokes[1].Pen!.Thickness);
            // 没配分涨跌影线时影线照旧走 WickPen
            Assert.NotEmpty(Points(rec, Base.WickPen));
        }

        // 美国线:不画实体;每根一条最高→最低竖线 + 开盘横杠(左缘→竖线外沿)+ 收盘横杠(竖线外沿→右缘),横杠宽度规则同实体
        [Theory]
        [InlineData(1.0, 1.0)]
        [InlineData(1.25, 1.0)]
        [InlineData(1.5, 2.0)]
        public void Ohlc_RecordsBarAndTicks(double k, double wick)
        {
            var (o, h, l, c) = Bars(37);
            var view = new RealRange(-0.37, 36.63);
            var upPen = Pen(Up, wick); var downPen = Pen(Down, wick);
            var style = Base with { BarKind = CandleBarKind.Ohlc, UpWickPen = upPen, DownWickPen = downPen };
            var rec = Record(style, o, h, l, c, view, k);
            Assert.Empty(rec.Of(DrawOp.DrawRectangles));

            int linePx = ColumnWidth.LinePixels(upPen, k);
            int bodyPx = ColumnWidth.Of(Slot(37), 0.8, k, linePx);
            double tickPx = (bodyPx - linePx) / 2.0;
            var pts = Points(rec, upPen).Concat(Points(rec, downPen)).ToList();
            Assert.True(pts.Count >= 6 * 30, "美国线没画出来");
            Assert.Equal(0, pts.Count % 6);
            for (int i = 0; i < pts.Count; i += 6)
            {
                var top = pts[i]; var bottom = pts[i + 1];
                Assert.Equal(top.X, bottom.X);
                Assert.True(top.Y <= bottom.Y);
                var (l0, l1, r0, r1) = (pts[i + 2], pts[i + 3], pts[i + 4], pts[i + 5]);
                Assert.Equal(l0.Y, l1.Y); Assert.Equal(r0.Y, r1.Y);
                Assert.Equal(tickPx, (l1.X - l0.X) * k, 3);
                Assert.Equal(tickPx, (r1.X - r0.X) * k, 3);
                Assert.Equal(bodyPx, (r1.X - l0.X) * k, 3);
                Assert.Equal(top.X, (l0.X + r1.X) / 2, 3);
                Assert.True(l0.Y >= top.Y && l0.Y <= bottom.Y && r0.Y >= top.Y && r0.Y <= bottom.Y);
            }
        }

        // LOD:变体样式每列只画一根 1px 宽、最高→最低的竖条,列与涨跌判定跟默认样式的聚合结果一致,颜色取该样式的主色
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        public void Lod_VariantsCollapseToHighLowLine(double k)
        {
            var (o, h, l, c) = Bars(2000);
            var view = new RealRange(-0.37, 1999.63);
            var classic = Record(Base, o, h, l, c, view, k);
            var classicWicks = Points(classic, Base.WickPen);
            var span = new Dictionary<int, (float Top, float Bottom)>();
            for (int i = 0; i < classicWicks.Count; i += 2) span[(int)Math.Floor(classicWicks[i].X * k)] = (classicWicks[i].Y, classicWicks[i + 1].Y);
            static float Center(HevoRect r) => (float)(r.X + r.Width / 2.0);

            var upPen = Pen(Up); var downPen = Pen(Down);
            var outline = Pen(HevoColors.Orange);
            var variants = new (CandleStyle Style, IHevoBrush UpColor, IHevoBrush DownColor)[]
            {
                (Base with { UpWickPen = upPen, DownWickPen = downPen }, Base.UpBrush, Base.DownBrush),
                (Base with { UpBody = CandleBodyKind.Hollow, UpOutlinePen = outline }, outline.Brush, Base.DownBrush),
                (Base with { BarKind = CandleBarKind.Ohlc, UpWickPen = upPen, DownWickPen = downPen }, upPen.Brush, downPen.Brush),
            };
            foreach (var (style, upColor, downColor) in variants)
            {
                var rec = Record(style, o, h, l, c, view, k);
                Assert.Empty(rec.Of(DrawOp.DrawLineSegments));
                Assert.All(rec.Of(DrawOp.DrawRectangles), x => Assert.Null(x.Pen));
                var ups = Rects(rec, upColor, null); var downs = Rects(rec, downColor, null);
                Assert.Equal(rec.Of(DrawOp.DrawRectangles).Sum(x => ((List<HevoRect>)x.RefData!).Count), ups.Count + downs.Count);
                Assert.Equal(Rects(classic, Base.UpBrush, null).Select(Center), ups.Select(Center));
                Assert.Equal(Rects(classic, Base.DownBrush, null).Select(Center), downs.Select(Center));
                foreach (var r in ups.Concat(downs))
                {
                    Assert.Equal(1.0, r.Width * k, 3);
                    var (top, bottom) = span[(int)Math.Floor(Center(r) * k)];
                    Assert.Equal(top, r.Y);
                    Assert.True(r.Bottom >= bottom - 1e-3);
                }
            }
        }

        // ── 设备像素:离屏截图量 ─────────────────────────────────────────────────
        // 数据同 CandleColumnPixelTests:全部阳线,开 100 收 110 高 120 低 90;价格轴 80..130 铺 100 DIP 高,
        // 这几个价位在三种 DPI 下都正好落在设备像素边界上,行号可以精确断言

        private static int Row(double value, double k) => (int)Math.Round((CandleColumnPixelTests.CandleH
            - (value - CandleColumnPixelTests.PriceView.Min) / CandleColumnPixelTests.PriceView.Span * CandleColumnPixelTests.CandleH) * k);

        private static List<(int Start, int Width)> Runs(uint[] px, int pw, int row)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int x = 0; x <= pw; x++)
            {
                bool on = x < pw && CandleColumnPixelTests.Ink(px[row * pw + x]);
                if (on && start < 0) start = x;
                if (!on && start >= 0) { runs.Add((start, x - start)); start = -1; }
            }
            return runs;
        }

        private static int Semi(uint[] px) => px.Count(p => (p >> 24) != 0 && (p >> 24) != 255);

        // 空心:外框跟同样配置的实心实体是同一组像素;左右描边各 = 描边线宽;
        // 影线在实体正中那一列,从最高画到外框上沿、从外框下沿画到最低,实体内部一个像素都不涂
        [Theory]
        [InlineData(1.0, 8, 1.0)] [InlineData(1.0, 20, 1.0)] [InlineData(1.0, 20, 2.0)]
        [InlineData(1.25, 8, 1.0)] [InlineData(1.25, 20, 2.0)]
        [InlineData(1.5, 8, 1.0)] [InlineData(1.5, 20, 1.0)] [InlineData(1.5, 20, 2.0)]
        public void Hollow_Pixels_OutlineMatchesFilledBody_WickOutsideInterior(double k, int cellTarget, double wick) => IncrementalTestKit.RunOnSta(() =>
        {
            float w = CandleColumnPixelTests.PlotWidth(cellTarget);
            var pen = Pen(Up, wick);
            var filledStyle = CandleColumnPixelTests.Style(wick) with { UpWickPen = pen };
            var filled = CandleColumnPixelTests.RenderCandles(w, CandleColumnPixelTests.CandleH, k, filledStyle, doji: false, raster: false, out int pw, out int ph);
            var hollow = CandleColumnPixelTests.RenderCandles(w, CandleColumnPixelTests.CandleH, k, filledStyle with { UpBody = CandleBodyKind.Hollow }, doji: false, raster: false, out _, out _);

            int line = ColumnWidth.LinePixels(pen, k);
            var bodies = Runs(filled, pw, Row(105, k));
            var sides = Runs(hollow, pw, Row(105, k));
            _out.WriteLine($"k={k} cell≈{cellTarget} 线 {line}px:实体 [{string.Join(",", bodies.Select(b => b.Width).Distinct())}] 空心段 {sides.Count}");
            Assert.InRange(bodies.Count, CandleColumnPixelTests.Count - 3, CandleColumnPixelTests.Count);
            Assert.Equal(2 * bodies.Count, sides.Count);

            int top = Row(110, k), bottom = Row(100, k), high = Row(120, k), low = Row(90, k);
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i]; var left = sides[2 * i]; var right = sides[2 * i + 1];
                Assert.Equal(b.Start, left.Start);
                Assert.Equal(b.Start + b.Width, right.Start + right.Width);
                Assert.Equal(line, left.Width);
                Assert.Equal(line, right.Width);

                // 实体宽跟影线同奇偶时影线在正中:逐行扫这几列,墨迹 = 影线段 + 上下描边,内部为空
                Assert.Equal(line & 1, b.Width & 1);
                int wickStart = b.Start + (b.Width - line) / 2;
                for (int x = wickStart; x < wickStart + line; x++)
                    for (int y = 0; y < ph; y++)
                    {
                        bool interior = y >= top + line && y < bottom - line;
                        bool expected = y >= high && y < low && !interior;
                        Assert.True(expected == CandleColumnPixelTests.Ink(hollow[y * pw + x]), $"第 {i} 根 x={x} y={y}:期望{(expected ? "有" : "无")}墨");
                    }
                // 内部整块为空
                for (int y = top + line; y < bottom - line; y++)
                    for (int x = b.Start + line; x < b.Start + b.Width - line; x++)
                        Assert.False(CandleColumnPixelTests.Ink(hollow[y * pw + x]), $"第 {i} 根内部 x={x} y={y} 有墨");
            }
            Assert.Equal(0, Semi(hollow));
        });

        // 美国线:开盘行 = 左横杠 + 竖线,从实体左缘起;收盘行 = 竖线 + 右横杠,止于实体右缘;
        // 两侧横杠等长 = (实体宽 − 线宽) / 2,中间行只有竖线、在实体正中
        [Theory]
        [InlineData(1.0, 8, 1.0)] [InlineData(1.0, 20, 2.0)]
        [InlineData(1.25, 8, 1.0)] [InlineData(1.25, 20, 2.0)]
        [InlineData(1.5, 8, 1.0)] [InlineData(1.5, 20, 2.0)]
        public void Ohlc_Pixels_TicksFollowBodyWidth(double k, int cellTarget, double wick) => IncrementalTestKit.RunOnSta(() =>
        {
            float w = CandleColumnPixelTests.PlotWidth(cellTarget);
            var pen = Pen(Up, wick);
            var filledStyle = CandleColumnPixelTests.Style(wick) with { UpWickPen = pen };
            var filled = CandleColumnPixelTests.RenderCandles(w, CandleColumnPixelTests.CandleH, k, filledStyle, doji: false, raster: false, out int pw, out _);
            var ohlc = CandleColumnPixelTests.RenderCandles(w, CandleColumnPixelTests.CandleH, k, filledStyle with { BarKind = CandleBarKind.Ohlc }, doji: false, raster: false, out _, out _);

            int line = ColumnWidth.LinePixels(pen, k);
            var bodies = Runs(filled, pw, Row(105, k));
            var opens = Runs(ohlc, pw, Row(100, k));
            var closes = Runs(ohlc, pw, Row(110, k));
            var mids = Runs(ohlc, pw, Row(105, k));
            _out.WriteLine($"k={k} cell≈{cellTarget} 线 {line}px:实体 [{string.Join(",", bodies.Select(b => b.Width).Distinct())}] 开 [{string.Join(",", opens.Select(b => b.Width).Distinct())}] 收 [{string.Join(",", closes.Select(b => b.Width).Distinct())}]");
            Assert.InRange(bodies.Count, CandleColumnPixelTests.Count - 3, CandleColumnPixelTests.Count);
            Assert.Equal(bodies.Count, opens.Count);
            Assert.Equal(bodies.Count, closes.Count);
            Assert.Equal(bodies.Count, mids.Count);
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                Assert.Equal(line & 1, b.Width & 1);
                int tick = (b.Width - line) / 2;
                Assert.Equal((b.Start, tick + line), opens[i]);
                Assert.Equal((b.Start + tick, tick + line), closes[i]);
                Assert.Equal((b.Start + tick, line), mids[i]);
            }
            Assert.Equal(0, Semi(ohlc));
        });
    }
}
