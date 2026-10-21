using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 拖拽平移的变体:右键拖、按下不动松手补投影、非整根比例尺走小数、Pan 关掉不动、撞 Hard 墙钳住。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PanBehaviorTests
    {
        private const double Y = 150;

        /// <summary>不吸附的居中比例尺:平移不再按整根棘轮。</summary>
        private static readonly ScaleStrategyTrait FreeCandle = new(CategoryScale.Centered, new LinearScale());

        private static void RightDrag(Rig rig, double fromX, double toX, int steps = 6)
        {
            // 没被认领的右键松开会弹右键菜单;屏幕外窗口上弹菜单会让测试进程崩
            rig.Cell.ContextMenuOpening += (_, e) => e.Handled = true;
            rig.Mouse.Move(rig.Canvas, new Point(fromX, Y));
            rig.Mouse.RightDown(rig.Canvas, new Point(fromX, Y));
            rig.Frame();
            for (int i = 1; i <= steps; i++)
            {
                rig.Mouse.Move(rig.Canvas, new Point(fromX + (toX - fromX) * i / steps, Y));
                rig.Frame();
            }
            rig.Mouse.RightUp(rig.Canvas, new Point(toX, Y));
            rig.Frame();
        }

        [Fact]
        public void RightButtonDrag_Pans() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                RightDrag(rig, 100, 210);   // 110 px = 10 根
                var r = rig.ActiveRange;
                Assert.Equal(rig.Initial.Min - 10, r.Min, 6);
                Assert.Equal(rig.Initial.Span, r.Span, 6);
                Assert.False(rig.State().IsPanning);
                Assert.Null(System.Windows.Input.Mouse.Captured);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void LeftDrag_WholeBars_SpanPreserved() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Drag(100, 155);   // 55 px = 5 根
                var r = rig.ActiveRange;
                Assert.Equal(rig.Initial.Min - 5, r.Min, 6);
                Assert.Equal(rig.Initial.Span, r.Span, 6);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void PressReleaseWithoutMove_ReprojectsHover() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Move(rig.BarX(85), Y);
                var before = rig.Hit!.Value;
                rig.Mouse.Down(rig.Canvas, new Point(rig.BarX(85), Y));
                Assert.Null(rig.Hit);   // 按下准备平移,先藏
                rig.Mouse.Up(rig.Canvas, new Point(rig.BarX(85), Y));
                rig.Frame();
                var after = rig.Hit;
                Assert.False(rig.InertiaRunning);
                Assert.NotNull(after);
                Assert.Equal(before.Hit.LogicalIndex, after!.Value.Hit.LogicalIndex);
                Assert.Equal(before.CenterX, after.Value.CenterX, 3);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void SubBarDrag_SnappedScale_DoesNotMove() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Drag(100, 105);   // 不到半根
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void NonSnappedScale_PansFractionalAmount() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) => i.Canvas.Seed<ScaleStrategyTrait>(FreeCandle)).Isolated();
            try
            {
                Assert.Same(FreeCandle, rig.Cell.GetSharedData().Get<ScaleStrategyTrait>());
                rig.Drag(100, 105);
                var r = rig.ActiveRange;
                // 5 px / 330 px * 30 根 ≈ 0.4545 根
                double expected = 5.0 / rig.Plot().Width * (rig.Initial.Span + 1);
                Assert.Equal(rig.Initial.Min - expected, r.Min, 3);
                Assert.Equal(rig.Initial.Span, r.Span, 6);
                Assert.NotEqual(Math.Round(r.Min), r.Min);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void PanFlagOff_DragDoesNothing() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit,
                ChartInteractionMode.All & ~ChartInteractionMode.Pan)).Isolated();
            try
            {
                rig.Drag(100, 250);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
                Assert.False(rig.InertiaRunning);
                RightDrag(rig, 100, 250);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
                // 不平移时拖动照常更新十字光标
                Assert.NotNull(rig.Hit);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void HardWall_Left_ClampsAtFirstBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Write(rig.Viewport.UserRange, new RealRange(5, 34));
                rig.Frame();
                InteractionRigExtensions.AssertRange(5, 34, rig.ActiveRange);
                rig.Drag(100, 320);   // 20 根,只剩 5 根可走
                InteractionRigExtensions.AssertRange(0, 29, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void HardWall_Right_AlreadyAtEnd_DragLeftDoesNothing() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Drag(320, 100);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });
    }
}
