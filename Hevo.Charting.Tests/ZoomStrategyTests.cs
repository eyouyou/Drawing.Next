using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 缩放策略的纯几何:每种策略钉住哪一点、Span 怎么夹、锚点出了 plot 区怎么退。
    /// 不起 WPF,直接构造 ZoomContext 调 Calculate。
    /// </summary>
    public sealed class ZoomStrategyTests
    {
        private static readonly RealRange Base = new(800, 899);   // Span 99
        private static readonly SpanLimits Limits = new(4, 150);
        private const int Length = 1000;

        private static ZoomContext Ctx(double rawSpan, double mouseX = 0.5, PointerHitState? hit = null,
            SpanLimits? limits = null, IScale? scale = null, bool leftExhausted = false, int length = Length, RealRange? baseRange = null)
            => new ZoomContext(baseRange ?? Base, rawSpan, mouseX, hit, length, limits ?? Limits, scale, leftExhausted, false);

        private static PointerHitState HitAt(int index)
            => new PointerHitState(new HevoPoint(0, 0), new DomainHitResult(index, 0, 0), 0, index, false);

        /// <summary>按 Edge 标尺取 anchor 处的逻辑值,用来校验"锚点下那一点不动"。</summary>
        private static double At(RealRange r, double anchor) => r.Min + anchor * r.Span;

        private static void AssertRange(double min, double max, RealRange actual)
        {
            Assert.Equal(min, actual.Min, 9);
            Assert.Equal(max, actual.Max, 9);
        }

        public static TheoryData<string> ClampingStrategies => new()
        {
            nameof(MouseAnchorZoomStrategy), nameof(RightEdgeZoomStrategy), nameof(SmartAdaptiveZoomStrategy),
            nameof(LeftEdgeZoomStrategy), nameof(CrosshairAnchorZoomStrategy), nameof(DirectionalZoomStrategy),
            nameof(HistoryAwareZoomStrategy),
        };

        private static IZoomStrategy Make(string name) => name switch
        {
            nameof(MouseAnchorZoomStrategy) => MouseAnchorZoomStrategy.Instance,
            nameof(RightEdgeZoomStrategy) => RightEdgeZoomStrategy.Instance,
            nameof(SmartAdaptiveZoomStrategy) => SmartAdaptiveZoomStrategy.Instance,
            nameof(LeftEdgeZoomStrategy) => LeftEdgeZoomStrategy.Instance,
            nameof(CrosshairAnchorZoomStrategy) => CrosshairAnchorZoomStrategy.Instance,
            nameof(DirectionalZoomStrategy) => DirectionalZoomStrategy.Instance,
            nameof(HistoryAwareZoomStrategy) => new HistoryAwareZoomStrategy(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        // ================= 共性:Span 夹紧 =================

        [Theory]
        [MemberData(nameof(ClampingStrategies))]
        public void AllStrategies_ClampSpan_BothDirections(string name)
        {
            var s = Make(name);
            Assert.Equal(Limits.MinSpan, s.Calculate(Ctx(0.5)).Span, 9);
            Assert.Equal(Limits.MaxSpan, s.Calculate(Ctx(10_000)).Span, 9);
            Assert.Equal(60, s.Calculate(Ctx(60)).Span, 9);
        }

        [Theory]
        [MemberData(nameof(ClampingStrategies))]
        public void AllStrategies_SameSpan_ReturnsBase(string name)
        {
            // 跨度没变时任何锚点都应原样返回,HandleZoom 靠这个退帧
            var r = Make(name).Calculate(Ctx(Base.Span, mouseX: 0.3, hit: HitAt(850), scale: CategoryScale.Centered));
            AssertRange(Base.Min, Base.Max, r);
        }

        // ================= MouseAnchor =================

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.25)]
        [InlineData(0.7)]
        [InlineData(1.0)]
        public void MouseAnchor_KeepsPointUnderMouse_InAndOut(double x)
        {
            foreach (double span in new[] { 40.0, 140.0 })
            {
                var r = MouseAnchorZoomStrategy.Instance.Calculate(Ctx(span, x));
                Assert.Equal(span, r.Span, 9);
                Assert.Equal(At(Base, x), At(r, x), 9);
            }
        }

        [Fact]
        public void MouseAnchor_OutOfPlot_ClampsToNearestEdge()
        {
            // 跟 SmartAdaptive 不同:MouseAnchor 只夹到 [0,1],左边出界就锚左缘
            var left = MouseAnchorZoomStrategy.Instance.Calculate(Ctx(40, -1));
            var right = MouseAnchorZoomStrategy.Instance.Calculate(Ctx(40, 1.5));
            AssertRange(Base.Min, Base.Min + 40, left);
            AssertRange(Base.Max - 40, Base.Max, right);
        }

        // ================= RightEdge / LeftEdge =================

        [Theory]
        [InlineData(40, 0.3)]
        [InlineData(140, 0.3)]
        [InlineData(1, -1)]
        [InlineData(5000, 1.5)]
        public void RightEdge_KeepsMax_IgnoresMouse(double span, double x)
        {
            var r = RightEdgeZoomStrategy.Instance.Calculate(Ctx(span, x));
            Assert.Equal(Base.Max, r.Max, 9);
            Assert.Equal(ZoomMath.ClampSpan(span, Limits), r.Span, 9);
        }

        [Theory]
        [InlineData(40, 0.3)]
        [InlineData(140, 0.9)]
        [InlineData(1, 1.5)]
        [InlineData(5000, -1)]
        public void LeftEdge_KeepsMin_IgnoresMouse(double span, double x)
        {
            var r = LeftEdgeZoomStrategy.Instance.Calculate(Ctx(span, x));
            Assert.Equal(Base.Min, r.Min, 9);
            Assert.Equal(ZoomMath.ClampSpan(span, Limits), r.Span, 9);
        }

        // ================= SmartAdaptive =================

        [Fact]
        public void SmartAdaptive_InPlot_MouseAnchor()
        {
            foreach (double span in new[] { 40.0, 140.0 })
            {
                var r = SmartAdaptiveZoomStrategy.Instance.Calculate(Ctx(span, 0.3));
                Assert.Equal(At(Base, 0.3), At(r, 0.3), 9);
            }
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(-0.001)]
        [InlineData(1.5)]
        public void SmartAdaptive_OutOfPlot_RightEdge(double x)
        {
            // 左侧出界(包括未武装时写的 -1)也是右缘,不是左缘
            foreach (double span in new[] { 40.0, 140.0 })
                Assert.Equal(Base.Max, SmartAdaptiveZoomStrategy.Instance.Calculate(Ctx(span, x)).Max, 9);
        }

        [Fact]
        public void SmartAdaptive_PlotBoundary_IsInPlot()
        {
            AssertRange(Base.Min, Base.Min + 40, SmartAdaptiveZoomStrategy.Instance.Calculate(Ctx(40, 0)));
            AssertRange(Base.Max - 40, Base.Max, SmartAdaptiveZoomStrategy.Instance.Calculate(Ctx(40, 1)));
        }

        // ================= CrosshairAnchor =================

        [Theory]
        [InlineData(800)]
        [InlineData(833)]
        [InlineData(899)]
        public void CrosshairAnchor_Hit_KeepsHitBarNormalizedPosition(int index)
        {
            var scale = CategoryScale.Centered;
            foreach (double span in new[] { 40.0, 140.0 })
            {
                // 鼠标给一个离命中那根很远的位置:命中优先,鼠标不参与
                var r = CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(span, 0.95, HitAt(index), scale: scale));
                Assert.Equal(scale.Normalize(index, Base), scale.Normalize(index, r), 9);
            }
        }

        [Fact]
        public void CrosshairAnchor_HitOutsideView_ClampsAnchor()
        {
            var right = CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(40, 0.5, HitAt(2000), scale: CategoryScale.Edge));
            var left = CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(40, 0.5, HitAt(-50), scale: CategoryScale.Edge));
            Assert.Equal(Base.Max, right.Max, 9);
            Assert.Equal(Base.Min, left.Min, 9);
        }

        [Fact]
        public void CrosshairAnchor_HitWithoutScale_UsesLinearAnchor()
        {
            var r = CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(40, 0.95, HitAt(850)));
            Assert.Equal(ZoomMath.HitAnchoredZoom(Base, 40, 850), r);
            Assert.Equal((850 - Base.Min) / Base.Span, (850 - r.Min) / r.Span, 9);
        }

        [Fact]
        public void CrosshairAnchor_NoHit_FallsBackToMouseOrRightEdge()
        {
            var inPlot = CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(40, 0.3));
            Assert.Equal(At(Base, 0.3), At(inPlot, 0.3), 9);
            Assert.Equal(Base.Max, CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(40, -1)).Max, 9);
            Assert.Equal(Base.Max, CrosshairAnchorZoomStrategy.Instance.Calculate(Ctx(140, 1.5)).Max, 9);
        }

        // ================= Directional =================

        [Fact]
        public void Directional_ZoomIn_MouseAnchor_OutsideRightEdge()
        {
            var inPlot = DirectionalZoomStrategy.Instance.Calculate(Ctx(40, 0.3));
            Assert.Equal(At(Base, 0.3), At(inPlot, 0.3), 9);
            Assert.Equal(Base.Max, DirectionalZoomStrategy.Instance.Calculate(Ctx(40, -1)).Max, 9);
            Assert.Equal(Base.Max, DirectionalZoomStrategy.Instance.Calculate(Ctx(40, 1.5)).Max, 9);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.3)]
        [InlineData(-1.0)]
        public void Directional_ZoomOut_AlwaysRightEdge(double x)
        {
            var r = DirectionalZoomStrategy.Instance.Calculate(Ctx(140, x));
            AssertRange(Base.Max - 140, Base.Max, r);
        }

        [Fact]
        public void Directional_DirectionJudgedAfterClamp()
        {
            // 视口已在最小跨度时再放大:夹紧后跨度不变,不算放大,结果原样
            var atMin = new RealRange(800, 804);
            var r = DirectionalZoomStrategy.Instance.Calculate(Ctx(2, 0.3, baseRange: atMin));
            Assert.Equal(atMin, r);
        }

        // ================= FixedWindow =================

        [Theory]
        [InlineData(1.0)]
        [InlineData(40.0)]
        [InlineData(5000.0)]
        public void FixedWindow_IgnoresRawSpan_RightEdge(double raw)
        {
            var s = new FixedWindowZoomStrategy { WindowBars = 50 };
            AssertRange(Length - 1 - 49, Length - 1, s.Calculate(Ctx(raw, 0.2)));
        }

        [Fact]
        public void FixedWindow_LeftEdge_ShowsEarliest()
        {
            var s = new FixedWindowZoomStrategy { WindowBars = 50, Alignment = ViewportAlignment.LeftEdge };
            AssertRange(0, 49, s.Calculate(Ctx(10, 0.9)));
        }

        [Fact]
        public void FixedWindow_WindowClampedToLimits()
        {
            var big = new FixedWindowZoomStrategy { WindowBars = 500 };
            AssertRange(Length - 1 - Limits.MaxSpan, Length - 1, big.Calculate(Ctx(10)));
            var tiny = new FixedWindowZoomStrategy { WindowBars = 0 };
            AssertRange(Length - 1 - Limits.MinSpan, Length - 1, tiny.Calculate(Ctx(10)));
        }

        [Fact]
        public void FixedWindow_EmptyData_AnchorsAtZero()
        {
            var s = new FixedWindowZoomStrategy { WindowBars = 10 };
            AssertRange(-9, 0, s.Calculate(Ctx(10, length: 0)));
        }

        // ================= HistoryAware =================

        [Fact]
        public void HistoryAware_DuringPaging_RightEdge_ThenLeftEdgeWhenExhausted()
        {
            var s = new HistoryAwareZoomStrategy();
            foreach (double span in new[] { 40.0, 140.0 })
            {
                Assert.Equal(Base.Max, s.Calculate(Ctx(span, 0.3)).Max, 9);
                Assert.Equal(Base.Min, s.Calculate(Ctx(span, 0.3, leftExhausted: true)).Min, 9);
            }
        }

        [Fact]
        public void HistoryAware_CustomDuringPaging_IsDelegated()
        {
            var s = new HistoryAwareZoomStrategy { DuringPaging = MouseAnchorZoomStrategy.Instance };
            var r = s.Calculate(Ctx(40, 0.3));
            Assert.Equal(At(Base, 0.3), At(r, 0.3), 9);
            Assert.Equal(Base.Min, s.Calculate(Ctx(40, 0.3, leftExhausted: true)).Min, 9);
        }

        [Fact]
        public void HistoryAware_Exhausted_ClampsSpan()
        {
            var s = new HistoryAwareZoomStrategy();
            AssertRange(Base.Min, Base.Min + Limits.MaxSpan, s.Calculate(Ctx(10_000, leftExhausted: true)));
            AssertRange(Base.Min, Base.Min + Limits.MinSpan, s.Calculate(Ctx(0, leftExhausted: true)));
        }

        // ================= ZoomMath =================

        [Theory]
        [InlineData(1, 4)]
        [InlineData(4, 4)]
        [InlineData(77, 77)]
        [InlineData(150, 150)]
        [InlineData(151, 150)]
        public void ClampSpan_ClampsIntoLimits(double raw, double expected)
            => Assert.Equal(expected, ZoomMath.ClampSpan(raw, Limits));

        [Fact]
        public void ClampSpan_MaxBelowMin_MinWins()
        {
            // 数据很少时 MaxSpan 可能小于 MinSpan,不能让 Math.Clamp 抛异常
            var inverted = new SpanLimits(10, 3);
            Assert.Equal(10, ZoomMath.ClampSpan(1, inverted));
            Assert.Equal(10, ZoomMath.ClampSpan(100, inverted));
        }

        [Fact]
        public void HitAnchoredZoom_ZeroSpanBase_AnchorsAtCenter()
        {
            var r = ZoomMath.HitAnchoredZoom(new RealRange(50, 50), 10, 50);
            AssertRange(45, 55, r);
            // 带标尺重载 scale 为 null 时同上
            Assert.Equal(r, ZoomMath.HitAnchoredZoom(new RealRange(50, 50), 10, 50, null));
        }

        [Fact]
        public void HitAnchoredZoom_AnchorOutsideRange_Clamped()
        {
            AssertRange(Base.Max - 40, Base.Max, ZoomMath.HitAnchoredZoom(Base, 40, 5000));
            AssertRange(Base.Min, Base.Min + 40, ZoomMath.HitAnchoredZoom(Base, 40, -5000));
        }
    }
}
