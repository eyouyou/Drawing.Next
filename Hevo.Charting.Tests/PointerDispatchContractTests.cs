using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Probe = Hevo.Charting.Tests.PointerRouterTests.Probe;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;
using Schema = Hevo.Charting.Tests.PointerRouterTests.Schema;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 手势分发对黑板 Watch 机制的依赖,单独成组:黑板收敛 A1(Watch 收进黑板、改分发顺序与重入)合入后必须重跑这四条。
    /// ① 回调与路由器写端口同一调用栈;② 回调里写端口触发别的 Watch,写完立刻读到派生值;
    /// ③ Transact 热插立即能拖、拖到一半热拔收到 Cancel(OwnerRemoved);④ 联动副图收不到主图的手势。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PointerDispatchContractTests
    {
        private static PointerRegion Line(float y) => new PointerRegion { Shape = PointerShape.HLine(y, 0, 1000) };

        [Fact]
        public void A1Rerun_1_CallbackRunsOnRouterStack() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            var mismatches = new List<string>();
            bool sawActiveDuringPress = false;
            p.OnGesture = (b, g, later) =>
            {
                // 端口上就是这一条,且路由器状态还停在这一步:回调跑完路由器才往下走
                var onPort = b.Read(rig.Router.Ports.GesturePort);
                if (onPort.Sequence != g.Sequence) mismatches.Add($"{g} vs {onPort}");
                if (g.Phase == PointerPhase.Press) sawActiveDuringPress = rig.Router.IsGestureActive;
            };
            p.Set(rig, Line(150));
            rig.Drag(100, 150, 180, 150);
            rig.Close();
            Assert.Empty(mismatches);
            Assert.True(sawActiveDuringPress);
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.End);
        });

        [Fact]
        public void A1Rerun_2_WriteInCallback_TriggersOtherWatch_DerivedValueReadableImmediately() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var input = new DataPort<double>("T_In");
            var derived = new DataPort<double>("T_Derived");
            rig.Add(new DoublerFeature { Input = input, Output = derived });
            var p = rig.Add(new Probe());
            var reads = new List<(double In, double Derived)>();
            p.OnGesture = (b, g, later) =>
            {
                if (g.Phase != PointerPhase.Move && g.Phase != PointerPhase.Begin) return;
                b.WriteIfChanged(input, g.Position.X);
                reads.Add((g.Position.X, b.Read(derived)));   // 深度优先:别的 Watch 已经算完
            };
            p.Set(rig, Line(150));
            rig.Drag(100, 150, 200, 150);
            rig.Close();
            Assert.NotEmpty(reads);
            Assert.All(reads, r => Assert.Equal(r.In * 2, r.Derived));
        });

        private sealed class DoublerFeature : ChartFeature
        {
            public DataPort<double> Input { get; init; } = null!;
            public DataPort<double> Output { get; init; } = null!;
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
                => flow.Watch(new object[] { Input }, b => { using (b.AcquireWriteLock()) b.WriteIfChanged(Output, b.Read(Input) * 2); });
            protected override void OnProject(FeatureContext ctx) { }
        }

        [Fact]
        public void A1Rerun_3_HotAdd_DragsImmediately_HotRemove_CancelsOwnerRemoved() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = new Probe { Regions = new[] { Line(150) } };
            rig.Schema.Transact(c => c.Add(p));
            rig.Frame();                                   // 首帧发布区域
            rig.Drag(100, 150, 180, 150);
            bool gotEnd = p.Log.Any(g => g.Phase == PointerPhase.End);
            var afterFirst = rig.ActiveRange;

            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            rig.Schema.Transact(c => c.Remove(p));
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Frame();
            rig.Close();
            Assert.True(gotEnd);
            Assert.Equal(rig.Initial.Min, afterFirst.Min, 6);
            Assert.Equal(PointerCancelReason.OwnerRemoved, p.Log.Last(g => g.Phase == PointerPhase.Cancel).Reason);
        });

        [Fact]
        public void A1Rerun_4_LinkedPane_DoesNotReceiveMasterGestures() => IncrementalTestKit.RunOnSta(() =>
        {
            var context = new LinkedChartContext();
            var dashboard = new LinkedChartDashboard(context) { Width = 400, Height = 600 };
            var master = new Schema();
            var pane = new Schema();
            dashboard.AddMaster(master).AddPane(pane);
            var window = new Window
            {
                Content = dashboard, SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = 0,
            };
            window.Show();
            window.UpdateLayout();
            var masterCell = dashboard.Cells[0];
            var paneCell = dashboard.Cells[1];
            var masterBoard = new DataBlackboard();
            var paneBoard = new DataBlackboard();
            Rig.Seed(masterBoard, master, ViewportPorts.RequireAttached(masterCell));
            Rig.Seed(paneBoard, pane, ViewportPorts.RequireAttached(paneCell));
            master.Trigger.Push(masterBoard);
            pane.Trigger.Push(paneBoard);

            var pm = new Probe { Regions = new[] { Line(60) } };
            var pp = new Probe { Regions = new[] { Line(60) } };
            master.Transact(c => c.Add(pm));
            pane.Transact(c => c.Add(pp));
            for (int i = 0; i < 2; i++) { masterCell.RunFrameNow(PlotMode.Sync); paneCell.RunFrameNow(PlotMode.Sync); }

            var panePort = pane.ListFeatures().OfType<PointerRouterFeature>().Single().Ports.GesturePort;
            var paneVersion = paneBoard.GetVersion(panePort);
            var mouse = new RawMouse(window);
            var canvas = masterCell.DrawingCanvas;
            mouse.Move(canvas, new Point(100, 60));
            mouse.Down(canvas, new Point(100, 60));
            mouse.Move(canvas, new Point(160, 60));
            masterCell.RunFrameNow(PlotMode.Sync); paneCell.RunFrameNow(PlotMode.Sync);
            mouse.Up(canvas, new Point(160, 60));
            var paneAfter = paneBoard.GetVersion(panePort);
            window.Close();

            Assert.Contains(pm.Log, g => g.Phase == PointerPhase.End);
            Assert.Empty(pp.Log);
            Assert.Equal(paneVersion, paneAfter);
            Assert.NotSame(pm.Pointer.GesturePort, pp.Pointer.GesturePort);
        });
    }
}
