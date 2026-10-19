using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DocSamples.Pointer;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>内置的可拖水平线、测量,以及 04 号文档引用的画线范例。真实输入管线,独占运行。</summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PointerFeatureTests
    {
        private static void AssertSameRange(RealRange expected, RealRange actual)
        {
            Assert.Equal(expected.Min, actual.Min, 6);
            Assert.Equal(expected.Max, actual.Max, 6);
        }

        // ================= 可拖水平线 =================

        private static (Rig Rig, DataPort<double> Price, List<double> Commits) LineRig(bool liveWrite = true)
        {
            var price = new DataPort<double>("T_Price");
            var commits = new List<double>();
            var rig = new Rig(extra: (i, s) => i.AddDraggableLine(price, s.YRange,
                new DraggableLineOptions { LiveWrite = liveWrite, OnCommitted = commits.Add }));
            rig.Write(price, 100.0);
            rig.Frame();
            return (rig, price, commits);
        }

        [Fact]
        public void DraggableLine_DragChangesPrice_CommitsOnceOnRelease_NoPan() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig();
            float y = rig.PriceToScreenY(100.0);
            rig.Mouse.Move(rig.Canvas, new Point(200, y));
            rig.Mouse.Down(rig.Canvas, new Point(200, y));
            rig.Move(200, y - 20);
            double mid = rig.Read(price);
            int commitsMid = commits.Count;
            rig.Move(200, y - 40);
            rig.Mouse.Up(rig.Canvas, new Point(200, y - 40));
            rig.Frame();
            double end = rig.Read(price);
            var after = rig.ActiveRange;
            rig.Close();

            Assert.True(mid > 100.0, "拖动中实时写价格");
            Assert.Equal(0, commitsMid);
            Assert.True(end > mid);
            Assert.Single(commits);
            Assert.Equal(end, commits[0], 9);
            // 40 DIP / 270 DIP × 40 价格单位 ≈ 5.9
            Assert.Equal(100 + 40.0 / 270 * 40, end, 1);
            AssertSameRange(rig.Initial, after);
        });

        [Fact]
        public void DraggableLine_Escape_RollsBack_NoCommit() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig();
            float y = rig.PriceToScreenY(100.0);
            rig.Mouse.Move(rig.Canvas, new Point(200, y));
            rig.Mouse.Down(rig.Canvas, new Point(200, y));
            rig.Move(200, y - 30);
            double dragged = rig.Read(price);
            rig.Escape();
            rig.Mouse.Up(rig.Canvas, new Point(200, y - 30));
            rig.Frame();
            double after = rig.Read(price);
            rig.Close();
            Assert.True(dragged > 100.0);
            Assert.Equal(100.0, after);
            Assert.Empty(commits);
        });

        [Fact]
        public void DraggableLine_PreviewOnly_WritesPriceOnlyOnRelease() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig(liveWrite: false);
            float y = rig.PriceToScreenY(100.0);
            rig.Mouse.Move(rig.Canvas, new Point(200, y));
            rig.Mouse.Down(rig.Canvas, new Point(200, y));
            rig.Move(200, y - 30);
            double mid = rig.Read(price);
            rig.Mouse.Up(rig.Canvas, new Point(200, y - 30));
            rig.Frame();
            double end = rig.Read(price);
            rig.Close();
            Assert.Equal(100.0, mid);
            Assert.True(end > 100.0);
            Assert.Single(commits);
        });

        [Fact]
        public void DraggableLine_OffTheLine_Pans() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, _) = LineRig();
            float y = rig.PriceToScreenY(100.0);
            rig.Drag(100, y + 60, 220, y + 60);
            var after = rig.ActiveRange;
            double p = rig.Read(price);
            rig.Close();
            Assert.True(after.Min < rig.Initial.Min);
            Assert.Equal(100.0, p);
        });

        /// <summary>R6:Transact 中途装上的 Feature,Watch 能收到,首帧就能拖。</summary>
        [Fact]
        public void HotAdded_DraggableLine_DragsImmediately() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var price = new DataPort<double>("T_Price");
            rig.Write(price, 100.0);
            rig.Schema.Transact(c => c.Add(new DraggableLineFeature { PricePort = price, ValueRangePort = rig.Schema.YRange }));
            rig.Frame();

            float y = rig.PriceToScreenY(100.0);
            rig.Drag(200, y, 200, y - 40);
            double p = rig.Read(price);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(p > 100.0);
            AssertSameRange(rig.Initial, after);
        });

        /// <summary>R6 反向:Transact 拔掉正在拖的线 → 收到 Cancel(OwnerRemoved)、价格回滚、之后拖动是平移。</summary>
        [Fact]
        public void HotRemoved_WhileDragging_RollsBack_ThenPans() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig();
            var line = rig.Schema.ListFeatures().OfType<DraggableLineFeature>().Single();
            float y = rig.PriceToScreenY(100.0);
            rig.Mouse.Move(rig.Canvas, new Point(200, y));
            rig.Mouse.Down(rig.Canvas, new Point(200, y));
            rig.Move(200, y - 30);
            rig.Schema.Transact(c => c.Remove(line));
            double rolledBack = rig.Read(price);
            rig.Mouse.Up(rig.Canvas, new Point(200, y - 30));
            rig.Frame();
            rig.Drag(100, y, 220, y);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal(100.0, rolledBack);
            Assert.Empty(commits);
            Assert.True(after.Min < rig.Initial.Min);
        });

        // ================= 测量 =================

        [Fact]
        public void Measure_CtrlDrag_Reports_NoPan_WithoutCtrl_Pans() => IncrementalTestKit.RunOnSta(() =>
        {
            (MeasurePoint From, MeasurePoint To)? measured = null;
            var rig = new Rig(extra: (i, s) => i.AddMeasure(s.YRange, new MeasureOptions { OnMeasured = (a, b) => measured = (a, b) }));
            try
            {
                WpfPointerAdapter.ModifiersOverride = () => PointerModifiers.Control;
                rig.Drag(100, 200, 220, 120);
            }
            finally { WpfPointerAdapter.ModifiersOverride = null; }
            var afterMeasure = rig.ActiveRange;
            rig.Drag(100, 220);
            var afterPan = rig.ActiveRange;
            rig.Close();

            Assert.NotNull(measured);
            Assert.True(measured!.Value.To.Index > measured.Value.From.Index);
            Assert.True(measured.Value.To.Value > measured.Value.From.Value);
            AssertSameRange(rig.Initial, afterMeasure);
            Assert.True(afterPan.Min < rig.Initial.Min);
        });

        // ================= 画线范例 =================

        private static (Rig Rig, LineDrawingFeature Tool) DrawingRig(bool dismissOnEscape = false)
        {
            LineDrawingFeature? tool = null;
            var rig = new Rig(dismissOnEscape: dismissOnEscape, extra: (i, s) => i.Canvas.Add(tool = new LineDrawingFeature { ValueRangePort = s.YRange }));
            return (rig, tool!);
        }

        private static void Arm(Rig rig, LineDrawingFeature tool) { rig.Write(tool.ArmedPort, true); rig.Frame(); }

        [Fact]
        public void DocSample_LineDrawing_TwoClicks_DrawsLine_NoPan_NoReset() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, tool) = DrawingRig();
            rig.Drag(100, 220, y: 60);   // 先平移一下,用来检查快速点两下没有复原视口
            var panned = rig.ActiveRange;
            Arm(rig, tool);
            rig.Click(120, 100);
            rig.Move(200, 160);          // 两次单击之间拉橡皮筋
            rig.Click(200, 160);
            var lines = rig.Read(tool.LinesPort);
            bool armed = rig.Read(tool.ArmedPort);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Single(lines!);
            Assert.True(lines![0].To.Index > lines[0].From.Index);
            Assert.True(lines[0].To.Value < lines[0].From.Value);   // 屏幕往下 = 价格往下
            Assert.False(armed);
            AssertSameRange(panned, after);
        });

        [Fact]
        public void DocSample_LineDrawing_PressDrag_DrawsLine() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, tool) = DrawingRig();
            Arm(rig, tool);
            rig.Drag(120, 100, 260, 200);
            var lines = rig.Read(tool.LinesPort);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Single(lines!);
            AssertSameRange(rig.Initial, after);
        });

        [Fact]
        public void DocSample_LineDrawing_Escape_ExitsTool_ThenDismissOnEscape() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, tool) = DrawingRig(dismissOnEscape: true);
            Arm(rig, tool);
            rig.Click(120, 100);         // 半截线
            rig.Move(200, 150);
            rig.Escape();                // 第一次:工具退出
            bool armed = rig.Read(tool.ArmedPort);
            var hitAfterFirst = rig.Hit;
            rig.Escape();                // 第二次:DismissOnEscape
            var hitAfterSecond = rig.Hit;
            var lines = rig.Read(tool.LinesPort);
            rig.Drag(100, 220);          // 退出后拖动是平移
            var after = rig.ActiveRange;
            rig.Close();
            Assert.False(armed);
            Assert.Null(lines);
            Assert.NotNull(hitAfterFirst);
            Assert.Null(hitAfterSecond);
            Assert.True(after.Min < rig.Initial.Min);
        });

        [Fact]
        public void DocSample_LineDrawing_ModalBeatsDraggableLine() => IncrementalTestKit.RunOnSta(() =>
        {
            var price = new DataPort<double>("T_Price");
            LineDrawingFeature? tool = null;
            // 价格线后装配:画线优先不靠装配顺序,靠模态优先级
            var rig = new Rig(extra: (i, s) => { i.Canvas.Add(tool = new LineDrawingFeature { ValueRangePort = s.YRange }); i.AddDraggableLine(price, s.YRange); });
            rig.Write(price, 100.0);
            rig.Frame();
            Arm(rig, tool!);
            float y = rig.PriceToScreenY(100.0);
            rig.Drag(200, y, 260, y - 40);
            double p = rig.Read(price);
            var lines = rig.Read(tool!.LinesPort);
            rig.Close();
            Assert.Equal(100.0, p);
            Assert.Single(lines!);
        });

        // ================= 投影 =================

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Projection_RoundTrip(int mode)
        {
            var scale = mode switch { 0 => ScaleStrategyTrait.Default, 1 => ScaleStrategyTrait.CandleMode, _ => ScaleStrategyTrait.LogCandleMode };
            var proj = new ChartProjection(new HevoRect(50, 10, 330, 270), new RealRange(20, 79), new RealRange(80, 120), scale);
            Assert.True(proj.IsValid);
            foreach (double v in new[] { 80.0, 93.7, 101.25, 120.0 })
                Assert.True(Math.Abs(proj.ScreenToValue(proj.ValueToScreenY(v)) - v) < 1e-3 * Math.Max(1, v), $"value {v}");
            foreach (double d in new[] { 20.0, 33.3, 79.0 })
                Assert.True(Math.Abs(proj.ScreenToDomain(proj.DomainToScreenX(d)) - d) < 1e-3, $"domain {d}");
            // double 版往返(不经 float 屏幕坐标)精确到 1e-6
            double sy = 10 + 270 * (1 - scale.ValueScale.Normalize(101.25, new RealRange(80, 120)));
            Assert.True(Math.Abs(proj.ScreenToValue(sy) - 101.25) < 1e-6);
        }
    }
}
