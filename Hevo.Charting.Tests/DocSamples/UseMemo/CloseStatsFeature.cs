using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

// 01 号文档「UseMemo 是干什么的」:标准用法。
namespace Hevo.Charting.DocSamples.UseMemo
{
    /// <summary>一段数据的统计结果。</summary>
    public readonly struct WindowStats : IEquatable<WindowStats>
    {
        public WindowStats(double min, double max, double mean)
        {
            Min = min;
            Max = max;
            Mean = mean;
        }

        public double Min { get; }
        public double Max { get; }
        public double Mean { get; }

        public bool Equals(WindowStats other) => Min.Equals(other.Min) && Max.Equals(other.Max) && Mean.Equals(other.Mean);
        public override bool Equals(object? obj) => obj is WindowStats other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Min, Max, Mean);
        public override string ToString() => $"({Min}, {Max}, {Mean})";

        /// <summary>纯函数:同样的输入永远给同样的输出,适合当 UseMemo 的 factory。</summary>
        public static WindowStats Of(ReadOnlyMemory<double> data)
        {
            var span = data.Span;
            if (span.Length == 0) return default;
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            foreach (var v in span)
            {
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            return new WindowStats(min, max, sum / span.Length);
        }
    }

    /// <summary>
    /// 每帧都会跑 OnProject,但收盘价列大多数帧没变。统计要遍历整列,所以用 UseMemo:
    /// 列没变就拿上次的结果,IsChanged = false,也不用重新发布。
    /// </summary>
    public sealed class CloseStatsFeature : Feature
    {
        public DataPort<ReadOnlyMemory<double>> Source { get; init; } = null!;

        /// <summary>最近一次发布的统计。真实 Feature 在这里换成 <c>ctx.For(layer).PublishData(...)</c>。</summary>
        public WindowStats Published { get; private set; }

        /// <summary>统计实际算了几次(测试用)。</summary>
        public int ComputeCount { get; private set; }

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected override void OnProject(FeatureContext ctx)
        {
            var (closes, _) = ctx.UsePort(Source);

            // 依赖(closes)跟上一帧相等 → 直接返回上次的 stats,factory 不跑。
            // factory 写成 static lambda:不捕获 this,每帧 0 分配。
            var (stats, changed) = ctx.UseMemo(closes, static data => WindowStats.Of(data));

            // IsChanged 决定这一帧要不要往下游发布。
            if (changed)
            {
                ComputeCount++;
                Published = stats;
            }
        }
    }
}
