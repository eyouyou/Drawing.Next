using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 联动 dashboard 上的真实输入:主图 / 副图上拖、滚轮、悬停,视口和十字光标经端口镜像同步到每个 cell。
    /// hit 端口用基类的 <c>HitPort</c>(联动时就是 dashboard 共享的 linkedHit),不自建。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class LinkedInteractionTests
    {
        private sealed class LinkedSchema : ChartReactiveSchema
        {
            public CandlePorts Ports { get; } = new();
            public DataPort<RealRange> YRange { get; } = new("L_YRange");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public DataPort<PointerHitState?> Hit => HitPort;

            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                canvas
                    .Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge,
                        defaultVisibleCount: 30, overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard))
                    .Series(s => s.AddCandle(YRange, Ports))
                    .Interactions(i => i.EnableStandardPointer(HitPort, ChartInteractionMode.All,
                        inertiaPanConfig: new InertiaPanOptions { VelocityWindowMs = 2000 }));
            }
        }

        private sealed class Dash
        {
            private const int Length = 100;
            public readonly LinkedChartContext Context = new();
            public readonly LinkedChartDashboard Dashboard;
            public readonly LinkedSchema[] Schemas = { new(), new(), new() };
            public readonly DataBlackboard[] Boards = { new(), new(), new() };
            public readonly Window Window;
            public readonly RawMouse Mouse;
            public readonly RealRange Initial;

            public Dash()
            {
                Dashboard = new LinkedChartDashboard(Context) { Width = 400, Height = 600 }
                    .AddMaster(Schemas[0]).AddPane(Schemas[1]).AddPane(Schemas[2]);
                Window = new Window
                {
                    Content = Dashboard, SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = 0,
                };
                Window.Show();
                Window.UpdateLayout();
                for (int i = 0; i < 3; i++)
                {
                    Seed(Boards[i], Schemas[i], ViewportPorts.RequireAttached(Dashboard.Cells[i]));
                    Schemas[i].Trigger.Push(Boards[i]);
                }
                Frame();
                Frame();
                Mouse = new RawMouse(Window);
                Initial = ActiveRange(0);
            }

            private static void Seed(DataBlackboard board, LinkedSchema schema, ViewportPorts viewport)
            {
                var time = new DateTime[Length];
                double[] open = new double[Length], high = new double[Length], low = new double[Length], close = new double[Length];
                for (int i = 0; i < Length; i++)
                {
                    time[i] = new DateTime(2026, 1, 1).AddMinutes(i);
                    open[i] = 100 + i % 7; close[i] = 101 + i % 5; high[i] = 110; low[i] = 95;
                }
                using (board.WriteScope())
                {
                    board.ForceWrite(schema.Ports.Time, new ReadOnlyMemory<DateTime>(time));
                    board.ForceWrite(schema.Ports.Open, new ReadOnlyMemory<double>(open));
                    board.ForceWrite(schema.Ports.High, new ReadOnlyMemory<double>(high));
                    board.ForceWrite(schema.Ports.Low, new ReadOnlyMemory<double>(low));
                    board.ForceWrite(schema.Ports.Close, new ReadOnlyMemory<double>(close));
                    board.Write(viewport.LogicalLength, Length);
                    board.Write(schema.YRange, new RealRange(80, 120));
                }
            }

            public UIElement Canvas(int pane) => Dashboard.Cells[pane].DrawingCanvas;

            public void Frame()
            {
                foreach (var cell in Dashboard.Cells) cell.RunFrameNow(PlotMode.Sync);
            }

            public T Read<T>(int pane, DataPort<T> port) { using (Boards[pane].ReadScope()) return Boards[pane].Read(port); }
            public RealRange ActiveRange(int pane) => Read(pane, Context.SharedViewport.ActiveRange);
            public PointerHitState? Hit(int pane) => Read(pane, Schemas[pane].Hit);

            public void Move(int pane, double x, double y) { Mouse.Move(Canvas(pane), new Point(x, y)); Frame(); }

            public void Drag(int pane, double fromX, double toX, double y)
            {
                var c = Canvas(pane);
                Mouse.Move(c, new Point(fromX, y));
                Mouse.Down(c, new Point(fromX, y));
                Frame();
                for (int i = 1; i <= 6; i++)
                {
                    Mouse.Move(c, new Point(fromX + (toX - fromX) * i / 6, y));
                    Frame();
                }
                Mouse.Up(c, new Point(toX, y));
                Frame();
            }
        }

        private static void WithDash(Action<Dash> body) => IncrementalTestKit.RunOnSta(() =>
        {
            var d = new Dash();
            try { body(d); }
            finally { d.Window.Close(); }
        });

        private static void AssertSameRange(RealRange expected, RealRange actual)
        {
            Assert.Equal(expected.Min, actual.Min, 6);
            Assert.Equal(expected.Max, actual.Max, 6);
        }

        [Fact]
        public void Setup_AllCellsShareInitialViewport() => WithDash(d =>
        {
            Assert.True(d.Initial.IsValid);
            AssertSameRange(d.Initial, d.ActiveRange(1));
            AssertSameRange(d.Initial, d.ActiveRange(2));
        });

        [Fact]
        public void DragOnMaster_PansEveryPaneIdentically() => WithDash(d =>
        {
            d.Drag(0, 100, 220, 200);
            var master = d.ActiveRange(0);
            Assert.True(master.Min < d.Initial.Min, $"{d.Initial} → {master}");
            AssertSameRange(master, d.ActiveRange(1));
            AssertSameRange(master, d.ActiveRange(2));
        });

        /// <summary>副图没有视口管家:它写的 UserRange 镜像到主图,由主图钳制后再镜像回来。</summary>
        [Fact]
        public void DragOnPane_PansMasterAndOtherPane() => WithDash(d =>
        {
            d.Drag(1, 100, 220, 60);
            var pane = d.ActiveRange(1);
            Assert.True(pane.Min < d.Initial.Min, $"{d.Initial} → {pane}");
            AssertSameRange(pane, d.ActiveRange(0));
            AssertSameRange(pane, d.ActiveRange(2));
        });

        [Fact]
        public void WheelOnMaster_ZoomsEveryPane() => WithDash(d =>
        {
            d.Mouse.Move(d.Canvas(0), new Point(200, 200));
            d.Mouse.Wheel(d.Canvas(0), new Point(200, 200), 240);
            d.Frame();
            var master = d.ActiveRange(0);
            Assert.True(master.Span < d.Initial.Span, $"{d.Initial} → {master}");
            AssertSameRange(master, d.ActiveRange(1));
            AssertSameRange(master, d.ActiveRange(2));
        });

        [Fact]
        public void HoverOnPane_CrosshairOnEveryCell_SameIndex() => WithDash(d =>
        {
            d.Move(1, 250, 60);
            var onPane = d.Hit(1);
            var onMaster = d.Hit(0);
            var onOther = d.Hit(2);
            Assert.NotNull(onPane);
            Assert.NotNull(onMaster);
            Assert.NotNull(onOther);
            Assert.Equal(onPane!.Value.Hit.LogicalIndex, onMaster!.Value.Hit.LogicalIndex);
            Assert.Equal(onPane.Value.Hit.LogicalIndex, onOther!.Value.Hit.LogicalIndex);
            // 镜像的是同一个 hit,判断"是不是我这张图产生的"要看各自的 IsPointerOver
            var paneRouter = d.Schemas[1].ListFeatures().OfType<PointerRouterFeature>().Single();
            var masterRouter = d.Schemas[0].ListFeatures().OfType<PointerRouterFeature>().Single();
            Assert.True(d.Read(1, paneRouter.Ports.IsPointerOver));
            Assert.False(d.Read(0, masterRouter.Ports.IsPointerOver));
        });

        [Fact]
        public void HoverMovesAcrossCells_IndexFollowsPointerX() => WithDash(d =>
        {
            d.Move(0, 150, 200);
            int left = d.Hit(2)!.Value.Hit.LogicalIndex;
            d.Move(2, 300, 60);
            int right = d.Hit(0)!.Value.Hit.LogicalIndex;
            Assert.True(right > left, $"{left} → {right}");
            Assert.Equal(right, d.Hit(1)!.Value.Hit.LogicalIndex);
        });
    }
}
