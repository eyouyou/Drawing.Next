using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 交互回归探针:用真实的系统输入(SendInput 注入鼠标 / 键盘)把内置交互和指针区域在一张真图上走一遍,
    /// 断言黑板上看得到的状态(视口、十字光标命中、可拖线价格、测量 / 框选状态、光标形状),每项留一张截图。
    /// <para>
    /// 用法(Windows,本机直接显示,不要远程桌面;运行期间别碰鼠标键盘,约 20 秒;结束时光标放回原处):
    /// <c>dotnet run -c Release --project Hevo.Charting.Benchmarks -- --interaction-probe [--out=interaction-probe] [--only=1,4,10] [--bars=2000]</c>
    /// </para>
    /// <para>
    /// 跟单元测试的区别:单元测试直接调路由器 / 行为链;这里走 Windows 输入队列 → WPF 输入管线 → WpfPointerAdapter,
    /// 能抓到只在真实输入下出现的问题(鼠标捕获时序、焦点、系统双击判定、修饰键状态、WPF 自己的方向键导航)。
    /// 键盘输入只发给探针自己的窗口:注入按键前确认它是前台窗口,不是就判失败,不往别的程序里打字。
    /// </para>
    /// </summary>
    internal static class InteractionProbe
    {
        private sealed record CheckResult(int Id, string Name, bool? Pass, string Expected, string Observed, string? Screenshot);

        public static int Run(string[] args)
        {
            string outDir = ProbeOptions.Get(args, "--out=") ?? "interaction-probe";
            int bars = int.Parse(ProbeOptions.Get(args, "--bars=") ?? "2000", CultureInfo.InvariantCulture);
            var only = (ProbeOptions.Get(args, "--only=") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToHashSet();

            int exit = 0;
            var thread = new Thread(() =>
            {
                try { exit = RunOnUiThread(outDir, bars, only); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 99; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return exit;
        }

        private static int RunOnUiThread(string outDir, int bars, HashSet<int> only)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            ProbeEnvironment.Standardize();
            var env = ProbeEnvironment.Capture();
            Console.WriteLine(env.Describe());
            if (env.RemoteSession)
                Console.WriteLine("[interaction-probe] ⚠ 远程桌面会话:注入的输入走远程会话,结果仍有效,但截图是远程桌面的画面。");
            Console.WriteLine("[interaction-probe] 运行期间请不要移动鼠标、不要按键。");
            Directory.CreateDirectory(outDir);

            var rig = InteractionRig.Create(bars);
            if (rig == null) return 99;
            Native.GetCursorPos(out var savedCursor);

            var results = new List<CheckResult>();
            string? abort = null;
            var done = new DispatcherFrame();
            var worker = new Thread(() =>
            {
                try
                {
                    var driver = new Driver(rig, outDir);
                    abort = driver.Preflight();
                    if (abort == null) driver.RunAll(only, results);
                }
                catch (ProbeAbort ex) { abort = ex.Message; }
                catch (Exception ex) { abort = ex.ToString(); }
                finally
                {
                    Input.ReleaseAll();
                    Native.SetCursorPos(savedCursor.X, savedCursor.Y);
                    rig.Window.Dispatcher.InvokeAsync(() => done.Continue = false);
                }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA); // 截图用 WPF 的 PNG 编码器(WIC)
            worker.Start();
            Dispatcher.PushFrame(done);
            rig.Window.Close();
            RenderProbe.Pump(50);

            return Report(env, results, abort, outDir);
        }

        private static int Report(ProbeEnvironment env, List<CheckResult> results, string? abort, string outDir)
        {
            var md = new StringBuilder();
            md.AppendLine("## interaction-probe 结果");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(env.Describe());
            md.AppendLine("```");
            md.AppendLine();
            if (abort != null)
            {
                md.AppendLine($"**中止**:{abort}");
                md.AppendLine();
            }
            md.AppendLine("| # | 场景 | 结果 | 期望 | 观测 | 截图 |");
            md.AppendLine("|---:|---|---|---|---|---|");
            foreach (var r in results)
            {
                string verdict = r.Pass switch { true => "PASS", false => "**FAIL**", null => "INFO" };
                md.AppendLine($"| {r.Id} | {r.Name} | {verdict} | {Esc(r.Expected)} | {Esc(r.Observed)} | {(r.Screenshot != null ? Path.GetFileName(r.Screenshot) : "")} |");
            }
            int failed = results.Count(r => r.Pass == false);
            md.AppendLine();
            md.AppendLine($"PASS {results.Count(r => r.Pass == true)} / FAIL {failed} / INFO {results.Count(r => r.Pass == null)}");
            Console.WriteLine(md.ToString());
            var mdPath = Path.Combine(outDir, "interaction-probe.md");
            File.WriteAllText(mdPath, md.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"[interaction-probe] 报告 {Path.GetFullPath(mdPath)}");
            if (abort != null) return 2;
            return failed > 0 ? 1 : 0;

            static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", "<br>");
        }

        private sealed class ProbeAbort : Exception
        {
            public ProbeAbort(string message) : base(message) { }
        }

        // ==========================================
        // 图表
        // ==========================================

        /// <summary>
        /// 探针窗口:K 线主图 + 成交量副图(LinkedChartDashboard),主图装 EnableStandard(单击武装、Esc 解除)、
        /// 可拖价格线(初值 = 最后收盘价)、Ctrl 测量,外加记录手势端口的探子和一个按需标 Handled 的旧式业务处理器。
        /// </summary>
        private sealed class InteractionRig
        {
            public required Window Window { get; init; }
            public required FrameworkElement Host { get; init; }
            public required ChartCell Cell { get; init; }
            public required InteractionProbeSchema Schema { get; init; }
            public required DataBlackboard Board { get; init; }
            public required double InitialPrice { get; init; }
            public RealRange InitialRange { get; set; }
            public ChartInteractionFeature Interaction { get; private set; } = null!;
            public DraggableLineFeature Line { get; private set; } = null!;
            public MeasureFeature Measure { get; private set; } = null!;
            public int MouseEvents;
            /// <summary>true 时每帧把惯性执行器的状态记进 <see cref="Arrivals"/>("r(v=速度)" = 在跑,"f" = 没跑)。</summary>
            public volatile bool RecordArrivals;
            public readonly List<(string Kind, double Ms)> Arrivals = new();
            public InertiaPanRunner Inertia { get; private set; } = null!;

            public static InteractionRig? Create(int bars)
            {
                var data = ProbeData.Generate(bars);
                var schema = new InteractionProbeSchema();
                var volume = new ProbeVolumeSchema();
                var dashboard = new Hevo.Charting.Linked.LinkedChartDashboard(new Hevo.Charting.Linked.LinkedChartContext())
                    .AddMaster(schema, heightRatio: 3)
                    .AddPane(volume, heightRatio: 1);
                dashboard.Width = 1280;
                dashboard.Height = 720;
                var host = new System.Windows.Controls.Grid { Width = 1280, Height = 720 };
                host.Children.Add(dashboard);
                var window = new Window
                {
                    Title = "Hevo interaction probe",
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    Content = host,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                };
                window.Show();
                RenderProbe.Pump(300);

                var board = new DataBlackboard();
                var volumeBoard = new DataBlackboard();
                var all = new RealRange(0, bars);
                double lastClose = data.Close[bars - 1];
                var p = schema.Ports;
                using (board.WriteScope())
                {
                    board.Write(p.Time, new ReadOnlyMemory<DateTime>(data.Time));
                    board.Write(p.Open, new ReadOnlyMemory<double>(data.Open));
                    board.Write(p.High, new ReadOnlyMemory<double>(data.High));
                    board.Write(p.Low, new ReadOnlyMemory<double>(data.Low));
                    board.Write(p.Close, new ReadOnlyMemory<double>(data.Close));
                    board.Write(schema.SmaPort, new ReadOnlyMemory<double>(data.Sma));
                    board.Write(schema.PricePort, lastClose);
                    board.Write(schema.Viewport.LogicalLength, bars);
                }
                schema.Trigger.Push(board);
                schema.InvalidateEnvironment();
                using (volumeBoard.WriteScope())
                {
                    volumeBoard.Write(volume.TimePort, new ReadOnlyMemory<DateTime>(data.Time));
                    volumeBoard.Write(volume.VolumePort, new ReadOnlyMemory<double>(data.Volume));
                }
                volume.Trigger.Push(volumeBoard);
                volume.InvalidateEnvironment();
                RenderProbe.Pump(300);
                var cell = dashboard.Cells[0];
                cell.RunFrameNow(PlotMode.Sync);
                dashboard.Cells[1].RunFrameNow(PlotMode.Sync);

                using (var ctx = cell.CreateContext())
                {
                    if (ctx.GetPlotArea().IsEmpty || cell.ActiveLayers.Count == 0)
                    {
                        Console.Error.WriteLine("[interaction-probe] 图表没有完成装配(PlotArea 为空或没有图层)。");
                        window.Close();
                        return null;
                    }
                }

                var rig = new InteractionRig
                {
                    Window = window, Host = host, Cell = cell, Schema = schema, Board = board, InitialPrice = lastClose,
                };
                var features = schema.ListFeatures();
                rig.Interaction = features.OfType<ChartInteractionFeature>().Single();
                rig.Line = features.OfType<DraggableLineFeature>().Single();
                rig.Measure = features.OfType<MeasureFeature>().Single();
                using (board.ReadScope()) rig.InitialRange = board.Read(schema.Viewport.ActiveRange);
                window.PreviewMouseMove += (_, _) => Interlocked.Increment(ref rig.MouseEvents);
                // 行为链里 ChartInteractionFeature 那组的共享状态 → 惯性执行器;每帧记一下它是否在跑、速度多少
                var groups = (System.Collections.IEnumerable)Field(cell.PointerRegions.Behaviors, "_groups")!;
                var state = groups.Cast<object>().Select(g => g.GetType().GetField("State")!.GetValue(g)).OfType<ChartInteractionState>().Single();
                rig.Inertia = state.Inertia;
                CompositionTarget.Rendering += (_, _) =>
                {
                    if (!rig.RecordArrivals) return;
                    var inertia = rig.Inertia;
                    string tag = inertia.IsRunning ? $"r(v={(double)Field(inertia, "_vPxPerSec")!:F0})" : "f";
                    rig.Arrivals.Add((tag, NowMs()));
                };
                window.Activate();
                RenderProbe.Pump(200);
                return rig;
            }
        }

        /// <summary>主图 schema:跟 render-probe 的 K 线图同一套装配,交互部分换成本探针要验的配置。</summary>
        internal sealed class InteractionProbeSchema : ChartReactiveSchema
        {
            public CandlePorts Ports { get; } = new(new("I_Time"), new("I_Open"), new("I_High"), new("I_Low"), new("I_Close"));
            public DataPort<ReadOnlyMemory<double>> SmaPort { get; } = new("I_SMA20");
            public DataPort<RealRange> YRangePort { get; } = new("I_YRange");
            public DataPort<double> PricePort { get; } = new("I_OrderPrice");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public ViewportPorts Viewport { get; private set; } = null!;
            public DataPort<PointerHitState?> Hit => HitPort;

            /// <summary>OnCommitted 调用记录(UI 线程写读)。</summary>
            public readonly List<double> Commits = new();
            /// <summary>手势端口上看到的每一次写(UI 线程写读)。</summary>
            public readonly List<string> Gestures = new();
            /// <summary>true = 旧式业务处理器把冒泡的 MouseDown 标 Handled(场景 15)。</summary>
            public bool LegacyHandledEnabled;

            protected override void DefineDataFlow(ChartCell chart)
            {
                Viewport = ViewportPorts.RequireAttached(Chart);
                Trigger.BindTo(chart);
            }

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                var timeMeta = FieldMeta.Literal("时间", Colors.White, "yyyy-MM-dd HH:mm");
                var priceMeta = FieldMeta.Literal("价", Colors.LightGray, "F2");
                var smaMeta = FieldMeta.Literal("SMA20", Color.FromRgb(0xFF, 0xB7, 0x4D), "F2");

                canvas
                    .Environment(env => env
                        .SetupLayout(left: ChartLength.Pixel(60), top: ChartLength.Pixel(28), right: ChartLength.Pixel(0), bottom: ChartLength.Pixel(24))
                        .SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge, defaultVisibleCount: 120,
                            overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard)
                        .SetupAutoScale(yRangePort: YRangePort, valuePorts: new[] { Ports.High, Ports.Low, SmaPort }, paddingRatio: 0.05)
                        .SetupUniversalHeader(hitPort: HitPort))
                    .Axes(axes => axes
                        .AddDomainAxis(Ports.Time, ViewportPorts.RequireAttached(Chart), timeMeta)
                        .AddRangeAxis(YRangePort, priceMeta, AxisPlacement.Right))
                    .Series(series => series
                        .AddCandle(rangePort: YRangePort, ports: Ports, groupName: "MainCandle")
                        .AddLine(dataPort: SmaPort, rangePort: YRangePort, meta: smaMeta, thickness: 1.5))
                    .Interactions(i => i
                        .EnableStandard(Ports.Time, timeMeta, new InteractionOptions<DateTime>
                        {
                            HitPort = HitPort,
                            Modes = ChartInteractionMode.All,
                            TooltipXMeta = timeMeta,
                            RequireClickToActivate = true,
                            DismissOnEscape = true,
                        })
                        .AddDraggableLine(PricePort, YRangePort, new DraggableLineOptions { OnCommitted = Commits.Add })
                        .AddMeasure(YRangePort));

                canvas.Add(new GestureSpyFeature(this));
                canvas.Add(new LegacyHandledFeature(this));
            }
        }

        /// <summary>把手势端口上的每次写记下来(路由器写端口时同步回调)。</summary>
        private sealed class GestureSpyFeature : ChartFeature
        {
            private readonly InteractionProbeSchema _owner;
            public GestureSpyFeature(InteractionProbeSchema owner) => _owner = owner;

            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
            {
                var port = Pointer.GesturePort;
                flow.Watch(new object[] { port }, board =>
                {
                    PointerGesture g;
                    using (board.ReadScope()) g = board.Read(port);
                    string owner = g.Owner?.GetType().Name.Replace("Feature", "") ?? "-";
                    _owner.Gestures.Add($"{owner}:{g.Phase}{(g.Reason != PointerCancelReason.None ? "(" + g.Reason + ")" : "")}");
                });
            }

            protected override void OnProject(FeatureContext ctx) { }
        }

        /// <summary>
        /// 场景 15:业务用已废弃的 OnMouse 订冒泡 MouseDown 并标 Handled(老代码"挡住图表平移"的写法)。
        /// 按决策 D3 它不再挡平移,探针只记录现象。
        /// </summary>
        private sealed class LegacyHandledFeature : ChartFeature
        {
            private readonly InteractionProbeSchema _owner;
            public LegacyHandledFeature(InteractionProbeSchema owner) => _owner = owner;
            public override FeaturePhase Phase => FeaturePhase.PreLayout;

            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
            {
#pragma warning disable CS0618
                this.OnMouse(UIElement.MouseDownEvent).Subscribe(e => { if (_owner.LegacyHandledEnabled) e.Handled = true; }).OwnedBy(this);
#pragma warning restore CS0618
            }

            protected override void OnProject(FeatureContext ctx) { }
        }

        // ==========================================
        // 驱动(后台线程):注入输入、读状态、截图
        // ==========================================

        /// <summary>一次读到的可观测状态。</summary>
        private readonly record struct Snap(
            RealRange Active, PointerHitState? Hit, double Price, bool LineHot, bool MeasureActive, bool BoxActive,
            string Cursor, int Commits, double LastCommit, float LineY, string Focus, bool Foreground, Point WpfMouse)
        {
            public int? HitIndex => Hit?.Hit.LogicalIndex;
        }

        private sealed class Driver
        {
            private readonly InteractionRig _rig;
            private readonly string _outDir;
            private HevoRect _plot;
            private Point _origin;          // DrawingCanvas (0,0) 的屏幕像素坐标
            private double _scaleX, _scaleY; // DIP → 屏幕像素
            private IntPtr _hwnd;

            public Driver(InteractionRig rig, string outDir) { _rig = rig; _outDir = outDir; }

            private T Ui<T>(Func<T> f) => _rig.Window.Dispatcher.Invoke(f, DispatcherPriority.Background);
            private void Ui(Action a) => _rig.Window.Dispatcher.Invoke(a, DispatcherPriority.Background);

            /// <summary>等若干毫秒,再排一个低于输入 / 渲染优先级的空任务,确保这之前注入的输入和排队的帧都处理完。</summary>
            private void Settle(int ms = 80)
            {
                Thread.Sleep(ms);
                Ui(() => { });
            }

            // ---------- 坐标(DrawingCanvas DIP) ----------
            private double X(double f) => _plot.Left + _plot.Width * f;
            private double Y(double f) => _plot.Top + _plot.Height * f;

            private Point Screen(double x, double y) => new(_origin.X + x * _scaleX, _origin.Y + y * _scaleY);

            /// <summary>远离价格线的空白行(价格线在上半就用下半,反之亦然)。</summary>
            private double EmptyY()
            {
                float lineY = Read().LineY;
                return lineY < Y(0.5) ? Y(0.8) : Y(0.2);
            }

            public string? Preflight()
            {
                Ui(() =>
                {
                    using (var ctx = _rig.Cell.CreateContext()) _plot = ctx.GetPlotArea();
                    var canvas = _rig.Cell.DrawingCanvas;
                    var o = canvas.PointToScreen(new Point(0, 0));
                    var one = canvas.PointToScreen(new Point(100, 100));
                    _origin = o;
                    _scaleX = (one.X - o.X) / 100;
                    _scaleY = (one.Y - o.Y) / 100;
                    _hwnd = new System.Windows.Interop.WindowInteropHelper(_rig.Window).Handle;
                });
                if (Native.GetSystemMetrics(Native.SM_REMOTESESSION) == 0 && IsDesktopLocked())
                    return "桌面被锁定 / 处于安全桌面(OpenInputDesktop 失败),无法注入输入。";

                // 输入能不能到窗口:动一下鼠标,1.5 秒内窗口应收到 PreviewMouseMove
                int before = Volatile.Read(ref _rig.MouseEvents);
                for (int i = 0; i < 3 && Volatile.Read(ref _rig.MouseEvents) == before; i++)
                {
                    Input.Move(Screen(X(0.5), Y(0.5)));
                    Thread.Sleep(100);
                    Input.Move(Screen(X(0.52), Y(0.5)));
                    Thread.Sleep(400);
                }
                if (Volatile.Read(ref _rig.MouseEvents) == before)
                    return "注入的鼠标移动 1.5 秒内没有到达探针窗口(屏幕锁定 / 睡眠 / 窗口被遮挡 / UIPI 拦截?)。";
                return null;
            }

            private static bool IsDesktopLocked()
            {
                IntPtr desk = Native.OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
                if (desk == IntPtr.Zero) return true;
                Native.CloseDesktop(desk);
                return false;
            }

            public void RunAll(HashSet<int> only, List<CheckResult> results)
            {
                var checks = new (int Id, string Name, Func<int, CheckResult> Run)[]
                {
                    (1, "单击武装后移动,十字光标跟随", C1),
                    (2, "空白处拖动平移;快速甩动松手后惯性", C2),
                    (3, "惯性中按空格立即停(D1)", C3),
                    (4, "Shift+拖动框选缩放", C4),
                    (5, "平移 / 缩放后双击复原", C5),
                    (6, "滚轮以指针为锚缩放", C6),
                    (7, "←/→ 步进一根;↑/↓ 不动(KeyboardZoom 默认关)", C7),
                    (8, "Esc 解除十字光标", C8),
                    (9, "悬停价格线:SizeNS 光标、线变亮", C9),
                    (10, "拖价格线:价格跟随、不平移,松手提交一次", C10),
                    (11, "拖价格线中 Esc:价格回滚、不提交", C11),
                    (12, "单击价格线(不拖):价格不变、不平移", C12),
                    (13, "Ctrl+拖动测量、不平移;不按 Ctrl 照常平移", C13),
                    (14, "拖价格线时滚轮:缩放,线留在指针下", C14),
                    (15, "旧式 OnMouse 标 Handled 是否挡平移(D3,只记录)", C15),
                };
                foreach (var (id, name, run) in checks)
                {
                    if (only.Count > 0 && !only.Contains(id)) continue;
                    CheckResult r;
                    try
                    {
                        Prepare();
                        r = run(id) with { Name = name };
                    }
                    catch (ProbeAbort) { throw; }
                    catch (Exception ex) { r = new CheckResult(id, name, false, "", "异常:" + ex.Message, null); }
                    finally { Input.ReleaseAll(); }
                    results.Add(r);
                    Console.WriteLine($"[interaction-probe] {id,2} {(r.Pass switch { true => "PASS", false => "FAIL", null => "INFO" })}  {name}  — {r.Observed}");
                }
            }

            /// <summary>每项开始前:等惯性停、视口回首屏、价格回初值、清提交记录和手势日志、指针停在空白处。</summary>
            private void Prepare()
            {
                WaitStable(2000);
                Ui(() =>
                {
                    _rig.Schema.LegacyHandledEnabled = false;
                    var b = _rig.Board;
                    using (b.WriteScope())
                    {
                        b.Write(_rig.Schema.Viewport.UserRange, _rig.InitialRange);
                        b.Write(_rig.Schema.PricePort, _rig.InitialPrice);
                    }
                    _rig.Schema.Commits.Clear();
                    _rig.Schema.Gestures.Clear();
                });
                Settle(120);
                var snap = Read();
                if (!Same(snap.Active, _rig.InitialRange)) throw new InvalidOperationException($"复位失败:视口 {R(snap.Active)}");
            }

            /// <summary>等视口 100ms 不再变(惯性停下)。</summary>
            private void WaitStable(int timeoutMs)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var last = Read().Active;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    Thread.Sleep(100);
                    var now = Read().Active;
                    if (Same(now, last)) return;
                    last = now;
                }
            }

            private Snap Read() => Ui(() =>
            {
                var s = _rig.Schema;
                var b = _rig.Board;
                RealRange active;
                PointerHitState? hit;
                double price;
                bool hot, measure, box;
                using (b.ReadScope())
                {
                    active = b.Read(s.Viewport.ActiveRange);
                    hit = b.Read(s.Hit);
                    price = b.Read(s.PricePort);
                    hot = (bool)(ReadPrivatePort(b, _rig.Line, "_hotPort") ?? false);
                    var ms = ReadPrivatePort(b, _rig.Measure, "_statePort");
                    measure = ms != null && (bool)ms.GetType().GetProperty("Active")!.GetValue(ms)!;
                    box = ReadPrivatePort(b, _rig.Interaction, "_boxStatePort") is BoxZoomTrait bt && bt.IsActive;
                }
                var proj = (ChartProjection)Field(_rig.Line, "_projection")!;
                double drawn = (double)Field(_rig.Line, "_drawnPrice")!;
                float lineY = proj.IsValid && !double.IsNaN(drawn) ? proj.ValueToScreenY(drawn) : float.NaN;
                string cursor = _rig.Cell.Cursor?.ToString() ?? "(默认)";
                var focus = Keyboard.FocusedElement;
                string focusName = focus == null ? "(无)" : ReferenceEquals(focus, _rig.Cell) ? "主图" : focus.GetType().Name;
                return new Snap(active, hit, price, hot, measure, box, cursor, s.Commits.Count,
                    s.Commits.Count > 0 ? s.Commits[^1] : double.NaN, lineY, focusName, Native.GetForegroundWindow() == _hwnd,
                    Mouse.GetPosition(_rig.Cell.DrawingCanvas));
            });

            private (double Domain, RealRange Range) DomainAt(double x) => Ui(() =>
            {
                var proj = (ChartProjection)Field(_rig.Line, "_projection")!;
                return (proj.ScreenToDomain(x), proj.DomainRange);
            });

            private string Gestures() => Ui(() => Compress(_rig.Schema.Gestures));

            private static string Compress(List<string> log)
            {
                // 连续的同名 Move 合并成 Move×n
                var sb = new StringBuilder();
                for (int i = 0; i < log.Count;)
                {
                    int j = i;
                    while (j < log.Count && log[j] == log[i]) j++;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(log[i]);
                    if (j - i > 1) sb.Append('×').Append(j - i);
                    i = j;
                }
                return sb.Length == 0 ? "(无)" : sb.ToString();
            }

            // ---------- 输入 ----------
            private void MoveTo(double x, double y) => Input.Move(Screen(x, y));

            /// <summary>分 n 步从当前位置移到目标,每步间隔 stepMs。</summary>
            private void Glide(double x0, double y0, double x1, double y1, int steps, int stepMs)
            {
                for (int i = 1; i <= steps; i++)
                {
                    MoveTo(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
                    Thread.Sleep(stepMs);
                }
            }

            private void Click(double x, double y)
            {
                MoveTo(x, y);
                Thread.Sleep(40);
                Input.LeftDown();
                Thread.Sleep(40);
                Input.LeftUp();
            }

            /// <summary>慢拖:按下 → 分步移动 → 停 150ms(让惯性取样窗口里速度归零)→ 松开。</summary>
            private void SlowDrag(double x0, double y0, double x1, double y1, Action? midDrag = null)
            {
                MoveTo(x0, y0);
                Thread.Sleep(60);
                Input.LeftDown();
                Thread.Sleep(40);
                Glide(x0, y0, x1, y1, 12, 16);
                Thread.Sleep(150);
                midDrag?.Invoke();
                Input.LeftUp();
            }

            /// <summary>快速甩动:按下后 8 步、每步 8ms 横移,最后一步后立刻松手。</summary>
            private void Flick(double x0, double y, double dx)
            {
                MoveTo(x0, y);
                Thread.Sleep(60);
                Input.LeftDown();
                Thread.Sleep(30);
                for (int i = 1; i <= 8; i++)
                {
                    MoveTo(x0 + dx * i / 8, y);
                    Thread.Sleep(8);
                }
                Input.LeftUp();
            }

            /// <summary>注入按键前确认探针窗口在前台,不往别的程序里打字。</summary>
            private void EnsureForeground()
            {
                if (Native.GetForegroundWindow() == _hwnd) return;
                Ui(() => _rig.Window.Activate());
                Thread.Sleep(100);
                if (Native.GetForegroundWindow() != _hwnd)
                    throw new ProbeAbort("探针窗口不是前台窗口,为避免把按键打进别的程序,停止。");
            }

            private void Key(ushort vk)
            {
                EnsureForeground();
                Input.Key(vk, down: true);
                Thread.Sleep(30);
                Input.Key(vk, down: false);
            }

            private void KeyDown(ushort vk) { EnsureForeground(); Input.Key(vk, down: true); }
            private void KeyUp(ushort vk) => Input.Key(vk, down: false);

            private string Shot(int id, string tag = "")
            {
                Thread.Sleep(40);
                var (x, y, w, h) = Ui(() =>
                {
                    var o = _rig.Host.PointToScreen(new Point(0, 0));
                    var e = _rig.Host.PointToScreen(new Point(_rig.Host.ActualWidth, _rig.Host.ActualHeight));
                    return ((int)Math.Round(o.X), (int)Math.Round(o.Y), (int)Math.Round(e.X - o.X), (int)Math.Round(e.Y - o.Y));
                });
                string path = Path.Combine(_outDir, $"check{id:00}{(tag.Length > 0 ? "-" + tag : "")}.png");
                ScreenShot.Save(x, y, w, h, path);
                return path;
            }

            private static CheckResult Res(int id, bool? pass, string expected, string observed, string? shot)
                => new(id, "", pass, expected, observed, shot);

            // ==========================================
            // 场景
            // ==========================================

            private CheckResult C1(int id)
            {
                double y = EmptyY();
                // 先让 hover 回到未武装:Esc(DismissOnEscape)
                Click(X(0.5), y);
                Settle();
                Key(Native.VK_ESCAPE);
                Settle();
                Glide(X(0.5), y, X(0.3), y, 6, 15);
                Settle(120);
                var before = Read();
                Click(X(0.3), y);
                Settle();
                var afterClick = Read();
                Glide(X(0.3), y, X(0.6), y, 8, 15);
                Settle(120);
                var after = Read();
                var shot = Shot(id);
                bool pass = before.Hit == null && after.Hit != null && after.HitIndex != afterClick.HitIndex
                            && Same(after.Active, _rig.InitialRange);
                return Res(id, pass, "未武装时移动无十字光标;单击后移动,命中索引跟着变;视口不动",
                    $"武装前 hit={H(before)};单击后 hit={H(afterClick)};移到 60% 后 hit={H(after)};视口 {R(after.Active)}", shot);
            }

            private CheckResult C2(int id)
            {
                double y = EmptyY();
                var r0 = Read().Active;
                RealRange mid = default;
                SlowDrag(X(0.4), y, X(0.6), y, () => mid = Read().Active);
                Settle(300);
                var afterPan = Read().Active;
                Settle(200);
                var afterPan2 = Read().Active;
                bool panned = mid.Min < r0.Min - 1 && Same(afterPan, afterPan2);

                // 快速甩动 5 次:每次看松手后视口还动不动、惯性执行器有没有起来、跑了几帧
                var trials = new List<FlickTrial>();
                string? shot = null;
                for (int i = 0; i < 5; i++)
                {
                    if (i > 0) Prepare();
                    trials.Add(DoFlick(y));
                    shot ??= trials[^1].Moved > 0 ? Shot(id) : null;
                }
                shot ??= Shot(id);
                int ok = trials.Count(t => t.Moved > 0);
                return Res(id, panned && ok == trials.Count,
                    "拖动中视口左移(看更早的 K 线),停顿后松手无惯性;每次甩动松手后视口都继续移动一段",
                    $"慢拖:{R(r0)} → 拖动中 {R(mid)} → 松手后 {R(afterPan)}(200ms 后 {R(afterPan2)});" +
                    $"甩动 {trials.Count} 次,惯性移动 {ok} 次:{string.Join(";", trials.Select(t => t.ToString()))}", shot);
            }

            /// <summary>一次甩动:松手后惯性移了几根、惯性执行器起来时的速度、它跑了几帧。</summary>
            private readonly record struct FlickTrial(double Moved, double StartV, int RunFrames)
            {
                public override string ToString() => string.Create(CultureInfo.InvariantCulture,
                    $"惯性移 {Moved:F0} 根(起速 {(double.IsNaN(StartV) ? "未启动" : StartV.ToString("F0") + "px/s")},跑 {RunFrames} 帧)");
            }

            private FlickTrial DoFlick(double y)
            {
                Ui(() => { _rig.Arrivals.Clear(); _rig.RecordArrivals = true; });
                Flick(X(0.3), y, _plot.Width * 0.3);
                var atRelease = Read().Active;   // Background 优先级:松手已处理
                WaitStable(3000);
                var end = Read().Active;
                return Ui(() =>
                {
                    _rig.RecordArrivals = false;
                    var running = _rig.Arrivals.Where(x => x.Kind.StartsWith("r(", StringComparison.Ordinal)).ToList();
                    double v = running.Count > 0 ? double.Parse(running[0].Kind[4..^1], CultureInfo.InvariantCulture) : double.NaN;
                    return new FlickTrial(atRelease.Min - end.Min, v, running.Count);
                });
            }

            private CheckResult C3(int id)
            {
                double y = EmptyY();
                // 惯性不是每次都起得来(见场景 2):最多甩 8 次,直到松手后视口确实还在动再按键
                RealRange released = default, before = default;
                int attempts = 0;
                bool moving = false;
                while (attempts < 8 && !moving)
                {
                    if (attempts++ > 0) Prepare();
                    Flick(X(0.3), y, _plot.Width * 0.3);
                    Thread.Sleep(40);
                    released = Read().Active;
                    Thread.Sleep(40);
                    before = Read().Active;
                    moving = !Same(released, before) && Ui(() => _rig.Inertia.IsRunning);
                }
                Key(Native.VK_SPACE);
                Settle(40);
                var r1 = Read().Active;
                Thread.Sleep(300);
                var r2 = Read().Active;
                var shot = Shot(id);
                bool started = moving;
                bool pass = started && Same(r1, r2);
                return Res(id, pass, "惯性开始后按空格,视口立即停住(前提:松手后视口还在动)",
                    $"{(moving ? $"第 {attempts} 次甩动起了惯性" : $"甩 {attempts} 次惯性都没起来")};松手 +40ms {R(released)},+80ms {R(before)}(惯性{(started ? "在跑" : "没在跑,无法验证")});按键后 +40ms {R(r1)},+340ms {R(r2)}", shot);
            }

            private CheckResult C4(int id)
            {
                double y0 = EmptyY();
                double y1 = y0 < Y(0.5) ? y0 + 60 : y0 - 60;
                double x0 = X(0.35), x1 = X(0.65);
                var (d0, range0) = DomainAt(x0);
                var (d1, _) = DomainAt(x1);
                bool boxMid = false;
                RealRange midRange = default;
                Point wpfMid = default;
                string? shotMid = null;
                KeyDown(Native.VK_SHIFT);
                Thread.Sleep(50);
                SlowDrag(x0, y0, x1, y1, () =>
                {
                    var s = Read();
                    boxMid = s.BoxActive;
                    midRange = s.Active;
                    wpfMid = s.WpfMouse;
                    shotMid = Shot(id, "drag");
                });
                Thread.Sleep(30);
                KeyUp(Native.VK_SHIFT);
                Settle(150);
                var after = Read();
                var shot = Shot(id);
                bool fit = Math.Abs(after.Active.Min - d0) <= 1.5 && Math.Abs(after.Active.Max - d1) <= 1.5;
                bool pass = boxMid && Same(midRange, range0) && !after.BoxActive && fit;
                return Res(id, pass, $"拖动中有选框、视口不动;松手缩放到选框 ≈ [{d0:F1}, {d1:F1}]",
                    $"拖动中 选框={boxMid},视口 {R(midRange)},指针 x={x1:F1}(WPF 看到 {wpfMid.X:F1});松手后 选框={after.BoxActive},视口 {R(after.Active)}(原 {R(range0)})", shotMid ?? shot);
            }

            private CheckResult C5(int id)
            {
                double y = EmptyY();
                // 先平移 + 缩放,离开首屏
                SlowDrag(X(0.4), y, X(0.6), y);
                Settle(150);
                MoveTo(X(0.5), y);
                Settle();
                Input.Wheel(120);
                Settle(200);
                var moved = Read().Active;
                Click(X(0.5), y);
                Thread.Sleep(90);
                Input.LeftDown();
                Thread.Sleep(40);
                Input.LeftUp();
                Settle(200);
                var after = Read().Active;
                var shot = Shot(id);
                bool pass = !Same(moved, _rig.InitialRange) && Same(after, _rig.InitialRange);
                return Res(id, pass, $"双击后视口回到首屏 {R(_rig.InitialRange)}",
                    $"双击前 {R(moved)};双击后 {R(after)}", shot);
            }

            private CheckResult C6(int id)
            {
                double y = EmptyY();
                double x = X(0.3);
                MoveTo(x, y);
                Settle(120);
                var (dBefore, rBefore) = DomainAt(x);
                Input.Wheel(120);
                Settle(200);
                var (dAfter, rAfter) = DomainAt(x);
                var shot = Shot(id);
                Input.Wheel(-120);
                Settle(200);
                var (dBack, rBack) = DomainAt(x);
                bool pass = rAfter.Span < rBefore.Span - 1 && Math.Abs(dAfter - dBefore) <= 1.0 && rBack.Span > rAfter.Span;
                return Res(id, pass, "上滚放大(跨度变小),指针下的 K 线索引基本不变(≤1 根);下滚还原方向",
                    $"指针处索引 {dBefore:F2} → {dAfter:F2}(回滚后 {dBack:F2});跨度 {rBefore.Span:F1} → {rAfter.Span:F1} → {rBack.Span:F1}", shot);
            }

            private CheckResult C7(int id)
            {
                double y = EmptyY();
                Click(X(0.5), y);
                Settle();
                MoveTo(X(0.5) + 1, y);
                Settle(120);
                var s0 = Read();
                Key(Native.VK_RIGHT);
                Settle(120);
                var s1 = Read();
                Key(Native.VK_LEFT);
                Settle(120);
                var s2 = Read();
                var shot = Shot(id);
                Key(Native.VK_UP);
                Settle(120);
                var s3 = Read();
                Key(Native.VK_DOWN);
                Settle(120);
                var s4 = Read();
                bool step = s0.HitIndex != null && s1.HitIndex == s0.HitIndex + 1 && s2.HitIndex == s0.HitIndex;
                bool upDownNoop = Same(s3.Active, s2.Active) && Same(s4.Active, s2.Active) && s3.HitIndex == s2.HitIndex && s4.HitIndex == s2.HitIndex;
                return Res(id, step && upDownNoop, "→ 索引 +1,← 回原位;↑/↓ 视口和十字光标都不变",
                    $"索引 {s0.HitIndex} →(→){s1.HitIndex} →(←){s2.HitIndex} →(↑){s3.HitIndex} →(↓){s4.HitIndex};" +
                    $"视口 {R(s2.Active)} / ↑ {R(s3.Active)} / ↓ {R(s4.Active)};焦点 {s0.Focus}→{s4.Focus}", shot);
            }

            private CheckResult C8(int id)
            {
                double y = EmptyY();
                Click(X(0.5), y);
                Settle();
                MoveTo(X(0.55), y);
                Settle(120);
                var before = Read();
                Key(Native.VK_ESCAPE);
                Settle(120);
                var after = Read();
                var shot = Shot(id);
                Glide(X(0.55), y, X(0.6), y, 4, 15);
                Settle(120);
                var moved = Read();
                bool pass = before.Hit != null && after.Hit == null;
                return Res(id, pass, "Esc 后十字光标消失(hit = null);再移动也不出现,直到下次单击",
                    $"Esc 前 hit={H(before)};Esc 后 hit={H(after)};之后移动 hit={H(moved)}", shot);
            }

            private CheckResult C9(int id)
            {
                double lineY = Read().LineY;
                double x = X(0.5);
                double from = lineY < Y(0.5) ? lineY + 40 : lineY - 40;
                MoveTo(x, from);
                Settle(120);
                var away = Read();
                Glide(x, from, x, lineY, 6, 15);
                Settle(150);
                var on = Read();
                var shot = Shot(id);
                bool pass = !away.LineHot && on.LineHot && on.Cursor == "SizeNS";
                return Res(id, pass, "指针在线外:线不亮;移到线上:光标 SizeNS,线变亮",
                    $"线外 hot={away.LineHot} 光标={away.Cursor};线上(y={lineY:F1})hot={on.LineHot} 光标={on.Cursor}", shot);
            }

            private CheckResult C10(int id)
            {
                var s0 = Read();
                double x = X(0.5), y0 = s0.LineY;
                double y1 = y0 < Y(0.5) ? y0 + 60 : y0 - 60;
                Snap mid = default;
                string? shotMid = null;
                SlowDrag(x, y0, x, y1, () => { mid = Read(); shotMid = Shot(id, "drag"); });
                Settle(150);
                var after = Read();
                string g = Gestures();
                bool pass = Math.Abs(mid.Price - s0.Price) > 1e-6 && Same(mid.Active, s0.Active)
                            && Math.Abs(mid.LineY - y1) <= 2
                            && after.Commits == 1 && Math.Abs(after.LastCommit - after.Price) < 1e-9
                            && Same(after.Active, s0.Active);
                return Res(id, pass, "拖动中价格跟着变、线在指针下、视口不动;松手 OnCommitted 调一次,值 = 最终价格",
                    $"价格 {s0.Price:F3} → 拖动中 {mid.Price:F3}(线 y={mid.LineY:F1},指针 y={y1:F1},WPF 看到的指针 y={mid.WpfMouse.Y:F1})→ 松手 {after.Price:F3};" +
                    $"提交 {after.Commits} 次 值={after.LastCommit:F3};视口 {R(s0.Active)} → {R(after.Active)};手势 {g}", shotMid);
            }

            private CheckResult C11(int id)
            {
                var s0 = Read();
                double x = X(0.5), y0 = s0.LineY;
                double y1 = y0 < Y(0.5) ? y0 + 50 : y0 - 50;
                MoveTo(x, y0);
                Thread.Sleep(60);
                Input.LeftDown();
                Thread.Sleep(40);
                Glide(x, y0, x, y1, 10, 16);
                Settle(100);
                var mid = Read();
                Key(Native.VK_ESCAPE);
                Settle(120);
                var afterEsc = Read();
                var shot = Shot(id);
                Input.LeftUp();
                Settle(150);
                var after = Read();
                string g = Gestures();
                bool pass = Math.Abs(mid.Price - s0.Price) > 1e-6 && Math.Abs(afterEsc.Price - s0.Price) < 1e-9
                            && Math.Abs(after.Price - s0.Price) < 1e-9 && after.Commits == 0 && Same(after.Active, s0.Active);
                return Res(id, pass, "Esc 后价格回到按下前,松手不提交、不平移",
                    $"价格 {s0.Price:F3} → 拖动中 {mid.Price:F3} → Esc 后 {afterEsc.Price:F3} → 松手 {after.Price:F3};" +
                    $"提交 {after.Commits} 次;视口 {R(s0.Active)} → {R(after.Active)};手势 {g}", shot);
            }

            private CheckResult C12(int id)
            {
                var s0 = Read();
                Click(X(0.5), s0.LineY);
                Settle(150);
                var after = Read();
                var shot = Shot(id);
                string g = Gestures();
                bool pass = Math.Abs(after.Price - s0.Price) < 1e-9 && Same(after.Active, s0.Active) && after.Commits == 0;
                return Res(id, pass, "价格不变、视口不变、不提交",
                    $"价格 {s0.Price:F3} → {after.Price:F3};视口 {R(s0.Active)} → {R(after.Active)};提交 {after.Commits} 次;手势 {g}", shot);
            }

            private CheckResult C13(int id)
            {
                double y0 = EmptyY();
                double y1 = y0 < Y(0.5) ? y0 + 80 : y0 - 80;
                var s0 = Read();
                Snap mid = default;
                string? shotMid = null;
                KeyDown(Native.VK_CONTROL);
                Thread.Sleep(50);
                SlowDrag(X(0.4), y0, X(0.6), y1, () => { mid = Read(); shotMid = Shot(id, "drag"); });
                Thread.Sleep(30);
                KeyUp(Native.VK_CONTROL);
                Settle(150);
                var afterMeasure = Read();
                string g = Gestures();

                SlowDrag(X(0.4), y0, X(0.6), y0);
                Settle(200);
                var afterPan = Read();
                bool measured = mid.MeasureActive && Same(mid.Active, s0.Active) && !afterMeasure.MeasureActive && Same(afterMeasure.Active, s0.Active);
                bool panned = afterPan.Active.Min < s0.Active.Min - 1;
                return Res(id, measured && panned, "Ctrl+拖:测量框出现、视口不动,松手消失;不按 Ctrl:平移",
                    $"Ctrl 拖动中 测量={mid.MeasureActive} 视口 {R(mid.Active)};松手后 测量={afterMeasure.MeasureActive} 视口 {R(afterMeasure.Active)};" +
                    $"不按 Ctrl 拖后视口 {R(afterPan.Active)};手势 {g}", shotMid);
            }

            private CheckResult C14(int id)
            {
                var s0 = Read();
                double x = X(0.5), y0 = s0.LineY;
                double y1 = y0 < Y(0.5) ? y0 + 30 : y0 - 30;
                MoveTo(x, y0);
                Thread.Sleep(60);
                Input.LeftDown();
                Thread.Sleep(40);
                Glide(x, y0, x, y1, 8, 16);
                Settle(100);
                var mid = Read();
                Input.Wheel(120);
                Settle(200);
                var afterWheel = Read();
                var shot = Shot(id);
                MoveTo(x, y1 + 1);
                Settle(120);
                var afterNudge = Read();
                Input.LeftUp();
                Settle(150);
                var after = Read();
                string g = Gestures();
                bool zoomed = afterWheel.Active.Span < mid.Active.Span - 1;
                double distWheel = Math.Abs(afterWheel.LineY - y1), distNudge = Math.Abs(afterNudge.LineY - (y1 + 1));
                bool pass = zoomed && distWheel <= 2 && distNudge <= 2 && after.Commits == 1;
                return Res(id, pass, "按住拖线时滚轮照常缩放;缩放后(不动鼠标)线仍在指针下(≤2px);松手提交一次",
                    $"跨度 {mid.Active.Span:F1} → {afterWheel.Active.Span:F1};滚轮后 线 y={afterWheel.LineY:F1} 指针 y={y1:F1}(差 {distWheel:F1}px);" +
                    $"再动 1px 后 线 y={afterNudge.LineY:F1}(差 {distNudge:F1}px);提交 {after.Commits} 次;手势 {g}", shot);
            }

            private CheckResult C15(int id)
            {
                double y = EmptyY();
                var s0 = Read();
                Ui(() => _rig.Schema.LegacyHandledEnabled = true);
                SlowDrag(X(0.4), y, X(0.6), y);
                Settle(200);
                var after = Read();
                var shot = Shot(id);
                Ui(() => _rig.Schema.LegacyHandledEnabled = false);
                bool panned = after.Active.Min < s0.Active.Min - 1;
                return Res(id, null, "按决策 D3:旧式 OnMouse 标 Handled 不再挡平移(只记录,不判失败)",
                    $"业务处理器标 Handled 后拖动:{(panned ? "照常平移" : "没有平移")},视口 {R(s0.Active)} → {R(after.Active)}", shot);
            }

            // ---------- 格式 ----------
            private static bool Same(RealRange a, RealRange b) => Math.Abs(a.Min - b.Min) < 1e-6 && Math.Abs(a.Max - b.Max) < 1e-6;
            private static string R(RealRange r) => string.Create(CultureInfo.InvariantCulture, $"[{r.Min:F1}, {r.Max:F1}]");
            private static string H(Snap s) => s.Hit is { } h ? $"#{h.Hit.LogicalIndex}" : "null";
        }

        private static double NowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // ---------- 反射读私有状态(探针只读,不改框架) ----------
        private static readonly MethodInfo ReadMethod = typeof(DataBlackboard).GetMethods()
            .Single(m => m.Name == "Read" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType.IsGenericType
                         && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(DataPort<>));

        private static object? Field(object owner, string name)
            => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
               ?? throw new MissingFieldException(owner.GetType().Name, name);

        private static object? ReadPrivatePort(DataBlackboard board, object owner, string fieldName)
        {
            var port = Field(owner, fieldName)!;
            var valueType = port.GetType().GetGenericArguments()[0];
            return ReadMethod.MakeGenericMethod(valueType).Invoke(board, new[] { port });
        }

        // ==========================================
        // 输入注入 / 截图
        // ==========================================

        private static class Input
        {
            private static bool _left, _shift, _ctrl;

            public static void Move(Point screen)
            {
                int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
                SendMouse(new Native.MOUSEINPUT
                {
                    dx = (int)Math.Round((screen.X - vx) * 65535.0 / Math.Max(1, vw - 1)),
                    dy = (int)Math.Round((screen.Y - vy) * 65535.0 / Math.Max(1, vh - 1)),
                    dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
                });
            }

            public static void LeftDown() { SendMouse(new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_LEFTDOWN }); _left = true; }
            public static void LeftUp() { SendMouse(new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_LEFTUP }); _left = false; }
            public static void Wheel(int delta) => SendMouse(new Native.MOUSEINPUT { mouseData = delta, dwFlags = Native.MOUSEEVENTF_WHEEL });

            public static void Key(ushort vk, bool down)
            {
                bool extended = vk is Native.VK_LEFT or Native.VK_RIGHT or Native.VK_UP or Native.VK_DOWN;
                Send(new Native.INPUT
                {
                    type = Native.INPUT_KEYBOARD,
                    u = new Native.InputUnion
                    {
                        ki = new Native.KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = (down ? 0u : Native.KEYEVENTF_KEYUP) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0u),
                        },
                    },
                });
                if (vk == Native.VK_SHIFT) _shift = down;
                if (vk == Native.VK_CONTROL) _ctrl = down;
            }

            /// <summary>收尾:别把按下的键 / 鼠标键留在系统里。</summary>
            public static void ReleaseAll()
            {
                try
                {
                    if (_left) LeftUp();
                    if (_shift) Key(Native.VK_SHIFT, false);
                    if (_ctrl) Key(Native.VK_CONTROL, false);
                }
                catch { /* 收尾尽力而为 */ }
            }

            private static void SendMouse(Native.MOUSEINPUT mi) => Send(new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.InputUnion { mi = mi } });

            private static void Send(Native.INPUT input)
            {
                if (Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>()) != 1)
                    throw new ProbeAbort($"SendInput 失败,Win32 错误 {Marshal.GetLastWin32Error()}(屏幕锁定 / UIPI 拦截?)");
            }
        }

        private static class ScreenShot
        {
            /// <summary>从屏幕(DWM 合成后的桌面)抓一块存 PNG。</summary>
            public static void Save(int x, int y, int w, int h, string path)
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                IntPtr mem = Native.CreateCompatibleDC(screen);
                var bmi = new Native.BITMAPINFO
                {
                    bmiHeader = new Native.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                        biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
                    },
                };
                IntPtr bmp = Native.CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
                IntPtr old = Native.SelectObject(mem, bmp);
                try
                {
                    Native.BitBlt(mem, 0, 0, w, h, screen, x, y, Native.SRCCOPY);
                    var pixels = new byte[w * h * 4];
                    Marshal.Copy(bits, pixels, 0, pixels.Length);
                    var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(src));
                    using var fs = File.Create(path);
                    enc.Save(fs);
                }
                finally
                {
                    Native.SelectObject(mem, old);
                    Native.DeleteObject(bmp);
                    Native.DeleteDC(mem);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        /// <summary>本探针的 Win32 声明(键盘 INPUT 需要联合体,跟 latency-probe 只发鼠标的那份分开)。</summary>
        private static class Native
        {
            public const int SM_REMOTESESSION = 0x1000;
            public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
            public const int SRCCOPY = 0x00CC0020;
            public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
            public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                              MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
            public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;
            public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
                                VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

            [StructLayout(LayoutKind.Sequential)]
            public struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

            [StructLayout(LayoutKind.Sequential)]
            public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

            [StructLayout(LayoutKind.Explicit)]
            public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

            [StructLayout(LayoutKind.Sequential)]
            public struct INPUT { public uint type; public InputUnion u; }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct BITMAPINFOHEADER
            {
                public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
                public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
                public uint biClrUsed; public uint biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

            [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);
            [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
            [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
            [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desk);
            [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
            [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
            [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
            [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
            [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
            [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
            [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        }
    }
}
