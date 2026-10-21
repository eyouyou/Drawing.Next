using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 双击复原:左对齐首屏、自定义目标、超时 / 超位移不算双击、三连击只复原一次、开关关掉不复原。
    /// 先把视口挪开(直接写 UserRange,不用拖:拖动的按下也会被计作一次单击),再点两下看回没回来。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class DoubleClickResetTests
    {
        private static void MoveViewport(Rig rig, double min, double max)
        {
            rig.Write(rig.Viewport.UserRange, new RealRange(min, max));
            rig.Frame();
            InteractionRigExtensions.AssertRange(min, max, rig.ActiveRange);
        }

        private static Rig WithReset(DoubleClickResetOptions options, ChartInteractionMode modes = ChartInteractionMode.All)
            => new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, modes, doubleClickResetConfig: options)).Isolated();

        /// <summary>
        /// LeftEdge(SetupViewport 的默认值)时也复原到首屏:首屏不看 Alignment、总是贴最新一根,复原跟它一致。
        /// 以前这里按 Alignment 复原到数据开头 (0,29),打开时看的是最新、双击却跳到最早。
        /// </summary>
        [Fact]
        public void LeftEdgeAlignment_ResetsToFirstScreen() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) => i.Canvas.Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.LeftEdge,
                defaultVisibleCount: 30, overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard))).Isolated();
            try
            {
                InteractionRigExtensions.AssertRange(70, 99, rig.Initial);
                MoveViewport(rig, 40, 69);
                rig.Click(300, 100);
                rig.Click(300, 100);
                InteractionRigExtensions.AssertRange(70, 99, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void CustomReset_CallbackDecidesTarget() => IncrementalTestKit.RunOnSta(() =>
        {
            int seenLength = -1;
            var rig = WithReset(new DoubleClickResetOptions { CustomReset = len => { seenLength = len; return new RealRange(10, 39); } });
            try
            {
                MoveViewport(rig, 50, 79);
                rig.Click(300, 100);
                rig.Click(300, 100);
                Assert.Equal(100, seenLength);
                InteractionRigExtensions.AssertRange(10, 39, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void IntervalExceeded_NoReset() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = WithReset(new DoubleClickResetOptions { MaxInterval = TimeSpan.FromMilliseconds(30) });
            try
            {
                MoveViewport(rig, 40, 69);
                rig.Click(300, 100);
                Thread.Sleep(120);
                rig.Click(300, 100);
                InteractionRigExtensions.AssertRange(40, 69, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void MovementExceeded_NoReset() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                MoveViewport(rig, 40, 69);
                rig.Click(300, 100);
                rig.Click(310, 100);   // 默认 6px 以外
                InteractionRigExtensions.AssertRange(40, 69, rig.ActiveRange);
                rig.Click(310, 110);   // 跟上一下比 Y 方向也超了
                InteractionRigExtensions.AssertRange(40, 69, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void WithinMovementTolerance_Resets() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                MoveViewport(rig, 40, 69);
                rig.Click(300, 100);
                rig.Click(304, 103);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void TripleClick_ResetsOnlyOnce() => IncrementalTestKit.RunOnSta(() =>
        {
            int resets = 0;
            var rig = WithReset(new DoubleClickResetOptions { CustomReset = _ => { resets++; return new RealRange(10, 39); } });
            try
            {
                MoveViewport(rig, 50, 79);
                rig.Click(300, 100);
                rig.Click(300, 100);
                InteractionRigExtensions.AssertRange(10, 39, rig.ActiveRange);
                MoveViewport(rig, 50, 79);
                rig.Click(300, 100);   // 第三下:不能跟第二下再凑成一次双击
                Assert.Equal(1, resets);
                InteractionRigExtensions.AssertRange(50, 79, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void DoubleClickFlagOff_NoReset() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = WithReset(new DoubleClickResetOptions(), ChartInteractionMode.All & ~ChartInteractionMode.DoubleClickReset);
            try
            {
                MoveViewport(rig, 40, 69);
                rig.Click(300, 100);
                rig.Click(300, 100);
                InteractionRigExtensions.AssertRange(40, 69, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });
    }
}
