using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

// 01 号文档「UseMemo 报错怎么改」情况 1 改后:UseMemo 移回 OnProject,helper 变成纯函数。
namespace Hevo.Charting.DocSamples.UseMemo
{
    public sealed class DualStatsFeature : Feature
    {
        public DataPort<ReadOnlyMemory<double>> Highs { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>> Lows { get; init; } = null!;

        public WindowStats HighStats { get; private set; }
        public WindowStats LowStats { get; private set; }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            var (highs, _) = ctx.UsePort(Highs);
            var (lows, _) = ctx.UsePort(Lows);

            // ✅ 两行 UseMemo 各有各的缓存
            HighStats = ctx.UseMemo(highs, static d => WindowStats.Of(d)).Value;
            LowStats = ctx.UseMemo(lows, static d => WindowStats.Of(d)).Value;
        }
    }
}
