using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 指针路由器:Feature 发布区域,路由器按命中裁决,手势写进 Pointer.GesturePort。
    /// 两组用例最要紧:没有区域时行为跟原来完全一样(NoRegion_*);有区域时点中才归它,没点中照常平移。
    /// 鼠标走真实输入管线(<see cref="RawMouse"/>),帧用 RunFrameNow 手动驱动;不跟别的测试类并行。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PointerRouterTests
    {
        internal sealed class Schema : ChartReactiveSchema
        {
            public CandlePorts Ports { get; } = new();
            public DataPort<RealRange> YRange { get; } = new("T_YRange");
            public DataPort<PointerHitState?> Hit { get; } = new("T_Hit");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public bool RequireClickToActivate { get; init; }
            public bool DismissOnEscape { get; init; }
            public BoxZoomOptions? BoxZoom { get; init; }
            public Action<InteractionBuilder, Schema>? Extra { get; init; }

            protected override void DefineDataFlow(ChartCell chart) => Trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                canvas
                    .Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge,
                        defaultVisibleCount: 30, overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard))
                    .Series(s => s.AddCandle(YRange, Ports))
                    .Interactions(i =>
                    {
                        i.EnableStandardPointer(Hit, ChartInteractionMode.All,
                            boxZoomConfig: BoxZoom, requireClickToActivate: RequireClickToActivate, dismissOnEscape: DismissOnEscape,
                            // 速度采样窗口放宽:CI 上一帧可能很慢,默认 60ms 窗口里可能一个采样都不剩,惯性就不启动
                            inertiaPanConfig: new InertiaPanOptions { VelocityWindowMs = 2000 });
                        Extra?.Invoke(i, this);
                    });
            }
        }

        internal sealed class Rig
        {
            private const int Length = 100;
            public readonly ChartCell Cell = new();
            public readonly Schema Schema;
            public readonly DataBlackboard Board = new();
            public readonly RawMouse Mouse;
            public readonly ChartInteractionFeature Interaction;
            public readonly PointerRouterFeature Router;
            private readonly ViewportPorts _viewport;
            private readonly Window _window;

            public Rig(bool requireClickToActivate = false, BoxZoomOptions? boxZoom = null, bool dismissOnEscape = false,
                Action<InteractionBuilder, Schema>? extra = null, double left = -10000)
            {
                Schema = new Schema { RequireClickToActivate = requireClickToActivate, BoxZoom = boxZoom, DismissOnEscape = dismissOnEscape, Extra = extra };
                Cell.Template = Schema;
                Cell.Width = 400; Cell.Height = 300;
                _window = new Window
                {
                    Content = Cell, SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                    Left = left, Top = 0,
                };
                _window.Show();
                _window.UpdateLayout();
                _viewport = ViewportPorts.RequireAttached(Cell);
                Interaction = Schema.ListFeatures().OfType<ChartInteractionFeature>().Single();
                Router = Schema.ListFeatures().OfType<PointerRouterFeature>().Single();

                Seed(Board, Schema, _viewport);
                Schema.Trigger.Push(Board);
                Schema.InvalidateEnvironment();
                Frame();
                Frame();   // 第二帧让视口钳制落定:否则第一次拖动的位移被首屏钳制吃掉,跟"先加 Feature 再拖"的对照组差一步
                Mouse = new RawMouse(_window);
                Initial = ActiveRange;
            }

            /// <summary>100 根 K 线 + Y 量程 80–120。</summary>
            public static void Seed(DataBlackboard board, Schema schema, ViewportPorts viewport)
            {
                var time = new DateTime[Length];
                double[] open = new double[Length], high = new double[Length], low = new double[Length], close = new double[Length];
                for (int i = 0; i < Length; i++)
                {
                    time[i] = new DateTime(2026, 1, 1).AddMinutes(i);
                    open[i] = 100 + i % 7; close[i] = 101 + i % 5; high[i] = 110; low[i] = 95;
                }
                using (board.BeginTransaction())
                {
                    board.ForceWrite(schema.Ports.Time, new ReadOnlyMemory<DateTime>(time));
                    board.ForceWrite(schema.Ports.Open, new ReadOnlyMemory<double>(open));
                    board.ForceWrite(schema.Ports.High, new ReadOnlyMemory<double>(high));
                    board.ForceWrite(schema.Ports.Low, new ReadOnlyMemory<double>(low));
                    board.ForceWrite(schema.Ports.Close, new ReadOnlyMemory<double>(close));
                    board.WriteIfChanged(viewport.LogicalLength, Length);
                    board.WriteIfChanged(schema.YRange, new RealRange(80, 120));
                }
            }

            public RealRange Initial { get; }
            public UIElement Canvas => Cell.DrawingCanvas;
            public ViewportPorts Viewport => _viewport;

            public void Frame() => Cell.RunFrameNow(PlotMode.Sync);
            public void Close() => _window.Close();

            public T Read<T>(DataPort<T> port) { using (Board.AcquireReadLock()) return Board.Read(port); }
            public void Write<T>(DataPort<T> port, T value) { using (Board.AcquireWriteLock()) Board.WriteIfChanged(port, value); }

            public RealRange ActiveRange => Read(_viewport.ActiveRange);
            public PointerHitState? Hit => Read(Schema.Hit);
            public PointerGesture LastGesture => Read(Router.Ports.GesturePort);

            public bool InertiaRunning
            {
                get
                {
                    var runner = typeof(ChartInteractionFeature).GetField("_inertia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Interaction)!;
                    return (bool)runner.GetType().GetProperty("IsRunning")!.GetValue(runner)!;
                }
            }

            /// <summary>加一个 Feature 并跑一帧,让它的区域生效。</summary>
            public T Add<T>(T feature) where T : Feature
            {
                Schema.Transact(c => c.Add(feature));
                Frame();
                Frame();
                return feature;
            }

            public float PriceToScreenY(double price)
                => CoordinateExtensions.ProjectValueToScreen(Cell.GetPlotAreaForTest(), Read(Schema.YRange), ScaleStrategyTrait.CandleMode, price);

            public void Move(double x, double y) { Mouse.Move(Canvas, new Point(x, y)); Frame(); }

            public void Drag(double fromX, double fromY, double toX, double toY, int steps = 6, int sleepMs = 0)
            {
                Mouse.Move(Canvas, new Point(fromX, fromY));
                Mouse.Down(Canvas, new Point(fromX, fromY));
                Frame();
                for (int i = 1; i <= steps; i++)
                {
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                    Mouse.Move(Canvas, new Point(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps));
                    Frame();
                }
                Mouse.Up(Canvas, new Point(toX, toY));
                Frame();
            }

            public void Drag(double fromX, double toX, double y = 150) => Drag(fromX, y, toX, y);

            public void Click(double x, double y)
            {
                Mouse.Move(Canvas, new Point(x, y));
                Mouse.Down(Canvas, new Point(x, y));
                Mouse.Up(Canvas, new Point(x, y));
                Frame();
            }

            public void Escape()
            {
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Cell)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Cell.RaiseEvent(e);
                if (!e.Handled)
                {
                    // 跟真实键盘一样:Preview 没人处理才走冒泡
                    var bubble = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Cell)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                    Cell.RaiseEvent(bubble);
                }
                Frame();
            }

            public void FlingRight() => Drag(100, 150, 220, 150, steps: 6, sleepMs: 8);
        }

        /// <summary>测试用交互 Feature:发布给定区域,记录收到的手势。</summary>
        internal sealed class Probe : ChartFeature
        {
            public PointerRegion[] Regions = Array.Empty<PointerRegion>();
            public ChartLayerType? Layer { get; init; }
            public readonly DataPort<int> Rev = new("Probe_Rev");
            public readonly List<PointerGesture> Log = new();
            public Action<DataBlackboard, PointerGesture, Action<Action>>? OnGesture;

            public override FeaturePhase Phase => FeaturePhase.Interaction;

            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
            {
                if (Layer is ChartLayerType level) AttachLayer(new DummyLayer { Level = level });
                flow.WatchPointer(this, (b, g, later) => { Log.Add(g); OnGesture?.Invoke(b, g, later); });
            }

            protected override void OnProject(FeatureContext ctx)
            {
                ctx.UsePort(Rev);
                ctx.Pointer().Publish(Regions);
            }

            public void Set(Rig rig, params PointerRegion[] regions)
            {
                Regions = regions;
                rig.Write(Rev, rig.Read(Rev) + 1);
                rig.Frame();
            }

            public PointerPhase[] Phases => Log.Select(g => g.Phase).ToArray();
        }

        internal sealed class DummyLayer : ChartLayer
        {
            protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget) { }
        }

        private static PointerRegion Line(float y, PointerAccepts accepts = PointerAccepts.Drag, int id = 0, int priority = PointerPriority.Object)
            => new PointerRegion { Id = id, Shape = PointerShape.HLine(y, 0, 1000), Accepts = accepts, Priority = priority, Cursor = PointerCursor.SizeNS };

        private static PointerRegion Box(float x, float y, float w, float h, PointerAccepts accepts = PointerAccepts.Drag, int id = 0)
            => new PointerRegion { Id = id, Shape = PointerShape.Rect(new HevoRect(x, y, w, h)), Accepts = accepts, Cursor = PointerCursor.Hand };

        private static void AssertSameRange(RealRange expected, RealRange actual)
        {
            Assert.Equal(expected.Min, actual.Min, 6);
            Assert.Equal(expected.Max, actual.Max, 6);
        }

        // ================= 没有区域:行为不变 =================

        [Fact]
        public void NoRegion_Drag_Pans() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Drag(100, 220);
            var after = rig.ActiveRange;
            bool captured = System.Windows.Input.Mouse.Captured != null;
            rig.Close();
            Assert.True(after.Min < rig.Initial.Min, $"向右拖应看更早的数据: {rig.Initial} → {after}");
            Assert.False(captured);
        });

        [Fact]
        public void NoRegion_Press_CapturesMouse_AndClearsHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Move(200, 150);
            var hitBefore = rig.Hit;
            rig.Mouse.Down(rig.Canvas, new Point(200, 150));
            var captured = System.Windows.Input.Mouse.Captured;
            var hitDuring = rig.Hit;
            rig.Mouse.Up(rig.Canvas, new Point(200, 150));
            rig.Close();
            Assert.NotNull(hitBefore);
            Assert.Same(rig.Cell, captured);
            Assert.Null(hitDuring);
        });

        [Fact]
        public void NoRegion_BoxZoom_Zooms() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(boxZoom: new BoxZoomOptions { ActivationModifier = ModifierKeys.None });
            rig.Drag(100, 250);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(after.Span < rig.Initial.Span);
        });

        [Fact]
        public void NoRegion_DoubleClick_ResetsViewport() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Drag(100, 220);
            var panned = rig.ActiveRange;
            rig.Click(300, 100);
            rig.Click(300, 100);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.NotEqual(rig.Initial.Min, panned.Min);
            AssertSameRange(rig.Initial, after);
        });

        [Fact]
        public void NoRegion_Fling_StartsInertia_NextPressStopsIt() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.FlingRight();
            bool runningAfterFling = rig.InertiaRunning;
            rig.Mouse.Down(rig.Canvas, new Point(300, 150));
            bool runningAfterPress = rig.InertiaRunning;
            rig.Mouse.Up(rig.Canvas, new Point(300, 150));
            rig.Close();
            Assert.True(runningAfterFling);
            Assert.False(runningAfterPress);
        });

        [Fact]
        public void NoRegion_RequireClickToActivate_PressArmsHover() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(requireClickToActivate: true);
            rig.Move(200, 150);
            var hitBefore = rig.Hit;
            rig.Click(200, 150);
            rig.Move(210, 150);
            var hitAfter = rig.Hit;
            rig.Close();
            Assert.Null(hitBefore);
            Assert.NotNull(hitAfter);
        });

        [Fact]
        public void NoRegion_DismissOnEscape_HidesHover() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(dismissOnEscape: true);
            rig.Move(200, 150);
            var before = rig.Hit;
            rig.Escape();
            var after = rig.Hit;
            rig.Close();
            Assert.NotNull(before);
            Assert.Null(after);
        });

        /// <summary>没有区域:路由器不标 Handled、不改光标、不写手势端口。</summary>
        [Fact]
        public void NoRegion_RouterIsInvisible() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Cell.Cursor = Cursors.Help;   // 业务自己设的光标不被碰
            bool downHandled = false, upHandled = false;
            rig.Cell.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler((_, e) => downHandled = e.Handled), true);
            rig.Cell.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler((_, e) => upHandled = e.Handled), true);
            var portVersion = rig.Board.VersionOf(rig.Router.Ports.GesturePort);
            rig.Drag(100, 220);
            rig.Move(200, 120);
            rig.Escape();
            var after = rig.Board.VersionOf(rig.Router.Ports.GesturePort);
            var cursor = rig.Cell.Cursor;
            rig.Close();
            Assert.False(downHandled);
            Assert.False(upHandled);
            Assert.Equal(portVersion, after);
            Assert.Same(Cursors.Help, cursor);
        });

        // ================= 仲裁 =================

        [Fact]
        public void Region_ClaimsPress_NoPan_NoBoxZoom() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(boxZoom: new BoxZoomOptions { ActivationModifier = ModifierKeys.None });
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Drag(100, 220);
            var after = rig.ActiveRange;
            rig.Close();
            AssertSameRange(rig.Initial, after);
            Assert.Equal(new[] { PointerPhase.HoverEnter, PointerPhase.Press, PointerPhase.Begin }, p.Phases.Take(3));
            Assert.Equal(PointerPhase.End, p.Log.Last(g => g.Phase is not (PointerPhase.HoverMove or PointerPhase.HoverLeave or PointerPhase.HoverEnter)).Phase);
        });

        [Fact]
        public void Region_Miss_PansExactlyLikeNoRegion() => IncrementalTestKit.RunOnSta(() =>
        {
            var plain = new Rig();
            plain.Add(new Probe());
            plain.Drag(100, 220, y: 150);
            var expected = plain.ActiveRange;
            plain.Close();

            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(40));
            rig.Drag(100, 220, y: 150);
            var actual = rig.ActiveRange;
            rig.Close();

            AssertSameRange(expected, actual);
            Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.Press);
        });

        [Fact]
        public void Priority_Wins_RegardlessOfAssemblyOrder() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var high = rig.Add(new Probe());
            var low = rig.Add(new Probe());   // 后装配
            high.Set(rig, Line(150, priority: PointerPriority.Handle));
            low.Set(rig, Line(150));
            rig.Click(200, 150);
            rig.Close();
            Assert.Contains(high.Log, g => g.Phase == PointerPhase.Click);
            Assert.DoesNotContain(low.Log, g => g.Phase == PointerPhase.Press);
        });

        [Fact]
        public void SamePriority_UpperLayerWins() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var top = rig.Add(new Probe { Layer = ChartLayerType.Overlay });
            var bottom = rig.Add(new Probe { Layer = ChartLayerType.Main });   // 后装配,但画在下层
            top.Set(rig, Line(150));
            bottom.Set(rig, Line(150));
            rig.Click(200, 150);
            rig.Close();
            Assert.Contains(top.Log, g => g.Phase == PointerPhase.Click);
            Assert.DoesNotContain(bottom.Log, g => g.Phase == PointerPhase.Press);
        });

        [Fact]
        public void SameLayer_NearerWins() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(148, id: 1), Line(153, id: 2));
            rig.Click(200, 152);
            rig.Close();
            Assert.Equal(2, p.Log.Single(g => g.Phase == PointerPhase.Click).RegionId);
        });

        [Fact]
        public void RequiredModifier_NotHeld_Pans() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, new PointerRegion { Shape = PointerShape.WholePlot(), RequiredModifiers = PointerModifiers.Control });
            rig.Drag(100, 220);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(after.Min < rig.Initial.Min);
            Assert.Empty(p.Log);
        });

        [Fact]
        public void RightClick_OnContextRegion_ContextClick_ElseFallback() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150, PointerAccepts.Drag | PointerAccepts.ContextClick, id: 7), Line(60, PointerAccepts.Drag, id: 8));
            // 没被认领的右键松开会弹 schema 注入的右键菜单;屏幕外窗口上弹菜单会让测试进程崩,这里拦掉
            rig.Cell.ContextMenuOpening += (_, e) => e.Handled = true;
            rig.Mouse.Move(rig.Canvas, new Point(200, 150));
            rig.Mouse.RightDown(rig.Canvas, new Point(200, 150));
            rig.Mouse.RightUp(rig.Canvas, new Point(200, 150));
            rig.Mouse.Move(rig.Canvas, new Point(200, 60));
            rig.Mouse.RightDown(rig.Canvas, new Point(200, 60));     // 区域不收右键:兜底(图表平移按任意键)
            var capturedByPan = System.Windows.Input.Mouse.Captured;
            rig.Mouse.RightUp(rig.Canvas, new Point(200, 60));
            rig.Close();
            var ctx = p.Log.Single(g => g.Phase == PointerPhase.ContextClick);
            Assert.Equal(7, ctx.RegionId);
            Assert.Equal(PointerButton.Right, ctx.Button);
            Assert.DoesNotContain(p.Log, g => g.RegionId == 8 && g.Phase == PointerPhase.Press);
            Assert.Same(rig.Cell, capturedByPan);
        });

        // ================= 过程 =================

        [Fact]
        public void Drag_PhasesAndCoordinates() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Drag(100, 150, 160, 190, steps: 3);
            rig.Close();
            var g = p.Log.Where(x => x.Phase is not (PointerPhase.HoverEnter or PointerPhase.HoverMove or PointerPhase.HoverLeave)).ToArray();
            Assert.Equal(new[] { PointerPhase.Press, PointerPhase.Begin, PointerPhase.Move, PointerPhase.Move, PointerPhase.End }, g.Select(x => x.Phase));
            Assert.All(g, x => { Assert.Equal(100, x.PressPosition.X, 0); Assert.Equal(150, x.PressPosition.Y, 0); });
            Assert.Equal(120, g[1].Position.X, 0);
            Assert.Equal(160, g[4].Position.X, 0);
            Assert.Equal(190, g[4].Position.Y, 0);
            Assert.True(g.Zip(g.Skip(1), (a, b) => b.Sequence > a.Sequence).All(x => x));
        });

        [Fact]
        public void BelowThreshold_IsClick_AndRepeatedClicksBothFire() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150, PointerAccepts.Click | PointerAccepts.Drag));
            rig.Mouse.Move(rig.Canvas, new Point(200, 150));
            rig.Mouse.Down(rig.Canvas, new Point(200, 150));
            rig.Mouse.Move(rig.Canvas, new Point(202, 151));    // 不到 4 DIP
            rig.Mouse.Up(rig.Canvas, new Point(202, 151));
            rig.Frame();
            Thread.Sleep(450);                                   // 超过双击间隔:两次单击
            rig.Click(200, 150);
            rig.Close();
            Assert.Equal(2, p.Log.Count(g => g.Phase == PointerPhase.Click));
            Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.Begin);
        });

        [Fact]
        public void ClickOnlyRegion_DraggedPastThreshold_SupersededByPan_PansFromPressPoint() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150, PointerAccepts.Click));
            rig.Drag(100, 220);
            var after = rig.ActiveRange;
            bool captured = System.Windows.Input.Mouse.Captured != null;
            rig.Close();
            var cancel = p.Log.Single(g => g.Phase == PointerPhase.Cancel);
            Assert.Equal(PointerCancelReason.SupersededByPan, cancel.Reason);
            // 从按下点开始平移:120 DIP / (330 DIP / 30 根) ≈ 10.9 根,按整根棘轮取整 → 至少 10 根一点不丢
            // (不跟"直接拖"的对照组比:测试里图表自己 CaptureMouse 时 WPF 按屏幕外的真光标补发一次移动,对照组会被它带偏)
            Assert.True(rig.Initial.Min - after.Min >= 10, $"{rig.Initial} → {after}");
            Assert.False(captured);
        });

        [Fact]
        public void DoubleClick_OnRegion_FiresAndDoesNotResetViewport() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Drag(100, 220, y: 60);
            var panned = rig.ActiveRange;
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150, PointerAccepts.Click | PointerAccepts.DoubleClick));
            rig.Click(300, 150);
            rig.Click(300, 150);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal(new[] { PointerPhase.Click, PointerPhase.DoubleClick }, p.Phases.Where(x => x is PointerPhase.Click or PointerPhase.DoubleClick));
            AssertSameRange(panned, after);
        });

        [Fact]
        public void OtherButton_DuringDrag_Cancels() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Mouse.Move(rig.Canvas, new Point(150, 150));
            rig.Mouse.RightDown(rig.Canvas, new Point(150, 150));
            var captured = System.Windows.Input.Mouse.Captured;
            rig.Mouse.RightUp(rig.Canvas, new Point(150, 150));
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Frame();
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal(PointerCancelReason.OtherButton, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
            Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.End);
            Assert.Null(captured);
            AssertSameRange(rig.Initial, after);
        });

        // ================= 时序 =================

        [Fact]
        public void Watch_RunsInsideDispatch_AndCanWriteOwnPortWithoutDeadlock() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var own = new DataPort<int>("T_Own");
            var p = rig.Add(new Probe());
            var seen = new List<(PointerPhase Phase, long Seq, long PortSeq)>();
            p.OnGesture = (b, g, later) =>
            {
                b.WriteIfChanged(own, b.Read(own) + 1);
                // 回调执行时端口上就是这一条:同一调用栈,路由器还没往下走
                seen.Add((g.Phase, g.Sequence, b.Read(rig.Router.Ports.GesturePort).Sequence));
            };
            p.Set(rig, Line(150));
            rig.Drag(100, 160);
            int writes = rig.Read(own);
            int calls = seen.Count;
            rig.Close();
            Assert.All(seen, s => Assert.Equal(s.Seq, s.PortSeq));
            Assert.Equal(calls, writes);
        });

        [Fact]
        public void Later_RunsOutsideLock_AfterDispatch() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            bool? lockHeldInLater = null;
            p.OnGesture = (b, g, later) =>
            {
                if (g.Phase == PointerPhase.End)
                    later(() => lockHeldInLater = IsAnyLockHeld(b));
            };
            p.Set(rig, Line(150));
            rig.Drag(100, 160);
            rig.Close();
            Assert.False(lockHeldInLater);
        });

        private static bool IsAnyLockHeld(DataBlackboard b)
        {
            var rw = (ReaderWriterLockSlim)typeof(DataBlackboard).GetField("_rwLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(b)!;
            return rw.IsReadLockHeld || rw.IsWriteLockHeld || rw.IsUpgradeableReadLockHeld;
        }

        [Fact]
        public void ReentrantDispatch_FromCallback_Throws() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            // 回调里同步触发了路由器(这里是同步送一个 Esc)→ 路由器想在分发途中再分发 → 抛
            p.OnGesture = (b, g, later) => { if (g.Phase == PointerPhase.Press) rig.Escape(); };
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            var ex = Record.Exception(() => rig.Mouse.Down(rig.Canvas, new Point(100, 150)));
            rig.Mouse.Up(rig.Canvas, new Point(100, 150));
            p.OnGesture = null;
            rig.Drag(100, 220, y: 60);    // 之后照常工作
            var after = rig.ActiveRange;
            rig.Close();
            Assert.IsType<InvalidOperationException>(ex);
            Assert.True(after.Min < rig.Initial.Min);
        });

        [Fact]
        public void PendingFrame_FlushedBeforeHitTest() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(40));
            // 状态刚变,帧还排着:按下时先跑掉它,命中用新区域
            p.Regions = new[] { Line(150) };
            rig.Write(p.Rev, rig.Read(p.Rev) + 1);
            Assert.True(rig.Cell.HasPendingFrame);
            rig.Mouse.Move(rig.Canvas, new Point(200, 150));
            rig.Mouse.Down(rig.Canvas, new Point(200, 150));
            bool pendingAfter = rig.Cell.HasPendingFrame;
            rig.Mouse.Up(rig.Canvas, new Point(200, 150));
            rig.Close();
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.Click);
            Assert.False(pendingAfter);
        });

        // ================= 取消 =================

        [Fact]
        public void Escape_CancelsDrag() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(dismissOnEscape: true);
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            var hitBefore = rig.Hit;
            rig.Escape();
            var captured = System.Windows.Input.Mouse.Captured;
            var hitAfter = rig.Hit;
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Close();
            Assert.Equal(PointerCancelReason.Escape, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
            Assert.Null(captured);
            Assert.NotNull(hitBefore);
            Assert.NotNull(hitAfter);   // 第一次 Esc 给了手势,没到 DismissOnEscape
        });

        [Fact]
        public void CaptureLost_Cancels() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            System.Windows.Input.Mouse.Capture(null);
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Close();
            Assert.Equal(PointerCancelReason.CaptureLost, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
            Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.End);
        });

        [Fact]
        public void DragOutsideChart_StillReceivesMoveAndEnd() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            rig.Move(150, 600);   // 出了窗口
            rig.Mouse.Up(rig.Canvas, new Point(150, 600));
            rig.Close();
            var end = p.Log.Single(g => g.Phase == PointerPhase.End);
            Assert.Equal(600, end.Position.Y, 0);
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.Move && g.Position.Y > 500);
        });

        [Fact]
        public void TransactRemove_WhileDragging_CancelsOwnerRemoved_ThenPanWorks() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            rig.Schema.Transact(c => c.Remove(p));
            var captured = System.Windows.Input.Mouse.Captured;
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Frame();
            rig.Drag(100, 220);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal(PointerCancelReason.OwnerRemoved, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
            Assert.Null(captured);
            Assert.True(after.Min < rig.Initial.Min);
        });

        [Fact]
        public void SameInstance_RemovedAndReAdded_RepublishesRegions() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe { Regions = new[] { Line(150) } });
            rig.Schema.Transact(c => c.Remove(p));
            rig.Frame();
            rig.Drag(100, 220);
            var pannedWhileRemoved = rig.ActiveRange;
            int logBefore = p.Log.Count;
            rig.Add(p);
            rig.Drag(100, 220);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(pannedWhileRemoved.Min < rig.Initial.Min);
            AssertSameRange(pannedWhileRemoved, after);
            Assert.Contains(p.Log.Skip(logBefore), g => g.Phase == PointerPhase.End);
        });

        [Fact]
        public void ThrowingCallback_Propagates_RouterResets_ThenPanWorks() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            p.OnGesture = (b, g, later) => { if (g.Phase == PointerPhase.Press) throw new InvalidOperationException("boom"); };
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            var ex = Record.Exception(() => rig.Mouse.Down(rig.Canvas, new Point(100, 150)));
            var captured = System.Windows.Input.Mouse.Captured;
            bool active = rig.Router.IsGestureActive;
            rig.Mouse.Move(rig.Canvas, new Point(200, 150));
            rig.Frame();
            var duringFailedPress = rig.ActiveRange;
            rig.Mouse.Up(rig.Canvas, new Point(200, 150));
            p.OnGesture = null;
            rig.Drag(100, 220, y: 60);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal("boom", Assert.IsType<InvalidOperationException>(ex).Message);
            Assert.Null(captured);
            Assert.False(active);
            AssertSameRange(rig.Initial, duringFailedPress);   // 按下抛异常时不在底下开始平移
            Assert.True(after.Min < rig.Initial.Min);
        });

        // ================= 跟图表自带交互的配合 =================

        [Fact]
        public void ClaimedPress_StopsInertia() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            rig.FlingRight();
            bool runningAfterFling = rig.InertiaRunning;
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(300, 150));
            rig.Mouse.Down(rig.Canvas, new Point(300, 150));
            bool runningAfterPress = rig.InertiaRunning;
            rig.Mouse.Up(rig.Canvas, new Point(300, 150));
            rig.Close();
            Assert.True(runningAfterFling);
            Assert.False(runningAfterPress);
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.Press);
        });

        [Fact]
        public void ClaimedPress_ArmsHover_AndProjectsInPlace() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(requireClickToActivate: true);
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150, PointerAccepts.Click));
            rig.Move(200, 150);
            var before = rig.Hit;
            rig.Click(200, 150);
            var afterClick = rig.Hit;
            rig.Close();
            Assert.Null(before);
            Assert.NotNull(afterClick);
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.Click);
        });

        [Fact]
        public void CrosshairFollows_DuringDrag_UnlessHidden() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            var h1 = rig.Hit;
            rig.Move(220, 150);
            var h2 = rig.Hit;
            rig.Mouse.Up(rig.Canvas, new Point(220, 150));
            rig.Frame();

            p.Set(rig, Line(150) with { HideCrosshairWhileActive = true });
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            var hidden = rig.Hit;
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Frame();
            var restored = rig.Hit;
            rig.Close();
            Assert.NotNull(h1);
            Assert.True(h2!.Value.Hit.LogicalIndex > h1!.Value.Hit.LogicalIndex);
            Assert.Null(hidden);
            Assert.NotNull(restored);
        });

        [Fact]
        public void WheelZoom_DuringDrag_StillZooms_GestureContinues() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150));
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            rig.Mouse.Wheel(rig.Canvas, new Point(150, 150), 120);
            rig.Frame();
            var zoomed = rig.ActiveRange;
            rig.Move(170, 150);
            rig.Mouse.Up(rig.Canvas, new Point(170, 150));
            rig.Close();
            Assert.True(zoomed.Span < rig.Initial.Span);
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.End);
            Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.Cancel);
        });

        [Fact]
        public void Modal_FirstEscapeGoesToTool_SecondToDismiss() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(dismissOnEscape: true);
            var p = rig.Add(new Probe());
            p.Set(rig, new PointerRegion { Shape = PointerShape.WholePlot(), Accepts = PointerAccepts.Click, Priority = PointerPriority.Modal, Modal = true });
            p.OnGesture = (b, g, later) => { if (g.Phase == PointerPhase.Escape) p.Regions = Array.Empty<PointerRegion>(); };
            rig.Move(200, 150);
            rig.Escape();
            p.Set(rig, p.Regions);
            var hitAfterFirst = rig.Hit;
            rig.Escape();
            var hitAfterSecond = rig.Hit;
            rig.Close();
            Assert.Single(p.Log, g => g.Phase == PointerPhase.Escape);
            Assert.NotNull(hitAfterFirst);
            Assert.Null(hitAfterSecond);
        });

        [Fact]
        public void EachSchema_HasItsOwnGesturePort() => IncrementalTestKit.RunOnSta(() =>
        {
            var a = new Rig();
            var b = new Rig(left: -12000);
            var pa = a.Add(new Probe());
            var pb = b.Add(new Probe());
            pa.Set(a, Line(150));
            pb.Set(b, Line(150));
            a.Click(200, 150);
            a.Close(); b.Close();
            Assert.NotSame(a.Router.Ports.GesturePort, b.Router.Ports.GesturePort);
            Assert.Same(a.Router.Ports, pa.Pointer);
            Assert.Empty(pb.Log.Where(g => g.Phase == PointerPhase.Click));
        });

        // ================= 光标 =================

        [Fact]
        public void Cursor_Hover_Leave_Drag_AndRemoval() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, Line(150) with { DragCursor = PointerCursor.SizeAll });
            rig.Move(200, 150);
            var hover = rig.Cell.Cursor;
            rig.Move(200, 60);
            var away = rig.Cell.Cursor;
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            var dragging = rig.Cell.Cursor;
            rig.Mouse.Up(rig.Canvas, new Point(150, 150));
            rig.Frame();
            var afterUp = rig.Cell.Cursor;
            p.Set(rig);   // 撤掉区域
            var afterRemoval = rig.Cell.Cursor;
            rig.Close();
            Assert.Same(Cursors.SizeNS, hover);
            Assert.Null(away);
            Assert.Same(Cursors.SizeAll, dragging);
            Assert.Same(Cursors.SizeNS, afterUp);
            Assert.Null(afterRemoval);
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.HoverLeave);
        });
    }

    internal static class ChartCellTestExtensions
    {
        public static HevoRect GetPlotAreaForTest(this ChartCell cell)
            => cell.GetSharedData().Get<PlotAreaTrait>()?.Area ?? HevoRect.Empty;
    }
}
