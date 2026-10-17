using System;
using System.Collections.Generic;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

// 开发规范 §5 布林带 SOP —— 步骤 E:在业务 Schema 里声明式装配。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>示例用的最小数据源:一根收盘价序列,整批替换。</summary>
    public sealed class SampleCloseSource : BufferedDataSource<SampleCloseSource, SampleCloseSource.Bar>
    {
        public sealed class Bar
        {
            public Bar(double close) { Close = close; }
            public double Close { get; }
        }

        public void Load(IReadOnlyList<double> closes)
        {
            lock (_lock)
            {
                _buffer.Clear();
                for (int i = 0; i < closes.Count; i++) _buffer.Add(new Bar(closes[i]));
                Publish();
            }
        }
    }

    /// <summary>三根轨道由哪种写法计算。结果一致,差别见开发规范 §5.2。</summary>
    public enum BollingerComputeStyle
    {
        /// <summary>写法①:数据管线内 <c>.Compute(state, static ...)</c>,摄入时同步算。</summary>
        Pipeline,
        /// <summary>写法②:框架 <see cref="ComputeFeature"/> + C# 委托,后台线程算。</summary>
        ComputeFeature,
        /// <summary>进阶:自己继承 ComputeFeatureBase 写的 <see cref="BollingerComputeNode"/>,后台线程算,零分配。能用 ComputeFeature 就别这么写。</summary>
        ComputeNode,
    }

    /// <summary>
    /// 收盘价折线 + 布林带。数据流:数据源 → 摄入器写 Close 列 → 计算写三轨 → Feature 投影 → 图层绘制。
    /// </summary>
    public sealed class BollingerSampleSchema : ChartReactiveSchema
    {
        private readonly DataPort<ReadOnlyMemory<double>> _close = new DataPort<ReadOnlyMemory<double>>("BS_Close");
        private readonly DataPort<RealRange> _yRange = new DataPort<RealRange>("BS_YRange");
        private readonly BollingerComputeStyle _style;

        public BollingerSampleSchema(BollingerComputeStyle style = BollingerComputeStyle.Pipeline)
        {
            _style = style;
        }

        public SampleCloseSource Source { get; } = new SampleCloseSource();
        public BollingerPorts Bands { get; } = new BollingerPorts("BS_Boll");
        public DataPort<ReadOnlyMemory<double>> ClosePort => _close;

        protected override void DefineDataFlow(ChartCell chart)
        {
            var pipe = Source.Pipe()
                .LinkStream(cfg => cfg.Map(_close, x => x.Close));

            // 写法①:计算挂在数据管线上,跟摄入同一个写事务
            if (_style == BollingerComputeStyle.Pipeline)
                pipe = pipe.Compute(new BollingerPipelineState(_close, Bands, period: 20, width: 2.0),
                    static (board, source, st) => st.Recompute(board));

            pipe.BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            // 手写 Schema 必须 Seed 比例尺策略(蓝图里对应 InitialTraits),否则所有图层拿不到 IScale
            canvas.Seed(ScaleStrategyTrait.LineMode);

            var priceMeta = FieldMeta.Literal("收盘", Colors.SteelBlue, "F2");

            // 三根轨道也进 AutoScale,Y 轴量程才能包住上下轨
            var valuePorts = new List<DataPort<ReadOnlyMemory<double>>> { _close };
            valuePorts.AddRange(Bands.All);

            canvas
                .Environment(env => env
                    .SetupViewport()
                    .SetupAutoScale(_yRange, valuePorts.ToArray()))
                .Axes(axes => axes
                    .AddRangeAxis(_yRange, priceMeta))
                .Series(series =>
                {
                    series.AddLine(_close, _yRange, priceMeta);

                    // 写法②和进阶写法:计算是一个 Feature,跟渲染一样在 DefineFeatures 里挂
                    if (_style == BollingerComputeStyle.ComputeFeature)
                        series.AddBollingerCompute(_close, Bands, period: 20, width: 2.0);
                    else if (_style == BollingerComputeStyle.ComputeNode)
                        series.AddBollingerNode(_close, Bands, period: 20, width: 2.0);

                    series.AddBollinger(Bands, _yRange, Colors.Orange);
                });
        }

        /// <summary>
        /// 运行时显隐布林带(开发规范 §4 UI 事务)。增删 Feature 必须包在 <c>Transact</c> 里:
        /// 事务内摘挂订阅、事务结束统一补发数据,不会出现半装配的撕裂帧。
        /// 这里只增删渲染 Feature;三根轨道照常计算,重新显示时立刻有数据。
        /// </summary>
        public void SetBollingerVisible(bool visible)
        {
            Transact(canvas =>
            {
                canvas.Remove<BollingerFeature>();
                if (visible) canvas.Series(series => series.AddBollinger(Bands, _yRange, Colors.Orange));
            });
        }
    }
}
