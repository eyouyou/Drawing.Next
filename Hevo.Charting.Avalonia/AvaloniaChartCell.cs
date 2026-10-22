using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.Skia;
using SkiaSharp;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// Avalonia 下的图表单元(对应 WPF 的 ChartCell):<c>new AvaloniaChartCell { Template = schema }</c>。
    /// <para>
    /// 帧管线、图层注册表、黑板都在核心的 <see cref="ChartEngine"/> 里,本控件只做 Avalonia 相关的事:
    /// 按需挂 <see cref="TopLevel.RequestAnimationFrame"/>、把脏图层的前台指令用 <see cref="SkiaChartRenderer"/>
    /// 录成 SKPicture、在静态面 / 交互面上回放、控件类图层(悬浮窗)摆进控件层、输入接到指针路由器。
    /// </para>
    /// <para>
    /// 结构(从下到上):静态面(Grid / Axis / Series)→ 交互面(十字线等 Interaction 级图层)→ 控件层(懒建)→ loading 蒙层。
    /// 两块面分开,十字光标移动只重画交互面,K 线那块的绘制数据不动。
    /// </para>
    /// </summary>
    public class AvaloniaChartCell : Decorator, IChartCell, IChartCellHost
    {
        // 亚像素尺寸抖动不触发重排(同 WPF ChartCell)
        private const double SubPixelResizeThreshold = 0.5;


        private readonly ChartEngine _engine;
        private readonly Grid _root = new() { Background = Brushes.Transparent };
        private readonly LayerSurface _staticSurface = new();
        private readonly LayerSurface _overlaySurface = new();
        private readonly Border _loadingVeil;
        private Canvas? _widgetCanvas;
        private readonly Dictionary<IChartLayer, AvaloniaWidgetPool> _widgetPools = new();

        private readonly SkiaChartRenderer _renderer = new(AvaloniaSkiaResources.Instance);
        private readonly List<PictureRef?> _framePictures = new();
        private int _staticCount = -1, _overlayCount = -1;
        private bool _staticChanged = true, _overlayChanged = true;

        private ChartSchema? _schema;
        private bool _composed;
        private Decorator? _decorationSeed;

        private TopLevel? _topLevel;
        private Window? _window;
        private bool _frameArmed;
        private bool _framePending;
        private Size _lastSize;

        static AvaloniaChartCell() => AvaloniaHostRegistration.EnsureRegistered();

        public AvaloniaChartCell()
        {
            _engine = new ChartEngine(this, this);

            _loadingVeil = BuildLoadingVeil();
            _root.Children.Add(_staticSurface);
            _root.Children.Add(_overlaySurface);
            _root.Children.Add(_loadingVeil);
            _root.SizeChanged += OnRootSizeChanged;
            Child = _root;

            // 键盘(Esc 取消、方向键平移)要能拿焦点
            Focusable = true;
        }

        private static Border BuildLoadingVeil() => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = new TextBlock
            {
                Text = "Loading…",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // ==========================================
        // IChartCell
        // ==========================================

        /// <inheritdoc/>
        public ChartEngine Engine => _engine;

        /// <summary>
        /// 挂上的 schema。挂进可视树时装配(已在树里就立即装配);换掉时旧 schema 先卸载。同一个 schema 重复赋值什么也不做。
        /// </summary>
        public ChartSchema? Template
        {
            get => _schema;
            set
            {
                if (ReferenceEquals(_schema, value)) return;
                var old = _schema;
                if (old != null && _composed) _engine.DecomposeReplaced(old);
                _composed = false;
                _schema = value;
                _engine.Schema = value;
                if (value == null) ResetDecoration();
                else if (_topLevel != null) ComposeSchema();
            }
        }

        public double ActualWidth => Bounds.Width;
        public double ActualHeight => Bounds.Height;

        public bool IsLoading
        {
            get => _loadingVeil.IsVisible;
            set => _loadingVeil.IsVisible = value;
        }

        public void RequestUpdate(Action<RenderContext> updateAction) => _engine.RequestUpdate(updateAction);

        public IReadOnlyList<IChartLayer> ActiveLayers => _engine.ActiveLayers;
        public bool HasLayer(string name) => _engine.HasLayer(name);
        public IChartLayer? GetLayer(string name) => _engine.GetLayer(name);
        public VisualProxy<TLayer> AddUnmanagedLayer<TLayer>(TLayer layer) where TLayer : IChartLayer => _engine.AddUnmanagedLayer(layer);
        public void RemoveUnmanagedLayer(IChartLayer layer) => _engine.RemoveUnmanagedLayer(layer);
        public DiagnosticsSnapshot GetDiagnostics() => _engine.GetDiagnostics();

        /// <summary>彻底卸载 schema、释放 compose 期订阅与录好的 picture(cell 不再使用时调)。</summary>
        public void Shutdown()
        {
            _engine.Shutdown();
            _composed = false;
            _staticSurface.Clear();
            _overlaySurface.Clear();
        }

        /// <summary>所在屏的 DPI 缩放(物理像素 / DIP)。挂进可视树 / 缩放变化时更新,之前为 1。</summary>
        public double PixelsPerDip => _engine.PixelsPerDip;

        // 测试 / 诊断用的入口(同 WPF ChartCell)
        internal int RunFrameNow(PlotMode mode = PlotMode.Sync) => _engine.RunFrameNow(mode);
        internal bool HasPendingFrame => _engine.HasPendingFrame;
        internal LayerSurface StaticSurface => _staticSurface;
        internal LayerSurface OverlaySurface => _overlaySurface;
        internal Canvas? WidgetCanvas => _widgetCanvas;

        /// <summary>指针坐标的参照(对应 WPF 的 DrawingCanvas):外壳装饰之内、图层画布的左上角。</summary>
        internal Control PlotSurface => _staticSurface;

        // ==========================================
        // 装配 / 外壳
        // ==========================================

        private void ComposeSchema()
        {
            if (_schema == null || _composed) return;
            _composed = true;
            // 新 session → ComposeAll → 包外壳 → 首次提交(顺序约束见 ChartEngine.Compose)
            _engine.Compose(_schema, ApplyDecoration);
        }

        private void ApplyDecoration(ChartSchema schema)
        {
            DetachRoot();
            _decorationSeed = new Decorator { Child = _root };
            // 沿 Aspect 链从内到外包:实现 IAvaloniaDecorator 的切面各包一层
            Child = (Control)schema.Aspect.DecorateHost(_decorationSeed,
                (aspect, inner) => aspect is IAvaloniaDecorator d ? d.Decorate((Control)inner) : inner);
        }

        private void ResetDecoration()
        {
            DetachRoot();
            Child = _root;
        }

        // _root 在任何时刻只能有一个父:重建外壳前先从旧种子 / 本控件上摘下
        private void DetachRoot()
        {
            if (_decorationSeed != null) { _decorationSeed.Child = null; _decorationSeed = null; }
            if (ReferenceEquals(Child, _root)) Child = null;
        }

        void IChartCellHost.RefreshDecoration()
        {
            if (_schema != null && _composed) ApplyDecoration(_schema);
        }

        // ==========================================
        // 可视树生命周期
        // ==========================================

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            HookApplicationExit();
            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel != null)
            {
                _topLevel.ScalingChanged += OnScalingChanged;
                SetPixelsPerDip(_topLevel.RenderScaling);
            }
            _window = _topLevel as Window;
            if (_window != null) _window.PropertyChanged += OnWindowPropertyChanged;

            ComposeSchema();
            // 挂进树之前请求过的帧:现在才有 TopLevel 可以挂回调
            if (_frameArmed) ScheduleFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
            if (_window != null) _window.PropertyChanged -= OnWindowPropertyChanged;
            _topLevel = null;
            _window = null;
        }

        // 切 Tab / 移出树:schema 暂停(停定时器、断数据源),回来时恢复;session 不烧(同 WPF Loaded / Unloaded)
        protected override void OnLoaded(global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (_schema is IPausable p) p.Resume();
        }

        protected override void OnUnloaded(global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            if (_schema is IPausable p) p.Suspend();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && _schema is IPausable p)
            {
                if (change.GetNewValue<bool>()) p.Resume();
                else p.Suspend();
            }
        }

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // 最小化不改 IsVisible,单独跟窗口状态
            if (e.Property != Window.WindowStateProperty || _schema is not IPausable p) return;
            if (e.GetNewValue<WindowState>() == WindowState.Minimized) p.Suspend();
            else p.Resume();
        }

        private void OnScalingChanged(object? sender, EventArgs e)
        {
            if (_topLevel == null) return;
            SetPixelsPerDip(_topLevel.RenderScaling);
            // 按物理像素做 LOD 的图层要跟着重录:走环境纪元,下一帧全量重投影 + 重绘
            RequestUpdate(_ => (_schema as IFeatureProjector)?.InvalidateEnvironment());
        }

        private void SetPixelsPerDip(double value)
        {
            if (value <= 0 || double.IsNaN(value) || value == _engine.PixelsPerDip) return;
            _engine.PixelsPerDip = value;
        }

        private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            var size = e.NewSize;
            if (size.Width == 0 || size.Height == 0) return;
            if (Math.Abs(size.Width - _lastSize.Width) < SubPixelResizeThreshold &&
                Math.Abs(size.Height - _lastSize.Height) < SubPixelResizeThreshold)
                return;
            _lastSize = size;

            RequestUpdate(ctx =>
            {
                ctx.Shared().PublishData(new ViewportSizeTrait(size.Width, size.Height));
                (_schema as IFeatureProjector)?.InvalidateEnvironment();
            });
        }

        // ==========================================
        // 帧调度
        // ==========================================

        void IChartCellHost.ArmFrame()
        {
            // UI 线程同步挂(十字光标跟手不多等一轮调度);后台线程投递到 UI 线程
            if (Dispatcher.UIThread.CheckAccess()) ScheduleFrame();
            else Dispatcher.UIThread.Post(ScheduleFrame, DispatcherPriority.Render);
        }

        void IChartCellHost.DisarmFrame() => _frameArmed = false;

        // RequestAnimationFrame 是一次性回调:挂着时只挂一次,回调里没活(已被拔除)就什么也不做
        private void ScheduleFrame()
        {
            _frameArmed = true;
            if (_framePending || _topLevel == null) return;
            _framePending = true;
            _topLevel.RequestAnimationFrame(OnAnimationFrame);
        }

        private void OnAnimationFrame(TimeSpan _)
        {
            _framePending = false;
            if (!_frameArmed) return;
            _engine.OnFrame();
            // 帧内回流的 RequestUpdate 已重新 Arm(_frameArmed = true),这里补挂下一帧
            if (_frameArmed) ScheduleFrame();
        }

        // ==========================================
        // 上屏
        // ==========================================

        void IChartCellHost.Present(IReadOnlyList<ChartLayer> staticLayers, IReadOnlyList<ChartLayer> overlayLayers)
        {
            // picture 按设备像素录制:DPI 变了所有图层按新 DPI 重录
            if (_renderer.PixelsPerDip != _engine.PixelsPerDip)
            {
                _renderer.PixelsPerDip = _engine.PixelsPerDip;
                MarkAllPicturesStale();
            }

            var bounds = new SKRect(0, 0,
                (float)Math.Max(1, _root.Bounds.Width * _engine.PixelsPerDip),
                (float)Math.Max(1, _root.Bounds.Height * _engine.PixelsPerDip));

            if (Record(staticLayers, bounds, ref _staticCount) || _staticChanged)
            {
                _staticSurface.SetPictures(_framePictures);
                _staticChanged = false;
            }
            if (Record(overlayLayers, bounds, ref _overlayCount) || _overlayChanged)
            {
                _overlaySurface.SetPictures(_framePictures);
                _overlayChanged = false;
            }
            _framePictures.Clear();

            SyncWidgets();
        }

        // 脏图层重录 picture,结果按 Z 序留在 _framePictures;有重录或图层数变了返回 true(整面重画)
        private bool Record(IReadOnlyList<ChartLayer> layers, SKRect bounds, ref int lastCount)
        {
            bool changed = layers.Count != lastCount;
            lastCount = layers.Count;
            _framePictures.Clear();
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (layer.HostVisual is not AvaloniaLayerVisual visual) continue;
                if (layer.IsDirty || visual.Stale)
                {
                    visual.Replace(layer.Buffer is LayerBuffer lb ? RecordPicture(lb, bounds) : null);
                    visual.Stale = false;
                    changed = true;
                }
                _framePictures.Add(visual.Picture);
            }
            return changed;
        }

        private PictureRef? RecordPicture(LayerBuffer buffer, SKRect bounds)
        {
            if (buffer.Drawing.Commands.Count == 0) return null;
            using var recorder = new SKPictureRecorder();
            _renderer.Render(buffer.Drawing, recorder.BeginRecording(bounds));
            return new PictureRef(recorder.EndRecording());
        }

        private void MarkAllPicturesStale()
        {
            MarkStale(_engine.StaticLayers);
            MarkStale(_engine.OverlayLayers);
            _staticChanged = _overlayChanged = true;
        }

        private static void MarkStale(IReadOnlyList<ChartLayer> layers)
        {
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].HostVisual is AvaloniaLayerVisual v) v.Stale = true;
        }

        void IChartCellHost.OnEnvironmentChanged()
        {
            _renderer.ClearResourceCaches();
            MarkAllPicturesStale();
        }

        void IChartCellHost.InsertLayerVisual(ChartLayer layer, bool overlay, int index)
        {
            // 新挂的图层要等它第一次录制(变脏)才有内容;Stale 让它即使先上屏一次也按前台指令录
            if (layer.HostVisual is not AvaloniaLayerVisual) layer.HostVisual = new AvaloniaLayerVisual { Stale = true };
            if (overlay) _overlayChanged = true; else _staticChanged = true;
        }

        void IChartCellHost.RemoveLayerVisual(ChartLayer layer, bool overlay)
        {
            if (overlay) _overlayChanged = true; else _staticChanged = true;
        }

        void IChartCellHost.OnLayerRemoved(ChartLayer layer)
        {
            // picture 随图层 Dispose(HostVisual)放掉;合成面持有的引用在下一次 Present 换组时放掉
            _staticChanged = _overlayChanged = true;
        }

        // ==========================================
        // 控件层(悬浮窗等)
        // ==========================================

        private void SyncWidgets()
        {
            var layers = _engine.ActiveLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] is ChartLayer { IsDirty: true } cl && cl.Buffer is LayerBuffer buffer)
                {
                    if (buffer.Widget.IsEmpty && !_widgetPools.ContainsKey(cl)) continue;
                    GetOrAddWidgetPool(cl).Sync(buffer.Widget.Commands);
                }
            }
        }

        private AvaloniaWidgetPool GetOrAddWidgetPool(IChartLayer layer)
        {
            if (_widgetPools.TryGetValue(layer, out var pool)) return pool;
            if (_widgetCanvas == null)
            {
                _widgetCanvas = new Canvas { IsHitTestVisible = false };
                // 在两块绘制面之上、loading 蒙层之下
                _root.Children.Insert(_root.Children.IndexOf(_loadingVeil), _widgetCanvas);
            }
            pool = new AvaloniaWidgetPool(_widgetCanvas);
            _widgetPools[layer] = pool;
            return pool;
        }

        void IChartCellHost.DestroyLayerWidgets(IChartLayer layer)
        {
            if (!_widgetPools.TryGetValue(layer, out var pool)) return;
            _widgetPools.Remove(layer);
            pool.Destroy();
        }

        // ==========================================
        // 线程 / 输入
        // ==========================================

        // 应用退出(生命周期 Exit)之后不再渲染;没有 Application(裸单测)也算
        private static volatile bool s_exiting;
        private static bool s_exitHooked;

        private static void HookApplicationExit()
        {
            if (s_exitHooked || Application.Current?.ApplicationLifetime is not global::Avalonia.Controls.ApplicationLifetimes.IControlledApplicationLifetime lifetime) return;
            s_exitHooked = true;
            lifetime.Exit += (_, _) => s_exiting = true;
        }

        ChartHostServices IChartCellHost.Services => AvaloniaHostRegistration.Services;

        bool IChartCellHost.IsShuttingDown => s_exiting || Application.Current == null;

        void IChartCellHost.Post(Action action, HostPostPriority priority)
            => Dispatcher.UIThread.Post(action, priority == HostPostPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal);

        void IChartCellHost.WhenLoaded(Action action)
        {
            if (IsLoaded)
            {
                action();
                return;
            }
            EventHandler<global::Avalonia.Interactivity.RoutedEventArgs>? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                action();
            };
            Loaded += handler;
        }

        IDisposable IChartCellHost.CreatePointerInput(PointerRouter router, PointerRegionStore store,
            Func<DataBlackboard?> board, Func<HevoRect> plotArea, Func<double> dragThreshold)
            => new AvaloniaPointerAdapter(this, router, store, board, plotArea, dragThreshold);
    }
}
