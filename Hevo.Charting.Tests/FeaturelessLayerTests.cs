using System;
using System.Threading.Tasks;
using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 不写 Feature 的两条挂层路径:裸 ChartCell 的 <see cref="ChartCell.AddUnmanagedLayer{TLayer}"/>,
    /// 和 Schema 里的 <c>canvas.AddLayer</c>。规则跟 Feature 喂的图层一样:喂了本层数据才画,没人喂就一直睡。
    /// </summary>
    public sealed class FeaturelessLayerTests
    {
        private static ChartCell NewCell() => new()
        {
            Template = new ChartTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)) },
            Width = 200,
            Height = 100,
        };

        [Fact]
        public void UnmanagedLayer_FedThroughReturnedProxy_Draws() => RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ProbeLayer();
            var proxy = cell.AddUnmanagedLayer(layer);

            proxy.PublishData(new ProbeTraitA(1));
            cell.RunFrameNow(PlotMode.Sync);

            Assert.Equal(1, layer.UpdateCount);
        });

        [Fact]
        public void UnmanagedLayer_LaterFeeds_Redraw() => RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ProbeLayer();
            var proxy = cell.AddUnmanagedLayer(layer);
            proxy.PublishData(new ProbeTraitA(1));
            cell.RunFrameNow(PlotMode.Sync);

            proxy.PublishData(new ProbeTraitA(2));
            cell.RunFrameNow(PlotMode.Sync);
            Assert.Equal(2, layer.UpdateCount);

            // 没再喂:不重录
            cell.RunFrameNow(PlotMode.Sync);
            Assert.Equal(2, layer.UpdateCount);
        });

        [Fact]
        public void UnmanagedLayer_ProxyReadsPendingValue_AndUpdateDataChains() => RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ProbeLayer();
            var proxy = cell.AddUnmanagedLayer(layer);

            // 两次修改都在下一帧之前:第二次要读到第一次的结果,不能读到旧值
            proxy.UpdateData<ProbeTraitA>(old => new ProbeTraitA((old?.Value ?? 0) + 1));
            proxy.UpdateData<ProbeTraitA>(old => new ProbeTraitA((old?.Value ?? 0) + 1));
            Assert.Equal(2, proxy.Read<ProbeTraitA>()?.Value);

            cell.RunFrameNow(PlotMode.Sync);
            Assert.Equal(1, layer.UpdateCount);
            Assert.Equal(2, proxy.Read<ProbeTraitA>()?.Value);
        });

        [Fact]
        public void UnmanagedLayer_FedFromBackgroundThread_Draws() => RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ProbeLayer();
            var proxy = cell.AddUnmanagedLayer(layer);

            Task.Run(() => proxy.PublishData(new ProbeTraitA(1))).Wait();
            cell.RunFrameNow(PlotMode.Sync);

            Assert.Equal(1, layer.UpdateCount);
        });

        [Fact]
        public void UnmanagedLayer_NotFed_StaysAsleep() => RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ProbeLayer();
            cell.AddUnmanagedLayer(layer);

            cell.RunFrameNow(PlotMode.Sync);
            cell.RunFrameNow(PlotMode.Sync);

            Assert.Equal(0, layer.UpdateCount);
            Assert.False(layer.IsDiscovered);
        });

        [Fact]
        public void UnmanagedLayer_FedWithoutRequiredTrait_FailsFast() => RunOnSta(() =>
        {
            // 喂了数据但缺 [RequiredTrait] 声明的那份:调用方错误,照常 fail-fast
            var cell = NewCell();
            var layer = new RequiresATestLayer();
            var proxy = cell.AddUnmanagedLayer(layer);

            proxy.PublishData(new ProbeTraitB(1));

            Assert.Throws<InvalidOperationException>(() => cell.RunFrameNow(PlotMode.Sync));
        });

        [Fact]
        public void SchemaAddLayer_NotFed_StaysAsleep() => RunOnSta(() =>
        {
            var layer = new ProbeLayer();
            var cell = new ChartCell();
            var schema = new AddLayerSchema(layer);
            cell.Template = schema;
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
            schema.Trigger.Push(new DataBlackboard());
            schema.InvalidateEnvironment();

            for (int i = 0; i < 3; i++)
            {
                using var ctx = cell.CreateContext();
                cell.ExecutePipeline(ctx, PlotMode.Sync);
                cell.Invalidate();
            }

            Assert.Equal(0, layer.UpdateCount);
        });

        [Fact]
        public void FeatureAttachProxy_StillDoesNotWakeLayer() => RunOnSta(() =>
        {
            // Feature 路径不变:OnCompose 里经 AttachLayer 返回的代理发的样式不唤醒图层(要等 OnProject 里 ctx.For 发数据),
            // 这样 [RequiredTrait] 图层不会在数据到齐前被叫起
            var layer = new ProbeLayer();
            var feature = new ProbeFeature("style-only") { OnComposeAction = f => f.AttachAndPublish(layer, new ProbeTraitA(1)) };
            var h = new SchemaHarness(feature);
            h.InvalidateEnvironment();

            h.Frame();
            h.Frame();

            Assert.Equal(0, layer.UpdateCount);
        });

#if DEBUG
        [Fact]
        public void UnmanagedLayer_NotFed_IsDiagnosed() => RunOnSta(() =>
        {
            var cell = NewCell();
            var layer = new ProbeLayer();
            cell.AddUnmanagedLayer(layer);

            for (int i = 0; i < ChartLayer.SilentLayerFrameThreshold; i++)
            {
                cell.RequestUpdate(_ => { });
                cell.RunFrameNow(PlotMode.Sync);
            }

            Assert.NotNull(layer.SilentLayerDiagnosis);
            Assert.Contains("没有喂过数据", layer.SilentLayerDiagnosis);
            Assert.Equal(0, layer.UpdateCount);
        });
#endif

        private sealed class AddLayerSchema : ReactiveSchema
        {
            private readonly IChartLayer _layer;
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public AddLayerSchema(IChartLayer layer) => _layer = layer;
            protected override void DefineDataFlow(ChartCell chart) => Trigger.BindTo(chart);
            // AddLayer 包出来的是 ChartFeature,要求 cell 上挂着 ViewportPorts(ChartReactiveSchema 会自动补,这里手动加)
            protected override void DefineFeatures(IFeatureContext canvas) => canvas.Add(new ViewportPortsFeature()).AddLayer(_layer);
        }
    }
}
