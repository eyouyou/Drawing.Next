using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 十字光标命中:吸附策略、边缘容差、有效根数、离开清除、视口变化后重投、Esc 解除与再武装。
    /// 视口默认看最后 30 根(70..99),CandleMode 下每根 11 px,第 i 根中心 = 50 + (i - 69.5) * 11。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class HoverCrosshairTests
    {
        private const double Y = 150;

        private static Rig WithOptions(PointerOutOfBoundsStrategy snap, DataPort<int>? valid = null, ChartInteractionMode modes = ChartInteractionMode.All, bool dismissOnEscape = false)
            => new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, modes, snap,
                validCountPort: valid, dismissOnEscape: dismissOnEscape)).Isolated();

        // ================= 吸附策略 =================

        [Fact]
        public void Free_BeyondValidData_ReportsRawIndex_OutOfBounds() => IncrementalTestKit.RunOnSta(() =>
        {
            var valid = new DataPort<int>("T_Valid");
            var rig = WithOptions(PointerOutOfBoundsStrategy.Free, valid);
            try
            {
                rig.Write(valid, 80);
                rig.Move(rig.BarX(90), Y);
                var hit = rig.Hit!.Value;
                Assert.Equal(90, hit.Hit.LogicalIndex);
                Assert.True(hit.IsOutOfBounds);

                rig.Move(rig.BarX(75), Y);
                Assert.False(rig.Hit!.Value.IsOutOfBounds);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Snap_BeyondValidData_ClampsToLastValidVisibleBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var valid = new DataPort<int>("T_Valid");
            var rig = WithOptions(PointerOutOfBoundsStrategy.SnapToValidData, valid);
            try
            {
                rig.Write(valid, 80);
                rig.Move(rig.BarX(90), Y);
                var hit = rig.Hit!.Value;
                Assert.Equal(79, hit.Hit.LogicalIndex);
                Assert.False(hit.IsOutOfBounds);
                Assert.Equal(rig.BarX(79), hit.CenterX, 3);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Snap_VisibleAndValidDisjoint_FallsBackToDataRange() => IncrementalTestKit.RunOnSta(() =>
        {
            var valid = new DataPort<int>("T_Valid");
            var rig = WithOptions(PointerOutOfBoundsStrategy.SnapToValidData, valid);
            try
            {
                // 可见 70..99,有效只有 0..49:交集为空,退回数据范围 → 收到 49
                rig.Write(valid, 50);
                rig.Move(rig.BarX(90), Y);
                var hit = rig.Hit!.Value;
                Assert.Equal(49, hit.Hit.LogicalIndex);
                Assert.False(hit.IsOutOfBounds);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void ValidCount_NonPositive_FallsBackToLogicalLength() => IncrementalTestKit.RunOnSta(() =>
        {
            var valid = new DataPort<int>("T_Valid");
            var rig = WithOptions(PointerOutOfBoundsStrategy.SnapToValidData, valid);
            try
            {
                rig.Write(valid, 0);
                rig.Move(rig.BarX(90), Y);
                Assert.Equal(90, rig.Hit!.Value.Hit.LogicalIndex);

                rig.Write(valid, -5);
                rig.Move(rig.BarX(95), Y);
                Assert.Equal(95, rig.Hit!.Value.Hit.LogicalIndex);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Snap_ValidCountSmallerThanVisible_RightEdgeSnapsToLastValid() => IncrementalTestKit.RunOnSta(() =>
        {
            var valid = new DataPort<int>("T_Valid");
            var rig = WithOptions(PointerOutOfBoundsStrategy.SnapToValidData, valid);
            try
            {
                rig.Write(valid, 85);
                rig.Move(rig.Plot().Right - 2, Y);
                Assert.Equal(84, rig.Hit!.Value.Hit.LogicalIndex);
            }
            finally { rig.Close(); }
        });

        // ================= 边缘容差 =================

        [Fact]
        public void EdgeTolerance_JustOutsideLeft_HitsFirstVisibleBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                var plot = rig.Plot();
                rig.Move(plot.Left - 0.5, Y);
                var hit = rig.Hit!.Value;
                Assert.Equal(70, hit.Hit.LogicalIndex);
                Assert.Equal(rig.BarX(70), hit.CenterX, 3);

                rig.Move(plot.Left - 3, Y);   // 超出默认 1px 容差
                Assert.Null(rig.Hit);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void EdgeTolerance_JustOutsideRight_Snap_HitsLastVisibleBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = WithOptions(PointerOutOfBoundsStrategy.SnapToValidData);
            try
            {
                var plot = rig.Plot();
                rig.Move(plot.Right + 0.5, Y);
                var hit = rig.Hit!.Value;
                Assert.Equal(99, hit.Hit.LogicalIndex);
                Assert.True(hit.CenterX <= plot.Right, $"centerX {hit.CenterX} 应在绘图区内 (Right={plot.Right})");

                rig.Move(plot.Right + 3, Y);
                Assert.Null(rig.Hit);
            }
            finally { rig.Close(); }
        });

        /// <summary>
        /// 指针压在绘图区边沿(容差内),Free 模式应命中边上那根可见 K 线。
        /// 现状:relativeX 收到 0 / 1 后 Denormalize 得到 Min-0.5 / Max+0.5,Math.Round 银行家舍入按奇偶决定方向,
        /// 视口 [70,99] 的右沿落到第 100 根(数据外、视口外,centerX=385.5 超出 Right=380);视口 [69,98] 的左沿同理落到第 68 根。
        /// </summary>
        [Fact(Skip = "BUG: Free 模式指针在绘图区边沿时 Math.Round(Max+0.5) 银行家舍入命中视口外一根(右沿 index=100 / 左沿 index=Min-1),十字线画到绘图区外")]
        public void EdgeTolerance_Free_PlotEdge_HitsEdgeVisibleBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                var plot = rig.Plot();
                rig.Move(plot.Right, Y);
                var right = rig.Hit!.Value;
                Assert.True(right.Hit.LogicalIndex == 99 && !right.IsOutOfBounds && right.CenterX <= plot.Right,
                    $"右沿 index={right.Hit.LogicalIndex} oob={right.IsOutOfBounds} centerX={right.CenterX} plotRight={plot.Right} range={rig.ActiveRange}");

                rig.Write(rig.Viewport.UserRange, new RealRange(69, 98));
                rig.Frame();
                rig.Move(plot.Left, Y);
                var left = rig.Hit!.Value;
                Assert.True(left.Hit.LogicalIndex == 69 && left.CenterX >= plot.Left,
                    $"左沿 index={left.Hit.LogicalIndex} centerX={left.CenterX} plotLeft={plot.Left} range={rig.ActiveRange}");
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void EdgeTolerance_Custom_WidensHitZone() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) =>
            {
                i.Remove<ChartInteractionFeature>();
                i.Canvas.Add(new ChartInteractionFeature { PointerHitPort = s.Hit, SupportedModes = ChartInteractionMode.All, EdgeHitTolerance = 5 });
            }).Isolated();
            try
            {
                var plot = rig.Plot();
                rig.Move(plot.Left - 3, Y);
                Assert.Equal(70, rig.Hit!.Value.Hit.LogicalIndex);
                rig.Move(plot.Left - 8, Y);
                Assert.Null(rig.Hit);
                rig.Move(plot.Left + 100, plot.Bottom + 4);   // Y 方向同样有容差
                Assert.NotNull(rig.Hit);
                rig.Move(plot.Left + 100, plot.Bottom + 8);
                Assert.Null(rig.Hit);
            }
            finally { rig.Close(); }
        });

        // ================= 投影一致性 =================

        [Fact]
        public void Hit_CenterX_EqualsBarCenter() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                // 在第 85 根中心偏右 3px:吸到第 85 根的中心
                rig.Move(rig.BarX(85) + 3, Y);
                var hit = rig.Hit!.Value;
                double expected = CoordinateExtensions.ProjectDomainToScreen(rig.Plot(), rig.ActiveRange, ScaleStrategyTrait.CandleMode, 85);
                Assert.Equal(85, hit.Hit.LogicalIndex);
                Assert.Equal(85, hit.LocalIndex);
                Assert.Equal(expected, hit.CenterX, 3);
                Assert.Equal(expected, hit.Hit.SnapX, 3);
                Assert.Equal((float)expected, hit.MousePos.X, 3);
                Assert.Equal(Y, hit.MousePos.Y, 0);
                Assert.False(hit.IsOutOfBounds);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void ViewportChangedProgrammatically_HitReprojects_StaysUnderMouse() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                const double mouseX = 200;
                rig.Move(mouseX, Y);
                var before = rig.Hit!.Value;

                rig.Write(rig.Viewport.UserRange, new RealRange(rig.Initial.Min - 10, rig.Initial.Max - 10));
                rig.Frame();
                var after = rig.Hit!.Value;

                Assert.Equal(rig.Initial.Min - 10, rig.ActiveRange.Min, 6);
                Assert.Equal(before.Hit.LogicalIndex - 10, after.Hit.LogicalIndex);
                double barWidth = rig.Plot().Width / (rig.ActiveRange.Span + 1);
                Assert.True(Math.Abs(after.CenterX - mouseX) <= barWidth / 2 + 0.01, $"centerX {after.CenterX} 应在鼠标 {mouseX} 半根之内");
            }
            finally { rig.Close(); }
        });

        // ================= 离开 =================

        [Fact]
        public void MouseLeave_ClearsHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Move(200, Y);
                Assert.NotNull(rig.Hit);
                rig.Leave();
                Assert.Null(rig.Hit);
                Assert.Null(rig.State().LastMousePos);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void MouseLeave_WhilePanning_DoesNotClearHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Mouse.Move(rig.Canvas, new Point(200, Y));
                rig.Mouse.Down(rig.Canvas, new Point(200, Y));
                rig.Mouse.Move(rig.Canvas, new Point(230, Y));
                Assert.True(rig.State().IsPanning);
                // 平移按下时已清过 hit;这里放一个哨兵值,看离开时有没有被清
                var sentinel = new PointerHitState(new HevoPoint(1, 2), new DomainHitResult(80, 1, 0.5), 1, 80, false);
                rig.Write(rig.Schema.Hit, sentinel);
                rig.Leave();
                var hit = rig.Hit;
                rig.Mouse.Up(rig.Canvas, new Point(230, Y));
                Assert.Equal(sentinel, hit);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void MouseLeave_WhileBoxZooming_DoesNotClearHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(boxZoom: new BoxZoomOptions { Activation = PointerModifiers.None }).Isolated();
            try
            {
                rig.Mouse.Move(rig.Canvas, new Point(200, Y));
                rig.Mouse.Down(rig.Canvas, new Point(200, Y));
                rig.Mouse.Move(rig.Canvas, new Point(260, Y));
                Assert.True(rig.State().IsBoxActive);
                var sentinel = new PointerHitState(new HevoPoint(1, 2), new DomainHitResult(80, 1, 0.5), 1, 80, false);
                rig.Write(rig.Schema.Hit, sentinel);
                rig.Leave();
                var hit = rig.Hit;
                rig.Mouse.Up(rig.Canvas, new Point(260, Y));
                Assert.Equal(sentinel, hit);
            }
            finally { rig.Close(); }
        });

        // ================= 开关 / 武装 =================

        [Fact]
        public void HoverFlagOff_MoveWritesNoHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = WithOptions(PointerOutOfBoundsStrategy.Free, modes: ChartInteractionMode.All & ~ChartInteractionMode.Hover);
            try
            {
                rig.Move(200, Y);
                rig.Move(210, Y);
                Assert.Null(rig.Hit);
                rig.Click(210, Y);   // 松手补投影也看 Hover 开关
                Assert.Null(rig.Hit);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void DismissOnEscape_ClearsAndDisarms_ClickRearms() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(dismissOnEscape: true).Isolated();
            try
            {
                rig.Move(200, Y);
                Assert.NotNull(rig.Hit);
                rig.Escape();
                Assert.Null(rig.Hit);
                Assert.False(rig.State().IsHoverArmed);

                rig.Move(220, Y);   // 未武装:移动不投影
                Assert.Null(rig.Hit);

                rig.Click(220, Y);   // 单击再武装,松手就地投影
                Assert.True(rig.State().IsHoverArmed);
                Assert.NotNull(rig.Hit);
                rig.Move(240, Y);
                Assert.NotNull(rig.Hit);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void EscapeWithoutDismissOption_KeepsHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig().Isolated();
            try
            {
                rig.Move(200, Y);
                rig.Escape();
                Assert.NotNull(rig.Hit);
                Assert.True(rig.State().IsHoverArmed);
            }
            finally { rig.Close(); }
        });
    }
}
