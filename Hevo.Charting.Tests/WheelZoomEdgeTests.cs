using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 滚轮 / 键盘缩放的边界:未武装时不采信鼠标、plot 区外滚轮钉右缘、灵敏度、跨度上下限、
    /// 模式开关、打断惯性、各策略端到端。走真实输入管线,帧手动驱动。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class WheelZoomEdgeTests
    {
        // 100 根数据里取中间一段,离两端够远,缩放不会被 Hard 钳制干扰
        private static readonly RealRange MiddleView = new(40, 69);

        private static ChartInteractionState State(PointerRouterTests.Rig rig)
            => rig.Cell.PointerRegions.Behaviors.StateOf<ChartInteractionState>(rig.Interaction)!;

        private static HevoRect Plot(PointerRouterTests.Rig rig) => State(rig).Ctx.GetPlotArea();

        private static double PlotX(PointerRouterTests.Rig rig, double anchor)
        {
            var plot = Plot(rig);
            return plot.Left + anchor * plot.Width;
        }

        private static void Reset(PointerRouterTests.Rig rig, RealRange view)
        {
            rig.Write(rig.Viewport.UserRange, view);
            rig.Frame();
            Assert.Equal(view, rig.ActiveRange);
        }

        private static void Wheel(PointerRouterTests.Rig rig, double x, int delta, double y = 150)
        {
            rig.Mouse.Wheel(rig.Canvas, new Point(x, y), delta);
            rig.Frame();
        }

        private static KeyEventArgs PreviewKey(ChartCell cell, Key key) =>
            new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cell)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        private static Action<InteractionBuilder, PointerRouterTests.Schema> Pointer(
            ChartInteractionMode modes = ChartInteractionMode.All, ZoomOptions? zoom = null)
            => (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, modes, zoomConfig: zoom);

        // ================= 未武装 / plot 区外:钉右缘 =================

        [Fact]
        public void Wheel_BeforeClickToActivate_AnchorsRightEdge() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(requireClickToActivate: true);
            try
            {
                double x = PlotX(rig, 0.3);
                rig.Move(x, 150);
                Assert.False(State(rig).IsHoverArmed);

                Reset(rig, MiddleView);
                Wheel(rig, x, +120);
                Assert.True(rig.ActiveRange.Span < MiddleView.Span, $"应放大: {rig.ActiveRange}");
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
                Wheel(rig, x, -120);
                Wheel(rig, x, -120);
                Assert.True(rig.ActiveRange.Span > MiddleView.Span, $"应缩小: {rig.ActiveRange}");
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);

                // 对照:点一下武装后同一位置滚轮,锚点回到鼠标,右缘就会动
                rig.Click(x, 150);
                Assert.True(State(rig).IsHoverArmed);
                Reset(rig, MiddleView);
                Wheel(rig, x, +120);
                Assert.True(rig.ActiveRange.Max < MiddleView.Max, $"武装后应按鼠标锚: {rig.ActiveRange}");
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_AfterEscapeDismiss_AnchorsRightEdge() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(dismissOnEscape: true);
            try
            {
                double x = PlotX(rig, 0.3);
                rig.Move(x, 150);
                Assert.True(State(rig).IsHoverArmed);
                rig.Escape();
                Assert.False(State(rig).IsHoverArmed);

                Reset(rig, MiddleView);
                Wheel(rig, x, +120);
                Assert.True(rig.ActiveRange.Span < MiddleView.Span, $"应放大: {rig.ActiveRange}");
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_OutsidePlot_AnchorsRightEdge() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig();
            try
            {
                var plot = Plot(rig);
                Assert.True(plot.Right + 10 < 400 && plot.Left - 10 > 0, $"测试假设 plot 两侧留有轴区: {plot}");
                // Y 轴区(右侧)和左侧空白都算 plot 外
                foreach (double x in new[] { plot.Right + 10, plot.Left - 10 })
                {
                    foreach (int delta in new[] { +120, -120 })
                    {
                        Reset(rig, MiddleView);
                        Wheel(rig, x, delta);
                        Assert.NotEqual(MiddleView.Span, rig.ActiveRange.Span);
                        Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
                    }
                }
            }
            finally { rig.Close(); }
        });

        // ================= 灵敏度 =================

        [Theory]
        [InlineData(0.15)]
        [InlineData(0.5)]
        public void Wheel_Sensitivity_SpanRatioPerTick(double sensitivity) => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(length: 1000, extra: Pointer(zoom: new ZoomOptions { Sensitivity = sensitivity }));
            try
            {
                var view = new RealRange(400, 599);
                double x = PlotX(rig, 0.5);
                // 整根对齐会把两端各取整,允许 ±1 根
                foreach (var (delta, ratio) in new[] { (120, 1 - sensitivity), (240, Math.Pow(1 - sensitivity, 2)), (-120, 1 / (1 - sensitivity)) })
                {
                    Reset(rig, view);
                    Wheel(rig, x, delta);
                    Assert.InRange(rig.ActiveRange.Span, view.Span * ratio - 1, view.Span * ratio + 1);
                }
            }
            finally { rig.Close(); }
        });

        // ================= 跨度上下限 =================

        [Fact]
        public void Wheel_ZoomOut_StopsAtMaxSpanMultiplier() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig();
            try
            {
                var limits = State(rig).Ctx.Shared().Read<ViewportSpanLimitsTrait>()!;
                Assert.Equal(1.5, limits.MaxSpanMultiplier);
                double maxSpan = 99 * limits.MaxSpanMultiplier;

                double x = PlotX(rig, 0.5);
                for (int i = 0; i < 20; i++)
                {
                    Wheel(rig, x, -120);
                    Assert.True(rig.ActiveRange.Span <= maxSpan + 1e-9, $"第 {i + 1} 档超过上限 {maxSpan}: {rig.ActiveRange}");
                }
                Assert.True(rig.ActiveRange.Span >= maxSpan - 1, $"缩小 20 档应停在上限 {maxSpan}: {rig.ActiveRange}");
                var stopped = rig.ActiveRange;
                Wheel(rig, x, -120);
                Assert.Equal(stopped, rig.ActiveRange);
                // 跨度超过数据宽度时按 RightEdge 停靠:最新一根贴右
                Assert.Equal(99, rig.ActiveRange.Max);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_ZoomOut_CustomMaxSpanMultiplier() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(extra: (i, s) => i.Canvas.Environment(env => env.SetupViewport(minVisibleCount: 5,
                alignment: ViewportAlignment.RightEdge, defaultVisibleCount: 30, maxSpanMultiplier: 1.0)));
            try
            {
                Assert.Equal(1.0, State(rig).Ctx.Shared().Read<ViewportSpanLimitsTrait>()!.MaxSpanMultiplier);
                double x = PlotX(rig, 0.5);
                for (int i = 0; i < 20; i++) Wheel(rig, x, -120);
                Assert.Equal(new RealRange(0, 99), rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_ZoomIn_StopsAtMinVisibleCount() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig();
            try
            {
                // Rig 装的 minVisibleCount = 5 → MinSpan = 4
                Assert.Equal(4, State(rig).Ctx.Shared().Read<ViewportSpanLimitsTrait>()!.MinSpan);
                Reset(rig, MiddleView);
                double x = PlotX(rig, 0.5);
                for (int i = 0; i < 30; i++)
                {
                    Wheel(rig, x, +120);
                    Assert.True(rig.ActiveRange.Span >= 4, $"第 {i + 1} 档低于最少根数: {rig.ActiveRange}");
                }
                Assert.Equal(4, rig.ActiveRange.Span);
            }
            finally { rig.Close(); }
        });

        // ================= 开关 / 惯性 =================

        [Fact]
        public void Wheel_ZoomModeOff_DoesNothing() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(extra: Pointer(ChartInteractionMode.All & ~ChartInteractionMode.Zoom));
            try
            {
                double x = PlotX(rig, 0.5);
                Wheel(rig, x, +120);
                Wheel(rig, x, -360);
                Assert.Equal(rig.Initial, rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_StopsRunningInertia() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig();
            try
            {
                rig.FlingRight();
                Assert.True(rig.InertiaRunning);
                rig.Mouse.Wheel(rig.Canvas, new Point(PlotX(rig, 0.5), 150), +120);
                Assert.False(rig.InertiaRunning);
            }
            finally { rig.Close(); }
        });

        // ================= 各策略端到端 =================

        [Fact]
        public void Wheel_RightEdgeStrategy_KeepsMax() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(zoomStrategy: RightEdgeZoomStrategy.Instance);
            try
            {
                double x = PlotX(rig, 0.2);
                foreach (int delta in new[] { +120, -120 })
                {
                    Reset(rig, MiddleView);
                    Wheel(rig, x, delta);
                    Assert.NotEqual(MiddleView.Span, rig.ActiveRange.Span);
                    Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
                }
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_LeftEdgeStrategy_KeepsMin() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(zoomStrategy: LeftEdgeZoomStrategy.Instance);
            try
            {
                double x = PlotX(rig, 0.8);
                foreach (int delta in new[] { +120, -120 })
                {
                    Reset(rig, MiddleView);
                    Wheel(rig, x, delta);
                    Assert.NotEqual(MiddleView.Span, rig.ActiveRange.Span);
                    Assert.Equal(MiddleView.Min, rig.ActiveRange.Min);
                }
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_DirectionalStrategy_InAtMouse_OutAtRightEdge() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(zoomStrategy: DirectionalZoomStrategy.Instance);
            try
            {
                double x = PlotX(rig, 0.2);
                Reset(rig, MiddleView);
                Wheel(rig, x, -120);
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);

                Reset(rig, MiddleView);
                Wheel(rig, x, +120);
                // 锚在左侧 0.2 处:放大后右缘明显往左收,左缘只动一点
                Assert.True(rig.ActiveRange.Max < MiddleView.Max, $"放大应按鼠标锚: {rig.ActiveRange}");
                Assert.True(MiddleView.Max - rig.ActiveRange.Max > rig.ActiveRange.Min - MiddleView.Min, $"{rig.ActiveRange}");
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_HistoryAwareStrategy_NoPaging_KeepsMax() => IncrementalTestKit.RunOnSta(() =>
        {
            // 没装分页:可用性视为未耗尽,走 DuringPaging(右缘)
            var rig = new PointerRouterTests.Rig(zoomStrategy: new HistoryAwareZoomStrategy());
            try
            {
                double x = PlotX(rig, 0.2);
                foreach (int delta in new[] { +120, -120 })
                {
                    Reset(rig, MiddleView);
                    Wheel(rig, x, delta);
                    Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
                }
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_FixedWindowStrategy_SnapsToLatestWindow() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(zoomStrategy: new FixedWindowZoomStrategy { WindowBars = 20 });
            try
            {
                Reset(rig, MiddleView);
                Wheel(rig, PlotX(rig, 0.3), +120);
                Assert.Equal(new RealRange(80, 99), rig.ActiveRange);
                Wheel(rig, PlotX(rig, 0.3), -120);
                Assert.Equal(new RealRange(80, 99), rig.ActiveRange);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_CrosshairAnchorStrategy_OutsidePlot_KeepsMax() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(zoomStrategy: CrosshairAnchorZoomStrategy.Instance);
            try
            {
                // 移出 plot 区后十字光标清掉,无命中 → 右缘
                var plot = Plot(rig);
                rig.Move(PlotX(rig, 0.3), 150);
                rig.Move(plot.Right + 10, 150);
                Assert.Null(rig.Hit);
                Reset(rig, MiddleView);
                Wheel(rig, plot.Right + 10, +120);
                Assert.True(rig.ActiveRange.Span < MiddleView.Span);
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
            }
            finally { rig.Close(); }
        });

        // ================= 键盘 ↑ / ↓ =================

        [Fact]
        public void KeyboardZoom_NoPointerYet_AnchorsPlotRight() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(extra: Pointer(ChartInteractionMode.All | ChartInteractionMode.KeyboardZoom));
            try
            {
                Assert.Null(State(rig).LastMousePos);
                Reset(rig, MiddleView);

                var up = PreviewKey(rig.Cell, Key.Up);
                rig.Cell.RaiseEvent(up);
                rig.Frame();
                Assert.True(up.Handled);
                Assert.True(rig.ActiveRange.Span < MiddleView.Span, $"↑ 应放大: {rig.ActiveRange}");
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);

                Reset(rig, MiddleView);
                rig.Cell.RaiseEvent(PreviewKey(rig.Cell, Key.Down));
                rig.Frame();
                Assert.True(rig.ActiveRange.Span > MiddleView.Span, $"↓ 应缩小: {rig.ActiveRange}");
                Assert.Equal(MiddleView.Max, rig.ActiveRange.Max);
            }
            finally { rig.Close(); }
        });
    }
}
