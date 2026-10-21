using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 框选缩放的边界:选框过小丢弃、伸出绘图区的部分裁掉、拖动中有选框松手即清、拖到一半卸载 Feature 也清、结果按整根对齐。
    /// 激活键设为 None,任意左键拖动即框选,不用模拟 Shift。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class BoxZoomEdgeTests
    {
        private const double Y = 150;

        private static Rig BoxRig(double minPixelSize = 8)
            => new Rig(boxZoom: new BoxZoomOptions { Activation = PointerModifiers.None, MinPixelSize = minPixelSize }).Isolated();

        private static void AssertIntegral(double v) => Assert.Equal(Math.Round(v), v, 9);

        [Fact]
        public void BoxSmallerThanMinPixelSize_Discarded() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                rig.Drag(200, 205);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
                Assert.False(rig.BoxTrait()!.IsActive);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void LargerMinPixelSize_DiscardsMediumBox() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig(minPixelSize: 60);
            try
            {
                rig.Drag(200, 250);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void BoxMostlyOutsidePlot_RemainderBelowMin_Discarded() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                // 30 px 宽,但落在绘图区内只有 5 px
                var left = rig.Plot().Left;
                rig.Drag(left - 25, left + 5);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void BoxPartlyLeftOfPlot_ClampedToPlotLeft() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                // 左沿裁到 plot.Left → 第一根可见(70);右沿 176 px → 69.5 + 126/11 ≈ 80.95 → 81
                rig.Drag(20, 176);
                InteractionRigExtensions.AssertRange(70, 81, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void BoxPartlyRightOfPlot_ClampedToDataEnd() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                rig.Drag(300, 398);
                var r = rig.ActiveRange;
                Assert.Equal(99, r.Max, 6);
                Assert.True(r.Span < rig.Initial.Span, $"{rig.Initial} → {r}");
                AssertIntegral(r.Min);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void ResultingRange_SnappedToIntegers() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                rig.Drag(103, 247);
                var r = rig.ActiveRange;
                Assert.True(r.Span < rig.Initial.Span);
                AssertIntegral(r.Min);
                AssertIntegral(r.Max);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void RightToLeftDrag_SameAsLeftToRight() => IncrementalTestKit.RunOnSta(() =>
        {
            var a = BoxRig();
            RealRange ra;
            try { a.Drag(120, 260); ra = a.ActiveRange; }
            finally { a.Close(); }
            var b = BoxRig();
            try
            {
                b.Drag(260, 120);
                InteractionRigExtensions.AssertRange(ra.Min, ra.Max, b.ActiveRange);
            }
            finally { b.Close(); }
        });

        [Fact]
        public void TraitActiveWhileDragging_ClearedOnRelease() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                Assert.False(rig.BoxTrait()?.IsActive ?? false);
                rig.Mouse.Move(rig.Canvas, new Point(100, 120));
                rig.Mouse.Down(rig.Canvas, new Point(100, 120));
                rig.Mouse.Move(rig.Canvas, new Point(200, 180));
                rig.Frame();
                var during = rig.BoxTrait()!;
                Assert.True(during.IsActive);
                Assert.Equal(100, during.Rect.X, 0);
                Assert.Equal(120, during.Rect.Y, 0);
                Assert.Equal(100, during.Rect.Width, 0);
                Assert.Equal(60, during.Rect.Height, 0);
                Assert.Null(rig.Hit);   // 框选中藏十字光标

                rig.Mouse.Up(rig.Canvas, new Point(200, 180));
                rig.Frame();
                Assert.False(rig.BoxTrait()!.IsActive);
                Assert.False(rig.State().IsBoxActive);
                Assert.Null(System.Windows.Input.Mouse.Captured);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void FeatureRemovedMidDrag_TraitCleared_CaptureReleased() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = BoxRig();
            try
            {
                rig.Mouse.Move(rig.Canvas, new Point(100, Y));
                rig.Mouse.Down(rig.Canvas, new Point(100, Y));
                rig.Mouse.Move(rig.Canvas, new Point(220, Y));
                rig.Frame();
                Assert.True(rig.BoxTrait()!.IsActive);
                var state = rig.State();

                rig.Schema.Transact(c => c.Remove<ChartInteractionFeature>());
                rig.Frame();

                Assert.False(rig.BoxTrait()!.IsActive);
                Assert.False(state.IsBoxActive);
                Assert.Null(System.Windows.Input.Mouse.Captured);

                rig.Mouse.Up(rig.Canvas, new Point(220, Y));
                rig.Frame();
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });
    }
}
