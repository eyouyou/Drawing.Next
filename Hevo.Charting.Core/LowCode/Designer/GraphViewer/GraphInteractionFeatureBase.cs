using System.Linq;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using static Hevo.Charting.LowCode.Designer.GraphViewer.GraphInteractionHelpers;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>图编辑器认的按键(宿主把自己的键码翻译成它)。</summary>
    public enum GraphKey { Other, Z, Y, Home, Delete }

    /// <summary>
    /// graph editor 的"交互层"feature 的平台无关部分:拖拽节点、橡皮筋连线、框选、画布平移 / 缩放 / 触屏捏合、
    /// Ctrl+Z/Y 撤销重做、Delete 删除节点、minimap 浮窗的点击 / 拖动跳转,以及命中检测 + 对齐吸附。
    /// <para>
    /// 宿主子类(WPF <c>GraphInteractionFeature</c> / Avalonia <c>AvaloniaGraphInteractionFeature</c>)只订平台输入事件,
    /// 把坐标(cell 坐标)、按键、修饰键翻译后调这里的 Handle* 方法,返回 true 时把事件标成已处理;
    /// 捕获 / 焦点经 <see cref="CaptureInput"/> / <see cref="ReleaseInput"/> / <see cref="FocusHost"/> 交回宿主。
    /// </para>
    /// <para>
    /// 跟 <see cref="BlueprintDataSource"/> 的边界:本 feature 不持有状态,只读 <c>DataSource.State</c>
    /// 算 hit / mutate,拿到结果回写到 <c>DataSource.State</c> / <c>DataSource.PushUndoAndApply</c> 等。
    /// </para>
    /// </summary>
    public abstract class GraphInteractionFeatureBase : Feature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>
        /// 由装配方(<see cref="GraphEditorSchema"/>)注入的状态 source-of-truth。null = 未注入,装配错误。
        /// </summary>
        public BlueprintDataSource DataSource { get; init; } = null!;

        /// <summary>
        /// Minimap 几何参数(渲染 + 命中两端共享)。改尺寸只动一处,
        /// 由 <see cref="GraphEditorSchema.MinimapGeometry"/> init 注入,跟 <see cref="GraphMinimapFeature"/> 同一份。
        /// </summary>
        public MinimapGeometry Geometry { get; init; } = MinimapGeometry.Default;

        /// <summary>节点被双击时触发。NodeEditorWindow 监听此事件,弹出节点属性编辑器。</summary>
        public event Action<Node>? NodeEditRequested;

        /// <summary>
        /// 画布拿到尺寸时,如果还是初始变换就自动适配视图;之后窗口改尺寸,只要用户没动过视图就跟着重新适配。
        /// 默认开:自动布局后的蓝图往往比窗口大,停在原点只能看到左上一角。宿主自己管理初始视图时关掉。
        /// </summary>
        public bool FitOnFirstShow { get; init; } = true;

        /// <summary>
        /// 适配视图时最小缩放。节点标题 13px、端口名 11px,低于 0.9 端口名就只剩十像素上下、看着吃力;
        /// 放不下的部分留在视野外,右键拖动或 Shift+滚轮横向平移查看。
        /// </summary>
        public float MinFitScale { get; init; } = 0.9f;

        // 最近一次自动适配给出的变换。当前变换还等于它,说明用户没平移缩放过,改窗口尺寸时可以放心重算。
        private CanvasTransform? _autoFitTransform;

        // ==========================================
        //  交互运行时状态(纯 UI,不进 GraphState,避免污染序列化产物)
        // ==========================================
        private enum DragMode { None, PanCanvas, MoveNodes, RubberEdge, BoxSelect }
        // 拖节点的起动阈值(屏幕像素),低于它算点选。
        private const float DragThreshold = 4f;
        private DragMode _drag = DragMode.None;
        private HevoPoint _dragStartScreen;          // 屏幕坐标:按下点
        private CanvasTransform _dragStartTransform; // 按下时的画布变换
        private Dictionary<string, HevoPoint>? _dragStartPositions; // 节点 id → 按下时的画布坐标
        private string? _rubberFromNodeId;
        private string? _rubberFromPortId;
        // 拖前模型快照,用于 OnMouseUp 时跟当前态比对决定是否真正入栈(0 位移拖拽不污染历史)。
        private GraphState? _dragStartModelSnapshot;

        // _isMinimapDrag = true 时 PanCanvas 走 minimap 模式(屏幕坐标按缩放反向应用到 Transform)。
        private bool _isMinimapDrag;
        // minimap 拖动开始时的"中心点 → 画布坐标"换算因子缓存,避免每个 mouse-move 重算。
        private float _minimapDragOx, _minimapDragOy, _minimapDragScale;

        /// <summary>
        /// cell 尺寸变了:还是初始变换(或上次自动适配的结果)就重新适配视图。
        /// </summary>
        protected void HandleSizeChanged(double width, double height)
        {
            if (!FitOnFirstShow) return;
            if (width <= 0 || height <= 0 || DataSource.State.Nodes.Count == 0) return;
            // 用户或宿主已经动过视图就不抢。
            var current = DataSource.State.Transform;
            bool untouched = _autoFitTransform == null ? current == CanvasTransform.Identity : current == _autoFitTransform.Value;
            if (!untouched) return;
            FitToContent();
            _autoFitTransform = DataSource.State.Transform;
        }

        /// <summary>
        /// 缩放平移到能看全节点和连线折线(不低于 <see cref="MinFitScale"/>,不放大超过 1)。
        /// 只改视图变换,不进撤销栈。
        /// </summary>
        public void FitToContent()
        {
            // 还没挂到 cell 上(宿主先装蓝图、后进可视树):不算;挂上后尺寸到位时 FitOnFirstShow 会适配
            var chart = AttachedChart;
            if (chart == null) return;
            var s = DataSource.State;
            if (s.Nodes.Count == 0) return;
            float vw = (float)chart.ActualWidth, vh = (float)chart.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            DataSource.State = s with { Transform = ComputeFitTransform(s, vw, vh, MinFitScale) };
        }

        /// <summary>
        /// 能看全节点和连线折线的变换:缩放夹在 [<paramref name="minScale"/>, 1],内容左上角对齐视口左上(顶部让出图例
        /// 和泳道标题)。缩放被下限卡住时内容会超出视口右侧 / 下方,由用户平移查看。
        /// </summary>
        public static CanvasTransform ComputeFitTransform(GraphState s, float viewWidth, float viewHeight, float minScale)
        {
            var (x, y, w, h) = GraphGeometry.ComputeNodesAabb(s.Nodes);
            float minX = x, minY = y, maxX = x + w, maxY = y + h;
            if (s.EdgeRoutes != null)
                foreach (var r in s.EdgeRoutes.Values)
                    foreach (var p in r)
                    {
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    }

            const float margin = 20f, topReserve = 70f;
            float scale = Math.Min((viewWidth - 2 * margin) / Math.Max(1f, maxX - minX),
                                   (viewHeight - topReserve - margin) / Math.Max(1f, maxY - minY));
            scale = Math.Max(minScale, Math.Min(1f, scale));
            return new CanvasTransform(margin - minX * scale, topReserve - minY * scale, scale);
        }

        // ==========================================
        //  宿主提供
        // ==========================================

        /// <summary>开始拖拽:把指针捕获到 cell 上(拖出 cell 外仍收得到移动 / 松开)。</summary>
        protected abstract void CaptureInput();

        /// <summary>拖拽结束:放掉捕获。</summary>
        protected abstract void ReleaseInput();

        /// <summary>按下时让 cell 拿键盘焦点(撤销 / Delete 要收得到键)。</summary>
        protected abstract void FocusHost();

        protected override void OnProject(FeatureContext ctx)
        {
            // 不渲染 —— 交互层只 mutate 状态,渲染交给 GraphCanvasFeature / NodeFeature 等。
        }

        // ==========================================
        //  Pointer 事件
        // ==========================================
        /// <summary>左键按下(屏幕 = cell 坐标)。返回 true = 已处理(宿主标 Handled)。</summary>
        protected bool HandleLeftDown(HevoPoint screen, int clickCount, bool ctrlOrShift)
        {
            // 这些是 chart 上直接挂的 WPF 事件,不在任何 Feature 作用域里;不进作用域的话
            // 改画布状态 → 写 StatePort 会被拓扑监控记成数据源,看不出是交互触发的。
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            FocusHost();
            var s = DataSource.State;

            // Minimap 优先拦截:左键落在右下角 minimap 浮窗内 → 进入 PanCanvas 的镜像模式
            //(后续 mouse-move 跟随,松开退出),不进入节点/端口的 hit-test 流程。
            if (TryHandleMinimapClick(screen, s))
            {
                _drag = DragMode.PanCanvas;
                _dragStartScreen = screen;
                _dragStartTransform = DataSource.State.Transform;
                CaptureInput();
                return true;
            }

            var canvas = s.Transform.ScreenToCanvas(screen);
            var hit = HitTest(s, canvas);

            _dragStartScreen = screen;
            _dragStartTransform = s.Transform;

            // 1. 输出端口 → 拖出连线
            if (hit.Node != null && hit.Port != null && !hit.IsInput)
            {
                _drag = DragMode.RubberEdge;
                _dragStartModelSnapshot = s;   // 拖到目标 input 落下时,跟这个 snapshot 比对决定入栈
                _rubberFromNodeId = hit.Node.Id;
                _rubberFromPortId = hit.Port.Id;
                DataSource.State = s with { RubberBand = new EdgeRubberBand(hit.Node.Id, hit.Port.Id, canvas, IsValidTarget: false) };
                CaptureInput();
                return true;
            }

            // 2. 输入端口直接点 → 移除已连线 (便利操作)
            if (hit.Node != null && hit.Port != null && hit.IsInput)
            {
                if (s.Edges.Any(x => x.ToNodeId == hit.Node.Id && x.ToPortId == hit.Port.Id))
                {
                    DataSource.PushUndoAndApply(s.DisconnectInput(hit.Node.Id, hit.Port.Id));
                }
                return true;
            }

            // 3. 节点本体 → 双击进编辑器 / 单击选中 + 准备拖移
            if (hit.Node != null)
            {
                // 双击不进入拖动模式,直接抛事件给宿主开 NodeEditorWindow。
                // (拖拽开始的 mouse-down 是 ClickCount=1;ClickCount=2 时 WPF 已经判定双击,不会再触发拖动)
                if (clickCount >= 2)
                {
                    NodeEditRequested?.Invoke(hit.Node);
                    return true;
                }

                bool addToSelection = ctrlOrShift;
                var sel = new HashSet<string>(addToSelection ? s.SelectedNodeIds : Enumerable.Empty<string>());
                if (addToSelection && sel.Contains(hit.Node.Id)) sel.Remove(hit.Node.Id);
                else sel.Add(hit.Node.Id);
                DataSource.State = s with { SelectedNodeIds = sel };

                _drag = DragMode.MoveNodes;
                _dragStartModelSnapshot = DataSource.State;   // 落点时跟它比对,0 位移就不入栈
                _dragStartPositions = sel.ToDictionary(id => id, id => DataSource.State.FindNode(id)?.Position ?? default);
                CaptureInput();
                return true;
            }

            // 4. 空白 → 框选 (默认就开框选;Shift 表示叠加,这里先简化为替换)
            _drag = DragMode.BoxSelect;
            DataSource.State = s with
            {
                SelectedNodeIds = new HashSet<string>(),
                BoxSelection = new BoxSelection(canvas, canvas)
            };
            CaptureInput();
            return true;
        }

        /// <summary>右键按下:拖动画布。</summary>
        protected bool HandleRightDown(HevoPoint screen)
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            _drag = DragMode.PanCanvas;
            _dragStartScreen = screen;
            _dragStartTransform = DataSource.State.Transform;
            CaptureInput();
            return true;
        }

        /// <summary>右键松开。</summary>
        protected bool HandleRightUp()
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            if (_drag == DragMode.PanCanvas) { _drag = DragMode.None; _isMinimapDrag = false; ReleaseInput(); return true; }
            return false;
        }

        /// <summary>指针移动。</summary>
        protected void HandleMove(HevoPoint screen)
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            var s = DataSource.State;
            var canvas = s.Transform.ScreenToCanvas(screen);

            // 非拖拽态:只更新 hover tooltip
            if (_drag == DragMode.None)
            {
                UpdateHoveredPort(s, canvas);
                return;
            }

            switch (_drag)
            {
                case DragMode.PanCanvas:
                {
                    // Minimap 拖动:鼠标在 minimap 浮窗里时,把屏幕拖动量按 minimap 缩放因子的倒数
                    // 反向应用到 Transform.OffsetX/Y,等价于"在缩略图里抓取并平移"。
                    if (TryHandleMinimapDrag(screen))
                    {
                        break;
                    }
                    float dx = screen.X - _dragStartScreen.X;
                    float dy = screen.Y - _dragStartScreen.Y;
                    var t = _dragStartTransform;
                    DataSource.State = s with { Transform = new CanvasTransform(t.OffsetX + dx, t.OffsetY + dy, t.Scale) };
                    break;
                }
                case DragMode.MoveNodes:
                {
                    if (_dragStartPositions == null) break;
                    // 鼠标挪出几像素才算拖动:点选时手一抖、或者吸附在 0 位移时就把节点吸过去,
                    // 都会让节点偏几像素,自动布局的折线首尾对不上端口、整片退回贝塞尔。
                    if (Math.Abs(screen.X - _dragStartScreen.X) < DragThreshold
                        && Math.Abs(screen.Y - _dragStartScreen.Y) < DragThreshold
                        && ReferenceEquals(s, _dragStartModelSnapshot)) break;
                    var startCanvas = _dragStartTransform.ScreenToCanvas(_dragStartScreen);
                    float ddx = canvas.X - startCanvas.X;
                    float ddy = canvas.Y - startCanvas.Y;

                    // 吸附:对被拖节点的 left/center/right 与 top/center/bottom 找最近的非拖节点对应位,
                    // 命中阈值内 → 修正 ddx/ddy 让它对齐 + 收集要画的辅助线。
                    var (snappedDdx, snappedDdy, guides) = ComputeAlignmentSnap(s, _dragStartPositions, ddx, ddy);

                    var nodes = new List<Node>(s.Nodes.Count);
                    foreach (var n in s.Nodes)
                    {
                        if (_dragStartPositions.TryGetValue(n.Id, out var orig))
                            nodes.Add(n with { Position = new HevoPoint(orig.X + snappedDdx, orig.Y + snappedDdy) });
                        else
                            nodes.Add(n);
                    }
                    DataSource.State = s with { Nodes = nodes, AlignmentGuides = guides };
                    break;
                }
                case DragMode.RubberEdge:
                {
                    if (_rubberFromNodeId == null || _rubberFromPortId == null) break;
                    var hit = HitTest(s, canvas);
                    bool valid = hit.Node != null && hit.Port != null && hit.IsInput
                                 && hit.Node.Id != _rubberFromNodeId
                                 && PortsCompatible(s, _rubberFromNodeId, _rubberFromPortId, hit.Node, hit.Port);
                    DataSource.State = s with { RubberBand = new EdgeRubberBand(_rubberFromNodeId, _rubberFromPortId, canvas, valid) };
                    break;
                }
                case DragMode.BoxSelect:
                {
                    if (s.BoxSelection == null) break;
                    DataSource.State = s with { BoxSelection = s.BoxSelection with { EndCanvas = canvas } };
                    break;
                }
            }
        }

        /// <summary>左键松开。</summary>
        protected bool HandleLeftUp(HevoPoint screen)
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            if (_drag == DragMode.None) return false;
            switch (_drag)
            {
                case DragMode.RubberEdge:
                {
                    if (_rubberFromNodeId != null && _rubberFromPortId != null)
                    {
                        var s = DataSource.State;
                        var canvas = s.Transform.ScreenToCanvas(screen);
                        var hit = HitTest(s, canvas);
                        if (hit.Node != null && hit.Port != null && hit.IsInput
                            && hit.Node.Id != _rubberFromNodeId
                            && PortsCompatible(s, _rubberFromNodeId, _rubberFromPortId, hit.Node, hit.Port))
                        {
                            var edge = new Edge(
                                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                                FromNodeId: _rubberFromNodeId, FromPortId: _rubberFromPortId,
                                ToNodeId: hit.Node.Id, ToPortId: hit.Port.Id);
                            // 拖出连线成功:跟拖前 snapshot 比对入栈
                            CommitDrag(s.AddEdge(edge) with { RubberBand = null });
                        }
                        else
                        {
                            // 没落到合法 input,只是清掉橡皮筋,不算编辑
                            DataSource.State = s with { RubberBand = null };
                            _dragStartModelSnapshot = null;
                        }
                    }
                    _rubberFromNodeId = null; _rubberFromPortId = null;
                    break;
                }
                case DragMode.BoxSelect:
                {
                    var s = DataSource.State;
                    if (s.BoxSelection != null)
                    {
                        var rect = s.BoxSelection.ToRect();
                        var sel = new HashSet<string>();
                        foreach (var n in s.Nodes)
                        {
                            if (rect.Intersect(n.GetBounds()).IsEmpty) continue;
                            sel.Add(n.Id);
                        }
                        // 框选只改 view (SelectedNodeIds),不入 undo 栈
                        DataSource.State = s with { SelectedNodeIds = sel, BoxSelection = null };
                    }
                    _dragStartModelSnapshot = null;
                    break;
                }
                case DragMode.MoveNodes:
                {
                    // 落点时:OnMouseMove 一路写新位置,这里只决定要不要入 undo 栈
                    // 顺手清掉对齐辅助线,免得拖完留在画布上
                    CommitDrag(DataSource.State with { AlignmentGuides = null });
                    _dragStartPositions = null;
                    break;
                }
            }
            _drag = DragMode.None;
            _isMinimapDrag = false;
            ReleaseInput();
            return true;
        }

        // 拖拽落点统一收口:跟拖前 snapshot 比对,模型有变化才入栈,避免"按下没动"也产生 undo 条目。
        // newState 就是当前 DataSource.State(MoveNodes 已经被 OnMouseMove 实时更新)或者刚拼好的新态(RubberEdge)。
        private void CommitDrag(GraphState newState)
        {
            var snap = _dragStartModelSnapshot;
            _dragStartModelSnapshot = null;
            if (snap != null)
            {
                DataSource.PushUndoIfChanged(snap);
            }
            DataSource.State = newState;
        }

        /// <summary>指针离开 cell。</summary>
        protected void HandleLeave()
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            var s = DataSource.State;
            if (s.HoveredPort != null)
                DataSource.State = s with { HoveredPort = null };
        }

        /// <summary>丢了指针捕获:中断进行中的拖拽。</summary>
        protected void HandleLostCapture()
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            if (_drag == DragMode.None) return;
            _drag = DragMode.None;
            _isMinimapDrag = false;
            _dragStartPositions = null;
            _dragStartModelSnapshot = null;
            _rubberFromNodeId = null; _rubberFromPortId = null;
            var s = DataSource.State;
            if (s.RubberBand != null || s.BoxSelection != null || s.AlignmentGuides != null)
                DataSource.State = s with { RubberBand = null, BoxSelection = null, AlignmentGuides = null };
        }

        // ──────────────────────────────────────────
        // 触屏:Delta 翻译 Translation → pan,Scale → zoom 围绕 Origin(chart 那边走 board / Viewport 反应式,
        // 这里 graph 是像素空间直接改 Transform,算法等价但状态层不同)。
        // ──────────────────────────────────────────
        /// <summary>
        /// 触屏增量(平移 = 相对上一帧的屏幕像素;scale = 相对上一帧的倍数;origin = 捏合中心,cell 坐标)。
        /// 跟滚轮同款公式:保持 origin 下的画布点不动。
        /// </summary>
        protected bool HandleManipulationDelta(double translateX, double translateY, double deltaScaleFactor, HevoPoint origin)
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            var s  = DataSource.State;
            var tr = s.Transform;

            // 1. 平移:已经是相对前一帧的增量(屏幕像素),直接加到 Offset。
            float ox = tr.OffsetX + (float)translateX;
            float oy = tr.OffsetY + (float)translateY;

            // 2. 缩放:跟 OnMouseWheel 同款公式,只是缩放因子来自捏合而非 wheel delta。
            //    保持 origin 下的画布点不动:newOffset = origin - canvasPt * newScale
            float scale = tr.Scale;
            float deltaScale = (float)deltaScaleFactor;
            if (Math.Abs(deltaScale - 1f) > 1e-4f)
            {
                float newScale = Math.Clamp(scale * deltaScale, 0.2f, 4f);
                if (Math.Abs(newScale - scale) > 1e-6f)
                {
                    // ScreenToCanvas 用的是当前 Transform(平移已合进 ox/oy 但 Scale 还是 tr.Scale),
                    // 这里手算 canvasPt 跟 ScreenToCanvas 等价但避开半成品 Transform。
                    float canvasX = (float)((origin.X - tr.OffsetX) / scale);
                    float canvasY = (float)((origin.Y - tr.OffsetY) / scale);
                    ox = (float)origin.X - canvasX * newScale;
                    oy = (float)origin.Y - canvasY * newScale;
                    scale = newScale;
                }
            }

            if (ox == tr.OffsetX && oy == tr.OffsetY && scale == tr.Scale) return false;
            DataSource.State = s with { Transform = new CanvasTransform(ox, oy, scale) };
            return true;
        }

        /// <summary>滚轮(<paramref name="delta"/> 按 WPF 口径,一格 = 120)。</summary>
        protected bool HandleWheel(HevoPoint screen, int delta, bool shift)
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            var s = DataSource.State;

            // Shift+滚轮横向平移:适配视图有缩放下限,宽图会超出视口右侧,触控板 / 鼠标都能横着翻。
            if (shift)
            {
                var t = s.Transform;
                DataSource.State = s with { Transform = new CanvasTransform(t.OffsetX + delta * 0.6f, t.OffsetY, t.Scale) };
                return true;
            }

            var canvasUnderMouse = s.Transform.ScreenToCanvas(screen);

            float factor = delta > 0 ? 1.15f : 1f / 1.15f;
            float newScale = Math.Clamp(s.Transform.Scale * factor, 0.2f, 4f);

            // 保持鼠标下的画布点不动:newOffset = screen - canvasPt * newScale
            float ox = screen.X - canvasUnderMouse.X * newScale;
            float oy = screen.Y - canvasUnderMouse.Y * newScale;
            DataSource.State = s with { Transform = new CanvasTransform(ox, oy, newScale) };
            return true;
        }

        /// <summary>键盘:撤销 / 重做 / Home 适配视图 / Delete 删选中节点。</summary>
        protected bool HandleKey(GraphKey key, bool ctrl, bool shift)
        {
#if DEBUG
            using var traceScope = Hevo.Charting.DevTools.TopologyTracer.EnterScope(this);
#endif
            // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z 统一在这里拦,优先于 Delete。
            if (ctrl && (key == GraphKey.Z) && !shift) { DataSource.Undo(); return true; }
            if (ctrl && (key == GraphKey.Y || (key == GraphKey.Z && shift))) { DataSource.Redo(); return true; }

            if (key == GraphKey.Home) { FitToContent(); return true; }

            var s = DataSource.State;
            if (key == GraphKey.Delete && s.SelectedNodeIds.Count > 0)
            {
                var next = s;
                foreach (var id in s.SelectedNodeIds.ToArray()) next = next.WithoutNode(id);
                DataSource.PushUndoAndApply(next with { SelectedNodeIds = new HashSet<string>() });
                return true;
            }
            return false;
        }

        // hover 检测:命中端口才更新 HoveredPort,空白时清掉。
        // 用 ReferenceEquals 短路避免每次 mouse-move 都 SetState (即使 HoveredPort 没变也会重画 preview 层)。
        private void UpdateHoveredPort(GraphState s, HevoPoint canvasPt)
        {
            const float r = 8f;
            HoveredPort? next = null;
            for (int i = s.Nodes.Count - 1; i >= 0; i--)
            {
                var node = s.Nodes[i];
                foreach (var p in node.OutputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= r)
                    {
                        var screen = s.Transform.CanvasToScreen(c);
                        next = new HoveredPort(node.Id, p.Id, false, screen);
                        goto done;
                    }
                }
                foreach (var p in node.InputPorts)
                {
                    var c = node.GetPortPosition(p);
                    if (Distance(canvasPt, c) <= r)
                    {
                        var screen = s.Transform.CanvasToScreen(c);
                        next = new HoveredPort(node.Id, p.Id, true, screen);
                        goto done;
                    }
                }
            }
            done:
            // 同样的 hover 状态不重发 (按 tuple 等价比较)
            if (HoverEquals(s.HoveredPort, next)) return;
            DataSource.State = s with { HoveredPort = next };
        }

        private static bool HoverEquals(HoveredPort? a, HoveredPort? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return a.NodeId == b.NodeId && a.PortId == b.PortId && a.IsInput == b.IsInput;
        }

        // ==========================================
        //  Minimap 命中 + 拖动跳转
        // ==========================================

        /// <summary>
        /// 落在 minimap 浮窗内 → 立即把视口中心移到点击位对应的画布点;返回 true 表示被本路径接管。
        /// </summary>
        private bool TryHandleMinimapClick(HevoPoint screen, GraphState s)
        {
            var chart = Chart;
            if (!ComputeMinimapMapping(chart, s, Geometry, out var mx, out var my, out var ox, out var oy, out var scale, out var winW, out var winH)) return false;
            // 点击是否落在 minimap 浮窗
            var rect = new HevoRect(mx, my, Geometry.Width, Geometry.Height);
            if (!rect.Contains(screen)) return false;

            _isMinimapDrag = true;
            _minimapDragOx = ox; _minimapDragOy = oy; _minimapDragScale = scale;
            // 把视口中心移到点击位对应的画布点
            CenterViewportOnMinimapClick(screen, ox, oy, scale, winW, winH);
            return true;
        }

        /// <summary>
        /// PanCanvas 模式下,如果当前是 minimap 拖动,沿用同样的"点 → 视口中心"逻辑跟随鼠标;
        /// 返回 true 表示走了 minimap 路径,外层别再做普通画布平移。
        /// </summary>
        private bool TryHandleMinimapDrag(HevoPoint screen)
        {
            if (!_isMinimapDrag) return false;
            var chart = Chart;
            float winW = (float)chart.ActualWidth;
            float winH = (float)chart.ActualHeight;
            CenterViewportOnMinimapClick(screen, _minimapDragOx, _minimapDragOy, _minimapDragScale, winW, winH);
            return true;
        }

        private void CenterViewportOnMinimapClick(HevoPoint screen, float ox, float oy, float scale, float winW, float winH)
        {
            // 屏幕点(在 minimap 内) → 画布坐标:canvasPt = (screenPt - (ox, oy)) / scale
            float canvasX = (screen.X - ox) / scale;
            float canvasY = (screen.Y - oy) / scale;
            // 让该画布点出现在 chart cell 的中心:Transform.Offset = winSize/2 - canvasPt * scale_view
            var s = DataSource.State;
            var tr = s.Transform;
            float newOffsetX = winW / 2f - canvasX * tr.Scale;
            float newOffsetY = winH / 2f - canvasY * tr.Scale;
            DataSource.State = s with { Transform = new CanvasTransform(newOffsetX, newOffsetY, tr.Scale) };
        }

        // ==========================================
        //  对齐吸附 (MoveNodes 拖动时)
        // ==========================================

        /// <summary>
        /// 拖动节点的吸附 + 辅助线计算。
        /// 思路:候选位 = 所有"未被拖"节点的 left/centerX/right + top/centerY/bottom。
        /// 对每个被拖节点的同三档位计算"靠近后的差距",取最小;若 <= 阈值就吸附 + 收一根辅助线。
        /// 阈值用画布坐标的固定值(不随 zoom 缩),保证视觉感受一致。
        /// </summary>
        private static (float ddx, float ddy, AlignmentGuides? guides) ComputeAlignmentSnap(
            GraphState state, Dictionary<string, HevoPoint> dragStartPositions, float ddx, float ddy)
        {
            const float snapThreshold = 6f;   // 画布坐标距离 ≤ 6 即吸附
            // 1) 收集"参考位"——所有未被拖节点的边/中线坐标
            var refXs = new List<float>(state.Nodes.Count * 3);
            var refYs = new List<float>(state.Nodes.Count * 3);
            foreach (var n in state.Nodes)
            {
                if (dragStartPositions.ContainsKey(n.Id)) continue;
                var b = n.GetBounds();
                refXs.Add(b.X);
                refXs.Add(b.X + b.Width / 2f);
                refXs.Add(b.X + b.Width);
                refYs.Add(b.Y);
                refYs.Add(b.Y + b.Height / 2f);
                refYs.Add(b.Y + b.Height);
            }
            if (refXs.Count == 0 && refYs.Count == 0) return (ddx, ddy, null);

            // 2) 对每个被拖节点的"目标位"(orig + ddx/ddy)计算同三档与参考位的最小差距,挑总冠军
            //    stackalloc 提到循环外避免重复分配(CA2014)。
            Span<float> tgtXs = stackalloc float[3];
            Span<float> tgtYs = stackalloc float[3];
            float bestDx = 0f, bestDxAbs = float.MaxValue, bestSnapX = 0f;
            float bestDy = 0f, bestDyAbs = float.MaxValue, bestSnapY = 0f;
            foreach (var kv in dragStartPositions)
            {
                var node = state.FindNode(kv.Key);
                if (node == null) continue;
                var orig = kv.Value;
                var size = node.Size;
                tgtXs[0] = orig.X + ddx;
                tgtXs[1] = orig.X + ddx + size.X / 2f;
                tgtXs[2] = orig.X + ddx + size.X;
                tgtYs[0] = orig.Y + ddy;
                tgtYs[1] = orig.Y + ddy + size.Y / 2f;
                tgtYs[2] = orig.Y + ddy + size.Y;
                for (int i = 0; i < 3; i++)
                {
                    foreach (var rx in refXs)
                    {
                        float diff = rx - tgtXs[i];
                        if (Math.Abs(diff) < bestDxAbs) { bestDxAbs = Math.Abs(diff); bestDx = diff; bestSnapX = rx; }
                    }
                    foreach (var ry in refYs)
                    {
                        float diff = ry - tgtYs[i];
                        if (Math.Abs(diff) < bestDyAbs) { bestDyAbs = Math.Abs(diff); bestDy = diff; bestSnapY = ry; }
                    }
                }
            }

            float finalDdx = bestDxAbs <= snapThreshold ? ddx + bestDx : ddx;
            float finalDdy = bestDyAbs <= snapThreshold ? ddy + bestDy : ddy;

            // 3) 命中的对齐位写成一根辅助线;两个方向分别独立判定
            var vlines = bestDxAbs <= snapThreshold ? new[] { bestSnapX } : Array.Empty<float>();
            var hlines = bestDyAbs <= snapThreshold ? new[] { bestSnapY } : Array.Empty<float>();
            AlignmentGuides? guides = vlines.Length == 0 && hlines.Length == 0
                ? null
                : new AlignmentGuides(vlines, hlines);

            return (finalDdx, finalDdy, guides);
        }

        private static bool PortsCompatible(GraphState s, string fromNodeId, string fromPortId, Node toNode, Port toPort)
        {
            var fromNode = s.FindNode(fromNodeId);
            if (fromNode == null) return false;
            var fromPort = fromNode.FindPort(fromPortId, isInput: false);
            if (fromPort == null) return false;
            return GraphTypeCompatibility.AreCompatible(fromPort.DataTypeName, toPort.DataTypeName, toPort.IsArray);
        }
    }
}
