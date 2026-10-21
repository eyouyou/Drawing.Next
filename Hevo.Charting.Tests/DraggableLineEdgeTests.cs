using System.Reflection;
using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 可拖水平线 / 测量的边角:只读线、线出了绘图区、丢捕获、预览模式下取消、悬停高亮;测量取消清掉选框。
    /// 悬停 / 草稿 / 测量框是 Feature 的私有端口,反射拿到端口再从黑板读。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class DraggableLineEdgeTests
    {
        private static (Rig Rig, DataPort<double> Price, List<double> Commits) LineRig(bool liveWrite = true, bool readOnly = false)
        {
            var price = new DataPort<double>("T_Price");
            var commits = new List<double>();
            var rig = new Rig(extra: (i, s) => i.AddDraggableLine(price, s.YRange,
                new DraggableLineOptions { LiveWrite = liveWrite, ReadOnly = readOnly, OnCommitted = commits.Add }));
            rig.Write(price, 100.0);
            rig.Frame();
            return (rig, price, commits);
        }

        private static object PrivatePort(object feature, string field)
            => feature.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(feature)!;

        private static T ReadPrivate<T>(Rig rig, object feature, string field) => rig.Read((DataPort<T>)PrivatePort(feature, field));

        private static DraggableLineFeature Line(Rig rig) => rig.Schema.ListFeatures().OfType<DraggableLineFeature>().Single();

        private static int RegionCount(Rig rig) => rig.Cell.PointerRegions.Count;

        // ================= 可拖水平线 =================

        [Fact]
        public void ReadOnly_PublishesNoRegion_DragOnLinePans() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig(readOnly: true);
            try
            {
                float y = rig.PriceToScreenY(100.0);
                int regions = RegionCount(rig);
                rig.Drag(100, y, 220, y);
                Assert.Equal(0, regions);
                Assert.True(rig.ActiveRange.Min < rig.Initial.Min);
                Assert.Equal(100.0, rig.Read(price));
                Assert.Empty(commits);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void LineOutsidePlot_NoRegion_ComesBackWhenInside() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, _) = LineRig();
            try
            {
                int inside = RegionCount(rig);
                rig.Write(price, 200.0);   // 量程 80–120 之外
                rig.Frame();
                int outside = RegionCount(rig);
                float top = rig.Cell.GetPlotAreaForTest().Top;
                rig.Drag(100, top + 1, 220, top + 1);
                var panned = rig.ActiveRange;
                rig.Write(price, 100.0);
                rig.Frame();
                int back = RegionCount(rig);

                Assert.Equal(1, inside);
                Assert.Equal(0, outside);
                Assert.True(panned.Min < rig.Initial.Min);
                Assert.Equal(1, back);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void CaptureLost_RollsBack_NoCommit() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig();
            try
            {
                var line = Line(rig);
                float y = rig.PriceToScreenY(100.0);
                rig.Mouse.Move(rig.Canvas, new Point(200, y));
                rig.Mouse.Down(rig.Canvas, new Point(200, y));
                rig.Move(200, y - 30);
                double dragged = rig.Read(price);
                System.Windows.Input.Mouse.Capture(null);
                double rolledBack = rig.Read(price);
                bool hot = ReadPrivate<bool>(rig, line, "_hotPort");
                rig.Mouse.Up(rig.Canvas, new Point(200, y - 30));
                rig.Frame();

                Assert.True(dragged > 100.0);
                Assert.Equal(100.0, rolledBack);
                Assert.False(hot);
                Assert.Equal(100.0, rig.Read(price));
                Assert.Empty(commits);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void PreviewOnly_Escape_ClearsDraft_PriceUntouched() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, price, commits) = LineRig(liveWrite: false);
            try
            {
                var line = Line(rig);
                float y = rig.PriceToScreenY(100.0);
                rig.Mouse.Move(rig.Canvas, new Point(200, y));
                rig.Mouse.Down(rig.Canvas, new Point(200, y));
                rig.Move(200, y - 30);
                double? draftMid = ReadPrivate<double?>(rig, line, "_draftPort");
                rig.Escape();
                double? draftAfter = ReadPrivate<double?>(rig, line, "_draftPort");
                rig.Mouse.Up(rig.Canvas, new Point(200, y - 30));
                rig.Frame();

                Assert.NotNull(draftMid);
                Assert.True(draftMid > 100.0);
                Assert.Null(draftAfter);
                Assert.Equal(100.0, rig.Read(price));
                Assert.Empty(commits);
                // 草稿清掉后线画回原价:区域也回到原价位置
                Assert.Equal(1, RegionCount(rig));
                Assert.Equal(y, rig.Cell.PointerRegions[0].Region.Shape.A, 1);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Hover_SetsHot_LeaveClears_DragKeepsHotOffTheLine() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, _, _) = LineRig();
            try
            {
                var line = Line(rig);
                float y = rig.PriceToScreenY(100.0);
                bool before = ReadPrivate<bool>(rig, line, "_hotPort");
                rig.Move(200, y);
                bool onLine = ReadPrivate<bool>(rig, line, "_hotPort");
                rig.Move(200, y + 60);
                bool away = ReadPrivate<bool>(rig, line, "_hotPort");

                rig.Mouse.Move(rig.Canvas, new Point(200, y));
                rig.Mouse.Down(rig.Canvas, new Point(200, y));
                rig.Move(260, y - 20);
                bool dragging = ReadPrivate<bool>(rig, line, "_hotPort");
                rig.Mouse.Up(rig.Canvas, new Point(260, y - 20));
                rig.Frame();

                Assert.False(before);
                Assert.True(onLine);
                Assert.False(away);
                Assert.True(dragging);
            }
            finally { rig.Close(); }
        });

        // ================= 测量 =================

        private static bool MeasureActive(Rig rig, MeasureFeature m)
        {
            var port = PrivatePort(m, "_statePort");
            var read = typeof(DataBlackboard).GetMethods()
                .Single(x => x.Name == nameof(DataBlackboard.Read) && x.IsGenericMethodDefinition && x.GetParameters().Length == 1
                    && x.GetParameters()[0].ParameterType.IsGenericType && x.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(DataPort<>))
                .MakeGenericMethod(port.GetType().GetGenericArguments()[0]);
            object state;
            using (rig.Board.ReadScope()) state = read.Invoke(rig.Board, new[] { port })!;
            return (bool)state.GetType().GetProperty("Active")!.GetValue(state)!;
        }

        [Fact]
        public void Measure_Escape_ClearsBox_NoReport() => IncrementalTestKit.RunOnSta(() =>
        {
            int reports = 0;
            var rig = new Rig(extra: (i, s) => i.AddMeasure(s.YRange, new MeasureOptions { OnMeasured = (_, _) => reports++ }));
            try
            {
                var m = rig.Schema.ListFeatures().OfType<MeasureFeature>().Single();
                bool activeMid, activeAfter;
                try
                {
                    WpfPointerAdapter.ModifiersOverride = () => PointerModifiers.Control;
                    rig.Mouse.Move(rig.Canvas, new Point(100, 200));
                    rig.Mouse.Down(rig.Canvas, new Point(100, 200));
                    rig.Move(220, 120);
                    activeMid = MeasureActive(rig, m);
                    rig.Escape();
                    activeAfter = MeasureActive(rig, m);
                    rig.Mouse.Up(rig.Canvas, new Point(220, 120));
                    rig.Frame();
                }
                finally { WpfPointerAdapter.ModifiersOverride = null; }

                Assert.True(activeMid);
                Assert.False(activeAfter);
                Assert.Equal(0, reports);
                Assert.Equal(rig.Initial.Min, rig.ActiveRange.Min, 6);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void Measure_CaptureLost_ClearsBox_NoReport() => IncrementalTestKit.RunOnSta(() =>
        {
            int reports = 0;
            var rig = new Rig(extra: (i, s) => i.AddMeasure(s.YRange, new MeasureOptions { OnMeasured = (_, _) => reports++ }));
            try
            {
                var m = rig.Schema.ListFeatures().OfType<MeasureFeature>().Single();
                bool activeAfter;
                try
                {
                    WpfPointerAdapter.ModifiersOverride = () => PointerModifiers.Control;
                    rig.Mouse.Move(rig.Canvas, new Point(100, 200));
                    rig.Mouse.Down(rig.Canvas, new Point(100, 200));
                    rig.Move(220, 120);
                    System.Windows.Input.Mouse.Capture(null);
                    activeAfter = MeasureActive(rig, m);
                    rig.Mouse.Up(rig.Canvas, new Point(220, 120));
                    rig.Frame();
                }
                finally { WpfPointerAdapter.ModifiersOverride = null; }

                Assert.False(activeAfter);
                Assert.Equal(0, reports);
            }
            finally { rig.Close(); }
        });
    }
}
