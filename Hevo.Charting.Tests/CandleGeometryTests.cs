using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// K 线实体与影线在设备像素上的几何:逐根画时实体宽度跟 BodyPadding 配置一致(取整误差不超过 1 个设备像素);
    /// LOD(单根不足 1 个物理像素)时影线与 1px 实体在同一列。100% / 125% / 150% 都成立。
    /// </summary>
    public sealed class CandleGeometryTests
    {
        private readonly ITestOutputHelper _out;
        public CandleGeometryTests(ITestOutputHelper output) => _out = output;

        private const float W = 300, H = 120;
        private static readonly RealRange YView = new(80, 130);

        // 全部阳线、同样的 OHLC:实体都在同一段 y,影线在实体上下
        // gapEvery > 0:每 gapEvery 根留空 gapEvery 根(NaN = 缺失),让 LOD 列之间出现空列
        private static uint[] Render(int count, double k, out int pw, int gapEvery = 0)
        {
            var o = new double[count]; var h = new double[count]; var l = new double[count]; var c = new double[count];
            for (int i = 0; i < count; i++)
            {
                bool gap = gapEvery > 0 && (i / gapEvery) % 2 == 1;
                o[i] = gap ? double.NaN : 100; c[i] = 110; h[i] = 120; l[i] = 90;
            }

            var layer = new CandleLayer { PixelsPerDip = k };
            using (var ctx = IncrementalTestKit.NewRenderContext())
            {
                var g = ctx.Shared();
                g.PublishData(new PlotAreaTrait(new HevoRect(0, 0, W, H)));
                // 视口起点带小数,每根的 x 中心都落在小数位置
                g.PublishData(new XAxisTrait(new RealRange(-0.37, count - 0.37)));
                g.PublishData(new YAxisTrait(YView));
                g.PublishData(ScaleStrategyTrait.CandleMode);
                ctx.For(layer).PublishData(new CandleData(0, o, h, l, c));
                ctx.SubmitSync(new IChartLayer[] { layer });
                using var frame = ctx.PrepareTasks(new IChartLayer[] { layer });
                foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
            }
            layer.SwapBuffer();

            var provider = new WpfRenderProvider();
            provider.UpdateDpi(k);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) ((LayerBuffer)layer.Buffer!).Execute(provider, dc);
            pw = (int)Math.Ceiling(W * k);
            int ph = (int)Math.Ceiling(H * k);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new int[pw * ph];
            rtb.CopyPixels(px, pw * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        private static int Row(double yValue, double k) => (int)((H - (yValue - YView.Min) / YView.Span * H) * k);

        // 一行里有墨的连续像素段 (起点, 宽度)
        private static List<(int Start, int Width)> Runs(uint[] px, int pw, int row)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int x = 0; x <= pw; x++)
            {
                bool ink = x < pw && px[row * pw + x] != 0;
                if (ink && start < 0) start = x;
                if (!ink && start >= 0) { runs.Add((start, x - start)); start = -1; }
            }
            return runs;
        }

        // 实体宽度只由 BodyPadding 决定(默认 0.2 = 占一格 80%),渲染器按像素中心取整,每根误差不超过 1 个设备像素
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void Bodies_FollowBodyPadding(double k) => IncrementalTestKit.RunOnSta(() =>
        {
            const int count = 37;
            var px = Render(count, k, out int pw);
            var bodies = Runs(px, pw, Row(105, k));
            // 单格宽度按图层同一个比例尺算(CandleMode 的 CategoryScale)
            var view = new RealRange(-0.37, count - 0.37);
            var scale = ScaleStrategyTrait.CandleMode.DomainScale;
            double cell = W * Math.Abs(scale.Normalize(1, view) - scale.Normalize(0, view));
            double expected = cell * (1 - CandleStyle.Default.BodyPadding) * k;
            _out.WriteLine($"k={k}:期望 {expected:F2} px,实际 [{string.Join(",", bodies.Select(b => b.Width))}]");
            Assert.True(bodies.Count > 30, "实体没画出来");
            foreach (var b in bodies) Assert.InRange(b.Width, expected - 1, expected + 1);
        });

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        public void Lod_WickSharesColumnWithBody(double k) => IncrementalTestKit.RunOnSta(() =>
        {
            // 单根远小于 1 个物理像素,按列聚合;隔一段留空,影线跑到相邻空列就能看出来
            var px = Render(1500, k, out int pw, gapEvery: 10);
            var bodyRow = Row(105, k);
            var wickRow = Row(115, k);
            int mismatched = 0, columns = 0;
            for (int x = 0; x < pw; x++)
            {
                bool body = px[bodyRow * pw + x] != 0, wick = px[wickRow * pw + x] != 0;
                if (body) columns++;
                if (body != wick) mismatched++;
            }
            _out.WriteLine($"k={k}:实体列 {columns},影线 / 实体不同列 {mismatched}");
            Assert.True(columns > 50 && columns < pw - 50, "LOD 没画出来或没有空列");
            Assert.Equal(0, mismatched);
        });
    }
}
