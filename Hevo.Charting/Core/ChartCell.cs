using Hevo.Charting.Abstractions;
using Hevo.Charting.DevTools;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.Renderers.Raster;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 模板类型(限定只能用于 ChartCell)。
    /// 通常你不直接 new 这个类:给 <see cref="ChartCell.Template"/> 赋 <see cref="ChartSchema"/>(如 ReactiveSchema),cell 自己包一层。
    /// 类型守卫由 <see cref="ChartCell.CoerceTemplateValue"/> + <see cref="ChartCell.OnTemplateChanged"/> 在 Template 赋值时强制。
    /// </summary>
    public class ChartTemplate : ControlTemplate
    {
        /// <summary>调试用模板名(无业务语义,便于日志区分多个 schema 实例)。</summary>
        public string TemplateName { get; set; } = string.Empty;

        /// <summary>本模板承载的 schema(<see cref="ChartCell.Template"/> 赋值时包一层)。</summary>
        public ChartSchema? Schema { get; }

        public ChartTemplate()
        {
            TargetType = typeof(ChartCell);
        }

        internal ChartTemplate(ChartSchema schema) : this()
        {
            Schema = schema;
            // 它只负责产生一个"容器":外壳 UI(Aspect 装饰)挂在 PART_Root 上
            var factory = new FrameworkElementFactory(typeof(AdornerDecorator));
            factory.Name = "PART_Root";
            VisualTree = factory;
        }
    }

    /// <summary>
    /// 静态层(_drawingCanvas)BitmapCache 策略。
    /// 详见 <see cref="ChartCell.StaticCachePolicy"/>。
    /// </summary>
    public enum StaticCachePolicy
    {
        /// <summary>启用缓存(默认)。</summary>
        On,
        /// <summary>不缓存。</summary>
        Off,
    }

    /// <summary>
    /// Chart 最小可部署单元。
    /// 职责：
    /// 1. 物理容器：管理 DrawingCanvas(静态底层), OverlayCanvas(交互独立 host), InteractionCanvas(Widget 顶层)。
    /// 2. 层级管理：确保 Static → Overlay → Interaction 的物理 Z 顺序。
    /// 3. 生命周期：作为 ChartSession 的挂载点。
    ///
    /// LayerBuffer 三类子缓冲的路由(不变量):
    /// - Drawing(矢量指令):RenderWpfLayers 按图层绘制策略分发 —— Software 走 WpfRenderProvider 回放进 DrawingContext,
    ///   Bitmap 走 RasterRenderProvider 光栅化进图层自己的 WriteableBitmap(见 <see cref="RenderModeOverride"/>)。
    /// - Widget(WPF 控件):所有 Layer 通过 RenderWidgetLayers 进入 InteractionCanvas 控件池。
    ///
    /// 注:RenderMode.Hardware 枚举值在 API 层保留以避免破坏下游枚举调用,但当前所有 Hardware
    /// Layer 都被路由到 Software 路径(Skia 已从仓库移除,详见 TODO.md)。
    /// </summary>
    public class ChartCell : ContentControl, IChartCell, IChartCellHost
    {
        // ==========================================
        // 1. 物理结构
        // ==========================================

        // 亚像素震荡拦截阈值。WPF 容器在某些 DPI / 父布局下尺寸会以 0.1~0.4 像素的幅度反复抖动,
        // 0.5 是"半个物理像素"的物理意义边界 — 小于此值的变化在屏幕上肉眼不可见,
        // 但触发完整重排会让 chart 进入持续 20Hz 的伪 SizeChanged 风暴,白白烧 CPU。
        private const double SubPixelResizeThreshold = 0.5;

        private readonly Grid _rootContainer = new();

        // [中层] WPF 矢量层 (DrawingVisuals)
        private readonly ChartDrawingCanvas _drawingCanvas = new() { };

        // [中-高层] WPF 矢量交互层(独立 UIElement,跟 _drawingCanvas 物理隔离)。
        // CrosshairLayer 这类高频改动 + 满屏 dirty bbox 的 Software 层落到这里,
        // 避免它每次 dirty 时连带 _drawingCanvas 的 Grid / Axis 一起 tile-replay。
        private readonly ChartDrawingCanvas _overlayCanvas = new() { };

        // [顶层] 交互与控件层 (懒加载)
        private InteractionCanvas? _interactionCanvas;

        // viewport 不在 ChartCell 暴露 instance accessor:
        // ChartCell 是渲染容器,不该 self-knowledge viewport(纯 1D 量程语义,跟 cell 无关)。
        // 访问走 `ViewportPorts.GetAttached(chart)`。

        // ==========================================
        // 2. 引擎与注册表
        // ==========================================

        // 帧管线、图层注册表 / Z 序、黑板、指针区域都在核心的引擎里;本控件只做 WPF 相关的事(IChartCellHost)
        private readonly ChartEngine _engine;

        /// <inheritdoc/>
        public ChartEngine Engine => _engine;

        // Widget 宿主池: Layer -> 物理控件包装器
        private readonly Dictionary<IChartLayer, LayerWidgetPool> _widgetRegistry = new();

        // RendererProvider — 注入 per-cell diagnostics 收集器(收集器在引擎上)
        private readonly WpfRenderProvider _wpfProvider;

        // 位图后端:第一次有图层走 Bitmap 时才创建(字形 / 画刷缓存跟着 cell 走)
        private RasterRenderProvider? _rasterProvider;

        /// <summary>
        /// 整张图统一的绘制策略。null(默认)= 各图层按自己的 <see cref="ChartLayer.Mode"/>;
        /// 设成 <see cref="RenderMode.Bitmap"/> / <see cref="RenderMode.Software"/> 则所有图层统一走这一种。
        /// 运行时可改,下一帧生效,不重算 Feature、不重录图层。
        /// </summary>
        public RenderMode? RenderModeOverride
        {
            get => _renderModeOverride;
            set
            {
                if (_renderModeOverride == value) return;
                _renderModeOverride = value;
                RequestUpdate(s_noopUpdate);
            }
        }
        private RenderMode? _renderModeOverride;

        /// <summary>
        /// 上屏后端。null(默认)= WPF DrawingVisual 回放;设成自定义后端
        /// 后两块 WPF 矢量画布折叠,图层指令改由后端上屏。运行时可切换,下一帧生效,不重算 Feature、不重录图层。
        /// 新建 cell 的初值取 <see cref="DefaultRenderBackendFactory"/>。
        /// <para>
        /// 所有权:挂上的后端归本 cell 所有。被替换 / 置空时先 Detach 再 Dispose,<see cref="Shutdown"/> 时同样;
        /// Unloaded(如切 Tab)只是暂停,不释放后端。调用方不要再自己 Dispose 挂上去的后端。
        /// </para>
        /// </summary>
        public IChartRenderBackend? RenderBackend
        {
            get => _renderBackend;
            set
            {
                if (ReferenceEquals(_renderBackend, value)) return;
                // 先挂新的:Attach 抛异常(比如该实例已挂在别的 cell 上)时旧后端原样保留
                var newHost = value?.Attach(this);
                ReleaseRenderBackend();
                _renderBackend = value;
                if (value != null)
                {
                    _renderBackendHost = newHost!;
                    _renderBackendHost.IsHitTestVisible = false;
                    // 放在矢量画布原位置:控件层 / loading 蒙层之下
                    _rootContainer.Children.Insert(_rootContainer.Children.IndexOf(_overlayCanvas) + 1, _renderBackendHost);
                }
                else
                {
                    _replayAllWpfLayers = true;
                }
                var vis = value == null ? Visibility.Visible : Visibility.Collapsed;
                _drawingCanvas.Visibility = vis;
                _overlayCanvas.Visibility = vis;
                RequestUpdate(s_noopUpdate);
            }
        }
        private IChartRenderBackend? _renderBackend;
        private UIElement? _renderBackendHost;

        // 摘下并释放当前后端(先 Detach 再 Dispose)。只在替换 / 置空 / Shutdown 时调用。
        private void ReleaseRenderBackend()
        {
            var old = _renderBackend;
            if (old == null) return;
            _renderBackend = null;
            old.Detach();
            if (_renderBackendHost != null) _rootContainer.Children.Remove(_renderBackendHost);
            _renderBackendHost = null;
            old.Dispose();
        }

        /// <summary>
        /// 新建 ChartCell 时用来创建 <see cref="RenderBackend"/> 的全局工厂(null = WPF 默认)。
        /// 应用启动时设一次即可让所有图表换后端。
        /// </summary>
        public static Func<IChartRenderBackend?>? DefaultRenderBackendFactory { get; set; }

        private static readonly Action<RenderContext> s_noopUpdate = _ => { };

        /// <summary>
        /// 获取本 cell 渲染管线的诊断快照。所有计数 UI 线程累加,无锁。
        /// 业务侧典型用法:DispatcherTimer 每秒拉一次 → 绑定到 overlay TextBlock。
        /// </summary>
        public DiagnosticsSnapshot GetDiagnostics() => _engine.GetDiagnostics();

        internal VisualDataBag GetSharedData() => _engine.GetSharedData();
        internal VisualDataBag GetLocalBag(IChartLayer layer) => _engine.GetLocalBag(layer);

        // ==========================================
        // 静态层 BitmapCache 策略 (业务可配置)
        // ==========================================

        /// <summary>
        /// 静态层(_drawingCanvas) BitmapCache 策略,默认 On。
        /// <para>
        /// 缓存让十字光标 / tick 只动交互层时,静态层直接贴缓存位图,不用重画上千根 K 线的几何。
        /// 实测(latency-probe,集显 GPU,120 Hz):4 张图同屏、每张可见 1000 根 + 成交量,十字光标端到端
        /// 开缓存 16.6 ms、关缓存 33.2 ms(多等一帧);单图看不出差别。WPF 软件渲染(远程桌面)下缓存反而更慢
        /// (可见 120 根十字光标 18 → 33 ms),业务在这类环境可设 Off。
        /// 全图抗锯齿、横竖内容按设备像素对齐,静态画布原点平移到设备像素(SnapCanvasOrigin),开不开缓存画面逐像素一致、
        /// K 线 / 网格跟十字线同列(见 06 号文档 §14.9)。
        /// </para>
        /// </summary>
        public StaticCachePolicy StaticCachePolicy
        {
            get => _staticCachePolicy;
            set
            {
                if (_staticCachePolicy == value) return;
                _staticCachePolicy = value;
                ApplyStaticCacheState();
            }
        }
        private StaticCachePolicy _staticCachePolicy = StaticCachePolicy.On;

        /// <summary>
        /// 把策略应用到 _drawingCanvas.CacheMode。
        /// </summary>
        private void ApplyStaticCacheState()
        {
            bool shouldCache = _staticCachePolicy == StaticCachePolicy.On;

            if (shouldCache && _drawingCanvas.CacheMode == null)
            {
                _drawingCanvas.CacheMode = new BitmapCache { SnapsToDevicePixels = true, EnableClearType = false };
            }
            else if (!shouldCache && _drawingCanvas.CacheMode != null)
            {
                _drawingCanvas.CacheMode = null;
            }
            SnapCanvasOrigin();
        }

        // ==========================================
        // Loading overlay —— 蓝图加载期的 spinner 蒙层
        // ==========================================
        //
        // 默认 false —— ChartCell 本身不假设业务场景,直接 `new ChartCell { Template = schema }` 不会显示 spinner。
        // 蓝图路径由 BlueprintRunner.Run / DashboardLauncher.LaunchEx 显式 `chart.IsLoading = true`,
        // 在第一次 leaf DS publish 时 framework 翻 false,蒙层自动消失。
        // 业务可设 LoadingTemplate(DataTemplate)覆盖默认外观;Visibility=Visible 时 hit-test 透传不影响 crosshair 等交互。

        public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
            nameof(IsLoading),
            typeof(bool),
            typeof(ChartCell),
            new PropertyMetadata(false, OnIsLoadingChanged));   // 默认 false:非蓝图直接 Template 用户不背 spinner 默认值

        /// <summary>
        /// true 时显示 loading overlay(默认 ProgressBar IsIndeterminate;可由 <see cref="LoadingTemplate"/> 覆盖)。
        /// <para>初值 false —— 直接 `new ChartCell { Template = schema }` 不显示 spinner。
        /// 蓝图路径由 BlueprintRunner.Run / DashboardLauncher.LaunchEx 显式翻 true,首帧到达后自动翻 false。</para>
        /// </summary>
        public bool IsLoading
        {
            get => (bool)GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
        }

        public static readonly DependencyProperty LoadingTemplateProperty = DependencyProperty.Register(
            nameof(LoadingTemplate),
            typeof(DataTemplate),
            typeof(ChartCell),
            new PropertyMetadata(null, OnLoadingTemplateChanged));

        /// <summary>
        /// 覆盖默认 loading overlay 外观。null 时走 framework 内置 spinner(<see cref="BuildDefaultLoadingContent"/>)。
        /// 业务侧给品牌化 UI(spinner + 公司 logo + "加载中..." 文案)时设这个。
        /// </summary>
        public DataTemplate? LoadingTemplate
        {
            get => (DataTemplate?)GetValue(LoadingTemplateProperty);
            set => SetValue(LoadingTemplateProperty, value);
        }

        private readonly ContentPresenter _loadingPresenter;

        private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ChartCell cell) return;
            cell._loadingPresenter.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void OnLoadingTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ChartCell cell) cell.ApplyLoadingContent();
        }

        private void ApplyLoadingContent()
        {
            if (LoadingTemplate != null)
            {
                _loadingPresenter.ContentTemplate = LoadingTemplate;
                _loadingPresenter.Content = new object();   // DataTemplate 需要任意非空 DataContext
            }
            else
            {
                _loadingPresenter.ContentTemplate = null;
                _loadingPresenter.Content = BuildDefaultLoadingContent();
            }
        }

        // 默认 spinner:Grid 半透明蒙层 + 居中 ProgressBar IsIndeterminate。
        // 不依赖任何外部资源 / 第三方控件,纯 WPF。业务想要更花哨直接设 LoadingTemplate 覆盖。
        private static UIElement BuildDefaultLoadingContent()
        {
            var veil = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),   // 半透明黑
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bar = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 120,
                Height = 4,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x99, 0xFF)),
                BorderThickness = new Thickness(0),
            };
            var text = new TextBlock
            {
                Text = "Loading…",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stack.Children.Add(bar);
            stack.Children.Add(text);
            veil.Children.Add(stack);
            return veil;
        }

        // ==========================================
        // 3. 初始化
        // ==========================================
        static ChartCell()
        {
            // 强制要求 Template 必须是 ChartTemplate 类型
            Control.TemplateProperty.OverrideMetadata(typeof(ChartCell),
                new FrameworkPropertyMetadata(null, OnTemplateChanged, CoerceTemplateValue));
        }

        public ChartCell()
        {
            _engine = new ChartEngine(this, this);

            // diagnostics 注入到 renderer provider —— per-cell 计数无侵入
            _wpfProvider = new WpfRenderProvider(_engine.Diagnostics);

            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            // 显式断开默认 Template，防止污染
            SetCurrentValue(Control.TemplateProperty, null);

            // 全图抗锯齿(不设 EdgeMode.Aliased):横竖线 / 矩形 / 裁剪框由渲染器按设备像素对齐保持锐利,
            // 斜线 / 曲线平滑。开不开 BitmapCache 输出一样(缓存本来就按抗锯齿栅格化)。
            // 祖先在小数位置时图层原点靠画布(ChartDrawingCanvas)给图层设的 snapping guideline 贴到设备像素上(开缓存和位图模式见 SnapCanvasOrigin)。

            // [关键] hit-test 兜底:_rootContainer / Canvas 默认 Background=null → 整个 chart 不 hit-test-visible
            // → mouse 事件穿透到 Window 下面,ChartInteractionFeature 的 MouseMove 订阅永远收不到。
            // Transparent 跟 null 不同:它是个真 brush,视觉透明但 hit-test 命中,事件能 bubble 到 ChartCell。
            // (Skia 时代不需要这步,因为 SKElement 渲染的位图本身 hit-test-visible。)
            _rootContainer.Background = System.Windows.Media.Brushes.Transparent;

            // 静态画布挂了 BitmapCache:缓存位图的原点取内容包围盒,包围盒左上角常落在小数坐标(半根露出的 K 线、
            // 小数位置的折线顶点),合成时整张位图被平移,影线比覆盖层上的十字线偏左 1 个像素。
            // 透明背景把包围盒固定成整块画布,缓存位图原点就是画布原点,再由 SnapCanvasOrigin 把画布原点贴到设备像素。
            _drawingCanvas.Background = System.Windows.Media.Brushes.Transparent;

            // 触屏支持:开启 WPF Manipulation 通道,触屏事件不再 promote 成 mouse,
            // 由 ChartInteractionFeature 的 ManipulationStarting/Delta 订阅接管(双指捏合 + 单指拖)。
            // 桌面无触屏场景实际开销 ~ 0(WPF 不会主动启动 PenIMC 线程)。
            IsManipulationEnabled = true;

            // 静态画布原点贴设备像素(见 SnapCanvasOrigin);按 StaticCachePolicy 挂 / 不挂 BitmapCache(默认挂)
            _drawingCanvas.RenderTransform = _staticCanvasSnap;
            ApplyStaticCacheState();

            // 基础结构:从下到上 DrawingCanvas (静态 Software) → OverlayCanvas (交互 Software) → InteractionCanvas (Widget,懒加载)
            _rootContainer.Children.Add(_drawingCanvas); // Bottom-Middle (静态)
            _rootContainer.Children.Add(_overlayCanvas); // Middle-High (交互独立 host,跟静态层 dirty 互不牵连)
            base.Content = _rootContainer;

            // Loading overlay —— 默认 Collapsed 对齐 IsLoading=false。蓝图路径 BlueprintRunner.Run 显式翻 true 才显示,
            // 首次 leaf DS publish 时翻 false。蒙层在最顶层(Grid Z order = 后添加 == 上面)but `IsHitTestVisible=false`
            // 透 crosshair / 鼠标点击。LoadingTemplate 默认 null → 用 BuildDefaultLoadingContent 的内置 spinner
            // (ProgressBar IsIndeterminate);业务可设 ChartCell.LoadingTemplate 覆盖。
            _loadingPresenter = new ContentPresenter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            ApplyLoadingContent();
            _rootContainer.Children.Add(_loadingPresenter);

            // 💥 架构优化：已移除此处的 CompositionTarget.Rendering 订阅！
            // 绝对不允许在静置时挂载 VSync 泵，彻底消灭 GPU 空转 10% 的底层元凶！

            _rootContainer.SizeChanged += _rootContainer_SizeChanged;

            // 💥 Phase 11 / §H：Schema 可暂停生命周期宿主
            // 三种触发场景：
            //   1. Loaded/Unloaded —— TabControl 默认行为：切 Tab 时非激活的 TabItem 内容从 visual tree
            //      移除触发 Unloaded，回到时重新 Load 触发 Loaded。这是 TabControl 最可靠的事件。
            //   2. IsVisibleChanged —— 父容器 Visibility=Collapsed、虚拟化列表离屏等
            //   3. Window.StateChanged → Minimized —— WPF 最小化不改 IsVisible，必须单独挂 Window
            //
            // ChartHost 不再重复挂事件——由 ChartCell 统一承担，无论用户是走 ChartHost 包装还是
            // `new ChartCell { Template = schema }` 直接挂载（如 Window2.xaml.cs 的 KLine 用法）都能生效。
            Loaded += OnCellLoaded;
            Unloaded += OnCellUnloaded;
            IsVisibleChanged += OnCellIsVisibleChanged;

            if (DefaultRenderBackendFactory?.Invoke() is { } backend) RenderBackend = backend;
        }

        // ==========================================
        // 画布原点对齐设备像素
        // ==========================================
        //
        // 祖先摆在小数位置时,矢量图层靠画布(ChartDrawingCanvas.InsertVisual)设的 snapping guideline 由合成器把原点就近贴到设备像素(只作用于渲染)。
        // 静态画布挂了 BitmapCache 时 guideline 只在缓存位图内部生效,缓存位图本身由合成器按 floor 放到设备像素上,跟 guideline 的就近取整不一样:
        // 原点小数部分 ≥ 0.5 时整个静态层比交互层(十字线)偏左 / 偏上 1 个设备像素,线条照样清晰,数半透明像素查不出来。
        // 所以静态画布自己挂一个平移,把原点挪到最近的设备像素(取舍同 guideline):原点是整数,floor 和就近取整就一致,开不开缓存都落在同一列。
        // 开关缓存用同一条规则,运行时切换缓存画面和鼠标坐标都不跳。
        // 鼠标坐标走 GetPosition(DrawingCanvas),跟着平移 ≤ 半个设备像素,正好落在画出来的那一列上(交互层也画在就近取整的原点上)。
        // 画布的透明背景(构造函数)要留着:没有它缓存位图原点取内容包围盒,包围盒小数部分 ≥ 0.5 时仍差 1 个像素。
        //
        // 位图模式的图层:guideline 只挪几何不挪 DrawImage(实测 RTB / 真实屏幕都无效,ImageBrush 矩形、NearestNeighbor 也一样)。
        // 静态画布已经整体挪到整像素上,里面的位图不用再挪;交互画布没挂平移(挂了交互层的命中 / 鼠标口径就跟静态画布不一样了),
        // 它的位图图层套 _overlayBitmapSnap(BitmapLayerSurface.Mount 里只加在 DrawImage 外面),同一块画布上的位图图层共用这个可变 Transform。
        // 祖先 RenderTransform 动画不触发布局,所以每帧画图层前也重算一次(动画停在小数位置时,下一帧就对齐)。
        // 局限:动画进行中不重画就跟不上(静态层最多差 1 个像素,矢量图层不发虚);要跟就得常驻 CompositionTarget.Rendering,违背静置不占 GPU。
        // 不用给位图图层挂 BitmapCache:同上,合成器给缓存取整跟 guideline 不一样,偏移 20.6 / 7.5 时与矢量图层差 1 个设备像素。
        // 业务不要改 DrawingCanvas.RenderTransform,它由这里维护。
        private readonly TranslateTransform _staticCanvasSnap = new();
        private readonly TranslateTransform _overlayBitmapSnap = new();
        private EventHandler? _layoutUpdatedHandler;

        // LayoutUpdated / DPI 变化 / 每帧画图层前算:沿父链累加偏移,不分配;偏移不变时不写 Transform。
        // 窗口移动不用跟:窗口客户区原点本来就在整像素上。
        private void SnapCanvasOrigin()
        {
            double x = 0, y = 0;
            bool translateOnly = true;
            Visual? visual = _drawingCanvas;
            while (visual != null)
            {
                var offset = VisualTreeHelper.GetOffset(visual);
                x += offset.X;
                y += offset.Y;
                // 画布自己的 RenderTransform 就是 _staticCanvasSnap,算进去下一次会把上一次的平移当成已对齐,来回抖
                if (!ReferenceEquals(visual, _drawingCanvas) && VisualTreeHelper.GetTransform(visual) is { } t)
                {
                    var m = t.Value;
                    // 祖先带缩放 / 旋转时设备像素和局部坐标不再一一对应,对齐无从谈起,偏移归零
                    if (m.M11 != 1 || m.M12 != 0 || m.M21 != 0 || m.M22 != 1) { translateOnly = false; break; }
                    x += m.OffsetX;
                    y += m.OffsetY;
                }
                visual = VisualTreeHelper.GetParent(visual) as Visual;
            }

            double tx = translateOnly ? DeviceResidual(x) : 0;
            double ty = translateOnly ? DeviceResidual(y) : 0;
            SetOffset(_staticCanvasSnap, tx, ty);
            SetOffset(_overlayBitmapSnap, tx, ty);
        }

        private static void SetOffset(TranslateTransform t, double x, double y)
        {
            if (Math.Abs(t.X - x) > 1e-6) t.X = x;
            if (Math.Abs(t.Y - y) > 1e-6) t.Y = y;
        }

        // 原点到最近设备像素的距离(DIP)。半像素处向上取整,跟合成器对 guideline 的取舍一致,静态画布、位图和矢量图层落在同一列
        private double DeviceResidual(double dip)
        {
            double device = dip * _pixelsPerDip;
            double r = Math.Floor(device + 0.5) - device;
            return Math.Abs(r) < 1e-4 ? 0 : r / _pixelsPerDip;
        }

        private void OnAnyLayoutUpdated(object? sender, EventArgs e) => SnapCanvasOrigin();

        private Window? _hookedWindow;
        private EventHandler? _stateChangedHandler;

        private void OnCellLoaded(object sender, RoutedEventArgs e)
        {
            // TabControl 切回 Tab → schema.Resume:数据源 / 心跳触发 Resume,补一帧 snapshot 让 port 重写、
            //                                       layer 自然 mark dirty 触发重绘。
            if (Template is Abstractions.IPausable p) p.Resume();

            // Loaded 时注入真实 DPI:visual 已挂入 visual tree,VisualTreeHelper.GetDpi(this) 才能拿到所在屏的 DPI。
            SetPixelsPerDip(VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _wpfProvider.UpdateDpi(_pixelsPerDip);

            // LayoutUpdated 是全局事件(任何元素布局完成都会触发),父布局变化不管发生在哪一层都能跟上
            _layoutUpdatedHandler ??= OnAnyLayoutUpdated;
            LayoutUpdated -= _layoutUpdatedHandler;
            LayoutUpdated += _layoutUpdatedHandler;
            SnapCanvasOrigin();

            _hookedWindow = Window.GetWindow(this);
            if (_hookedWindow != null)
            {
                _stateChangedHandler = (sender, __) =>
                {
                    if (Template is not Abstractions.IPausable pp) return;
                    if (sender is not Window w) return;
                    if (w.WindowState == WindowState.Minimized) pp.Suspend();
                    else                                        pp.Resume();
                };
                _hookedWindow.StateChanged += _stateChangedHandler;
            }
        }

        private void OnCellUnloaded(object sender, RoutedEventArgs e)
        {
            // TabControl 切走 Tab → schema.Suspend:停定时器、Dispose 数据源 _pipelineSub 防止丧尸回包,
            // 但保留 buffer / compose 期订阅链 / lifeTimeSession 不动,等 Loaded 时 Resume 接续。
            //
            // session 的真正 Dispose 由 ChartLifecycle 绑 Window.Closed 兜底,跨 tab 不会误烧。
            if (Template is Abstractions.IPausable p) p.Suspend();

            if (_layoutUpdatedHandler != null) LayoutUpdated -= _layoutUpdatedHandler;

            if (_hookedWindow != null && _stateChangedHandler != null)
            {
                _hookedWindow.StateChanged -= _stateChangedHandler;
                _hookedWindow = null;
                _stateChangedHandler = null;
            }
        }

        private void OnCellIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Template is not Abstractions.IPausable p) return;
            if ((bool)e.NewValue) p.Resume();
            else                  p.Suspend();
        }

        // 跨屏拖动 / 系统 DPI 变更 → 重新注入到 renderer,FormattedText 重新按真实 DPI 排版。
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _wpfProvider.UpdateDpi(newDpi.PixelsPerDip);

            // 按物理像素做 LOD 的图层要跟着重录:走环境纪元,下一帧全量重投影 + 重绘
            SetPixelsPerDip(newDpi.PixelsPerDip);
            SnapCanvasOrigin();
            RequestUpdate(_ => (Template as IFeatureProjector)?.InvalidateEnvironment());
        }

        private void _rootContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width == 0 || e.NewSize.Height == 0) return;

            // 💥 依然保留亚像素震荡拦截防线！
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < SubPixelResizeThreshold &&
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < SubPixelResizeThreshold)
            {
                return;
            }

            // 4. 写入 ChartCell 的全局共享黑板
            RequestUpdate(ctx =>
            {
                // 1. 发送新的物理尺寸
                ctx.Shared().PublishData(new ViewportSizeTrait(e.NewSize.Width, e.NewSize.Height));

                // 如果你之前封装了 InvalidateEnvironment，就在这里调用
                if (Template is IFeatureProjector projector)
                {
                    projector.InvalidateEnvironment();
                }
            });
        }

        private double _pixelsPerDip = 1.0;

        // DPI 变了:同步给引擎(图层录制前写进 ChartLayer.PixelsPerDip)
        private void SetPixelsPerDip(double value)
        {
            _pixelsPerDip = value;
            _engine.PixelsPerDip = value;
        }

        /// <summary>所在屏的 DPI 缩放(物理像素 / DIP)。Loaded / DpiChanged 时更新,未挂入可视树前为 1。</summary>
        internal double PixelsPerDip => _pixelsPerDip;

        // 帧管线在引擎里;下面是测试 / 基准 / 诊断沿用的入口
        internal void ExecutePipeline(RenderContext ctx, PlotMode mode) => _engine.ExecutePipeline(ctx, mode);

        /// <summary>
        /// 申请一个事务,把 updateAction 排入下一帧的 pending 队列;
        /// 队列首次非空时才挂 CompositionTarget.Rendering(动态 VSync),帧尾立即拔除以保证静置 0 GPU 占用。
        /// </summary>
        /// <param name="updateAction">在下一帧 OnCompositionTargetRendering 内执行的事务体(写黑板 / 触发 invalidate 等)。</param>
        public void RequestUpdate(Action<RenderContext> updateAction) => _engine.RequestUpdate(updateAction);

        // 引擎第一笔待办入队时调(持引擎调度锁,任意线程)
        void IChartCellHost.ArmFrame()
        {
            // 💥 按需挂载机制 (Dynamic VSync Hook)
            // 只有真有任务了，才去唤醒 WPF 的 VSync 渲染泵。
            // UI 线程同步订阅,绕过 Dispatcher.InvokeAsync 的一帧延迟(crosshair tracking 关键路径)；
            // 后台线程仍走 InvokeAsync 跨线程投递。
            if (Dispatcher.CheckAccess())
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
                CompositionTarget.Rendering += OnCompositionTargetRendering;
            }
            else
            {
                Dispatcher.InvokeAsync(() =>
                {
                    CompositionTarget.Rendering -= OnCompositionTargetRendering;
                    CompositionTarget.Rendering += OnCompositionTargetRendering;
                }, System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        // 待办换出后调(持调度锁,UI 线程):画完即休眠,从 WPF 的 VSync 泵上拔除
        void IChartCellHost.DisarmFrame() => CompositionTarget.Rendering -= OnCompositionTargetRendering;

        private void OnCompositionTargetRendering(object? sender, EventArgs e) => _engine.OnFrame();

        /// <summary>
        /// 测量工具用(Charting.Benchmarks --render-probe):不等 VSync,立刻同步跑一帧,
        /// 跟 CompositionTarget 回调里那一帧是同一段代码(含 RequestUpdate 排队的事务,如 SizeChanged)。
        /// 返回本帧重录的图层数。
        /// </summary>
        internal int RunFrameNow(PlotMode mode) => _engine.RunFrameNow(mode);

        /// <summary>有排队没跑的帧。指针路由器按下时据此决定要不要先同步跑一帧(让命中用的区域是最新的)。</summary>
        internal bool HasPendingFrame => _engine.HasPendingFrame;

        /// <summary>本 cell 上各 Feature 发布的指针区域(纯数据,路由器在输入时读)。</summary>
        internal Interaction.PointerRegionStore PointerRegions => _engine.PointerRegions;

        /// <summary>微基准用:丢掉排队的事务(只测 ProjectAll 时,黑板写入排进来的空回调不跑帧也不能越积越多)。</summary>
        internal void DiscardPendingUpdates() => _engine.DiscardPendingUpdates();

        /// <summary>最近一帧 ExecutePipeline 判脏后重录的图层数(诊断用)。</summary>
        internal int LastFrameDirtyLayers => _engine.LastFrameDirtyLayers;

        /// <summary>把后台录制好的图层指令真正"画"到屏幕上(见 <see cref="ChartEngine"/>)。</summary>
        internal void Invalidate() => _engine.Invalidate();

        // 引擎上屏:图层已 SwapBuffer、尚未清脏标记
        void IChartCellHost.Present(IReadOnlyList<ChartLayer> staticLayers, IReadOnlyList<ChartLayer> overlayLayers)
        {
            // 管线 A: 矢量层 (保留模式，仅给脏图层发新指令)。挂了后端就交给后端,否则走 WPF DrawingVisual
            if (_renderBackend != null) _renderBackend.Present(staticLayers, overlayLayers);
            else RenderWpfLayers(staticLayers, overlayLayers);

            // 管线 B: WPF 控件层 (仅给脏图层同步新的控件池摆放)
            RenderWidgetLayers();
        }

        /// <summary>
        /// --- 管线 A: WPF Drawing ---
        /// 同时刷 _drawingCanvas (静态层) 和 _overlayCanvas (交互层) 各自的 Visual。
        /// 两个 UIElement 各管自己的 dirty rect,WPF compositor 间不会牵连。
        /// </summary>
        private void RenderWpfLayers(IReadOnlyList<ChartLayer> staticLayers, IReadOnlyList<ChartLayer> overlayLayers)
        {
            SnapCanvasOrigin();
            RenderVisualLayers(staticLayers, _drawingCanvas);
            RenderVisualLayers(overlayLayers, _overlayCanvas);
            _replayAllWpfLayers = false;
        }

        // 从外部后端切回 WPF 的那一帧:DrawingVisual 里还是挂后端之前的旧内容,所有图层都要按前台指令重放一次
        private bool _replayAllWpfLayers;

        // 每个图层按自己的绘制策略上屏:Software → WpfRenderProvider 回放进 DrawingContext;
        // Bitmap → RasterRenderProvider 光栅化进图层自己的 WriteableBitmap。
        // 不脏的图层只在"想要的策略 ≠ 当前挂着的"时才重放前台指令(策略刚切换的那一帧)。
        private void RenderVisualLayers(IReadOnlyList<ChartLayer> layers, ChartDrawingCanvas host)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                var cl = layers[i];
                if (cl.HostVisual is not LayerVisual visual) continue;
                bool wantBitmap = (RenderModeOverride ?? cl.Mode) == RenderMode.Bitmap;
                bool isBitmap = visual.BitmapSurface is { IsMounted: true };
                if (!cl.IsDirty && wantBitmap == isBitmap && !_replayAllWpfLayers) continue;
                if (cl.Buffer is not LayerBuffer buffer) continue;

                if (wantBitmap && TryRenderBitmap(visual, buffer, host)) continue;

                // 矢量路径;从位图切回(或位图这一帧画不了)时释放位图
                visual.ReleaseBitmap();
                using var dc = visual.RenderOpen();
                buffer.Execute(_wpfProvider, dc);
            }
        }

        private bool TryRenderBitmap(LayerVisual visual, LayerBuffer buffer, FrameworkElement host)
        {
            double w = host.ActualWidth, h = host.ActualHeight;
            if (w <= 0 || h <= 0) return false;                                  // 还没布局:先走矢量
            if (!RasterDrawingRenderer.CanRasterize(buffer.Drawing)) return false; // DrawVideo 只能交给 WPF

            _rasterProvider ??= new RasterRenderProvider(_engine.Diagnostics);
            var surface = visual.BitmapSurface ??= new BitmapLayerSurface();
            surface.Render(buffer, _rasterProvider, w, h, _pixelsPerDip);
            using var dc = visual.RenderOpen();
            surface.Mount(dc, ReferenceEquals(host, _overlayCanvas) ? _overlayBitmapSnap : Transform.Identity); // 固定五条 RenderData(裁剪 / 平移 / 位图 / 出栈 ×2),跟图层指令数无关
            return true;
        }

        // --- 管线 B: Widget Controls ---
        private void RenderWidgetLayers()
        {
            // for + 索引避免 IReadOnlyList<T> 上 foreach 的 IEnumerator 装箱(每帧都跑)。
            var layers = ActiveLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                // 【极其重要的优化】：只有图层变脏了，才去同步它的控件池！避免每帧无意义的遍历。
                if (layers[i] is ChartLayer cl && cl.IsDirty)
                {
                    if (cl.Buffer is LayerBuffer buffer)
                    {
                        var pool = GetOrAddWidgetPool(cl); // 使用上一轮优化好的 Pool

                        // 无论空还是有指令，一把梭哈交给 Pool 自己去处理显隐和复用
                        pool.Sync(buffer.Widget.Commands);
                    }
                }
            }
        }

        // --- Widget Host Factory ---
        private LayerWidgetPool GetOrAddWidgetPool(IChartLayer layer)
        {
            if (_widgetRegistry.TryGetValue(layer, out var pool)) return pool;

            EnsureInteractionCanvasInitialized();

            pool = new LayerWidgetPool(_interactionCanvas!);
            _widgetRegistry[layer] = pool;
            return pool;
        }

        // =================================================================
        // 2. 核心属性
        // =================================================================

        /// <summary>静态矢量层宿主(Grid / Axis / Series 等非交互图层)。可挂 BitmapCache,见 <see cref="StaticCachePolicy"/>。</summary>
        public ChartDrawingCanvas DrawingCanvas => _drawingCanvas;

        /// <summary>顶层交互控件宿主(Tooltip / WPF 控件)。懒加载:首次 AddLayer Widget 类时才创建。</summary>
        public InteractionCanvas? InteractionCanvas => _interactionCanvas;

        // =================================================================
        // 3. 模板与生命周期 (最关键部分)
        // =================================================================

        /// <summary>
        /// WPF Template 应用钩子。
        /// 数据流:首次 measure 触发 → 构造一个 ChartSession(compose 期订阅生命周期容器)
        /// → schema.ComposeAll(让所有 Feature 的 OnCompose 跑一遍,登记图层 + 订阅事件)
        /// → schema.Aspect.Decorate 把外壳 UI 包到 PART_Root → BindTo 把 session 绑到 ChartLifecycle.ActiveSession 附加属性。
        /// 之后第一次 SubmitSync 让所有图层吃到当前 trait 快照。
        /// </summary>
        public override void OnApplyTemplate()
        {
#if DEBUG
            // 诊断:同一 schema 实例多次触发 = 异常(栈应指向虚拟化容器 / 强制 ApplyTemplate / 重复 SetValue 等)。
            // 不同 schema = 正常的 Template 替换路径(蓝图 reload)。需要追源时把 Environment.StackTrace 接回来。
            System.Diagnostics.Debug.WriteLine(
                $"[ChartCell.OnApplyTemplate] cell=#{RuntimeHelpers.GetHashCode(this)} schema=#{(Template != null ? RuntimeHelpers.GetHashCode(Template) : 0)}({Template?.GetType().Name ?? "null"})");
#endif

            var rootDecorator = GetTemplateChild("PART_Root") as AdornerDecorator;
            if (rootDecorator != null && Template is ChartSchema schema)
            {
                // 新 session → ComposeAll → 包外壳 → 首次提交(顺序约束见 ChartEngine.Compose)
                _engine.Compose(schema, s => ApplyDecoration(rootDecorator, s));
            }
        }

        // 外壳 UI 的种子:业务 Content 的承载者。重建外壳时先把 Content 从旧种子上摘下,
        // 否则同一个元素挂到新种子上会因"已有逻辑父"抛异常。
        private ContentPresenter? _decorationSeed;

        private void ApplyDecoration(AdornerDecorator rootDecorator, ChartSchema schema)
        {
            if (_decorationSeed != null) _decorationSeed.Content = null;
            rootDecorator.Child = null;
            _decorationSeed = new ContentPresenter { Content = base.Content };
            // 沿 Aspect 链从内到外包:实现 IWpfDecorator 的切面各包一层
            rootDecorator.Child = (UIElement)schema.Aspect.DecorateHost(_decorationSeed,
                (aspect, inner) => aspect is IWpfDecorator d ? d.Decorate((UIElement)inner) : inner);
        }

        /// <summary>
        /// Aspect 链变了(Transact 增删了带外壳 UI 的 Feature)后重建外壳。模板还没应用时什么也不做 ——
        /// 之后 OnApplyTemplate 会按当时的 Aspect 链装饰。
        /// </summary>
        void IChartCellHost.RefreshDecoration() => RefreshDecoration();

        internal void RefreshDecoration()
        {
            if (Template is ChartSchema schema && GetTemplateChild("PART_Root") is AdornerDecorator rootDecorator)
                ApplyDecoration(rootDecorator, schema);
        }

        /// <summary>
        /// 显式清理:把 lifeTimeSession 烧掉(compose 期所有订阅) + DecomposeAll 把 schema 状态彻底回收。
        /// 业务侧在 cell 真不再需要时调用——典型场景:Window.Closed、动态移除 cell 不再挂回。
        /// 不调也没大问题,GC 最终会回收(只是回收时机不确定)。
        /// </summary>
        public void Shutdown()
        {
            // 手势取消 → DecomposeAll → 烧掉 session(见 ChartEngine.Shutdown)
            _engine.Shutdown();

            // 后端归 cell 所有:Detach + Dispose,矢量画布恢复可见(cell 若被复用就回到 WPF 路径)
            if (_renderBackend != null)
            {
                ReleaseRenderBackend();
                _replayAllWpfLayers = true;
                _drawingCanvas.Visibility = Visibility.Visible;
                _overlayCanvas.Visibility = Visibility.Visible;
            }
        }

        // =================================================================
        // 4. 层级管理逻辑(注册表与 Z 序在引擎里,这里是 WPF 上屏对象的增删)
        // =================================================================

        /// <summary>检查是否存在指定名称的图层(按 Layer.Name 字符串匹配)。</summary>
        public bool HasLayer(string name) => _engine.HasLayer(name);

        /// <summary>
        /// ⚠️ 添加非托管图层 (高危操作！)
        /// 此方法脱离了 Schema 的生命周期管理与 Transact 事务。
        /// 调用此方法的开发者，必须自行在适当的时机调用 RemoveUnmanagedLayer，否则会导致严重的内存泄漏与图层重影！
        /// <para>
        /// 不用 Feature 时这是给裸 ChartCell 挂层的入口:图层的数据要通过返回的代理喂(<c>PublishData</c> /
        /// <c>UpdateData</c>,任何线程都可以),喂进去的算本层数据,下一帧唤醒图层并录制,之后再喂就按引用判脏重录。
        /// 没人喂的图层一直不画(深度睡眠,见 00 号文档 §16.1)。用 Schema 时改在 Feature 里挂层、在 OnProject 里
        /// <c>ctx.For(layer)</c> 发数据。
        /// </para>
        /// </summary>
        public VisualProxy<TLayer> AddUnmanagedLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
            => _engine.AddUnmanagedLayer(layer);

        /// <summary>Feature.AttachLayer 用(见 <see cref="ChartEngine.AttachFeatureLayer{TLayer}"/>)。</summary>
        internal VisualProxy<TLayer> AttachFeatureLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
            => _engine.AttachFeatureLayer(layer);

        /// <summary>当前所有活跃图层的只读列表(静态层 + 交互层拼接结果)。读取无锁,内部走 volatile 缓存。</summary>
        public IReadOnlyList<IChartLayer> ActiveLayers => _engine.ActiveLayers;

        /// <summary>按 Name 查图层(找不到返回 null)。配合 <see cref="HasLayer"/> 使用。</summary>
        public IChartLayer? GetLayer(string name) => _engine.GetLayer(name);

        /// <summary>构造一个无 session 的渲染上下文。仅用于 Shutdown / OnTemplateChanged 等"无主"清理路径。</summary>
        internal RenderContext CreateContext() => _engine.CreateContext();

        /// <summary>
        /// 安全移除图层，并彻底清理其关联的物理资源、控件池与状态
        /// </summary>
        public void RemoveUnmanagedLayer(IChartLayer layer) => _engine.RemoveUnmanagedLayer(layer);

        // 图层的 WPF 上屏对象:每层一个 DrawingVisual(位图模式下再挂一块位图),按引擎给的 Z 序插进画布。
        // 交互层(Level == Interaction)进 _overlayCanvas,跟静态层 dirty 隔离;其它进 _drawingCanvas。
        void IChartCellHost.InsertLayerVisual(ChartLayer layer, bool overlay, int index)
        {
            var visual = layer.HostVisual as LayerVisual ?? new LayerVisual();
            layer.HostVisual = visual;
            (overlay ? _overlayCanvas : _drawingCanvas).InsertVisual(index, visual);
        }

        void IChartCellHost.RemoveLayerVisual(ChartLayer layer, bool overlay)
        {
            if (layer.HostVisual is LayerVisual visual) (overlay ? _overlayCanvas : _drawingCanvas).RemoveVisual(visual);
        }

        void IChartCellHost.DestroyLayerWidgets(IChartLayer layer)
        {
            if (_widgetRegistry.TryGetValue(layer, out var widgetPool))
            {
                // 1. 从字典中注销
                _widgetRegistry.Remove(layer);

                // 2. 【核心魔法】：销毁整个池子！
                // 这会将该图层在 InteractionCanvas 上创建的 N 个 ContentPresenter 全部 Remove 掉。
                // 彻底消灭屏幕上的“幽灵残影”和 WPF 强引用。
                widgetPool.Destroy();
            }
        }

        void IChartCellHost.OnLayerRemoved(ChartLayer layer) => _renderBackend?.OnLayerRemoved(layer);

        void IChartCellHost.OnEnvironmentChanged()
        {
            if (_renderBackend != null)
            {
                _renderBackend.OnEnvironmentChanged();
                return;
            }
            // 矢量层和位图层都按现有指令重放一遍(资源画刷按新主题重新解析)
            _wpfProvider.ClearResourceCaches();
            _rasterProvider?.ClearCaches();
            _replayAllWpfLayers = true;
        }

        IDisposable IChartCellHost.CreatePointerInput(Interaction.PointerRouter router, Interaction.PointerRegionStore store,
            Func<DataBlackboard?> board, Func<HevoRect> plotArea, Func<double> dragThreshold)
            => new Interaction.WpfPointerAdapter(this, router, store, board, plotArea, dragThreshold);

        // UI 防御:宿主进程即将关闭(或测试里没有 Application)时不再渲染
        ChartHostServices IChartCellHost.Services => WpfHostRegistration.Services;

        bool IChartCellHost.IsShuttingDown => Application.Current == null || Dispatcher.HasShutdownStarted;

        void IChartCellHost.Post(Action action, HostPostPriority priority)
            => Dispatcher.BeginInvoke(action, priority == HostPostPriority.Background
                ? System.Windows.Threading.DispatcherPriority.Background
                : System.Windows.Threading.DispatcherPriority.DataBind);

        void IChartCellHost.WhenLoaded(Action action)
        {
            if (IsLoaded)
            {
                action();
                return;
            }
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                action();
            };
            Loaded += handler;
        }

        private void EnsureInteractionCanvasInitialized()
        {
            if (_interactionCanvas == null)
            {
                // 懒加载：只有当真正添加了交互层时才创建 Canvas
                _interactionCanvas = new InteractionCanvas();
                // 确保交互层在绘图层之上 (Z-Order)
                // Grid 默认行为：后添加的在上面
                _rootContainer.Children.Add(_interactionCanvas);
            }
        }

        // =================================================================
        // 5. 辅助与校验
        // =================================================================

        private static object? CoerceTemplateValue(DependencyObject d, object baseValue)
        {
            // 设计模式下不做限制，方便预览
            if (DesignerProperties.GetIsInDesignMode(d)) return baseValue;

            // 运行时严格限制：如果不是 ChartTemplate，直接拒绝 (返回 null)
            return baseValue is ChartTemplate ? baseValue : null;
        }

        private static void OnTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue != null && e.NewValue is not ChartTemplate)
            {
                throw new InvalidOperationException($"ChartCell Template must be of type {nameof(ChartTemplate)}.");
            }

#if DEBUG
            // 诊断:记录每次 Template 赋值。如果同一 schema 实例反复出现(old==new),
            // 说明业务侧某处重复设了同样的 Template,DependencyProperty 还是会 raise PropertyChanged。
            int cellHash = d is ChartCell c ? RuntimeHelpers.GetHashCode(c) : 0;
            int oldHash = e.OldValue != null ? RuntimeHelpers.GetHashCode(e.OldValue) : 0;
            int newHash = e.NewValue != null ? RuntimeHelpers.GetHashCode(e.NewValue) : 0;
            System.Diagnostics.Debug.WriteLine(
                $"[ChartCell.OnTemplateChanged] cell=#{cellHash} old=#{oldHash} new=#{newHash}");
#endif

            if (d is not ChartCell cell) return;
            if (e.OldValue is ChartTemplate { Schema: { } oldSchema })
            {
                // 给旧图纸最后一次机会清理自己:触发旧 Schema (如 ReactiveSchema) 的 DecomposeAll，
                // 进而触发所有 Feature 的连根拔起和图层卸载！
                cell._engine.DecomposeReplaced(oldSchema);
            }
            cell._engine.Schema = (e.NewValue as ChartTemplate)?.Schema;
        }

        /// <summary>
        /// 挂上的 schema。赋值时包一层 <see cref="ChartTemplate"/> 交给 WPF 的 <see cref="Control.Template"/>,
        /// 由 WPF 在布局时触发 <see cref="OnApplyTemplate"/> 装配;换掉时旧 schema 先卸载。同一个 schema 重复赋值什么也不做。
        /// </summary>
        public new ChartSchema? Template
        {
            get => (base.Template as ChartTemplate)?.Schema;
            set
            {
                if (ReferenceEquals(Template, value)) return;
                base.Template = value == null ? null : new ChartTemplate(value);
            }
        }

        // 隐藏基类 Content 属性，避免外部开发者误用
        [EditorBrowsable(EditorBrowsableState.Never)]
        public new object Content
        {
            get => base.Content;
            private set => base.Content = value;
        }
    }

    public static class WpfChartPipelineExtensions
    {
        /// <summary>
        /// 💥 [全局流] 为整个 Chart 播种全局特质。
        /// </summary>
        public static ChartCell Seed<TTrait>(this ChartCell chart, TTrait trait)
            where TTrait : class, IVisualTrait
        {
            chart.GetSharedData().Publish<TTrait>(trait);
            return chart;
        }
    }
}
