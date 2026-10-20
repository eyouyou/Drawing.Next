using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 滚轮缩放的锚点:缩放前后鼠标下的逻辑位置不能挪。
    /// 整根对齐(CenteredSnapped)下每档都要取整,误差只能停在半根以内,不能一档档累积;缩小几档再放大同样几档要回到原视口。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class ZoomAnchorTests
    {
        public static TheoryData<double> Anchors => new() { 0.0, 0.13, 0.37, 0.5, 0.81, 1.0 };

        // ================= 纯几何 =================

        [Theory]
        [MemberData(nameof(Anchors))]
        public void AnchoredZoom_KeepsValueUnderAnchor(double anchor)
        {
            var baseRange = new RealRange(800, 999);
            foreach (var scale in new IScale[] { CategoryScale.Edge, CategoryScale.Centered })
            {
                double before = scale.Denormalize(anchor, baseRange);
                var zoomed = ZoomMath.AnchoredZoom(baseRange, 57.3, anchor);
                Assert.Equal(before, scale.Denormalize(anchor, zoomed), 9);
            }
        }

        [Theory]
        [InlineData(800)]
        [InlineData(833)]
        [InlineData(950)]
        [InlineData(999)]
        public void HitAnchoredZoom_CenteredScale_KeepsHitBarInPlace(int hitIndex)
        {
            var baseRange = new RealRange(800, 999);
            var scale = CategoryScale.Centered;
            double before = scale.Normalize(hitIndex, baseRange);
            var zoomed = ZoomMath.HitAnchoredZoom(baseRange, 57.3, hitIndex, scale);
            Assert.Equal(before, scale.Normalize(hitIndex, zoomed), 9);
        }

        // ================= 真实滚轮 =================

        // 幅度跟手动滚一样大:1000 根数据,视口放在中间(离数据两端够远,不撞墙),
        // 从 30 根缩小 15 档到 ~330 根再放大回来,然后一路放大 25 档到最少根数(会被 MinSpan 卡住)。
        private const int LargeLength = 1000;
        private static readonly RealRange MiddleView = new(480, 509);
        private static readonly int[] OutThenIn = Repeat(-120, 15).Concat(Repeat(+120, 15)).ToArray();
        private static readonly int[] DeepIn = Repeat(+120, 25);

        [Fact]
        public void Wheel_LargeRange_MouseAnchorDriftStaysWithinHalfBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(length: LargeLength);
            try
            {
                foreach (double anchor in new[] { 0.13, 0.37, 0.5, 0.81 })
                {
                    foreach (var deltas in new[] { OutThenIn, DeepIn })
                    {
                        Reset(rig, MiddleView);
                        double x = AnchorX(rig, anchor);
                        double v0 = ValueAt(rig, x);
                        for (int i = 0; i < deltas.Length; i++)
                        {
                            Wheel(rig, x, deltas[i]);
                            double drift = ValueAt(rig, x) - v0;
                            Assert.True(Math.Abs(drift) <= 0.5 + 1e-9,
                                $"anchor={anchor} 第 {i + 1} 档(delta={deltas[i]})鼠标下偏了 {drift:F3} 根: {rig.ActiveRange}");
                            if (deltas == OutThenIn && i == 14)
                                Assert.True(rig.ActiveRange.Span > 300, $"缩小 15 档应到 ~330 根: {rig.ActiveRange}");
                        }
                        if (deltas == OutThenIn)
                            Assert.Equal(MiddleView, rig.ActiveRange);
                        else
                            Assert.True(rig.ActiveRange.Span <= 5, $"放大 25 档应卡在最少根数: {rig.ActiveRange}");
                    }
                }
            }
            finally { rig.Close(); }
        });

        /// <summary>
        /// CrosshairAnchor(K 线业务默认用它)锚的是十字光标那根的中心,不是鼠标点。
        /// 十字光标停在同一根上时,那根离开始锚它时的位置不超过半根(整根对齐取整的极限),不随档数累积。
        /// 取整的偏差、缩小时根变窄,都会让鼠标落到相邻那根上,之后就锚新那根,从那一档重新比。
        /// </summary>
        [Fact]
        public void Wheel_LargeRange_CrosshairAnchor_HoveredBarStaysInPlace() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(zoomStrategy: CrosshairAnchorZoomStrategy.Instance, length: LargeLength);
            try
            {
                foreach (double anchor in new[] { 0.13, 0.37, 0.5, 0.81 })
                {
                    foreach (var deltas in new[] { OutThenIn, DeepIn })
                    {
                        Reset(rig, MiddleView);
                        double x = AnchorX(rig, anchor);
                        int bar = -1;
                        double origin = 0;
                        for (int i = 0; i < deltas.Length; i++)
                        {
                            if (rig.Hit!.Value.Hit.LogicalIndex != bar)
                            {
                                bar = rig.Hit!.Value.Hit.LogicalIndex;
                                origin = Normalized(rig, bar);
                            }
                            Wheel(rig, x, deltas[i]);
                            double drift = (Normalized(rig, bar) - origin) * BarsAcross(rig);
                            Assert.True(Math.Abs(drift) <= 0.5 + 1e-9,
                                $"anchor={anchor} 第 {i + 1} 档(delta={deltas[i]})第 {bar} 根偏了 {drift:F3} 根: {rig.ActiveRange}");
                        }
                    }
                }
            }
            finally { rig.Close(); }
        });

        /// <summary>开了按像素封顶(MinBarSpacingPx)后一路缩小:撞到上限就停在那,鼠标下那根照样不偏,不会被事后居中收回。</summary>
        [Fact]
        public void Wheel_PixelCap_StopsAtCap_AnchorStays() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig(length: LargeLength, minBarSpacingPx: 1);
            try
            {
                rig.Frame();
                double cap = State(rig).Ctx.Shared().Read<ViewportSpanLimitsTrait>()!.MaxSpanByPixels;
                Assert.InRange(cap, 100, 600);

                foreach (double anchor in new[] { 0.13, 0.37, 0.81 })
                {
                    Reset(rig, MiddleView);
                    double x = AnchorX(rig, anchor);
                    double v0 = ValueAt(rig, x);
                    for (int i = 0; i < 25; i++)
                    {
                        Wheel(rig, x, -120);
                        Assert.True(rig.ActiveRange.Span <= cap + 1, $"超过像素上限 {cap:F0}: {rig.ActiveRange}");
                        double drift = ValueAt(rig, x) - v0;
                        Assert.True(Math.Abs(drift) <= 0.5 + 1e-9,
                            $"anchor={anchor} 第 {i + 1} 档鼠标下偏了 {drift:F3} 根: {rig.ActiveRange}");
                    }
                    Assert.True(rig.ActiveRange.Span >= cap - 1, $"缩小 25 档应停在上限 {cap:F0}: {rig.ActiveRange}");
                }
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Wheel_AfterPan_RestartsFromCurrentViewport() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new PointerRouterTests.Rig();
            try
            {
                double x = AnchorX(rig, 0.37);
                Wheel(rig, x, +120);
                Wheel(rig, x, +120);
                rig.Drag(150, 250);
                var panned = rig.ActiveRange;
                double v0 = ValueAt(rig, x);
                Wheel(rig, x, +120);
                Assert.True(Math.Abs(ValueAt(rig, x) - v0) <= 0.5 + 1e-9,
                    $"平移后再缩放应从平移后的视口起算: {panned} → {rig.ActiveRange}");
            }
            finally { rig.Close(); }
        });

        private static ChartInteractionState State(PointerRouterTests.Rig rig)
            => rig.Cell.PointerRegions.Behaviors.StateOf<ChartInteractionState>(rig.Interaction)!;

        private static double AnchorX(PointerRouterTests.Rig rig, double anchor)
        {
            var state = State(rig);
            var plot = state.Ctx.GetPlotArea();
            // 先移过去,取路由器实际收到的坐标(真实输入会取整到设备像素)
            rig.Move(plot.Left + anchor * plot.Width, 150);
            return state.LastMousePos!.Value.X;
        }

        /// <summary>画布 X 处对应的逻辑位置(连续值,不吸附到整根)。</summary>
        private static double ValueAt(PointerRouterTests.Rig rig, double x)
        {
            var state = State(rig);
            var plot = state.Ctx.GetPlotArea();
            var scale = state.Ctx.Shared().Read<ScaleStrategyTrait>()!.DomainScale;
            return scale.Denormalize((x - plot.Left) / plot.Width, rig.ActiveRange);
        }

        private static double Normalized(PointerRouterTests.Rig rig, int bar)
            => Scale(rig).Normalize(bar, rig.ActiveRange);

        /// <summary>绘图区横向能放几根(当前根宽下)。</summary>
        private static double BarsAcross(PointerRouterTests.Rig rig)
        {
            var range = rig.ActiveRange;
            return Scale(rig).Denormalize(1, range) - Scale(rig).Denormalize(0, range);
        }

        private static IScale Scale(PointerRouterTests.Rig rig)
            => State(rig).Ctx.Shared().Read<ScaleStrategyTrait>()!.DomainScale;

        private static int[] Repeat(int delta, int count) => Enumerable.Repeat(delta, count).ToArray();

        private static void Reset(PointerRouterTests.Rig rig, RealRange view)
        {
            rig.Write(rig.Viewport.UserRange, view);
            rig.Frame();
            Assert.Equal(view, rig.ActiveRange);
        }

        private static void Wheel(PointerRouterTests.Rig rig, double x, int delta)
        {
            rig.Mouse.Wheel(rig.Canvas, new Point(x, 150), delta);
            rig.Frame();
        }
    }
}
