using System;
using Hevo.Charting.Core;
using Hevo.Charting.DocSamples.UseMemo;
using Hevo.Charting.LowCode;

// 01 号文档「Use 体系」:三种 Use 在同一个 OnProject 里。
namespace Hevo.Charting.DocSamples.Use
{
    /// <summary>
    /// 在绘图区里标出收盘价的统计带。统计只跟数据有关,摆放只跟绘图区有关,
    /// 两样都没变就什么都不发布 —— 哪怕这一帧因为别的原因(环境纪元变了)被叫起来。
    /// </summary>
    public sealed class StatsBandFeature : Feature
    {
        public DataPort<ReadOnlyMemory<double>> Source { get; init; } = null!;

        /// <summary>重新摆放了几次(测试用)。真实 Feature 在这里换成 <c>ctx.For(layer).PublishData(...)</c>。</summary>
        public int PublishCount { get; private set; }

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            // 黑板输入:按端口版本比。首次读到时顺带订阅,端口一写就唤醒本 Feature。
            var (closes, _) = ctx.UsePort(Source);

            // Trait:按引用比本 Feature 上一帧读到的那个。只报告,不唤醒 ——
            // 绘图区由 Layout 阶段的 Feature 在环境纪元变化时发布,那一帧所有 Feature 都会被叫起。
            var (area, areaChanged) = ctx.Shared().Use<PlotAreaTrait>();

            // 派生值:依赖相等就拿上次的结果。
            var (stats, statsChanged) = ctx.UseMemo(closes, static data => WindowStats.Of(data));

            if (area == null || area.Area.IsEmpty) return;
            if (areaChanged || statsChanged)
            {
                PublishCount++;
            }
        }
    }
}
