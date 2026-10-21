using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

// 01 号文档「UseMemo 报错怎么改」情况 2 改后:每一项各自缓存在字段数组里,不用 UseMemo。
namespace Hevo.Charting.DocSamples.UseMemo
{
    public sealed class MultiStatsFeature : Feature
    {
        private DataPort<ReadOnlyMemory<double>>[] _sources = Array.Empty<DataPort<ReadOnlyMemory<double>>>();
        private WindowStats[] _stats = Array.Empty<WindowStats>();

        // 缓存数组跟着来源数量在装配期分配一次,OnProject 里只读写、不 new。
        public DataPort<ReadOnlyMemory<double>>[] Sources
        {
            get => _sources;
            init
            {
                _sources = value;
                _stats = new WindowStats[value.Length];
            }
        }

        public ReadOnlySpan<WindowStats> Stats => _stats;

        /// <summary>统计实际算了几次(测试用)。</summary>
        public int ComputeCount { get; private set; }

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                var (data, changed) = ctx.UsePort(_sources[i]);
                // ✅ 第 i 项的缓存在 _stats[i],各管各的;端口没变就跳过
                if (!changed) continue;
                _stats[i] = WindowStats.Of(data);
                ComputeCount++;
            }
        }
    }
}
