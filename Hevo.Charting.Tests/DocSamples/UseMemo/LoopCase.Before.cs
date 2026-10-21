using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

// 01 号文档「UseMemo 报错怎么改」情况 2 改前:UseMemo 写在循环里(HEVO008)。
// 这个文件不参与 DocSamples 编译,由 HookAnalyzerTests 拿来验证分析器会报错。
namespace Hevo.Charting.DocSamples.UseMemo.Before
{
    public sealed class MultiStatsFeature : Feature
    {
        public DataPort<ReadOnlyMemory<double>>[] Sources { get; init; } = Array.Empty<DataPort<ReadOnlyMemory<double>>>();

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                var (data, _) = ctx.UsePort(Sources[i]);
                // ❌ 每一轮都落在这一行的缓存上:第 i 轮覆盖第 i-1 轮,缓存每次失效
                var stats = ctx.UseMemo(data, static d => WindowStats.Of(d)).Value;
            }
        }
    }
}
