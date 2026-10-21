using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// <see cref="AvaloniaChartCell"/> 上唯一的 Avalonia 输入入口,语义照 WPF 的 WpfPointerAdapter:
    /// <list type="bullet">
    ///   <item>隧道(Tunnel):只给路由器做"区域认领"。认领了按下 / 松开就标 Handled,冒泡那一层收不到这一次。</item>
    ///   <item>冒泡(不收已 Handled 的):没被认领的按下 / 移动 / 松开 / 离开、滚轮经路由器交给行为链 ——
    ///         图表里的子控件先处理、标了 Handled 的,行为链收不到。键盘在隧道 KeyDown:路由器先处理 Esc,剩下的交给行为链。</item>
    /// </list>
    /// 坐标:Position 相对图层画布(外壳装饰之内),HostPosition 相对 cell。
    /// 触屏对应 WPF 的 Manipulation 通道:行为链要接管时(TouchBehavior:开了平移 / 缩放),触点不当鼠标用,
    /// 单指横移 = 平移、双指捏合 = 以两指中点缩放,换算成跟 WPF 同样的 ManipulationDelta;不接管时主触点按鼠标处理(同 WPF 的触摸提升鼠标)。
    /// </summary>
    internal sealed class AvaloniaPointerAdapter : IPointerHost, IDisposable
    {
        /// <summary>WPF 一格滚轮 = 120;Avalonia 的 Delta 以格为单位。</summary>
        private const double WheelUnit = 120.0;

        private readonly AvaloniaChartCell _cell;
        private readonly PointerRouter _router;
        private readonly PointerRegionStore _store;
        private readonly Func<DataBlackboard?> _board;
        private readonly Func<HevoRect> _plotArea;
        private readonly Func<double> _dragThreshold;
        private IPointer? _pointer;
        private bool _ownsCapture;
        private bool _cursorSet;
        private bool _disposed;

        // 抓 / 放捕获时平台可能同步补发移动 / 离开,不是用户动作:路由器处理按下 / 松开 / Esc / 丢捕获期间的移动不给路由器,
        // 离开推迟到这一步处理完再看(同 WPF 版)
        private int _busy;
        private bool _leavePending;

        private readonly EventHandler<PointerPressedEventArgs> _onPreviewDown, _onDown;
        private readonly EventHandler<PointerReleasedEventArgs> _onPreviewUp, _onUp;
        private readonly EventHandler<PointerEventArgs> _onPreviewMove, _onMove, _onEnter, _onExit;
        private readonly EventHandler<PointerCaptureLostEventArgs> _onLostCapture;
        private readonly EventHandler<PointerWheelEventArgs> _onWheel;
        private readonly EventHandler<KeyEventArgs> _onKey;
        private readonly List<Action> _frameCallbacks = new();
        private bool _framePending;
        private int _backgroundCaptures;

        // 触屏:一次触摸序列(第一根手指按下到最后一根抬起)开头问一次行为链要不要接管
        private enum TouchMode { Idle, Manipulate, Mouse }
        private TouchMode _touchMode;
        private PointerManipulation _manipulation;
        private readonly Dictionary<int, Point> _touches = new();
        private Point _touchCentroid;
        private double _touchSpan;

        public AvaloniaPointerAdapter(AvaloniaChartCell cell, PointerRouter router, PointerRegionStore store,
            Func<DataBlackboard?> board, Func<HevoRect> plotArea, Func<double> dragThreshold)
        {
            _cell = cell;
            _router = router;
            _store = store;
            _board = board;
            _plotArea = plotArea;
            _dragThreshold = dragThreshold;
            _onPreviewDown = OnPreviewPressed;
            _onPreviewUp = OnPreviewReleased;
            _onPreviewMove = OnPreviewMoved;
            _onDown = OnPressed;
            _onUp = OnReleased;
            _onMove = OnMoved;
            _onEnter = OnEntered;
            _onExit = OnExited;
            _onLostCapture = OnCaptureLost;
            _onWheel = OnWheel;
            _onKey = OnPreviewKeyDown;

            cell.AddHandler(InputElement.PointerPressedEvent, _onPreviewDown, RoutingStrategies.Tunnel);
            cell.AddHandler(InputElement.PointerReleasedEvent, _onPreviewUp, RoutingStrategies.Tunnel);
            cell.AddHandler(InputElement.PointerMovedEvent, _onPreviewMove, RoutingStrategies.Tunnel);
            cell.AddHandler(InputElement.PointerPressedEvent, _onDown, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerReleasedEvent, _onUp, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerMovedEvent, _onMove, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerEnteredEvent, _onEnter, RoutingStrategies.Direct);
            cell.AddHandler(InputElement.PointerExitedEvent, _onExit, RoutingStrategies.Direct);
            cell.AddHandler(InputElement.PointerCaptureLostEvent, _onLostCapture, RoutingStrategies.Direct | RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerWheelChangedEvent, _onWheel, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.KeyDownEvent, _onKey, RoutingStrategies.Tunnel);
            router.AttachHost(this);
            store.Host = this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cell.RemoveHandler(InputElement.PointerPressedEvent, _onPreviewDown);
            _cell.RemoveHandler(InputElement.PointerReleasedEvent, _onPreviewUp);
            _cell.RemoveHandler(InputElement.PointerMovedEvent, _onPreviewMove);
            _cell.RemoveHandler(InputElement.PointerPressedEvent, _onDown);
            _cell.RemoveHandler(InputElement.PointerReleasedEvent, _onUp);
            _cell.RemoveHandler(InputElement.PointerMovedEvent, _onMove);
            _cell.RemoveHandler(InputElement.PointerEnteredEvent, _onEnter);
            _cell.RemoveHandler(InputElement.PointerExitedEvent, _onExit);
            _cell.RemoveHandler(InputElement.PointerCaptureLostEvent, _onLostCapture);
            _cell.RemoveHandler(InputElement.PointerWheelChangedEvent, _onWheel);
            _cell.RemoveHandler(InputElement.KeyDownEvent, _onKey);
            _frameCallbacks.Clear();
            _router.Detach();
            if (ReferenceEquals(_store.Host, this)) _store.Host = null;
            if (_ownsCapture && IsCapturedByCell) { _ownsCapture = false; _pointer!.Capture(null); }
            if (_cursorSet) _cell.ClearValue(InputElement.CursorProperty);
        }

        // ==========================================
        // 隧道:区域认领
        // ==========================================

        private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch && OnTouchPressed(e)) { e.Handled = true; return; }
            _pointer = e.Pointer;
            _busy++;
            try { if (_router.OnPress(MakeInput(e, PressedButton(e), e.ClickCount))) e.Handled = true; }
            finally { EndBusy(); }
        }

        private void OnPreviewReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch && OnTouchReleased(e.Pointer)) { e.Handled = true; return; }
            _busy++;
            try { if (_router.OnRelease(MakeInput(e, Map(e.InitialPressMouseButton)))) e.Handled = true; }
            finally { EndBusy(); }
        }

        private void OnPreviewMoved(object? sender, PointerEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch && OnTouchMoved(e)) { e.Handled = true; return; }
            _pointer = e.Pointer;
            if (_busy > 0) return;
            _router.OnMove(MakeInput(e, PointerButton.None));
        }

        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            // 触点被系统取消(丢了隐式捕获)按抬起算,免得残留触点让后面的捏合比例错乱
            if (e.Pointer.Type == PointerType.Touch && _touches.ContainsKey(e.Pointer.Id) && OnTouchReleased(e.Pointer)) return;
            // 子元素丢的捕获(按下时的隐式捕获)也会冒上来,只认宿主自己的
            if (!ReferenceEquals(e.Source, _cell) || !_ownsCapture) return;
            _ownsCapture = false;
            _busy++;
            try { _router.OnLostCapture(); }
            finally { EndBusy(); }
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            var modifiers = ToModifiers(e.KeyModifiers);
            var key = Map(e.Key);
            if (key == PointerKey.Escape)
            {
                bool handled;
                _busy++;
                try { handled = _router.OnEscape(modifiers); }
                finally { EndBusy(); }
                if (handled) { e.Handled = true; return; }
            }
            // 所有键都交给行为链(Keyboard 模式下任意键停惯性)
            var ev = new PointerEvent { Kind = PointerEventKind.Key, Key = key, Input = new PointerInput(default, default, PointerButton.None, modifiers, 0) };
            if (_router.RouteToBehaviors(ref ev)) e.Handled = true;
        }

        // ==========================================
        // 冒泡:行为链
        // ==========================================

        private void OnPressed(object? sender, PointerPressedEventArgs e) => Route(PointerEventKind.Press, MakeInput(e, PressedButton(e)));
        private void OnReleased(object? sender, PointerReleasedEventArgs e) => Route(PointerEventKind.Release, MakeInput(e, Map(e.InitialPressMouseButton)));
        private void OnMoved(object? sender, PointerEventArgs e) => Route(PointerEventKind.Move, MakeInput(e, PointerButton.None));

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            var ev = PointerEvent.Of(PointerEventKind.Wheel, MakeInput(e, PointerButton.None));
            ev.WheelDelta = (int)Math.Round(e.Delta.Y * WheelUnit);
            if (ev.WheelDelta == 0) return;
            if (_router.RouteToBehaviors(ref ev)) e.Handled = true;
        }

        private void Route(PointerEventKind kind, in PointerInput input)
        {
            var ev = PointerEvent.Of(kind, input);
            _router.RouteToBehaviors(ref ev);
        }

        private void OnEntered(object? sender, PointerEventArgs e) => _router.SetPointerOver(true);

        private void OnExited(object? sender, PointerEventArgs e)
        {
            var ev = new PointerEvent { Kind = PointerEventKind.Leave };
            _router.RouteToBehaviors(ref ev);
            _router.SetPointerOver(false);

            // 捕获中离开宿主是"拖出图表外",手势继续;放捕获时同步发的离开,等这一步处理完再看
            if (_ownsCapture) return;
            if (_busy > 0) { _leavePending = true; return; }
            _router.OnLeave();
        }

        // ==========================================
        // 触屏(对应 WPF Manipulation)
        // ==========================================

        /// <summary>返回 true = 这一下由触屏通道吃掉(不进鼠标那条路)。</summary>
        private bool OnTouchPressed(PointerPressedEventArgs e)
        {
            if (_touchMode == TouchMode.Idle)
            {
                var ev = new PointerEvent { Kind = PointerEventKind.ManipulationStarting };
                _router.RouteToBehaviors(ref ev);
                _manipulation = ev.ManipulationSet ? ev.Manipulation : PointerManipulation.None;
                _touchMode = _manipulation != PointerManipulation.None ? TouchMode.Manipulate : TouchMode.Mouse;
            }
            if (_touchMode == TouchMode.Mouse)
            {
                // 不接管:主触点当鼠标,其余手指不参与(WPF 只把主触点提升成鼠标)。都记着,序列何时结束靠它
                _touches[e.Pointer.Id] = default;
                return !e.Pointer.IsPrimary;
            }
            _touches[e.Pointer.Id] = e.GetPosition(_cell.PlotSurface);
            ResetTouchBaseline();
            return true;
        }

        private bool OnTouchMoved(PointerEventArgs e)
        {
            if (!_touches.ContainsKey(e.Pointer.Id)) return false;
            if (_touchMode != TouchMode.Manipulate) return !e.Pointer.IsPrimary;
            _touches[e.Pointer.Id] = e.GetPosition(_cell.PlotSurface);
            var (centroid, span) = MeasureTouches();

            double translateX = (_manipulation & PointerManipulation.TranslateX) != 0 ? centroid.X - _touchCentroid.X : 0;
            double scale = (_manipulation & PointerManipulation.Scale) != 0 && _touches.Count >= 2 && _touchSpan > 0 && span > 0
                ? span / _touchSpan : 1.0;
            _touchCentroid = centroid;
            _touchSpan = span;
            if (translateX == 0 && scale == 1.0) return true;

            var ev = new PointerEvent
            {
                Kind = PointerEventKind.ManipulationDelta,
                TranslateX = translateX,
                ScaleX = scale,
                Origin = new HevoPoint((float)centroid.X, (float)centroid.Y),
            };
            _router.RouteToBehaviors(ref ev);
            return true;
        }

        private bool OnTouchReleased(IPointer pointer)
        {
            if (!_touches.Remove(pointer.Id)) return false;
            bool asMouse = _touchMode == TouchMode.Mouse && pointer.IsPrimary;
            if (_touches.Count == 0) _touchMode = TouchMode.Idle;
            else if (_touchMode == TouchMode.Manipulate) ResetTouchBaseline();
            return !asMouse;
        }

        // 手指数变了:中点 / 指距换新基准,下一次移动才算增量(否则加减一根手指时视图会跳)
        private void ResetTouchBaseline() => (_touchCentroid, _touchSpan) = MeasureTouches();

        private (Point Centroid, double Span) MeasureTouches()
        {
            if (_touches.Count == 0) return (default, 0);
            double sx = 0, sy = 0;
            foreach (var p in _touches.Values) { sx += p.X; sy += p.Y; }
            var c = new Point(sx / _touches.Count, sy / _touches.Count);
            if (_touches.Count < 2) return (c, 0);
            // 取前两根手指的距离(同 WPF:捏合按两指间距的比例)
            using var it = _touches.Values.GetEnumerator();
            it.MoveNext(); var a = it.Current;
            it.MoveNext(); var b = it.Current;
            return (c, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
        }

        private void EndBusy()
        {
            if (--_busy > 0 || !_leavePending) return;
            _leavePending = false;
            if (!_ownsCapture && !_cell.IsPointerOver) _router.OnLeave();
        }

        private PointerInput MakeInput(PointerEventArgs e, PointerButton button, int clickCount = 0)
        {
            var plot = e.GetPosition(_cell.PlotSurface);
            var host = e.GetPosition(_cell);
            long ms = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
            return new PointerInput(new HevoPoint((float)plot.X, (float)plot.Y), new HevoPoint((float)host.X, (float)host.Y),
                button, ToModifiers(e.KeyModifiers), ms, clickCount);
        }

        // PointerModifiers 的取值跟 Avalonia KeyModifiers 对齐(Alt 1 / Control 2 / Shift 4 / Meta 8)
        private static PointerModifiers ToModifiers(KeyModifiers m) => (PointerModifiers)((int)m & 0xF);

        private PointerButton PressedButton(PointerPressedEventArgs e) => e.GetCurrentPoint(_cell).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => PointerButton.Left,
            PointerUpdateKind.RightButtonPressed => PointerButton.Right,
            PointerUpdateKind.MiddleButtonPressed => PointerButton.Middle,
            PointerUpdateKind.XButton1Pressed => PointerButton.XButton1,
            PointerUpdateKind.XButton2Pressed => PointerButton.XButton2,
            _ => PointerButton.None,
        };

        private static PointerButton Map(MouseButton b) => b switch
        {
            MouseButton.Left => PointerButton.Left,
            MouseButton.Right => PointerButton.Right,
            MouseButton.Middle => PointerButton.Middle,
            MouseButton.XButton1 => PointerButton.XButton1,
            MouseButton.XButton2 => PointerButton.XButton2,
            _ => PointerButton.None,
        };

        private static PointerKey Map(Key k) => k switch
        {
            Key.Escape => PointerKey.Escape,
            Key.Left => PointerKey.Left,
            Key.Right => PointerKey.Right,
            Key.Up => PointerKey.Up,
            Key.Down => PointerKey.Down,
            _ => PointerKey.Other,
        };

        // ==========================================
        // IPointerHost
        // ==========================================

        public bool HasPendingFrame => _cell.HasPendingFrame;
        public void RunPendingFrame() => _cell.RunFrameNow(PlotMode.Sync);

        public void Focus() => _cell.Focus();

        private bool IsCapturedByCell => _pointer != null && ReferenceEquals(_pointer.Captured, _cell);

        public void Capture()
        {
            if (_pointer == null) return;
            _ownsCapture = true;
            _pointer.Capture(_cell);
            if (!IsCapturedByCell) _ownsCapture = false;
        }

        public void ReleaseCapture()
        {
            if (!_ownsCapture) return;
            _ownsCapture = false;   // 先清标志:Capture(null) 同步触发的丢捕获不当成被打断
            if (IsCapturedByCell) _pointer!.Capture(null);
        }

        public void HandOverCapture() => _ownsCapture = false;

        /// <summary>
        /// 被宿主以外的元素捕获,或被宿主捕获但不是路由器发起的。
        /// Avalonia 按下时会把指针隐式捕获到命中的元素(图表里就是承载画布的面板),那不算别人抓着 —— WPF 没有隐式捕获。
        /// </summary>
        public bool IsMouseCapturedByOther
        {
            get
            {
                var captured = _pointer?.Captured;
                if (captured == null || _ownsCapture) return false;
                if (ReferenceEquals(captured, _cell)) return true;
                return !(captured is Visual v && IsImplicitCaptureTarget(v));
            }
        }

        // 图表自己的面板 / 绘制面(外壳装饰里的控件不算:它们抓指针是在处理自己的交互)
        private bool IsImplicitCaptureTarget(Visual v)
            => v is Panel or LayerSurface && (ReferenceEquals(v, _cell.PlotSurface.Parent) || ReferenceEquals(v.Parent, _cell.PlotSurface.Parent));

        public void CaptureForBackground()
        {
            _backgroundCaptures++;
            _pointer?.Capture(_cell);
        }

        public int BackgroundCaptureCount => _backgroundCaptures;

        public void ReleaseBackgroundCapture()
        {
            if (IsCapturedByCell) _pointer!.Capture(null);
        }

        public void AddFrameCallback(Action callback)
        {
            if (_frameCallbacks.Contains(callback)) return;
            _frameCallbacks.Add(callback);
            RequestFrame();
        }

        public void RemoveFrameCallback(Action callback) => _frameCallbacks.Remove(callback);

        // 惯性:有回调就每个显示帧跑一轮,回调全摘掉后不再挂
        private void RequestFrame()
        {
            if (_framePending || _disposed || TopLevel.GetTopLevel(_cell) is not { } top) return;
            _framePending = true;
            top.RequestAnimationFrame(_ =>
            {
                _framePending = false;
                // 回调里可能摘掉自己(惯性停下):倒着走
                for (int i = _frameCallbacks.Count - 1; i >= 0; i--)
                    if (i < _frameCallbacks.Count) _frameCallbacks[i]();
                if (_frameCallbacks.Count > 0) RequestFrame();
            });
        }

        public void SetCursor(PointerCursor cursor)
        {
            // 只还原自己设过的:业务自己设的 Cursor 不被碰
            if (cursor == PointerCursor.Default)
            {
                if (_cursorSet) _cell.ClearValue(InputElement.CursorProperty);
                _cursorSet = false;
            }
            else
            {
                _cell.Cursor = CursorOf(cursor);
                _cursorSet = true;
            }
        }

        private static readonly Dictionary<PointerCursor, Cursor> s_cursors = new();

        private static Cursor CursorOf(PointerCursor c)
        {
            if (s_cursors.TryGetValue(c, out var cursor)) return cursor;
            cursor = new Cursor(c switch
            {
                PointerCursor.Hand => StandardCursorType.Hand,
                PointerCursor.Cross => StandardCursorType.Cross,
                PointerCursor.SizeNS => StandardCursorType.SizeNorthSouth,
                PointerCursor.SizeWE => StandardCursorType.SizeWestEast,
                PointerCursor.SizeAll => StandardCursorType.SizeAll,
                PointerCursor.SizeNWSE => StandardCursorType.TopLeftCorner,
                PointerCursor.SizeNESW => StandardCursorType.TopRightCorner,
                PointerCursor.IBeam => StandardCursorType.Ibeam,
                PointerCursor.No => StandardCursorType.No,
                _ => StandardCursorType.Arrow,
            });
            s_cursors[c] = cursor;
            return cursor;
        }

        public HevoRect PlotArea => _plotArea();
        public DataBlackboard? Board => _board();
        public double DragThreshold => _dragThreshold();
    }
}
