using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 图层"深度睡眠"规则(还没录过的图层只被本层 Trait 唤醒)保持不变,
    /// Debug 构建下对挂上很久却一条指令都没录的图层只写一条诊断日志,不改变任何渲染行为。
    /// </summary>
    public sealed class LayerRedrawTests
    {
        // ── 深度睡眠规则(跟以前一致) ─────────────────────────────────────

        [Fact]
        public void SharedOnlyLayer_StaysAsleepUntilLocalTrait() => RunOnSta(() =>
        {
            var layer = new ProbeLayer { Reads = static d => d.Get<ProbeTraitB>() };
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(1)));
            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(2)));
            Assert.Equal(0, layer.UpdateCount);

            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));
            Assert.Equal(1, layer.UpdateCount);
        });

        [Fact]
        public void AttachOnlyLayer_StaysAsleep() => RunOnSta(() =>
        {
            // AddLayer 逃生舱的典型情况:Feature 只挂层、什么都不发
            var layer = new ProbeLayer();
            var feature = new ProbeFeature("attach-only") { OnComposeAction = f => f.Attach(layer) };
            var h = new SchemaHarness(feature);
            h.InvalidateEnvironment();

            h.Frame();
            h.Frame();

            Assert.Equal(0, layer.UpdateCount);
            Assert.False(layer.IsDiscovered);
        });

        [Fact]
        public void RequiredTraitMissing_FailsFastOnFirstLocalWake() => RunOnSta(() =>
        {
            var layer = new RequiresATestLayer();
            var driver = new LayerFrameDriver(layer);
            driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(1)));
            Assert.Equal(0, layer.UpdateCount);

            var ex = Assert.Throws<InvalidOperationException>(
                () => driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitB(2))));
            Assert.Contains("缺少必需 trait: [ProbeTraitA]", ex.Message);
        });

#if DEBUG
        // ── Debug 诊断:只写日志 ──────────────────────────────────────────

        [Fact]
        public void NeverWokenLayer_IsDiagnosed_WithoutChangingRendering() => RunOnSta(() =>
        {
            var layer = new ProbeLayer { Reads = static d => d.Get<ProbeTraitB>() };
            var driver = new LayerFrameDriver(layer);

            for (int i = 0; i < ChartLayer.SilentLayerFrameThreshold - 1; i++)
                driver.Frame(ctx => ctx.Shared().PublishData(new ProbeTraitB(i)));
            Assert.Null(layer.SilentLayerDiagnosis);

            driver.Frame(_ => { });
            Assert.NotNull(layer.SilentLayerDiagnosis);
            Assert.Contains("'probe'", layer.SilentLayerDiagnosis);
            Assert.Contains("ctx.For(layer)", layer.SilentLayerDiagnosis);
            Assert.Contains("AddLayer", layer.SilentLayerDiagnosis);

            // 诊断不唤醒、不标脏
            Assert.Equal(0, layer.UpdateCount);
            Assert.False(layer.IsDiscovered);
            Assert.False(layer.IsDirty);

            // 只报一次
            var first = layer.SilentLayerDiagnosis;
            for (int i = 0; i < ChartLayer.SilentLayerFrameThreshold; i++) driver.Frame(_ => { });
            Assert.Same(first, layer.SilentLayerDiagnosis);
        });

        [Fact]
        public void WokenButEmptyLayer_IsDiagnosedWithTheNullTraits() => RunOnSta(() =>
        {
            // 被本层 Trait 唤醒了,但画图要的 ProbeTraitA 没发(或发错了作用域):读到的一直是 null
            var layer = new DrawsWhenATestLayer();
            var driver = new LayerFrameDriver(layer);
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitB(1)));

            for (int i = 0; i < ChartLayer.SilentLayerFrameThreshold; i++) driver.Frame(_ => { });

            Assert.NotNull(layer.SilentLayerDiagnosis);
            Assert.Contains(nameof(ProbeTraitA), layer.SilentLayerDiagnosis);
            Assert.Contains(nameof(DrawsWhenATestLayer), layer.SilentLayerDiagnosis);
        });

        [Fact]
        public void LayerThatDraws_IsNotDiagnosed() => RunOnSta(() =>
        {
            var layer = new DrawsWhenATestLayer();
            var driver = new LayerFrameDriver(layer);
            driver.Frame(ctx => ctx.For(layer).PublishData(new ProbeTraitA(1)));

            for (int i = 0; i < ChartLayer.SilentLayerFrameThreshold * 2; i++) driver.Frame(_ => { });

            Assert.Null(layer.SilentLayerDiagnosis);
        });

        [Fact]
        public void HiddenLayer_IsNotDiagnosed() => RunOnSta(() =>
        {
            var layer = new DrawsWhenATestLayer();
            var driver = new LayerFrameDriver(layer);
            driver.Frame(ctx => ctx.For(layer).Visibility(false));

            for (int i = 0; i < ChartLayer.SilentLayerFrameThreshold * 2; i++) driver.Frame(_ => { });

            Assert.Null(layer.SilentLayerDiagnosis);
        });
#endif
    }

    /// <summary>模拟 RequiredTraitValidatorGenerator 为 [RequiredTrait(typeof(ProbeTraitA))] 生成的 override(测试工程不挂生成器)。</summary>
    internal sealed class RequiresATestLayer : ChartLayer
    {
        public int UpdateCount { get; private set; }

        protected override void ValidateRequiredTraits(IVisualData data)
        {
            if (data.Get<ProbeTraitA>() == null)
                throw new InvalidOperationException($"Layer '{Name}' (RequiresATestLayer) 缺少必需 trait: [ProbeTraitA]。请检查相关 Feature 是否被装配。");
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            UpdateCount++;
            data.Get<ProbeTraitA>();
        }
    }

    /// <summary>拿到 ProbeTraitA 才画一个矩形,否则什么都不录。</summary>
    internal sealed class DrawsWhenATestLayer : ChartLayer
    {
        private static readonly HevoSolidBrush s_fill = new(HevoColor.FromRgb(255, 0, 0));

        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            if (data.Get<ProbeTraitA>() == null) return;
            drawSink.DrawRectangle(s_fill, null, new HevoRect(0, 0, 10, 10));
        }
    }
}
