using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Interaction;
using Probe = Hevo.Charting.Tests.PointerRouterTests.Probe;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 区域形状的命中(竖线、线段、矩形、圆、折线;线类看容差,面类看包含),
    /// 以及路由器的几处边角:挂起时拖动中取消、区域在静止指针下移动时悬停跟着变、离开写 IsPointerOver。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PointerRegionShapeTests
    {
        private static readonly HevoRect Plot = new HevoRect(50, 10, 330, 270);

        private static bool Hits(PointerShape s, float x, float y, float tolerance = 4) => !float.IsNaN(s.Distance(x, y, tolerance, Plot));

        // ================= 形状:纯命中 =================

        [Fact]
        public void VLine_HitsWithinTolerance_InsideSpan()
        {
            var s = PointerShape.VLine(100, 200, 50);   // 上下颠倒也归一
            Assert.True(Hits(s, 100, 120));
            Assert.True(Hits(s, 104, 120));
            Assert.False(Hits(s, 104.5f, 120));
            Assert.True(Hits(s, 100, 52 - 4 + 1));      // 端点外容差内
            Assert.False(Hits(s, 100, 40));
            Assert.Equal(3f, s.Distance(97, 120, 4, Plot));
            Assert.False(Hits(s, 102, 120, tolerance: 1));
        }

        [Fact]
        public void Segment_UsesPerpendicularDistance_ClampedToEnds()
        {
            var s = PointerShape.Segment(new HevoPoint(0, 0), new HevoPoint(100, 100));
            Assert.True(Hits(s, 50, 52));
            Assert.Equal(0f, s.Distance(50, 50, 4, Plot), 4);
            Assert.False(Hits(s, 50, 60));
            Assert.Equal(5f, s.Distance(103, 104, 10, Plot), 3);   // 超出端点按到端点的距离
        }

        [Fact]
        public void Rect_ContainsOnly_IgnoresTolerance()
        {
            var s = PointerShape.Rect(new HevoRect(100, 100, 50, 20));
            Assert.Equal(0f, s.Distance(125, 110, 4, Plot));
            Assert.True(Hits(s, 100, 100));
            Assert.True(Hits(s, 150, 120));
            Assert.False(Hits(s, 151, 110, tolerance: 10));
            Assert.False(Hits(s, 125, 99, tolerance: 10));
        }

        [Fact]
        public void Circle_ContainsOnly_NegativeRadiusNormalized()
        {
            var s = PointerShape.Circle(new HevoPoint(200, 100), -10);
            Assert.True(Hits(s, 200, 100));
            Assert.True(Hits(s, 206, 108));            // 正好在圆周上
            Assert.False(Hits(s, 208, 108, tolerance: 20));
        }

        [Fact]
        public void Polyline_NearestSegment_WithinTolerance()
        {
            var pts = new[] { new HevoPoint(0, 0), new HevoPoint(100, 0), new HevoPoint(100, 100) };
            var s = PointerShape.Polyline(pts);
            Assert.Equal(2f, s.Distance(50, 2, 4, Plot), 4);
            Assert.Equal(3f, s.Distance(97, 50, 4, Plot), 4);
            Assert.False(Hits(s, 50, 50));             // 折线拐角内侧,两段都远
            Assert.False(Hits(PointerShape.Polyline(ReadOnlyMemory<HevoPoint>.Empty), 0, 0));
            var single = PointerShape.Polyline(new[] { new HevoPoint(10, 10) });
            Assert.True(Hits(single, 12, 10));
            Assert.False(Hits(single, 20, 10));
        }

        [Fact]
        public void WholePlot_UsesPlotAreaAtHitTime()
        {
            var s = PointerShape.WholePlot();
            Assert.True(Hits(s, 60, 20));
            Assert.False(Hits(s, 40, 20));
            Assert.True(float.IsNaN(s.Distance(60, 20, 4, HevoRect.Empty)));
        }

        [Fact]
        public void Bounds_LinesInflatedByTolerance_AreasNot()
        {
            Assert.True(PointerShape.VLine(100, 50, 200).TryGetBounds(4, out var l, out var t, out var r, out var b));
            Assert.Equal((96f, 46f, 104f, 204f), (l, t, r, b));
            Assert.True(PointerShape.Circle(new HevoPoint(10, 20), 5).TryGetBounds(4, out l, out t, out r, out b));
            Assert.Equal((5f, 15f, 15f, 25f), (l, t, r, b));
            Assert.True(PointerShape.Polyline(new[] { new HevoPoint(30, 5), new HevoPoint(10, 40) }).TryGetBounds(2, out l, out t, out r, out b));
            Assert.Equal((8f, 3f, 32f, 42f), (l, t, r, b));
            Assert.False(PointerShape.WholePlot().TryGetBounds(4, out _, out _, out _, out _));
        }

        // ================= 形状:经路由器 =================

        private static PointerRegion Region(PointerShape shape, int id = 1)
            => new PointerRegion { Id = id, Shape = shape, Accepts = PointerAccepts.Click | PointerAccepts.Drag };

        private static bool ClickHits(Rig rig, Probe p, double x, double y)
        {
            int before = p.Log.Count(g => g.Phase == PointerPhase.Click);
            rig.Click(x, y);
            Thread.Sleep(450);   // 跟下一次点击隔开,不凑成双击
            return p.Log.Count(g => g.Phase == PointerPhase.Click) > before;
        }

        [Fact]
        public void Router_VLineRectCirclePolyline_ClickInsideClaims_OutsidePans() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                var results = new List<(string Name, bool Inside, bool Outside)>();

                p.Set(rig, Region(PointerShape.VLine(200, 0, 1000)));
                results.Add(("VLine", ClickHits(rig, p, 203, 150), ClickHits(rig, p, 210, 150)));

                p.Set(rig, Region(PointerShape.Rect(new HevoRect(150, 100, 60, 40))));
                results.Add(("Rect", ClickHits(rig, p, 200, 130), ClickHits(rig, p, 215, 130)));

                p.Set(rig, Region(PointerShape.Circle(new HevoPoint(200, 150), 15)));
                results.Add(("Circle", ClickHits(rig, p, 210, 160), ClickHits(rig, p, 215, 165)));

                p.Set(rig, Region(PointerShape.Polyline(new[] { new HevoPoint(100, 100), new HevoPoint(200, 200), new HevoPoint(300, 100) })));
                results.Add(("Polyline", ClickHits(rig, p, 250, 152), ClickHits(rig, p, 200, 150)));

                Assert.All(results, r => Assert.True(r.Inside && !r.Outside, $"{r.Name}: inside={r.Inside} outside={r.Outside}"));
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Router_RegionTolerance_Respected() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Region(PointerShape.HLine(150, 0, 1000)) with { Tolerance = 12 });
                bool wide = ClickHits(rig, p, 200, 160);
                p.Set(rig, Region(PointerShape.HLine(150, 0, 1000)) with { Tolerance = 1 });
                bool narrow = ClickHits(rig, p, 200, 153);
                Assert.True(wide);
                Assert.False(narrow);
            }
            finally { rig.Close(); }
        });

        // ================= 路由器边角 =================

        [Fact]
        public void Suspend_MidDrag_CancelsChartUnloaded_ReleasesCapture() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Region(PointerShape.HLine(150, 0, 1000)));
                rig.Mouse.Move(rig.Canvas, new Point(100, 150));
                rig.Mouse.Down(rig.Canvas, new Point(100, 150));
                rig.Move(150, 150);
                rig.Schema.Suspend();
                var captured = Mouse.Captured;
                bool active = rig.Router.IsGestureActive;
                rig.Schema.Resume();
                rig.Mouse.Up(rig.Canvas, new Point(150, 150));
                rig.Frame();

                Assert.Equal(PointerCancelReason.ChartUnloaded, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
                Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.End);
                Assert.Null(captured);
                Assert.False(active);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Shutdown_MidDrag_CancelsChartUnloaded() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Region(PointerShape.HLine(150, 0, 1000)));
                rig.Mouse.Move(rig.Canvas, new Point(100, 150));
                rig.Mouse.Down(rig.Canvas, new Point(100, 150));
                rig.Move(150, 150);
                rig.Cell.Shutdown();
                Assert.Equal(PointerCancelReason.ChartUnloaded, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
                Assert.Null(Mouse.Captured);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void RegionMovesUnderStillPointer_HoverLeavesAndReenters_CursorFollows() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Region(PointerShape.HLine(150, 0, 1000)) with { Cursor = PointerCursor.SizeNS });
                rig.Move(200, 150);
                var cursorOn = rig.Cell.Cursor;
                int logAfterEnter = p.Log.Count;

                p.Set(rig, Region(PointerShape.HLine(60, 0, 1000)) with { Cursor = PointerCursor.SizeNS });   // 线挪走,指针没动
                var cursorAway = rig.Cell.Cursor;
                var leave = p.Log.Skip(logAfterEnter).Select(g => g.Phase).ToArray();
                int logAfterLeave = p.Log.Count;

                p.Set(rig, Region(PointerShape.HLine(151, 0, 1000)) with { Cursor = PointerCursor.SizeNS });  // 挪回指针下
                var cursorBack = rig.Cell.Cursor;
                var reenter = p.Log.Skip(logAfterLeave).Select(g => g.Phase).ToArray();

                Assert.Contains(PointerPhase.HoverEnter, p.Log.Take(logAfterEnter).Select(g => g.Phase));
                Assert.Same(Cursors.SizeNS, cursorOn);
                Assert.Equal(new[] { PointerPhase.HoverLeave }, leave);
                Assert.Null(cursorAway);
                Assert.Equal(new[] { PointerPhase.HoverEnter }, reenter);
                Assert.Same(Cursors.SizeNS, cursorBack);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void LeavingChart_IsPointerOverFalse_HoverLeaves() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                var p = rig.Add(new Probe());
                p.Set(rig, Region(PointerShape.HLine(150, 0, 1000)));
                rig.Move(200, 150);
                bool overInside = rig.Read(rig.Router.Ports.IsPointerOver);
                rig.Move(200, 2000);   // 出了窗口
                bool overOutside = rig.Read(rig.Router.Ports.IsPointerOver);

                Assert.True(overInside);
                Assert.False(overOutside);
                Assert.Equal(PointerPhase.HoverLeave, p.Log.Last().Phase);
                Assert.Null(rig.Hit);
            }
            finally { rig.Close(); }
        });
    }
}
