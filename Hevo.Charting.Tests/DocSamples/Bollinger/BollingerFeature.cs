using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

// 开发规范 §5 布林带 SOP —— 步骤 D:Feature + DSL 扩展。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 一组布林带输出端口。由 Schema 持有并传给 <see cref="BollingerFeatureExtensions.AddBollinger"/>,
    /// 这样 Schema 还能把三根轨道接进 AutoScale,让 Y 轴量程包住上下轨。
    /// </summary>
    public sealed class BollingerPorts
    {
        public BollingerPorts(string prefix)
        {
            Upper = new DataPort<ReadOnlyMemory<double>>(prefix + "_Upper");
            Middle = new DataPort<ReadOnlyMemory<double>>(prefix + "_Middle");
            Lower = new DataPort<ReadOnlyMemory<double>>(prefix + "_Lower");
        }

        public DataPort<ReadOnlyMemory<double>> Upper { get; }
        public DataPort<ReadOnlyMemory<double>> Middle { get; }
        public DataPort<ReadOnlyMemory<double>> Lower { get; }

        public DataPort<ReadOnlyMemory<double>>[] All => new[] { Upper, Middle, Lower };
    }

    /// <summary>
    /// 把黑板上的三根轨道翻译成 <see cref="BollingerDataTrait"/> 交给图层。不做计算:三根轨道由 <see cref="BollingerSampleSchema"/> 选择的计算写法(<see cref="BollingerComputeStyle"/>)填充。
    /// </summary>
    public sealed class BollingerFeature : ChartFeature
    {
        // 💥 执行顺序由 Phase 决定(Series 在坐标轴 / AutoScale 之后),不靠 Add 的先后
        public override FeaturePhase Phase => FeaturePhase.Series;

        // 端口由 DSL 扩展或蓝图注入。反射注入的端口用 null! 声明,非空由装配保证。
        public DataPort<ReadOnlyMemory<double>> UpperPort { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>> MiddlePort { get; init; } = null!;
        public DataPort<ReadOnlyMemory<double>> LowerPort { get; init; } = null!;
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        public HevoColor Color { get; init; } = HevoColors.Orange;

        // 图层由 Feature 私有,Schema 不直接接触
        private readonly BollingerLayer _layer = new BollingerLayer();

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 装配期:注册图层,发布不随数据变化的样式
            AttachLayer(_layer).PublishData(BollingerStyle.Create(Color));
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 先全部解包再统一防空只是为了读着清楚;UsePort 放进分支或 return 之后也成立(01 号文档 §4)
            var (upper, _) = ctx.UsePort(UpperPort);
            var (middle, _) = ctx.UsePort(MiddlePort);
            var (lower, _) = ctx.UsePort(LowerPort);
            var (yRange, _) = ctx.UsePort(YRangePort);
            var (xRange, _) = ctx.UsePort(Viewport.ActiveRange);

            if (upper.Length == 0 || yRange.IsEmpty || !xRange.IsValid) return;

            var proxy = ctx.For(_layer);
            proxy.PublishData(new XAxisTrait(xRange));
            proxy.UpdateYAxis(yRange);
            proxy.PublishData(new BollingerDataTrait(upper, middle, lower));
        }
    }

    /// <summary>
    /// DSL 扩展:"谁的孩子谁抱走"。业务 Schema 写 <c>series.AddBollinger(...)</c>,不直接 new Feature。
    /// 这里只管渲染;三根轨道由哪种计算写法填,见 <see cref="BollingerSampleSchema"/>。
    /// </summary>
    public static class BollingerFeatureExtensions
    {
        public static SeriesBuilder AddBollinger(
            this SeriesBuilder builder,
            BollingerPorts bands,
            DataPort<RealRange> yRangePort,
            HevoColor? color = null)
        {
            builder.Canvas.Add(new BollingerFeature
            {
                UpperPort = bands.Upper,
                MiddlePort = bands.Middle,
                LowerPort = bands.Lower,
                YRangePort = yRangePort,
                Color = color ?? HevoColors.Orange,
            });
            return builder;
        }
    }
}
