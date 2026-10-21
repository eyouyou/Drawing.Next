using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// ← / → 逐根移动十字光标:没有命中不动、到数据两端停住、出视口时视口跟着走、Keyboard 关掉则不响应。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class KeyboardNavigationTests
    {
        private const double Y = 150;

        [Fact]
        public void NoHit_ArrowIsNoOp() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                Assert.Null(rig.Hit);
                rig.Key(Key.Right);
                rig.Key(Key.Left);
                Assert.Null(rig.Hit);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void WithHit_ArrowsMoveOneBar_AndCrosshairFollows() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Move(rig.BarX(85), Y);
                Assert.Equal(85, rig.Hit!.Value.Hit.LogicalIndex);

                Assert.True(rig.Key(Key.Right));
                var right = rig.Hit!.Value;
                Assert.Equal(86, right.Hit.LogicalIndex);
                Assert.Equal(rig.BarX(86), right.CenterX, 3);
                Assert.Equal(Y, right.MousePos.Y, 0);   // Y 保持不动

                rig.Key(Key.Left);
                rig.Key(Key.Left);
                Assert.Equal(84, rig.Hit!.Value.Hit.LogicalIndex);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
                // 合成的指针位置也跟着走:之后视口变化在新位置重投
                Assert.Equal((float)rig.BarX(84), rig.State().LastMousePos!.Value.X, 2);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void AtLastBar_RightIsNoOp() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Move(rig.BarX(99), Y);
                Assert.Equal(99, rig.Hit!.Value.Hit.LogicalIndex);
                rig.Key(Key.Right);
                Assert.Equal(99, rig.Hit!.Value.Hit.LogicalIndex);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void AtFirstBar_LeftIsNoOp() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Write(rig.Viewport.UserRange, new RealRange(0, 29));
                rig.Frame();
                InteractionRigExtensions.AssertRange(0, 29, rig.ActiveRange);
                rig.Move(rig.BarX(0), Y);
                Assert.Equal(0, rig.Hit!.Value.Hit.LogicalIndex);
                rig.Key(Key.Left);
                Assert.Equal(0, rig.Hit!.Value.Hit.LogicalIndex);
                InteractionRigExtensions.AssertRange(0, 29, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void ValidDataCount_LimitsRightNavigation() => IncrementalTestKit.RunOnSta(() =>
        {
            var valid = new DataPort<int>("T_Valid");
            var rig = new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, ChartInteractionMode.All, validCountPort: valid)).Isolated();
            try
            {
                rig.Write(valid, 80);
                rig.Move(rig.BarX(78), Y);
                rig.Key(Key.Right);
                Assert.Equal(79, rig.Hit!.Value.Hit.LogicalIndex);
                rig.Key(Key.Right);   // 第 80 根起是占位:停在最后一根有效数据
                Assert.Equal(79, rig.Hit!.Value.Hit.LogicalIndex);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void LeavingViewportLeft_ViewportPansToFollow() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Move(rig.BarX(70), Y);
                Assert.Equal(70, rig.Hit!.Value.Hit.LogicalIndex);

                rig.Key(Key.Left);
                var hit = rig.Hit!.Value;
                Assert.Equal(69, hit.Hit.LogicalIndex);
                InteractionRigExtensions.AssertRange(69, 98, rig.ActiveRange);
                // 跟着走的视口里,目标在最左一根
                Assert.Equal(rig.BarX(69), hit.CenterX, 3);
                Assert.True(hit.CenterX >= rig.Plot().Left);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void LeavingViewportRight_ViewportPansToFollow() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Write(rig.Viewport.UserRange, new RealRange(40, 69));
                rig.Frame();
                rig.Move(rig.BarX(69), Y);
                Assert.Equal(69, rig.Hit!.Value.Hit.LogicalIndex);

                rig.Key(Key.Right);
                var hit = rig.Hit!.Value;
                Assert.Equal(70, hit.Hit.LogicalIndex);
                InteractionRigExtensions.AssertRange(41, 70, rig.ActiveRange);
                Assert.Equal(rig.BarX(70), hit.CenterX, 3);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void KeyboardFlagOff_ArrowsIgnored() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, ChartInteractionMode.All & ~ChartInteractionMode.Keyboard)).Isolated();
            try
            {
                rig.Move(rig.BarX(70), Y);
                bool handled = rig.Key(Key.Left);
                Assert.False(handled);
                Assert.Equal(70, rig.Hit!.Value.Hit.LogicalIndex);
                rig.Key(Key.Right);
                Assert.Equal(70, rig.Hit!.Value.Hit.LogicalIndex);
                InteractionRigExtensions.AssertRange(rig.Initial.Min, rig.Initial.Max, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });
    }
}
