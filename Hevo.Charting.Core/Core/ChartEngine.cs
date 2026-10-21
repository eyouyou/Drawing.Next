using Hevo.Charting.Abstractions;
using Hevo.Charting.DevTools;
using Hevo.Charting.Linked;
using System.ComponentModel;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 一个图表单元的宿主无关部分:帧调度(按需挂每帧回调)、ProjectAll → 图层录制 → 上屏的帧管线、
    /// 图层注册表与 Z 序、跨帧黑板、指针区域、compose 期 session。
    /// 每个宿主控件(WPF ChartCell 等)持有一份,平台相关的事经 <see cref="IChartCellHost"/> 交回宿主。
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ChartEngine
    {
        private readonly IChartCell _owner;
        private readonly IChartCellHost _host;

        internal ChartEngine(IChartCell owner, IChartCellHost host)
        {
            _owner = owner;
            _host = host;
        }

        /// <summary>持有本引擎的宿主控件。</summary>
        public IChartCell Owner => _owner;

        /// <summary>宿主控件替引擎做平台相关的事(排 UI 线程、等挂进可视树等)。</summary>
        internal IChartCellHost Host => _host;

        /// <summary>本 cell 所属宿主的平台实现。</summary>
        internal ChartHostServices HostServices => _host.Services;

        // ==========================================
        // 注册表
        // ==========================================

        // 静态层(低频:Grid / Axis / Series)
        private readonly List<(IChartLayer Layer, ChartLayerType Type)> _visualRegistry = new();

        // 交互层(Level == Interaction,典型 CrosshairLayer):跟静态层分开上屏,高频重画不牵连静态层
        private readonly List<(IChartLayer Layer, ChartLayerType Type)> _overlayVisualRegistry = new();

        // 修复 H4-c：跨帧复用的脏图层缓冲。每帧 Clear 后填充，替代
        // `ActiveLayers.OfType<ChartLayer>().Where(l => l.IsDirty).ToList()` 的 LINQ 迭代器 + List 分配。
        // ExecutePipeline 仅在 UI 线程帧回调内调用，单线程复用安全。
        private readonly List<ChartLayer> _dirtyLayerBuffer = new();

        // 跨帧持久化数据
        private readonly VisualDataBag _sharedData = new();
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IChartLayer, VisualDataBag> _localData = new();

        // per-cell 渲染诊断(各宿主的渲染器往里累加)
        internal RenderDiagnostics Diagnostics { get; } = new();

        /// <summary>
        /// 获取本 cell 渲染管线的诊断快照。所有计数 UI 线程累加,无锁。
        /// 业务侧典型用法:定时器每秒拉一次 → 绑定到 overlay 文本。
        /// </summary>
        public DiagnosticsSnapshot GetDiagnostics() => Diagnostics.Snapshot();

        internal VisualDataBag GetSharedData() => _sharedData;
        internal VisualDataBag GetLocalBag(IChartLayer layer)
        {
            return _localData.GetValue(layer, _ => new VisualDataBag());
        }

        /// <summary>当前挂上的 schema(宿主在模板变化时维护)。</summary>
        internal ChartSchema? Schema { get; set; }

        /// <summary>所在屏的 DPI 缩放(物理像素 / DIP)。宿主在挂入可视树 / DPI 变化时更新,之前为 1。</summary>
        internal double PixelsPerDip { get; set; } = 1.0;

        // ==========================================
        // 挂在 cell 上的运行时状态(原先是 WPF 附加属性)
        // ==========================================

        /// <summary>视口端口(见 <see cref="ViewportPorts.GetAttached"/>)。</summary>
        internal ViewportPorts? Viewport { get; set; }

        /// <summary>容器注入的 schema 上下文(见 <see cref="SchemaContext.GetAttached"/>)。</summary>
        internal SchemaContext? SchemaContext { get; set; }

        /// <summary>
        /// compose 期订阅的生命周期容器(见 <see cref="ChartLifecycle"/>)。换成别的值(含 null)时旧的随即 Dispose,
        /// 同一个值重复设不 Dispose —— 跟原先附加属性的 PropertyChanged 回调一致。
        /// </summary>
        internal IDisposable? ActiveSession
        {
            get => _activeSession;
            set
            {
                var old = _activeSession;
                if (ReferenceEquals(old, value)) return;
                _activeSession = value;
                old?.Dispose();
            }
        }
        private IDisposable? _activeSession;

        // ==========================================
        // schema 装配 / 卸载
        // ==========================================

        /// <summary>
        /// 宿主应用模板时调:新建 session → ComposeAll → <paramref name="decorate"/>(宿主包外壳 UI)→ 首次提交。
        /// </summary>
        internal void Compose(ChartSchema schema, Action<ChartSchema>? decorate)
        {
            var lifeTimeSession = new ChartSession();

            // ⚠ 顺序关键:必须 BindTo(设置 ActiveSession) 早于 ComposeAll。
            // ComposeAll 内部 BoundWorkflow.Subscribe 会查 GetActiveSession 决定订阅托管对象;
            // 如果先 ComposeAll 再 BindTo,ComposeAll 期间产生的订阅会被托管给"上一次"装配
            // 留下的旧 session — 接着 BindTo 把旧 session Dispose,刚托管给它的新订阅瞬间被一起 dispose,
            // boardStream.DoOnDispose 触发 pipe.Dispose → board.Dispose,新 schema 一启动 board 就死了。
            ChartLifecycle.BindTo(_owner, lifeTimeSession);
            using var ctx = new RenderContext(_sharedData, _localData, lifeTimeSession);

            schema.ComposeAll(_owner, ctx);
            decorate?.Invoke(schema);

            ctx.SubmitSync(ActiveLayers);
        }

        /// <summary>模板被替换时给旧 schema 最后一次机会清理自己(触发所有 Feature 卸载与图层移除)。</summary>
        internal void DecomposeReplaced(ChartSchema oldSchema)
        {
            using var ctx = CreateContext();
            oldSchema.DecomposeAll(_owner, ctx);
        }

        /// <summary>
        /// 显式清理:DecomposeAll 把 schema 状态彻底回收 + 烧掉 lifeTimeSession(compose 期所有订阅)。
        /// </summary>
        internal void Shutdown()
        {
            // 拖动中的手势先以 ChartUnloaded 取消(此时各 Feature 的 Watch 还活着,能回滚)
            PointerRegions.NotifyUnloading();
            if (Schema is { } schema && ActiveSession != null)
            {
                using var ctx = CreateContext();
                schema.DecomposeAll(_owner, ctx);
            }
            ActiveSession = null;
        }

        /// <summary>Aspect 链变了(Transact 增删了带外壳 UI 的 Feature)后让宿主重建外壳。</summary>
        internal void RefreshDecoration() => _host.RefreshDecoration();

        /// <summary>宿主进程正在退出(之后不再渲染)。</summary>
        internal bool IsHostShuttingDown => _host.IsShuttingDown;

        /// <summary>排到 UI 线程执行(总是异步排队)。</summary>
        internal void Post(Action action, HostPostPriority priority) => _host.Post(action, priority);

        /// <summary>宿主控件已在可视树里就立即执行,否则等第一次挂进去。UI 线程调用。</summary>
        internal void WhenLoaded(Action action) => _host.WhenLoaded(action);

        /// <summary>宿主输入接到指针路由器(PointerRouterFeature 调)。</summary>
        internal IDisposable CreatePointerInput(Interaction.PointerRouter router, Func<LowCode.DataBlackboard?> board,
            Func<HevoRect> plotArea, Func<double> dragThreshold)
            => _host.CreatePointerInput(router, PointerRegions, board, plotArea, dragThreshold);

        // ==========================================
        // 帧管线
        // ==========================================

#if DEBUG
        private HashSet<IChartLayer>? _denseViewportWarned;

        /// <summary>
        /// 视口过密诊断:ViewportManagerFeature 开了 MinBarSpacingPx 时把可见根数卡在绘图区物理宽度以内,
        /// 但外部直接发 XAxisTrait / 直接写 ActiveRange / AxisFeature 读别的端口都能绕开它。
        /// 这里只告警(每个图层一次)不改范围:改了图层看到的范围就跟坐标轴、十字线对不上。
        /// X 不是按根索引的图层(散点图等)可能误报。
        /// </summary>
        private void WarnTooDenseViewport(List<RenderTask> tasks)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                var data = tasks[i].DataSnapshot;
                var limits = data.Get<Features.ViewportSpanLimitsTrait>();
                if (limits == null || double.IsInfinity(limits.MaxSpanByPixels)) continue;
                var xAxis = data.Get<XAxisTrait>();
                // 容差 1 根:ISnappableScale 取整、浮点误差
                if (xAxis == null || !xAxis.Viewport.IsValid || xAxis.Viewport.Span <= limits.MaxSpanByPixels + 1) continue;

                var layer = tasks[i].Layer;
                if (!(_denseViewportWarned ??= new HashSet<IChartLayer>()).Add(layer)) continue;
                System.Diagnostics.Debug.WriteLine(
                    $"[Charting] 图层 {layer.Name}({layer.GetType().Name})收到的 X 视口跨度 {xAxis.Viewport.Span:F0} 超过像素上限 {limits.MaxSpanByPixels:F0}" +
                    "(ViewportManagerFeature.MinBarSpacingPx):视口绕开了 ViewportManagerFeature,密集时会出白缝、拖慢录制。");
            }
        }
#endif

        private static bool RequiresUiThread(IChartLayer layer) => layer is ChartLayer { RequiresUiThread: true };

        /// <summary>
        /// 核心渲染管线(每帧一次)。
        /// 三阶段:① schema.ProjectAll → 让所有 Feature 重算 trait;② 标脏 + 制作 snapshot 喂给 Layer.Update;
        /// ③ 视任务量同步 / 并行执行。Parallel 阈值 ≥3 是经验值,任务太少时多线程 dispatch 反而亏。
        /// </summary>
        internal void ExecutePipeline(RenderContext ctx, PlotMode mode)
        {
            if (Schema is IFeatureProjector projector)
            {
                projector.ProjectAll(ctx);
            }
            // 1. 提交与标脏
            ctx.SubmitSync(ActiveLayers);
            // 指针区域跟图层 Trait 同一帧提交:命中用的位置 = 这一帧画出来的位置
            PointerRegions.Commit();

            // 修复 H4-c：手写 for 循环 + 跨帧复用 _dirtyLayerBuffer，消除每帧 LINQ 迭代器 + List 分配。
            var active = ActiveLayers;
            _dirtyLayerBuffer.Clear();
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] is ChartLayer cl && cl.IsDirty) _dirtyLayerBuffer.Add(cl);
            }
            if (_dirtyLayerBuffer.Count == 0) return;
            for (int i = 0; i < _dirtyLayerBuffer.Count; i++) _dirtyLayerBuffer[i].PixelsPerDip = PixelsPerDip;

            // 2. 制作快照
            using var frame = ctx.PrepareTasks(_dirtyLayerBuffer);
#if DEBUG
            WarnTooDenseViewport(frame.Tasks);
#endif

            // 3. 执行更新 (智能降级)
            if (mode == PlotMode.Parallel && frame.Tasks.Count >= 3)
            {
                // 线程亲和的图层(OnUpdate 直接改 UI 控件,如 TooltipWidgetLayer)不能进线程池,留在 UI 线程录制。
                Parallel.ForEach(frame.Tasks, t => { if (!RequiresUiThread(t.Layer)) t.Layer.Update(t.DataSnapshot); });
                foreach (var t in frame.Tasks) if (RequiresUiThread(t.Layer)) t.Layer.Update(t.DataSnapshot);
            }
            else
            {
                foreach (var t in frame.Tasks) t.Layer.Update(t.DataSnapshot);
            }
        }

        private bool _isUpdateRequested = false;

        // 双缓冲队列:RequestUpdate 写 _pendingUpdates,渲染帧 swap 出 _processingBuffer 后离锁迭代。
        // 替代原先 `Action<RenderContext> _pendingUpdateChain += action` 的 Delegate.Combine 模式 ——
        // 那种写法每次 += 都 new MulticastDelegate + 拷贝内部 _invocationList 数组,
        // mouse move / WebSocket 高频灌入时是 O(N²) 拷贝 + N 个递增大小的 MulticastDelegate 堆分配。
        // 改成 List 后单次 Add 是 O(1) 摊销,跨帧复用 backing array,稳态零分配。
        private List<Action<RenderContext>> _pendingUpdates = new(8);
        private List<Action<RenderContext>> _processingBuffer = new(8);

        // 💥 增加一把专用的调度锁
        private readonly object _updateLock = new object();

        /// <summary>
        /// 申请一个事务,把 updateAction 排入下一帧的 pending 队列;
        /// 队列首次非空时才让宿主挂每帧回调(动态 VSync),帧尾立即拔除以保证静置 0 GPU 占用。
        /// </summary>
        /// <param name="updateAction">在下一帧内执行的事务体(写黑板 / 触发 invalidate 等)。</param>
        public void RequestUpdate(Action<RenderContext> updateAction)
        {
            // 💥 无论是后台 WebSocket 线程，还是前台鼠标线程，进来都要排队！
            lock (_updateLock)
            {
                _pendingUpdates.Add(updateAction);

                if (!_isUpdateRequested)
                {
                    _isUpdateRequested = true;

                    // 💥 按需挂载机制 (Dynamic VSync Hook):只有真有任务了，才去唤醒宿主的渲染泵。
                    _host.ArmFrame();
                }
            }
        }

        /// <summary>宿主的每帧回调:有排队的事务就跑一帧。</summary>
        internal void OnFrame()
        {
            // 出了锁之后，在安全的 UI 线程里慢慢执行管线
            if (SwapPendingUpdates()) RunSwappedFrame(PlotMode.Sync);
        }

        private bool SwapPendingUpdates()
        {
            // 💥 以极快的速度交换缓冲，然后立刻释放锁，绝不阻塞！
            // swap 后 _pendingUpdates 立即可承接新写入(比如本帧 ExecutePipeline 内回流的 RequestUpdate),
            // 本帧迭代用的是旧 _processingBuffer,锁外读不会被并发追加。
            bool hasWork = false;
            lock (_updateLock)
            {
                if (_isUpdateRequested)
                {
                    if (_pendingUpdates.Count > 0)
                    {
                        (_pendingUpdates, _processingBuffer) = (_processingBuffer, _pendingUpdates);
                        hasWork = true;
                    }
                    _isUpdateRequested = false;

                    // 💥 画完即休眠：立刻从宿主的渲染泵上拔除当前图表！
                    // 这行代码是消灭 10% GPU 静置占用的核心魔法。管线处理完当前帧后，直接让 GPU 进入绝对静默状态。
                    _host.DisarmFrame();
                }
            }
            return hasWork;
        }

        // 一帧的完整工作:排队事务 → 管线 → 上屏。_processingBuffer 由 SwapPendingUpdates 换出。
        private void RunSwappedFrame(PlotMode mode)
        {
            // 整帧(ProjectAll → 图层录制 → 上屏)登记为列读者:trait 里的列在锁外读,
            // 摄入器在本帧结束前不会改写它可能拿到的缓冲(见 LowCode.ColumnReadScope)
            using var readers = LowCode.ColumnReadScope.Begin();
            using var ctx = new RenderContext(_sharedData, _localData);

            // 直接 for 索引迭代,跳过 List<T>.Enumerator 的 IDisposable 调用链。
            var actions = _processingBuffer;
            for (int i = 0; i < actions.Count; i++) actions[i](ctx);
            actions.Clear(); // 释放对 captured closures 的引用,避免跨帧拖死 ctx 相关对象

            ExecutePipeline(ctx, mode);
            LastFrameDirtyLayers = _dirtyLayerBuffer.Count;
            Invalidate();
            DevTools.IncrementalRenderProbe.FrameRendered?.Invoke(_owner);
        }

        /// <summary>
        /// 测量工具用(Charting.Benchmarks --render-probe):不等 VSync,立刻同步跑一帧,
        /// 跟帧回调里那一帧是同一段代码(含 RequestUpdate 排队的事务,如 SizeChanged)。
        /// 返回本帧重录的图层数。
        /// </summary>
        internal int RunFrameNow(PlotMode mode)
        {
            SwapPendingUpdates();
            RunSwappedFrame(mode);
            return LastFrameDirtyLayers;
        }

        /// <summary>有排队没跑的帧。指针路由器按下时据此决定要不要先同步跑一帧(让命中用的区域是最新的)。</summary>
        internal bool HasPendingFrame { get { lock (_updateLock) return _isUpdateRequested; } }

        /// <summary>本 cell 上各 Feature 发布的指针区域(纯数据,路由器在输入时读)。</summary>
        internal Interaction.PointerRegionStore PointerRegions { get; } = new();

        /// <summary>微基准用:丢掉排队的事务(只测 ProjectAll 时,黑板写入排进来的空回调不跑帧也不能越积越多)。</summary>
        internal void DiscardPendingUpdates()
        {
            if (SwapPendingUpdates()) _processingBuffer.Clear();
        }

        /// <summary>最近一帧 ExecutePipeline 判脏后重录的图层数(诊断用)。</summary>
        internal int LastFrameDirtyLayers { get; private set; }

        /// <summary>
        /// 把后台录制好的图层指令真正"画"到屏幕上。
        /// 三阶段:① SwapBuffer 把所有脏图层 back→front 同步到同一帧;
        /// ② 宿主上屏(矢量指令 + 控件);③ PostRender 清脏标记 + Diagnostics 收口。
        /// </summary>
        internal void Invalidate()
        {
            // diagnostics:帧首清零累加器(per-layer Render 调用会向其内累加)
            Diagnostics.BeginFrame();

            // ==========================================
            // 阶段 1：统一同步点 (Sync Point)
            // ==========================================
            // 将所有后台录制好的 BackBuffer 一次性全部推到前台 FrontBuffer。
            // 确保在这一刻之后，各渲染器、Widget 读到的全都是同一帧的最新数据！
            // 走 IReadOnlyList<T> 索引 + is ChartLayer，避免 OfType<T> 的 LINQ 迭代器与 IEnumerator 装箱。
            var swapList = ActiveLayers;
            for (int i = 0; i < swapList.Count; i++)
            {
                if (swapList[i] is ChartLayer cl && cl.IsDirty) cl.SwapBuffer();
            }

            // ==========================================
            // 阶段 2：派发渲染指令(宿主:矢量层 + 控件层,仅给脏图层发新指令)
            // ==========================================
            _host.Present(_staticLayers, _overlayLayers);

            // ==========================================
            // 阶段 3：统一清理脏标记
            // ==========================================
            var postList = ActiveLayers;
            for (int i = 0; i < postList.Count; i++)
            {
                if (postList[i] is ChartLayer cl) cl.PostRender(); // IsDirty = false
            }

            // diagnostics:帧尾收口,FrameCount++ + 拍 LastFrame 快照
            Diagnostics.EndFrame();
        }

        // ==========================================
        // 层级管理
        // ==========================================

        private static readonly Action<RenderContext> s_noopUpdate = _ => { };
        private void OnLayerModeChanged(ChartLayer _) => RequestUpdate(s_noopUpdate);

        /// <summary>检查是否存在指定名称的图层(按 Layer.Name 字符串匹配)。</summary>
        public bool HasLayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // 使用 LINQ 遍历查找
            return ActiveLayers.Any(layer => layer.Name == name);
        }

        /// <summary>按 Name 查图层(找不到返回 null)。配合 <see cref="HasLayer"/> 使用。</summary>
        public IChartLayer? GetLayer(string name)
        {
            return ActiveLayers.FirstOrDefault(l => l.Name == name);
        }

        /// <summary>见 <see cref="IChartCell.AddUnmanagedLayer{TLayer}"/>。</summary>
        public VisualProxy<TLayer> AddUnmanagedLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
        {
            var localBag = AttachLayerVisual(layer);
            return new VisualProxy<TLayer>(layer, localBag, new LayerFeed(this, layer, localBag));
        }

        /// <summary>
        /// Feature.AttachLayer 用:返回的代理直写本层 live 袋、不唤醒图层。Feature 在 OnCompose 里经它发静态样式,
        /// 图层要等 OnProject 里 ctx.For(layer) 发的数据到了才第一次录 —— [RequiredTrait] 图层不会在数据到齐前被叫起。
        /// </summary>
        internal VisualProxy<TLayer> AttachFeatureLayer<TLayer>(TLayer layer) where TLayer : IChartLayer
        {
            var localBag = AttachLayerVisual(layer);
            return new VisualProxy<TLayer>(layer, localBag, localBag);
        }

        private VisualDataBag AttachLayerVisual(IChartLayer layer)
        {
            // 路由策略:
            //   Level == Interaction       → _overlayVisualRegistry (交互层,跟静态层 dirty 隔离)
            //   其它 Level                  → _visualRegistry (静态层)
            if (layer is ChartLayer v && v.Level == ChartLayerType.Interaction)
            {
                int insertIndex = 0;
                for (; insertIndex < _overlayVisualRegistry.Count; insertIndex++)
                {
                    if (_overlayVisualRegistry[insertIndex].Type > v.Level) break;
                }
                _host.InsertLayerVisual(v, overlay: true, insertIndex);
                _overlayVisualRegistry.Insert(insertIndex, (v, v.Level));
                v.ModeChanged = OnLayerModeChanged;
            }
            else if (layer is ChartLayer staticLayer)
            {
                int insertIndex = 0;
                for (; insertIndex < _visualRegistry.Count; insertIndex++)
                {
                    if (_visualRegistry[insertIndex].Type > staticLayer.Level) break;
                }
                _host.InsertLayerVisual(staticLayer, overlay: false, insertIndex);
                _visualRegistry.Insert(insertIndex, (staticLayer, staticLayer.Level));
                staticLayer.ModeChanged = OnLayerModeChanged;
            }
            UpdateActiveLayersCache();

            return _localData.GetValue(layer, _ => new VisualDataBag());
        }

        // 跨帧缓存:Add/RemoveUnmanagedLayer 内部维护,避免 ActiveLayers 每次访问都拼一个 List。
        // volatile 保证后台线程偶发读到的是已发布的最新引用(写永远在 UI 线程)。
        private volatile IReadOnlyList<IChartLayer> _activeLayersCache = Array.Empty<IChartLayer>();

        /// <summary>当前所有活跃图层的只读列表(静态层 + 交互层拼接结果)。读取无锁,内部走 volatile 缓存。</summary>
        public IReadOnlyList<IChartLayer> ActiveLayers => _activeLayersCache;

        /// <summary>静态层(按 Z 序)。只在 UI 线程读。</summary>
        internal IReadOnlyList<ChartLayer> StaticLayers => _staticLayers;

        /// <summary>交互层(按 Z 序)。只在 UI 线程读。</summary>
        internal IReadOnlyList<ChartLayer> OverlayLayers => _overlayLayers;

        // 当你 AddLayer 或 RemoveLayer 时，同步更新这个缓存：
        // 修复 H4-d：手写 for 循环替代 Select / AsReadOnly。
        // List<T> 已实现 IReadOnlyList<T>，AsReadOnly 的 ReadOnlyCollection 包装在此处无额外价值。
        private void UpdateActiveLayersCache()
        {
            var newList = new List<IChartLayer>(_visualRegistry.Count + _overlayVisualRegistry.Count);
            for (int i = 0; i < _visualRegistry.Count; i++) newList.Add(_visualRegistry[i].Layer);
            for (int i = 0; i < _overlayVisualRegistry.Count; i++) newList.Add(_overlayVisualRegistry[i].Layer);

            // 原子操作：volatile 字段赋值，读侧按 IReadOnlyList<IChartLayer> 访问
            _activeLayersCache = newList;

            // 宿主按 Z 序取两组图层(只在 UI 线程读写)
            _staticLayers = FilterChartLayers(_visualRegistry);
            _overlayLayers = FilterChartLayers(_overlayVisualRegistry);
        }
        private IReadOnlyList<ChartLayer> _staticLayers = Array.Empty<ChartLayer>();
        private IReadOnlyList<ChartLayer> _overlayLayers = Array.Empty<ChartLayer>();

        private static List<ChartLayer> FilterChartLayers(List<(IChartLayer Layer, ChartLayerType Type)> registry)
        {
            var list = new List<ChartLayer>(registry.Count);
            for (int i = 0; i < registry.Count; i++)
                if (registry[i].Layer is ChartLayer cl) list.Add(cl);
            return list;
        }

        /// <summary>构造一个无 session 的渲染上下文。仅用于 Shutdown / 模板替换等"无主"清理路径。</summary>
        internal RenderContext CreateContext()
        {
            return new RenderContext(_sharedData, _localData);
        }

        /// <summary>
        /// 安全移除图层，并彻底清理其关联的物理资源、控件池与状态
        /// </summary>
        public void RemoveUnmanagedLayer(IChartLayer layer)
        {
            if (layer == null) return;

            bool isRemoved = false;

            // ==========================================
            // 第一步：从底层渲染管线中连根拔起
            //   路由策略跟 AttachLayerVisual 对称: Interaction / 其它
            // ==========================================
            if (layer is ChartLayer v && v.Level == ChartLayerType.Interaction)
            {
                // 手写 for 循环避免 LINQ FirstOrDefault 捕获 layer 产生的闭包分配。
                for (int i = 0; i < _overlayVisualRegistry.Count; i++)
                {
                    if (ReferenceEquals(_overlayVisualRegistry[i].Layer, layer))
                    {
                        _overlayVisualRegistry.RemoveAt(i);
                        _host.RemoveLayerVisual(v, overlay: true);
                        isRemoved = true;
                        break;
                    }
                }
            }
            else if (layer is ChartLayer staticLayer)
            {
                for (int i = 0; i < _visualRegistry.Count; i++)
                {
                    if (ReferenceEquals(_visualRegistry[i].Layer, layer))
                    {
                        _visualRegistry.RemoveAt(i);
                        _host.RemoveLayerVisual(staticLayer, overlay: false);
                        isRemoved = true;
                        break;
                    }
                }
            }

            // ==========================================
            // 第二步：从顶层交互管线中销毁控件池
            // ==========================================
            _host.DestroyLayerWidgets(layer);

            // ==========================================
            // 第三步：物理资源释放与重绘
            // ==========================================
            // 先刷新图层缓存:下面那次 Invalidate(以及后端 Present)看到的就已经是移除后的图层列表
            UpdateActiveLayersCache();

            if (isRemoved)
            {
                if (layer is ChartLayer removedLayer) _host.OnLayerRemoved(removedLayer);

                // 调用 Dispose 释放该图层可能持有的上屏对象、画笔、路径等资源
                layer.Dispose();

                // 【幕后魔法说明】
                // 到此为止，layer 已经失去了所有的强引用。
                // 等待 GC 扫描时，ConditionalWeakTable 会自动发现 layer 死了，
                // 然后静悄悄地把属于它的 VisualDataBag (状态快照) 给清理掉。0 泄漏！

                // 强制触发一次空重绘，把移除图层后的最新画面刷到屏幕上
                Invalidate();
            }
        }

        /// <summary>
        /// 💥 [全局流] 为整个 Chart 播种全局特质。
        /// </summary>
        internal void Seed<TTrait>(TTrait trait) where TTrait : class, IVisualTrait
            => _sharedData.Publish<TTrait>(trait);
    }

    public enum PlotMode
    {
        Sync,       // UI 线程，直接跑
        Async,      // 后台线程，单核跑 (任务少时)
        Parallel    // 后台线程，压榨多核 (任务多时)
    }

    public static class ChartPipelineExtensions
    {
        /// <summary>
        /// 💥 [全局流] 为整个 Chart 播种全局特质。
        /// </summary>
        public static IChartCell Seed<TTrait>(this IChartCell chart, TTrait trait)
            where TTrait : class, IVisualTrait
        {
            chart.Engine.Seed(trait);
            return chart;
        }
    }
}
