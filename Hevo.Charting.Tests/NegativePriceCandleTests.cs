using System;
using System.Collections.Generic;
using System.Linq;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Renderers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 负价 / 零价 K 线(期货价差合约、2020 年原油负价):图层照常录制绘制指令,缺失值只认 NaN / ±∞,
    /// 自动量程覆盖负值,对数纵轴遇到非正值不崩、不塌线。
    /// </summary>
    public class NegativePriceCandleTests
    {
        private const float PlotWidth = 100, PlotHeight = 100;

        private static (List<HevoRect> Up, List<HevoRect> Down, List<HevoPoint> Wicks) RenderCandles(
            double[] o, double[] h, double[] l, double[] c, RealRange xView, RealRange yView, ScaleStrategyTrait scale)
        {
            List<HevoRect> up = new(), down = new();
            List<HevoPoint> wicks = new();
            IncrementalTestKit.RunOnSta(() =>
            {
                var layer = new CandleLayer { PixelsPerDip = 1.0 };
                using (var ctx = IncrementalTestKit.NewRenderContext())
                {
                    var g = ctx.Shared();
                    g.PublishData(new PlotAreaTrait(new HevoRect(0, 0, PlotWidth, PlotHeight)));
                    g.PublishData(new XAxisTrait(xView));
                    g.PublishData(new YAxisTrait(yView));
                    g.PublishData(scale);
                    ctx.For(layer).PublishData(new CandleData(0, o, h, l, c));
                    ctx.SubmitSync(new IChartLayer[] { layer });
                    using var frame = ctx.PrepareTasks(new IChartLayer[] { layer });
                    foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
                }
                layer.SwapBuffer();
                foreach (var cmd in ((LayerBuffer)layer.Buffer).Drawing.Commands)
                {
                    if (cmd.Op == DrawOp.DrawLineSegments) wicks.AddRange((List<HevoPoint>)cmd.RefData!);
                    if (cmd.Op == DrawOp.DrawRectangles)
                    {
                        var target = ReferenceEquals(cmd.Brush, CandleStyle.Default.UpBrush) ? up : down;
                        target.AddRange((IList<HevoRect>)cmd.RefData!);
                    }
                }
            });
            return (up, down, wicks);
        }

        /// <summary>从 +start 一路跌到负值区的序列:每根 OHLC 都有限,中途必然跨过 0、也有恰好为 0 的价格。</summary>
        private static (double[] O, double[] H, double[] L, double[] C) CrossingZero(int n, double start, double step)
        {
            double[] o = new double[n], h = new double[n], l = new double[n], c = new double[n];
            for (int i = 0; i < n; i++)
            {
                o[i] = start - i * step;
                c[i] = o[i] - step;                // 阴线
                if (i % 3 == 0) c[i] = o[i] + step; // 夹几根阳线
                h[i] = Math.Max(o[i], c[i]) + 1;
                l[i] = Math.Min(o[i], c[i]) - 1;
            }
            return (o, h, l, c);
        }

        private static double Y(double v, RealRange y) => PlotHeight - (v - y.Min) / y.Span * PlotHeight;

        [Fact]
        public void AllNegativeBars_EveryBarRecorded()
        {
            // 价差合约:全部在 0 以下
            double[] o = { -12, -10, -15, -9, -11 };
            double[] c = { -10, -15, -9, -11, -11 };
            double[] h = o.Zip(c, (a, b) => Math.Max(a, b) + 1).ToArray();
            double[] l = o.Zip(c, (a, b) => Math.Min(a, b) - 1).ToArray();
            var y = new RealRange(-20, 0);

            var (up, down, wicks) = RenderCandles(o, h, l, c, new RealRange(0, 5), y, ScaleStrategyTrait.CandleMode);

            Assert.Equal(5, up.Count + down.Count);
            Assert.Equal(10, wicks.Count);
            // c >= o 判阳:-10>=-12、-9>=-15、-11>=-11 → 3 根阳线
            Assert.Equal(3, up.Count);
            Assert.Equal(2, down.Count);
            // 第一根影线端点正确投影到负价区
            Assert.Equal(Y(h[0], y), wicks[0].Y, 3);
            Assert.Equal(Y(l[0], y), wicks[1].Y, 3);
            Assert.All(wicks, p => Assert.InRange(p.Y, 0f, PlotHeight));
        }

        [Fact]
        public void ZeroCrossingBars_IncludingExactZeroOpen_AllRecorded()
        {
            var (o, h, l, c) = CrossingZero(20, start: 18, step: 2); // o[9] == 0,后半段全负
            Assert.Contains(0.0, o);
            Assert.Contains(o, v => v < 0);

            var (up, down, wicks) = RenderCandles(o, h, l, c, new RealRange(0, 20), new RealRange(-30, 30), ScaleStrategyTrait.CandleMode);

            Assert.Equal(20, up.Count + down.Count);
            Assert.Equal(40, wicks.Count);
        }

        [Fact]
        public void ZeroCrossingBars_SubPixelLod_StillRecorded()
        {
            // 0.05 DIP/根,走按像素列聚合路径
            var (o, h, l, c) = CrossingZero(2000, start: 50, step: 0.05);
            var (up, down, wicks) = RenderCandles(o, h, l, c, new RealRange(0, 2000), new RealRange(-60, 60), ScaleStrategyTrait.CandleMode);

            int candles = up.Count + down.Count;
            Assert.InRange(candles, 90, (int)PlotWidth);
            Assert.Equal(2 * candles, wicks.Count);
            // 末尾的列落在负价区(Y 在 0 轴下方)
            Assert.True(wicks[^1].Y > Y(0, new RealRange(-60, 60)));
        }

        [Fact]
        public void MissingBars_AreNaN_NotNonPositive()
        {
            double nan = double.NaN;
            double[] o = { 1, nan, 0, -1, 2, double.PositiveInfinity };
            double[] h = { 2, nan, 1, 0, 3, 3 };
            double[] l = { 0, nan, -1, -2, 1, 1 };
            double[] c = { 1.5, nan, 0.5, -0.5, 2.5, 2 };

            var (up, down, _) = RenderCandles(o, h, l, c, new RealRange(0, 6), new RealRange(-5, 5), ScaleStrategyTrait.CandleMode);

            // NaN 行与含 ∞ 的行跳过,零价 / 负价行保留
            Assert.Equal(4, up.Count + down.Count);
            Assert.True(CandleLayer.IsMissing(nan, 1, 1, 1));
            Assert.True(CandleLayer.IsMissing(1, 1, double.NegativeInfinity, 1));
            Assert.False(CandleLayer.IsMissing(0, 0, 0, 0));
            Assert.False(CandleLayer.IsMissing(-37.63, -37.63, -40.32, -37.63));
        }

        // ── 对数纵轴 ───────────────────────────────────────────────────────────

        [Fact]
        public void LogScale_RangeWithNonPositiveMin_FallsBackToLinear()
        {
            var log = LogarithmicScale.Instance;
            var range = new RealRange(-10, 10);
            Assert.Equal(0.5, log.Normalize(0, range), 9);
            Assert.Equal(0.25, log.Normalize(-5, range), 9);
            Assert.Equal(-5, log.Denormalize(0.25, range), 9);

            // 原先 range.Min<=0 一律返回 0,整张图塌成下沿一条线
            Assert.NotEqual(log.Normalize(-9, range), log.Normalize(9, range));
        }

        [Fact]
        public void LogScale_PositiveRange_NonPositiveValueGoesBelowAxis()
        {
            var log = LogarithmicScale.Instance;
            var range = new RealRange(1, 100);
            Assert.Equal(LogarithmicScale.BelowAxis, log.Normalize(0, range));
            Assert.Equal(LogarithmicScale.BelowAxis, log.Normalize(-3, range));
            Assert.Equal(0.5, log.Normalize(10, range), 9);
            Assert.True(double.IsFinite(log.Normalize(-3, range)));
        }

        // LogCandleMode 的自变量是 EdgeSnapped(索引 0 贴左沿,半根柱体越界会被剔除),视口左边多留 1 格
        private static readonly RealRange LogView = new(-1, 20);

        [Fact]
        public void LogCandleMode_NegativeBars_DoesNotThrowAndRecordsFiniteGeometry()
        {
            var (o, h, l, c) = CrossingZero(20, start: 18, step: 2);

            // 量程跨零 → 线性退化,每根都画
            var (up, down, wicks) = RenderCandles(o, h, l, c, LogView, new RealRange(-30, 30), ScaleStrategyTrait.LogCandleMode);
            Assert.Equal(20, up.Count + down.Count);
            Assert.All(wicks, p => Assert.True(float.IsFinite(p.Y)));
            Assert.All(up.Concat(down), r => Assert.True(float.IsFinite(r.Y) && float.IsFinite(r.Height)));

            // 量程为正、部分 K 线 ≤ 0 → 不抛,几何仍有限(非正部分被推到下沿外裁掉)
            var (up2, down2, wicks2) = RenderCandles(o, h, l, c, LogView, new RealRange(1, 30), ScaleStrategyTrait.LogCandleMode);
            Assert.Equal(20, up2.Count + down2.Count);
            Assert.All(wicks2, p => Assert.True(float.IsFinite(p.Y)));
        }

        // ── 自动量程 ───────────────────────────────────────────────────────────

        private static (double Min, double Max) Extrema(params double[][] columns)
        {
            double min = double.MaxValue, max = double.MinValue;
            bool has = false;
            foreach (var col in columns)
                UniversalAutoScaleFeature.AccumulateExtrema(col, 0, col.Length - 1, ref min, ref max, ref has);
            Assert.True(has);
            return (min, max);
        }

        [Fact]
        public void AutoScale_ExtremaIncludeNegativeAndZero_SkipOnlyNonFinite()
        {
            var (min, max) = Extrema(
                new[] { 5.0, double.NaN, -37.63, 0 },
                new[] { double.NegativeInfinity, 3.0, double.PositiveInfinity });
            Assert.Equal(-37.63, min);
            Assert.Equal(5.0, max);
        }

        [Fact]
        public void AutoScale_RespectsViewportWindow()
        {
            double min = double.MaxValue, max = double.MinValue;
            bool has = false;
            UniversalAutoScaleFeature.AccumulateExtrema(new[] { -100.0, -2, -3, 50 }, 1, 2, ref min, ref max, ref has);
            Assert.Equal((-3.0, -2.0), (min, max));
        }

        [Theory]
        [InlineData(AutoScaleStrategy.Normal)]
        [InlineData(AutoScaleStrategy.IncludeZero)]
        [InlineData(AutoScaleStrategy.SymmetricZero)]
        [InlineData(AutoScaleStrategy.ZeroWithSpace)]
        public void AutoScale_StrategiesCoverNegativeExtrema(AutoScaleStrategy strategy)
        {
            var (range, _) = UniversalAutoScaleFeature.ApplyStrategy(strategy, -40.32, -12.5, 0.05, double.NaN);
            Assert.True(range.IsValid);
            Assert.True(range.Min <= -40.32 && range.Max >= -12.5, $"{strategy}: [{range.Min}, {range.Max}]");
        }

        [Fact]
        public void AutoScale_FlatNegativeSeries_PadsOutward()
        {
            var (range, _) = UniversalAutoScaleFeature.ApplyStrategy(AutoScaleStrategy.Normal, -20, -20, 0.05, double.NaN);
            Assert.True(range.IsValid);
            Assert.True(range.Min < -20 && range.Max > -20);
        }

        [Theory]
        [InlineData(-15.0)] // 负的昨收:以前 padding = ref × 1% 为负,量程反而往里收
        [InlineData(0.0)]   // 零昨收:以前 padding = 0,量程退化成一个点
        public void AutoScale_SymmetricReference_NonPositiveReference_StaysValid(double refVal)
        {
            var (range, baseLine) = UniversalAutoScaleFeature.ApplyStrategy(AutoScaleStrategy.SymmetricReference, refVal, refVal, 0.05, refVal);
            Assert.True(range.IsValid, $"[{range.Min}, {range.Max}]");
            Assert.True(range.Min < refVal && range.Max > refVal);
            Assert.Equal(refVal, baseLine);
            Assert.Equal(refVal, range.Center, 9);
        }
    }
}
