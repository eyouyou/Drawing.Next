using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 联动 dashboard 的 pane 分隔条:拖拽改比例、最小高度钳制、窗口缩放保比例、比例存取往返、复位,
    /// 以及拖拽 / 删 pane 之后跨 cell 联动照常。全部在离屏真窗口里跑,行高取真实排版结果。
    /// </summary>
    public sealed class LinkedPaneSplitterTests
    {
        private const double WindowHeight = 600;
        private const double Tolerance = 0.6;   // 星号行按设备像素取整,允许半像素误差

        private sealed class Host
        {
            public LinkedChartContext Context { get; } = new();
            public LinkedChartDashboard Dashboard { get; }
            public ProbeSchema[] Schemas { get; } = { new(), new(), new() };
            public DataBlackboard[] Boards { get; } = { new(), new(), new() };
            public Window Window { get; }

            public Host()
            {
                Dashboard = new LinkedChartDashboard(Context)
                    .AddMaster(Schemas[0], 3)
                    .AddPane(Schemas[1], 1)
                    .AddPane(Schemas[2], 1);
                Window = new Window
                {
                    Content = Dashboard, Width = 400, Height = WindowHeight,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = 0,
                };
                Window.Show();
                Window.UpdateLayout();
            }

            public double PaneHeight(int i) => Dashboard.Cells[i].ActualHeight;

            public double TotalPaneHeight()
            {
                double sum = 0;
                for (int i = 0; i < Dashboard.Cells.Count; i++) sum += PaneHeight(i);
                return sum;
            }

            public void Drag(int boundary, double deltaY)
            {
                Assert.True(Dashboard.BeginSplitterDrag(boundary));
                Dashboard.UpdateSplitterDrag(deltaY);
                Dashboard.EndSplitterDrag();
                Window.UpdateLayout();
            }

            public void ActivateBoards()
            {
                for (int i = 0; i < Schemas.Length; i++) Schemas[i].Trigger.Push(Boards[i]);
            }
        }

        private static void WithHost(Action<Host> body) => IncrementalTestKit.RunOnSta(() =>
        {
            var h = new Host();
            try { body(h); }
            finally { h.Window.Close(); }
        });

        [Fact]
        public void Splitters_SitBetweenPanes_OutsideCells_WithSizeNsCursor() => WithHost(h =>
        {
            var splitters = h.Dashboard.Splitters;
            Assert.Equal(2, splitters.Count);
            for (int i = 0; i < splitters.Count; i++)
            {
                Assert.Equal(i, splitters[i].BoundaryIndex);
                Assert.Same(Cursors.SizeNS, splitters[i].Cursor);
                // 分隔条和 cell 是兄弟节点,拖拽事件不会进图表交互管线
                Assert.IsType<Grid>(splitters[i].Parent);
                Assert.Same(h.Dashboard.Cells[0].Parent, splitters[i].Parent);
                Assert.Equal(i * 2 + 1, Grid.GetRow(splitters[i]));
            }
            Assert.Equal(new[] { 0.6, 0.2, 0.2 }, h.Dashboard.GetPaneRatios(), new DoubleComparer());
        });

        [Fact]
        public void Drag_ResizesAdjacentPanesOnly_AndRaisesChangedOnce() => WithHost(h =>
        {
            double h0 = h.PaneHeight(0), h1 = h.PaneHeight(1), h2 = h.PaneHeight(2);
            int changed = 0;
            h.Dashboard.PaneRatiosChanged += (_, _) => changed++;

            h.Drag(0, 50);

            Assert.Equal(1, changed);
            Assert.InRange(h.PaneHeight(0), h0 + 50 - Tolerance, h0 + 50 + Tolerance);
            Assert.InRange(h.PaneHeight(1), h1 - 50 - Tolerance, h1 - 50 + Tolerance);
            Assert.InRange(h.PaneHeight(2), h2 - Tolerance, h2 + Tolerance);
            var r = h.Dashboard.GetPaneRatios();
            Assert.Equal(1.0, r[0] + r[1] + r[2], 9);
            Assert.True(r[0] > 0.6 && r[1] < 0.2);
            Assert.Equal(0.2, r[2], 9);
        });

        [Fact]
        public void Drag_ClampsAtMinPaneHeight_AndFollowsPointerBackFromStart() => WithHost(h =>
        {
            double h1 = h.PaneHeight(1), h2 = h.PaneHeight(2);
            double min = h.Dashboard.MinPaneHeight;

            Assert.True(h.Dashboard.BeginSplitterDrag(1));
            h.Dashboard.UpdateSplitterDrag(1000);
            h.Window.UpdateLayout();
            Assert.InRange(h.PaneHeight(2), min - Tolerance, min + Tolerance);
            Assert.InRange(h.PaneHeight(1), h1 + h2 - min - Tolerance, h1 + h2 - min + Tolerance);

            h.Dashboard.UpdateSplitterDrag(-1000);
            h.Window.UpdateLayout();
            Assert.InRange(h.PaneHeight(1), min - Tolerance, min + Tolerance);

            // 位移按拖拽起点算:回到 0 就是原样,不因中途钳制漂移
            h.Dashboard.UpdateSplitterDrag(0);
            h.Dashboard.EndSplitterDrag();
            h.Window.UpdateLayout();
            Assert.InRange(h.PaneHeight(1), h1 - Tolerance, h1 + Tolerance);
            Assert.InRange(h.PaneHeight(2), h2 - Tolerance, h2 + Tolerance);
        });

        [Fact]
        public void WindowResize_KeepsRatios() => WithHost(h =>
        {
            h.Drag(0, 80);
            var before = h.Dashboard.GetPaneRatios();

            h.Window.Height = WindowHeight * 1.5;
            h.Window.UpdateLayout();

            Assert.Equal(before, h.Dashboard.GetPaneRatios(), new DoubleComparer());
            double total = h.TotalPaneHeight();
            for (int i = 0; i < before.Length; i++)
                Assert.InRange(h.PaneHeight(i) / total, before[i] - 0.003, before[i] + 0.003);
        });

        [Fact]
        public void SaveRestore_RoundTrip_AndRejectsMismatchedLayout()
        {
            double[] saved = Array.Empty<double>();
            double[] savedHeights = new double[3];
            WithHost(h =>
            {
                h.Drag(0, -60);
                h.Drag(1, 30);
                saved = h.Dashboard.GetPaneRatios();
                for (int i = 0; i < 3; i++) savedHeights[i] = h.PaneHeight(i);
            });

            WithHost(h =>
            {
                Assert.False(h.Dashboard.TrySetPaneRatios(new[] { 1.0, 1.0 }));
                Assert.False(h.Dashboard.TrySetPaneRatios(new[] { 1.0, 0.0, 1.0 }));
                Assert.False(h.Dashboard.TrySetPaneRatios(new[] { 1.0, double.NaN, 1.0 }));
                Assert.Equal(new[] { 0.6, 0.2, 0.2 }, h.Dashboard.GetPaneRatios(), new DoubleComparer());

                int changed = 0;
                h.Dashboard.PaneRatiosChanged += (_, _) => changed++;
                Assert.True(h.Dashboard.TrySetPaneRatios(saved));
                h.Window.UpdateLayout();

                Assert.Equal(1, changed);
                Assert.Equal(saved, h.Dashboard.GetPaneRatios(), new DoubleComparer());
                for (int i = 0; i < 3; i++)
                    Assert.InRange(h.PaneHeight(i), savedHeights[i] - Tolerance, savedHeights[i] + Tolerance);

                // 不要求归一:按原始 heightRatio 量纲给也行
                Assert.True(h.Dashboard.TrySetPaneRatios(new[] { 2.0, 1.0, 1.0 }));
                Assert.Equal(new[] { 0.5, 0.25, 0.25 }, h.Dashboard.GetPaneRatios(), new DoubleComparer());
            });
        }

        [Fact]
        public void Reset_RestoresAssemblyRatios() => WithHost(h =>
        {
            h.Drag(0, 90);
            h.Drag(1, -20);

            h.Dashboard.ResetPaneRatios();
            h.Window.UpdateLayout();

            Assert.Equal(new[] { 0.6, 0.2, 0.2 }, h.Dashboard.GetPaneRatios(), new DoubleComparer());
            double total = h.TotalPaneHeight();
            Assert.InRange(h.PaneHeight(0) / total, 0.597, 0.603);
        });

        [Fact]
        public void Linkage_StillMirrorsAfterDrag() => WithHost(h =>
        {
            h.ActivateBoards();
            var cells = h.Dashboard.Cells;

            h.Drag(0, 70);
            h.Drag(1, -40);

            Assert.Equal(cells, h.Dashboard.Cells);   // 拖拽只改行高,不重建 cell
            Assert.Equal(3, h.Context.AttachedBoards.Length);
            var port = h.Context.SharedViewport.LogicalLength;
            h.Boards[0].Write(port, 4242);
            for (int i = 1; i < 3; i++)
                using (h.Boards[i].AcquireReadLock()) Assert.Equal(4242, h.Boards[i].Read(port));

            h.Boards[2].Write(port, 777);
            using (h.Boards[0].AcquireReadLock()) Assert.Equal(777, h.Boards[0].Read(port));
        });

        [Fact]
        public void RemovePane_KeepsSplittersAndRatiosConsistent() => WithHost(h =>
        {
            h.Drag(0, 40);
            var before = h.Dashboard.GetPaneRatios();

            Assert.True(h.Dashboard.RemovePaneAtRuntime(h.Schemas[1]));
            h.Window.UpdateLayout();

            Assert.Single(h.Dashboard.Splitters);
            Assert.Equal(0, h.Dashboard.Splitters[0].BoundaryIndex);
            Assert.Equal(2, Grid.GetRow(h.Dashboard.Cells[1]));
            var after = h.Dashboard.GetPaneRatios();
            Assert.Equal(2, after.Length);
            Assert.Equal(before[0] / (before[0] + before[2]), after[0], 9);

            double total = h.TotalPaneHeight();
            Assert.InRange(total, WindowHeight - h.Dashboard.SplitterThickness - 1, WindowHeight);
            h.Drag(0, 25);
            Assert.True(h.Dashboard.GetPaneRatios()[0] > after[0]);

            h.Dashboard.AppendPaneAtRuntime(new ProbeSchema(), 1);
            h.Window.UpdateLayout();
            Assert.Equal(2, h.Dashboard.Splitters.Count);
            Assert.Equal(3, h.Dashboard.GetPaneRatios().Length);
        });

        private sealed class DoubleComparer : IEqualityComparer<double>
        {
            public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;
            public int GetHashCode(double obj) => 0;
        }
    }
}
