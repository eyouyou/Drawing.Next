using System.Runtime.CompilerServices;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 端口内容版本:UseMemo 以列为依赖时只比引用 / 起点 / 长度,最后一根原地改 + ForceWrite 后判定相等、缓存不更新;
    /// 把 <see cref="FeatureContext.PortVersion{T}"/> 并进依赖就能更新。两种写法并排验证。
    /// </summary>
    public sealed class PortVersionTests
    {
        private sealed class LastCloseFeature : Feature
        {
            public DataPort<ReadOnlyMemory<double>> Source { get; init; } = null!;
            public bool UseVersion { get; init; }
            public double Value { get; private set; }

            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

            protected override void OnProject(FeatureContext ctx)
            {
                var (closes, _) = ctx.UsePort(Source);
                var version = UseVersion ? ctx.PortVersion(Source) : default;
                var (value, _) = ctx.UseMemo((closes, version), static d => d.closes.Length == 0 ? double.NaN : d.closes.Span[d.closes.Length - 1]);
                Value = value;
            }
        }

        private sealed class ProbeSchema : ReactiveSchema
        {
            private readonly Feature _feature;
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public ProbeSchema(Feature feature) => _feature = feature;
            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas) => canvas.Add(_feature);
        }

        private static RenderContext NewRenderContext()
            => new RenderContext(new VisualDataBag(), new ConditionalWeakTable<IChartLayer, VisualDataBag>());

        [Theory]
        [InlineData(false, 3.0)]   // 只用列当依赖:拿不到原地改的最后一根
        [InlineData(true, 9.0)]    // 并上内容版本:能拿到
        public void InPlaceLastBar_SeenWithPortVersion(bool useVersion, double expected) => IncrementalTestKit.RunOnSta(() =>
        {
            var port = new DataPort<ReadOnlyMemory<double>>("C");
            var feature = new LastCloseFeature { Source = port, UseVersion = useVersion };
            var schema = new ProbeSchema(feature);
            var cell = new ChartCell { Template = schema };
            schema.ComposeAll(cell, NewRenderContext());
            var board = new DataBlackboard();
            schema.Trigger.Push(board);

            var data = new[] { 1.0, 2.0, 3.0 };
            using (board.WriteScope()) board.ForceWrite(port, new ReadOnlyMemory<double>(data));
            schema.InvalidateEnvironment();
            schema.ProjectAll(NewRenderContext());
            Assert.Equal(3.0, feature.Value);

            data[2] = 9.0;   // 最后一根原地改,同一块数组再 ForceWrite(实时 tick 的写法)
            using (board.WriteScope()) board.ForceWrite(port, new ReadOnlyMemory<double>(data));
            schema.ProjectAll(NewRenderContext());
            Assert.Equal(expected, feature.Value);
        });
    }
}
