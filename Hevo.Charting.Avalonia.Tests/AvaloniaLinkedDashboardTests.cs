using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Avalonia.Tests
{
    /// <summary>
    /// Avalonia 联动看板 + 顶部信息栏:三张图(主图 + 两张副图)共享视口和命中,夹具同 Hevo.Charting.Tests 的 LinkedInteractionTests。
    /// 每张图 400×200 竖排,第 i 张的纵向偏移 = i × 200(主图带头部,头部占的高度由 <see cref="PlotTop"/> 量出来)。
    /// </summary>
    public sealed class AvaloniaLinkedDashboardTests
    {
        private sealed class LinkedSchema : ChartReactiveSchema
        {
            public CandlePorts Ports { get; } = new();
            public DataPort<RealRange> YRange { get; } = new("L_YRange");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public DataPort<PointerHitState?> Hit => HitPort;
            public bool WithHeader { get; init; }
            public IHevoBrush? HeaderLabel { get; init; }
            public IHevoBrush? HeaderValue { get; init; }

            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                var close = FieldMeta.Literal("收", HevoColors.Gray, "F2");
                canvas
                    .Environment(env =>
                    {
                        env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge,
                            defaultVisibleCount: 30, overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard);
                        if (WithHeader) env.SetupUniversalHeader(HitPort, HeaderLabel, HeaderValue);
                    })
                    .Series(s => s.AddCandle(YRange, Ports, metas: new CandleMetas(close, close, close, close)))
                    .Interactions(i => i.EnableStandardPointer(HitPort, ChartInteractionMode.All));
            }
        }

        private sealed class Dash
        {
            private const int Length = 100;
            public readonly LinkedChartContext Context = new();
            public readonly AvaloniaLinkedChartDashboard Dashboard;
            public readonly LinkedSchema[] Schemas;
            public readonly DataBlackboard[] Boards = { new(), new(), new() };
            public readonly Window Window;
            public readonly RealRange Initial;

            public Dash(bool resizable = false, IHevoBrush? headerLabel = null, IHevoBrush? headerValue = null)
            {
                Schemas = new[] { new LinkedSchema { WithHeader = true, HeaderLabel = headerLabel, HeaderValue = headerValue }, new LinkedSchema(), new LinkedSchema() };
                Dashboard = new AvaloniaLinkedChartDashboard(Context) { Width = 400, Height = 600, IsPaneResizeEnabled = resizable }
                    .AddMaster(Schemas[0], heightRatio: 1).AddPane(Schemas[1]).AddPane(Schemas[2]);
                // 窗口直接给定尺寸并先把布局跑完:SizeToContent 是 Show 之后才收缩,取坐标时布局还是默认窗口大小
                Window = new Window { Content = Dashboard, Width = 400, Height = 600 };
                Window.Show();
                Dispatcher.UIThread.RunJobs();
                for (int i = 0; i < 3; i++)
                {
                    Seed(Boards[i], Schemas[i], ViewportPorts.RequireAttached(Dashboard.Cells[i]));
                    Schemas[i].Trigger.Push(Boards[i]);
                }
                Frame();
                Frame();
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

            public void Frame()
            {
                foreach (var cell in Dashboard.Cells) cell.RunFrameNow();
            }

            public T Read<T>(int pane, DataPort<T> port) { using (Boards[pane].ReadScope()) return Boards[pane].Read(port); }
            public RealRange ActiveRange(int pane) => Read(pane, Context.SharedViewport.ActiveRange);
            public PointerHitState? Hit(int pane) => Read(pane, Schemas[pane].Hit);

            // 图层画布在窗口里的左上角(主图有头部,画布往下挪)
            public Point PlotTop(int pane) => Dashboard.Cells[pane].PlotSurface.TranslatePoint(default, Window)!.Value;

            public void Move(int pane, double x, double y) { Window.MouseMove(PlotTop(pane) + new Point(x, y)); Frame(); }

            public void Drag(int pane, double fromX, double toX, double y)
            {
                var o = PlotTop(pane);
                Window.MouseMove(o + new Point(fromX, y));
                Window.MouseDown(o + new Point(fromX, y), MouseButton.Left);
                Frame();
                for (int i = 1; i <= 6; i++)
                {
                    Window.MouseMove(o + new Point(fromX + (toX - fromX) * i / 6, y));
                    Frame();
                }
                Window.MouseUp(o + new Point(toX, y), MouseButton.Left);
                Frame();
            }

            public AvaloniaUniversalHeaderFeature Header
                => Schemas[0].ListFeatures().OfType<AvaloniaUniversalHeaderFeature>().Single();
        }

        private static void AssertSameRange(RealRange expected, RealRange actual)
        {
            Assert.Equal(expected.Min, actual.Min, 6);
            Assert.Equal(expected.Max, actual.Max, 6);
        }

        [AvaloniaFact]
        public void Setup_AllCellsShareInitialViewport()
        {
            var d = new Dash();
            Assert.True(d.Initial.IsValid);
            AssertSameRange(d.Initial, d.ActiveRange(1));
            AssertSameRange(d.Initial, d.ActiveRange(2));
            d.Window.Close();
        }

        [AvaloniaFact]
        public void DragOnPane_PansEveryCell()
        {
            var d = new Dash();
            // 视口贴右沿:往右拖看更早的 K 线
            d.Drag(pane: 1, fromX: 100, toX: 300, y: 100);
            var after = d.ActiveRange(0);
            Assert.True(after.Min < d.Initial.Min, $"副图上往右拖应带着主图看更早的 K 线: {d.Initial} → {after}");
            AssertSameRange(after, d.ActiveRange(1));
            AssertSameRange(after, d.ActiveRange(2));
            d.Window.Close();
        }

        [AvaloniaFact]
        public void HoverOnMaster_PublishesSameHitToPanes()
        {
            var d = new Dash();
            d.Move(pane: 0, x: 200, y: 80);
            var hit = d.Hit(0);
            Assert.NotNull(hit);
            Assert.Equal(hit!.Value.Hit.LogicalIndex, d.Hit(1)!.Value.Hit.LogicalIndex);
            Assert.Equal(hit.Value.Hit.LogicalIndex, d.Hit(2)!.Value.Hit.LogicalIndex);
            d.Window.Close();
        }

        [AvaloniaFact]
        public void Header_Brushes_DefaultAndResource()
        {
            var d = new Dash();
            var (label, value) = d.Header.ForegroundsOf(0);
            Assert.Equal(Color.FromRgb(160, 160, 160), ((ISolidColorBrush)label!).Color);
            Assert.Equal(Colors.White, ((ISolidColorBrush)value!).Color);
            d.Window.Close();

            // 宿主传资源画刷:按应用资源取色,资源换值跟着变(换主题)
            var app = Application.Current!;
            app.Resources["Test.Header.Value"] = new SolidColorBrush(Color.FromRgb(1, 2, 3));
            try
            {
                d = new Dash(headerLabel: new HevoSolidBrush(HevoColor.FromRgb(4, 5, 6)), headerValue: new HevoResourceBrush("Test.Header.Value"));
                (label, value) = d.Header.ForegroundsOf(0);
                Assert.Equal(Color.FromRgb(4, 5, 6), ((ISolidColorBrush)label!).Color);
                Assert.Equal(Color.FromRgb(1, 2, 3), ((ISolidColorBrush)value!).Color);
                app.Resources["Test.Header.Value"] = new SolidColorBrush(Color.FromRgb(7, 8, 9));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Color.FromRgb(7, 8, 9), ((ISolidColorBrush)d.Header.ForegroundsOf(0).Value!).Color);
                d.Window.Close();
            }
            finally
            {
                app.Resources.Remove("Test.Header.Value");
            }
        }

        [AvaloniaFact]
        public void Header_ShowsLastBar_ThenHoveredBar()
        {
            var d = new Dash();
            // 主图的头部在图层画布上方,画布被往下挤
            Assert.True(d.PlotTop(0).Y > 0);
            var fields = d.Header.Snapshot().ToList();
            Assert.Equal(4, fields.Count);
            Assert.All(fields, f => Assert.Equal("收", f.Name));
            // 没命中:末根(i = 99,收 = 101 + 99 % 5 = 105)
            Assert.Equal("105.00", fields[3].Value);

            d.Move(pane: 0, x: 200, y: 80);
            int index = d.Hit(0)!.Value.Hit.LogicalIndex;
            d.Frame();
            Assert.Equal((101 + index % 5).ToString("F2"), d.Header.Snapshot().ToList()[3].Value);
            d.Window.Close();
        }

        [AvaloniaFact]
        public void Splitter_DragMovesOnlyNeighbours_AndResetRestoresDefaults()
        {
            var d = new Dash(resizable: true);
            Assert.Equal(2, d.Dashboard.Splitters.Count);
            int changed = 0;
            d.Dashboard.PaneRatiosChanged += (_, _) => changed++;

            Assert.True(d.Dashboard.BeginSplitterDrag(0));
            d.Dashboard.UpdateSplitterDrag(40);
            d.Dashboard.EndSplitterDrag();
            var r = d.Dashboard.GetPaneRatios();
            Assert.True(r[0] > r[1], $"往下拖主图应变高: {string.Join(",", r)}");
            Assert.Equal(1.0 / 3, r[2], 6);
            Assert.Equal(1, changed);

            d.Dashboard.ResetPaneRatios();
            Assert.All(d.Dashboard.GetPaneRatios(), x => Assert.Equal(1.0 / 3, x, 6));
            Assert.False(d.Dashboard.TrySetPaneRatios(new[] { 1.0, 2.0 }));
            Assert.True(d.Dashboard.TrySetPaneRatios(new[] { 2.0, 1.0, 1.0 }));
            Assert.Equal(0.5, d.Dashboard.GetPaneRatios()[0], 6);
            d.Window.Close();
        }

        [AvaloniaFact]
        public void Pane_HasNoViewportManagerOrTooltip()
        {
            var d = new Dash();
            Assert.Contains(d.Schemas[0].ListFeatures(), f => f is ViewportManagerFeature);
            Assert.DoesNotContain(d.Schemas[1].ListFeatures(), f => f is ViewportManagerFeature);
            Assert.DoesNotContain(d.Schemas[1].ListFeatures(), f => f is TooltipWidgetFeature);
            d.Window.Close();
        }
    }
}
