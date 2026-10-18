using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

// 01 号文档「UseMemo 报错怎么改」情况 1 改前:UseMemo 写在 helper 里(HEVO008)。
// 这个文件不参与 DocSamples 编译,由 HookAnalyzerTests 拿来验证分析器会报错。
namespace Hevo.Charting.DocSamples.UseMemo.Before
{
    public sealed class DualStatsFeature : Feature
    {
        public DataPort<ReadOnlyMemory<double>> Highs { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>> Lows { get; init; } = null!;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            var (highs, _) = ctx.UsePort(Highs);
            var (lows, _) = ctx.UsePort(Lows);
            var highStats = Stats(ctx, highs);
            var lowStats = Stats(ctx, lows);
        }

        // ❌ 两次调用都落在下面这一行的缓存上:highs、lows 交替写入,缓存每次失效。
        private static WindowStats Stats(FeatureContext ctx, ReadOnlyMemory<double> data)
            => ctx.UseMemo(data, static d => WindowStats.Of(d)).Value;
    }
}
