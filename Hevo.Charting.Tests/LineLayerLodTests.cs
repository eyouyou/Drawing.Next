using System;
using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// LineLayer 的按物理像素列 LOD:相邻点不足 1 个物理像素时,图层录进去的折线顶点数 ≤ 4 × 绘图区物理宽度
    /// (每列首 / 最低 / 最高 / 末),而不是可见点数。点距够宽时照常逐点录。
    /// </summary>
    public sealed class LineLayerLodTests
    {
        private const float PlotWidth = 100, PlotHeight = 100;

        private static int RecordedVertices(int count, double pixelsPerDip)
        {
            int vertices = 0;
            IncrementalTestKit.RunOnSta(() =>
            {
                var values = new double[count];
                for (int i = 0; i < count; i++) values[i] = 50 + 40 * Math.Sin(i * 0.37);

                var layer = new LineLayer { PixelsPerDip = pixelsPerDip };
                using (var ctx = IncrementalTestKit.NewRenderContext())
                {
                    var g = ctx.Shared();
                    g.PublishData(new PlotAreaTrait(new HevoRect(0, 0, PlotWidth, PlotHeight)));
                    g.PublishData(new XAxisTrait(new RealRange(0, count - 1)));
                    g.PublishData(new YAxisTrait(new RealRange(0, 100)));
                    g.PublishData(ScaleStrategyTrait.CandleMode);
                    ctx.For(layer).PublishData(new DoubleSeriesDataTrait(values));
                    ctx.SubmitSync(new IChartLayer[] { layer });
                    using var frame = ctx.PrepareTasks(new IChartLayer[] { layer });
                    foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
                }
                layer.SwapBuffer();
                foreach (var cmd in ((LayerBuffer)layer.Buffer!).Drawing.Commands)
                    if (cmd.Op == DrawOp.DrawPolyline) vertices += ((List<HevoPoint>)cmd.RefData!).Count;
            });
            return vertices;
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public void DensePoints_AreDecimatedPerPhysicalColumn(double k)
        {
            int vertices = RecordedVertices(20_000, k);
            int cap = 4 * (int)Math.Ceiling(PlotWidth * k) + 4;
            Assert.InRange(vertices, 2, cap);
        }

        // 列按物理像素划分:150% 下的列数是 100% 的 1.5 倍,顶点数要超过 100% 能给出的上限
        [Fact]
        public void HighDpi_UsesPhysicalColumns()
        {
            int at100 = RecordedVertices(20_000, 1.0);
            int at150 = RecordedVertices(20_000, 1.5);
            Assert.True(at150 > 4 * PlotWidth + 4, $"150% 下顶点 {at150},没按物理像素列抽稀(100% 下 {at100})");
        }

        [Fact]
        public void SparsePoints_AreNotDecimated()
        {
            Assert.Equal(50, RecordedVertices(50, 1.0)); // 点距 ≈ 2 DIP,不触发 LOD
        }
    }
}
