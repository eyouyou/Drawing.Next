using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Probe = Hevo.Charting.Tests.PointerRouterTests.Probe;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 触屏行为:WPF Manipulation 事件在测试里造不出来,直接把换算好的 <see cref="PointerEvent"/> 交给路由器的行为链,
    /// 跟 WpfPointerAdapter 收到 ManipulationStarting / Delta 后做的一样。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class TouchManipulationTests
    {
        private static Rig ModeRig(ChartInteractionMode modes, IZoomStrategy? zoom = null)
            => new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, modes,
                zoomStrategy: zoom, inertiaPanConfig: new InertiaPanOptions { VelocityWindowMs = 2000 }));

        private static PointerEvent Starting(Rig rig)
        {
            var e = new PointerEvent { Kind = PointerEventKind.ManipulationStarting };
            rig.Router.Router!.RouteToBehaviors(ref e);
            return e;
        }

        private static bool Delta(Rig rig, double translateX, double scaleX = 1.0, float originX = 200)
        {
            var e = new PointerEvent
            {
                Kind = PointerEventKind.ManipulationDelta,
                TranslateX = translateX,
                ScaleX = scaleX,
                Origin = new HevoPoint(originX, 150),
            };
            bool handled = rig.Router.Router!.RouteToBehaviors(ref e);
            rig.Frame();
            return handled;
        }

        private static PointerRegion Line(float y) => new PointerRegion { Shape = PointerShape.HLine(y, 0, 1000) };

        /// <summary>记下缩放上下文再交给鼠标锚点策略:断言比例与锚点,不依赖策略细节。</summary>
        private sealed class RecordingZoom : IZoomStrategy
        {
            public readonly List<ZoomContext> Calls = new();
            public RealRange Calculate(in ZoomContext ctx)
            {
                Calls.Add(ctx);
                return MouseAnchorZoomStrategy.Instance.Calculate(in ctx);
            }
        }

        // ================= Starting =================

        [Fact]
        public void Starting_PanAndZoom_RequestsTranslateXAndScale() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var e = Starting(rig);
                Assert.True(e.ManipulationSet);
                Assert.Equal(PointerManipulation.TranslateX | PointerManipulation.Scale, e.Manipulation);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Starting_PanOnly_RequestsTranslateX() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = ModeRig(ChartInteractionMode.Hover | ChartInteractionMode.Pan);
            try
            {
                var e = Starting(rig);
                Assert.True(e.ManipulationSet);
                Assert.Equal(PointerManipulation.TranslateX, e.Manipulation);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Starting_ZoomOnly_RequestsScale() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = ModeRig(ChartInteractionMode.Hover | ChartInteractionMode.Zoom);
            try
            {
                var e = Starting(rig);
                Assert.True(e.ManipulationSet);
                Assert.Equal(PointerManipulation.Scale, e.Manipulation);
            }
            finally { rig.Close(); }
        });

        /// <summary>平移、缩放都没开:显式给 None,让平台把触摸提升成鼠标(hover 照常)。</summary>
        [Fact]
        public void Starting_NeitherPanNorZoom_RequestsNone() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = ModeRig(ChartInteractionMode.Hover);
            try
            {
                var e = Starting(rig);
                Assert.True(e.ManipulationSet);
                Assert.Equal(PointerManipulation.None, e.Manipulation);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Starting_DuringRegionGesture_RequestsNone() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Line(150));
                rig.Mouse.Move(rig.Canvas, new Point(100, 150));
                rig.Mouse.Down(rig.Canvas, new Point(100, 150));
                var during = Starting(rig);
                rig.Mouse.Up(rig.Canvas, new Point(100, 150));
                rig.Frame();
                var after = Starting(rig);

                Assert.Equal(PointerManipulation.None, during.Manipulation);
                Assert.Equal(PointerManipulation.TranslateX | PointerManipulation.Scale, after.Manipulation);
            }
            finally { rig.Close(); }
        });

        // ================= Delta:平移 =================

        [Fact]
        public void Delta_PositiveTranslate_PansToEarlierData_NegativePansBack() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                bool handled = Delta(rig, 60);
                var right = rig.ActiveRange;
                Delta(rig, -60);
                var back = rig.ActiveRange;

                Assert.True(handled);
                // 手指往右 = 内容往右 = 看更早的数据,跟鼠标往右拖同向
                Assert.True(right.Min < rig.Initial.Min, $"{rig.Initial} → {right}");
                Assert.Equal(rig.Initial.Span, right.Span, 6);
                Assert.Equal(rig.Initial.Min, back.Min, 6);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Delta_PanOff_IgnoresTranslate() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = ModeRig(ChartInteractionMode.Hover | ChartInteractionMode.Zoom);
            try
            {
                Delta(rig, 80);
                Assert.Equal(rig.Initial.Min, rig.ActiveRange.Min, 6);
                Assert.Equal(rig.Initial.Max, rig.ActiveRange.Max, 6);
            }
            finally { rig.Close(); }
        });

        // ================= Delta:缩放 =================

        [Fact]
        public void Delta_Scale_ZoomsByInverseScale_AnchoredAtOrigin() => IncrementalTestKit.RunOnSta(() =>
        {
            var zoom = new RecordingZoom();
            var rig = ModeRig(ChartInteractionMode.All, zoom);
            try
            {
                var plot = rig.Cell.GetPlotAreaForTest();
                float originX = plot.Left + plot.Width * 0.25f;
                Delta(rig, 0, scaleX: 2.0, originX: originX);
                var after = rig.ActiveRange;

                var ctx = Assert.Single(zoom.Calls);
                // 两指张开 2 倍 = 可见跨度减半
                Assert.Equal(rig.Initial.Span * 0.5, ctx.RawTargetSpan, 6);
                Assert.Equal(0.25, ctx.MouseRelativeX, 3);
                Assert.True(after.Span < rig.Initial.Span);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Delta_ScaleWithinTolerance_DoesNotZoom() => IncrementalTestKit.RunOnSta(() =>
        {
            var zoom = new RecordingZoom();
            var rig = ModeRig(ChartInteractionMode.All, zoom);
            try
            {
                Delta(rig, 0, scaleX: 1.0 + MathTolerance.ManipulationScale / 2);
                Delta(rig, 0, scaleX: 1.0 - MathTolerance.ManipulationScale / 2);
                Assert.Empty(zoom.Calls);
                Assert.Equal(rig.Initial.Span, rig.ActiveRange.Span, 6);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Delta_ZoomOff_IgnoresScale() => IncrementalTestKit.RunOnSta(() =>
        {
            var zoom = new RecordingZoom();
            var rig = ModeRig(ChartInteractionMode.Hover | ChartInteractionMode.Pan, zoom);
            try
            {
                Delta(rig, 0, scaleX: 2.0);
                Assert.Empty(zoom.Calls);
                Assert.Equal(rig.Initial.Span, rig.ActiveRange.Span, 6);
            }
            finally { rig.Close(); }
        });

        // ================= Delta:附带效果 =================

        [Fact]
        public void Delta_StopsInertia_AndClearsHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                rig.FlingRight();
                rig.Move(200, 150);
                bool runningBefore = rig.InertiaRunning;
                var hitBefore = rig.Hit;
                Delta(rig, 0);
                Assert.True(runningBefore);
                Assert.NotNull(hitBefore);
                Assert.False(rig.InertiaRunning);
                Assert.Null(rig.Hit);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Delta_DuringRegionGesture_IsSwallowed_NoPanNoZoom() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Line(150));
                rig.Mouse.Move(rig.Canvas, new Point(100, 150));
                rig.Mouse.Down(rig.Canvas, new Point(100, 150));
                bool handled = Delta(rig, 80, scaleX: 2.0);
                var during = rig.ActiveRange;
                rig.Mouse.Up(rig.Canvas, new Point(100, 150));
                rig.Frame();

                Assert.True(handled);
                Assert.Equal(rig.Initial.Min, during.Min, 6);
                Assert.Equal(rig.Initial.Span, during.Span, 6);
            }
            finally { rig.Close(); }
        });
    }
}
