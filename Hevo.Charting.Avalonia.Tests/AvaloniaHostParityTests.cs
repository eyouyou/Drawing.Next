using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.Samples;

namespace Hevo.Charting.Avalonia.Tests
{
    /// <summary>
    /// Avalonia 宿主补齐 WPF 差距的部分:触屏捏合 / 平移、调试菜单与拓扑监控、蓝图 JSON 起看板、
    /// 双宿主外壳 Feature(AddHostFeature)、按宿主解析资源文本。
    /// </summary>
    public sealed class AvaloniaHostParityTests
    {
        // ==========================================
        // 触屏
        // ==========================================

        /// <summary>
        /// 触点注入:Avalonia 12 不开放平台输入口,直接在图层画布上发 Touch 类型指针的路由事件(隧道 + 冒泡,跟真触摸同一条路由)。
        /// 坐标 = cell 坐标(cell 撑满窗口)。
        /// </summary>
        private sealed class Touch
        {
            private readonly Window _window;
            private readonly Control _target;
            private readonly Dictionary<int, Pointer> _pointers = new();

            public Touch(AvaloniaChartCellTests.Rig rig)
            {
                _window = rig.Window;
                _target = rig.Cell.PlotSurface;
            }

            public void Down(int id, double x, double y)
            {
                var pointer = new Pointer(id, PointerType.Touch, isPrimary: _pointers.Count == 0);
                _pointers[id] = pointer;
                _target.RaiseEvent(new PointerPressedEventArgs(_target, pointer, _window, new Point(x, y), 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                Dispatcher.UIThread.RunJobs();
            }

            public void Move(int id, double x, double y)
            {
                _target.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, _target, _pointers[id], _window, new Point(x, y), 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                Dispatcher.UIThread.RunJobs();
            }

            public void Up(int id, double x, double y)
            {
                var pointer = _pointers[id];
                _pointers.Remove(id);
                _target.RaiseEvent(new PointerReleasedEventArgs(_target, pointer, _window, new Point(x, y), 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void Touch_SingleFingerDrag_PansLikeWpfManipulation()
        {
            var rig = new AvaloniaChartCellTests.Rig();
            var before = rig.ActiveRange;
            var touch = new Touch(rig);

            touch.Down(1, 100, 150);
            for (int i = 1; i <= 5; i++) { touch.Move(1, 100 + 20 * i, 150); rig.Frame(); }
            touch.Up(1, 200, 150);
            rig.Frame();

            var after = rig.ActiveRange;
            // 视口贴着最右:手指往右拖 = 往回看更早的数据;跨度不变
            Assert.True(after.Min < before.Min, $"应往左平移:{before.Min} → {after.Min}");
            Assert.Equal(before.Max - before.Min, after.Max - after.Min, 6);
        }

        [AvaloniaFact]
        public void Touch_PinchOut_ZoomsIn_AroundFingers()
        {
            var rig = new AvaloniaChartCellTests.Rig();
            var before = rig.ActiveRange;
            var touch = new Touch(rig);

            touch.Down(1, 180, 150);
            touch.Down(2, 220, 150);
            for (int i = 1; i <= 4; i++)
            {
                touch.Move(1, 180 - 15 * i, 150);
                touch.Move(2, 220 + 15 * i, 150);
                rig.Frame();
            }
            touch.Up(1, 120, 150);
            touch.Up(2, 280, 150);
            rig.Frame();

            var after = rig.ActiveRange;
            Assert.True(after.Max - after.Min < before.Max - before.Min,
                $"捏合放大后可见跨度应变小:{before.Min}-{before.Max} → {after.Min}-{after.Max}");
        }

        [AvaloniaFact]
        public void Touch_WithoutPanOrZoom_PrimaryTouchActsAsMouse()
        {
            // 平移 / 缩放都没开:TouchBehavior 不接管,主触点当鼠标(悬停出十字线命中),视口不动
            var rig = new AvaloniaChartCellTests.Rig(ChartInteractionMode.Hover);
            var before = rig.ActiveRange;
            var touch = new Touch(rig);

            touch.Down(1, 200, 150);
            touch.Move(1, 150, 150);
            rig.Frame();
            Assert.NotNull(rig.Hit);
            touch.Up(1, 150, 150);
            rig.Frame();

            Assert.Equal(before.Min, rig.ActiveRange.Min, 6);
            Assert.Equal(before.Max, rig.ActiveRange.Max, 6);
        }

        // ==========================================
        // 调试菜单 / 拓扑监控
        // ==========================================

#if DEBUG
        [AvaloniaFact]
        public void DebugMenu_InjectsTopologyEntry_AndRemovesOnTemplateSwap()
        {
            var rig = new AvaloniaChartCellTests.Rig();
            Assert.Single(rig.Cell.ContextMenu!.Items.OfType<MenuItem>(), m => (m.Header as string)?.Contains("拓扑") == true);

            // 换 schema:旧菜单项摘掉、新 schema 注入一份,不累加
            rig.Cell.Template = new AvaloniaChartCellTests.Schema();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(rig.Cell.ContextMenu!.Items.OfType<MenuItem>(), m => (m.Header as string)?.Contains("拓扑") == true);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void TopologyMonitor_ShowsFeaturesPortsAndLayers()
        {
            var rig = new AvaloniaChartCellTests.Rig();
            var win = AvaloniaDebugMenuHost.ShowTopologyMonitor(rig.Cell);
            Dispatcher.UIThread.RunJobs();
            var monitor = Assert.IsType<AvaloniaTopologyMonitor>(win.Content);
            monitor.Refresh();
            Dispatcher.UIThread.RunJobs();

            var graph = monitor.Graph;
            Assert.Contains(graph.Nodes.Values, n => n.Tier == 2 && n.Label.StartsWith("Candle"));
            Assert.Contains(graph.Nodes.Values, n => n.Tier == 3);   // 图层终点
            Assert.Contains(graph.Links.Keys, l => graph.Nodes[l.Tgt].Tier == 3);

            // 点一个 Feature 节点:选中并能描述
            var candleId = graph.Nodes.Values.First(n => n.Tier == 2 && n.Label.StartsWith("Candle")).Id;
            graph.Select(candleId);
            Assert.Equal(candleId, graph.SelectedId);
            Assert.Contains("Candle", graph.DescribeSelection());

            win.Close();
            rig.Window.Close();
        }
#endif

        // ==========================================
        // 蓝图 JSON 起看板
        // ==========================================

        [AvaloniaFact]
        public void Dashboard_FromBlueprintJson_RendersOnAvalonia()
        {
            SampleDashboards.RegisterComponents();
            var result = AvaloniaDashboardLauncher.LaunchFromJson(SampleDashboards.KLineDashboardJson());
            Assert.Null(result.Error);
            var dash = Assert.IsType<AvaloniaLinkedChartDashboard>(result.Dashboard);
            Assert.Equal(2, dash.Cells.Count);

            dash.Width = 600; dash.Height = 500;
            var window = new Window { Content = dash, Width = 600, Height = 500 };
            window.Show();

            // 数据源在 cell 挂进可视树后异步加载;等主图的 K 线图层拿到数据
            var master = dash.Cells[0];
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                foreach (var c in dash.Cells) c.RunFrameNow();
                if (!master.IsLoading && master.Engine.StaticLayers.OfType<CandleLayer>().Any(l => l.HostVisual is AvaloniaLayerVisual { Picture: not null }))
                    break;
                Thread.Sleep(20);
            }
            Assert.False(master.IsLoading);

            // 蓝图里写的 "UniversalHeaderFeature" 在 Avalonia cell 上解析成 Avalonia 实现
            var schema = Assert.IsAssignableFrom<ReactiveSchema>(master.Template);
            Assert.Contains(schema.ListFeatures(), f => f is AvaloniaUniversalHeaderFeature);
            Assert.Contains(schema.ListFeatures(), f => f is TooltipWidgetFeature<DateTime>);

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            window.Close();
            foreach (var c in dash.Cells) c.Shutdown();
        }

        // ==========================================
        // 双宿主外壳 Feature
        // ==========================================

        /// <summary>业务写在共享库里的外壳基类(平台无关部分)。</summary>
        public abstract class TitleBarFeatureBase : ChartFeature, IHostDecorator
        {
            public string Title { get; set; } = "";
            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
            protected override void OnProject(FeatureContext ctx) { }
        }

        /// <summary>Avalonia 侧的实现:只管控件。</summary>
        public sealed class AvaloniaTitleBarFeature : TitleBarFeatureBase, IAvaloniaDecorator
        {
            public Control Decorate(Control inner)
            {
                var panel = new DockPanel();
                var title = new TextBlock { Text = Title, Name = "TitleBar" };
                DockPanel.SetDock(title, Dock.Top);
                panel.Children.Add(title);
                panel.Children.Add(inner);
                return panel;
            }
        }

        public abstract class NotRegisteredShellBase : ChartFeature, IHostDecorator
        {
            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
            protected override void OnProject(FeatureContext ctx) { }
        }

        private sealed class ShellSchema : ChartReactiveSchema
        {
            public TitleBarFeatureBase? Added;
            public NotRegisteredShellBase? Missing;
            private readonly WorkflowTrigger<DataBlackboard> _trigger = new();

            protected override void DefineDataFlow(IChartCell chart) => _trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                Added = canvas.AddHostFeature<TitleBarFeatureBase>(f => f.Title = "标题");
                Missing = canvas.AddHostFeature<NotRegisteredShellBase>();
            }
        }

        [AvaloniaFact]
        public void AddHostFeature_CreatesThisHostsImplementation_AndDecorates()
        {
            AvaloniaChartHost.Services.RegisterFeature<TitleBarFeatureBase, AvaloniaTitleBarFeature>();
            var schema = new ShellSchema();
            var cell = new AvaloniaChartCell { Template = schema };
            var window = new Window { Content = cell, Width = 300, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var added = Assert.IsType<AvaloniaTitleBarFeature>(schema.Added);
            Assert.Equal("标题", added.Title);
            Assert.Null(schema.Missing);
            // 外壳包在 cell 里:cell 的子树里能找到标题栏
            var dock = Assert.IsType<DockPanel>(cell.Child);
            Assert.Contains(dock.Children, c => c is TextBlock { Text: "标题" });
            // 蓝图名默认取基类名,在 Avalonia 宿主下解析到本宿主实现
            Assert.Equal(typeof(AvaloniaTitleBarFeature), ComponentRegistry.Resolve(nameof(TitleBarFeatureBase), AvaloniaChartHost.Services));
            window.Close();
            cell.Shutdown();
        }

        // ==========================================
        // 资源文本按宿主解析
        // ==========================================

        [AvaloniaFact]
        public void ResourceString_ResolvesFromAvaloniaApplicationResources()
        {
            Application.Current!.Resources["T_ParityKey"] = "资源值";
            var cell = new AvaloniaChartCell();
            Assert.Equal("资源值", HevoStrings.Resolve(new HevoResourceString("T_ParityKey"), cell));
            // 找不到的键显示键名(同 WPF)
            Assert.Equal("T_Missing", HevoStrings.Resolve(new HevoResourceString("T_Missing"), cell));
            Assert.Equal("字面量", HevoStrings.Resolve(new HevoLiteralString("字面量"), cell));
        }
    }
}
