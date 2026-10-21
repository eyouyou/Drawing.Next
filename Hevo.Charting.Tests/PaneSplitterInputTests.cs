using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hevo.Charting.Linked;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 分隔条的鼠标处理:悬停变色、按下抓鼠标、拖动改比例、丢捕获收尾、双击复位。
    /// 鼠标走真实输入管线(<see cref="RawMouse"/>),坐标按 dashboard 算:拖动中分隔条自己会跟着挪。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PaneSplitterInputTests
    {
        private const double Tolerance = 0.6;

        private sealed class Host
        {
            public readonly LinkedChartDashboard Dashboard;
            public readonly Window Window;
            public readonly RawMouse Mouse;

            public Host()
            {
                Dashboard = new LinkedChartDashboard(new LinkedChartContext()) { IsPaneResizeEnabled = true }
                    .AddMaster(new ProbeSchema(), 3)
                    .AddPane(new ProbeSchema(), 1)
                    .AddPane(new ProbeSchema(), 1);
                Window = new Window
                {
                    Content = Dashboard, Width = 400, Height = 600,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = 0,
                };
                Window.Show();
                Window.UpdateLayout();
                Mouse = new RawMouse(Window);
            }

            public PaneSplitter Splitter(int i) => Dashboard.Splitters[i];

            /// <summary>分隔条中线在 dashboard 里的 Y。</summary>
            public double SplitterY(int i)
            {
                var s = Splitter(i);
                return s.TranslatePoint(new Point(0, s.ActualHeight / 2), Dashboard).Y;
            }

            public Brush LineBrush(int i) => ((Border)Splitter(i).Child).Background;
            public double PaneHeight(int i) => Dashboard.Cells[i].ActualHeight;

            public void Move(double y) { Mouse.Move(Dashboard, new Point(200, y)); Window.UpdateLayout(); }
            public void Down(double y) => Mouse.Down(Dashboard, new Point(200, y));
            public void Up(double y) { Mouse.Up(Dashboard, new Point(200, y)); Window.UpdateLayout(); }
        }

        private static void WithHost(Action<Host> body) => IncrementalTestKit.RunOnSta(() =>
        {
            var h = new Host();
            try { body(h); }
            finally { h.Window.Close(); }
        });

        [Fact]
        public void Hover_UsesHoverBrush_LeaveRestores() => WithHost(h =>
        {
            var normal = h.LineBrush(0);
            h.Move(h.SplitterY(0));
            var hovered = h.LineBrush(0);
            h.Move(h.SplitterY(0) - 100);
            var left = h.LineBrush(0);

            Assert.Same(h.Dashboard.SplitterBrush, normal);
            Assert.Same(h.Dashboard.SplitterHoverBrush, hovered);
            Assert.Same(h.Dashboard.SplitterBrush, left);
        });

        [Fact]
        public void PressDragRelease_CapturesThenResizesAdjacentPanes_RaisesChangedOnce() => WithHost(h =>
        {
            double h0 = h.PaneHeight(0), h1 = h.PaneHeight(1), h2 = h.PaneHeight(2);
            int changed = 0;
            h.Dashboard.PaneRatiosChanged += (_, _) => changed++;
            double y = h.SplitterY(0);

            h.Move(y);
            h.Down(y);
            var captured = System.Windows.Input.Mouse.Captured;
            h.Move(y + 20);
            h.Move(y + 50);
            var brushWhileDragging = h.LineBrush(0);
            int changedWhileDragging = changed;
            h.Up(y + 50);
            var capturedAfter = System.Windows.Input.Mouse.Captured;

            Assert.Same(h.Splitter(0), captured);
            Assert.Same(h.Dashboard.SplitterHoverBrush, brushWhileDragging);
            Assert.Equal(0, changedWhileDragging);
            Assert.Null(capturedAfter);
            Assert.Equal(1, changed);
            Assert.InRange(h.PaneHeight(0), h0 + 50 - Tolerance, h0 + 50 + Tolerance);
            Assert.InRange(h.PaneHeight(1), h1 - 50 - Tolerance, h1 - 50 + Tolerance);
            Assert.InRange(h.PaneHeight(2), h2 - Tolerance, h2 + Tolerance);
        });

        [Fact]
        public void LostCapture_EndsDrag_LaterMovesDoNothing() => WithHost(h =>
        {
            int changed = 0;
            h.Dashboard.PaneRatiosChanged += (_, _) => changed++;
            double y = h.SplitterY(1);
            double h1 = h.PaneHeight(1);

            h.Move(y);
            h.Down(y);
            h.Move(y - 30);
            System.Windows.Input.Mouse.Capture(null);   // 切窗口 / 弹框把捕获拿走
            h.Window.UpdateLayout();
            double afterLost = h.PaneHeight(1);
            int changedAfterLost = changed;
            var ratios = h.Dashboard.GetPaneRatios();
            h.Move(y - 60);
            h.Up(y - 60);

            Assert.Equal(1, changedAfterLost);
            Assert.InRange(afterLost, h1 - 30 - Tolerance, h1 - 30 + Tolerance);
            Assert.Equal(ratios, h.Dashboard.GetPaneRatios());
            Assert.Equal(1, changed);
            Assert.Same(h.Dashboard.SplitterBrush, h.LineBrush(1));
        });

        [Fact]
        public void DoubleClick_ResetsRatios() => WithHost(h =>
        {
            Assert.True(h.Dashboard.BeginSplitterDrag(0));
            h.Dashboard.UpdateSplitterDrag(80);
            h.Dashboard.EndSplitterDrag();
            h.Window.UpdateLayout();
            var dragged = h.Dashboard.GetPaneRatios();

            int changed = 0;
            h.Dashboard.PaneRatiosChanged += (_, _) => changed++;
            double y = h.SplitterY(0);
            h.Move(y);
            h.Down(y);
            h.Up(y);
            h.Down(y);
            h.Up(y);
            var after = h.Dashboard.GetPaneRatios();

            Assert.True(dragged[0] > 0.6);
            Assert.Equal(0.6, after[0], 9);
            Assert.Equal(0.2, after[1], 9);
            Assert.Equal(0.2, after[2], 9);
            Assert.Equal(1, changed);   // 第一下没挪不算改动,第二下复位
            Assert.InRange(h.PaneHeight(0) / (h.PaneHeight(0) + h.PaneHeight(1) + h.PaneHeight(2)), 0.597, 0.603);
        });
    }
}
