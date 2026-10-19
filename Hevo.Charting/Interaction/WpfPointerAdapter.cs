using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// ChartCell 上唯一的 WPF 输入入口:全仓指针交互里只有这个文件出现 WPF 输入类型。
    /// <para>
    /// 两层订阅,时序跟原来 <see cref="Features.ChartInteractionFeature"/> 自己订事件时一致:
    /// <list type="bullet">
    ///   <item>Preview(隧道):只给路由器做"区域认领"。认领了按下 / 松开就标 Handled,冒泡那一层(含业务自己的普通订阅)收不到这一次。
    ///         没有区域时什么都不做。</item>
    ///   <item>冒泡(不带 handledEventsToo):没被认领的按下 / 移动 / 松开 / 离开、滚轮、触屏经路由器交给行为链 ——
    ///         图表里的子控件(widget 模板里的按钮)先处理、标了 Handled 的,行为链收不到,图表不平移。
    ///         键盘在 PreviewKeyDown:路由器先处理 Esc,剩下的(所有键)交给行为链。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class WpfPointerAdapter : IPointerHost, IDisposable
    {
        /// <summary>测试用:替代 <see cref="Keyboard.Modifiers"/>(注入原始键盘报告太重)。</summary>
        internal static Func<PointerModifiers>? ModifiersOverride;

        private readonly ChartCell _cell;
        private readonly PointerRouter _router;
        private readonly PointerRegionStore _store;
        private readonly Func<DataBlackboard?> _board;
        private readonly Func<HevoRect> _plotArea;
        private readonly Func<double> _dragThreshold;
        private bool _ownsCapture;
        private bool _cursorSet;
        private bool _disposed;

        // 抓 / 放鼠标捕获时 WPF 会同步补发一次按系统光标位置算的移动 / 离开。它不是用户的动作:
        // 路由器处理按下 / 松开 / Esc / 丢捕获期间收到的移动不给路由器,离开推迟到这一步处理完再看
        private int _busy;
        private bool _leavePending;

        private readonly MouseButtonEventHandler _onPreviewDown, _onPreviewUp, _onDown, _onUp;
        private readonly MouseEventHandler _onPreviewMove, _onMove, _onEnter, _onLeave, _onLostCapture;
        private readonly MouseWheelEventHandler _onWheel;
        private readonly KeyEventHandler _onKey;
        private readonly EventHandler<ManipulationStartingEventArgs> _onManipStarting;
        private readonly EventHandler<ManipulationDeltaEventArgs> _onManipDelta;
        private readonly EventHandler _onRendering;
        private readonly List<Action> _frameCallbacks = new();
        private int _backgroundCaptures;

        public WpfPointerAdapter(ChartCell cell, PointerRouter router, PointerRegionStore store, Func<DataBlackboard?> board, Func<HevoRect> plotArea, Func<double> dragThreshold)
        {
            _dragThreshold = dragThreshold;
            _cell = cell;
            _router = router;
            _store = store;
            _board = board;
            _plotArea = plotArea;
            _onPreviewDown = OnPreviewMouseDown;
            _onPreviewUp = OnPreviewMouseUp;
            _onPreviewMove = OnPreviewMouseMove;
            _onDown = OnMouseDown;
            _onUp = OnMouseUp;
            _onMove = OnMouseMove;
            _onEnter = OnMouseEnter;
            _onLeave = OnMouseLeave;
            _onLostCapture = OnLostMouseCapture;
            _onWheel = OnMouseWheel;
            _onKey = OnPreviewKeyDown;
            _onManipStarting = OnManipulationStarting;
            _onManipDelta = OnManipulationDelta;
            _onRendering = (_, _) => RunFrameCallbacks();

            cell.AddHandler(UIElement.PreviewMouseDownEvent, _onPreviewDown);
            cell.AddHandler(UIElement.PreviewMouseUpEvent, _onPreviewUp);
            cell.AddHandler(UIElement.PreviewMouseMoveEvent, _onPreviewMove);
            cell.AddHandler(UIElement.MouseDownEvent, _onDown);
            cell.AddHandler(UIElement.MouseUpEvent, _onUp);
            cell.AddHandler(UIElement.MouseMoveEvent, _onMove);
            cell.AddHandler(UIElement.MouseEnterEvent, _onEnter);
            cell.AddHandler(UIElement.MouseLeaveEvent, _onLeave);
            cell.AddHandler(UIElement.LostMouseCaptureEvent, _onLostCapture);
            cell.AddHandler(UIElement.MouseWheelEvent, _onWheel);
            cell.AddHandler(UIElement.PreviewKeyDownEvent, _onKey);
            cell.AddHandler(UIElement.ManipulationStartingEvent, _onManipStarting);
            cell.AddHandler(UIElement.ManipulationDeltaEvent, _onManipDelta);
            router.AttachHost(this);
            store.Host = this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cell.RemoveHandler(UIElement.PreviewMouseDownEvent, _onPreviewDown);
            _cell.RemoveHandler(UIElement.PreviewMouseUpEvent, _onPreviewUp);
            _cell.RemoveHandler(UIElement.PreviewMouseMoveEvent, _onPreviewMove);
            _cell.RemoveHandler(UIElement.MouseDownEvent, _onDown);
            _cell.RemoveHandler(UIElement.MouseUpEvent, _onUp);
            _cell.RemoveHandler(UIElement.MouseMoveEvent, _onMove);
            _cell.RemoveHandler(UIElement.MouseEnterEvent, _onEnter);
            _cell.RemoveHandler(UIElement.MouseLeaveEvent, _onLeave);
            _cell.RemoveHandler(UIElement.LostMouseCaptureEvent, _onLostCapture);
            _cell.RemoveHandler(UIElement.MouseWheelEvent, _onWheel);
            _cell.RemoveHandler(UIElement.PreviewKeyDownEvent, _onKey);
            _cell.RemoveHandler(UIElement.ManipulationStartingEvent, _onManipStarting);
            _cell.RemoveHandler(UIElement.ManipulationDeltaEvent, _onManipDelta);
            if (_frameCallbacks.Count > 0) { _frameCallbacks.Clear(); CompositionTarget.Rendering -= _onRendering; }
            _router.Detach();
            if (ReferenceEquals(_store.Host, this)) _store.Host = null;
            if (_ownsCapture && Mouse.Captured == _cell) { _ownsCapture = false; _cell.ReleaseMouseCapture(); }
            if (_cursorSet) _cell.ClearValue(FrameworkElement.CursorProperty);
        }

        // ==========================================
        // Preview:区域认领
        // ==========================================

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _busy++;
            try { if (_router.OnPress(MakeInput(e, Map(e.ChangedButton), e.ClickCount))) e.Handled = true; }
            finally { EndBusy(); }
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _busy++;
            try { if (_router.OnRelease(MakeInput(e, Map(e.ChangedButton)))) e.Handled = true; }
            finally { EndBusy(); }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_busy > 0) return;
            _router.OnMove(MakeInput(e, PointerButton.None));
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            // LostMouseCapture 会从子元素冒泡上来,只认宿主自己丢的
            if (!ReferenceEquals(e.OriginalSource, _cell) || !_ownsCapture) return;
            _ownsCapture = false;
            _busy++;
            try { _router.OnLostCapture(); }
            finally { EndBusy(); }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = Map(e.Key);
            if (key == PointerKey.Escape)
            {
                bool handled;
                _busy++;
                try { handled = _router.OnEscape(Modifiers()); }
                finally { EndBusy(); }
                if (handled) { e.Handled = true; return; }
            }
            // 所有键都交给行为链(Keyboard 模式下任意键停惯性)
            var ev = new PointerEvent { Kind = PointerEventKind.Key, Key = key, Input = new PointerInput(default, default, PointerButton.None, Modifiers(), 0) };
            if (_router.RouteToBehaviors(ref ev)) e.Handled = true;
        }

        // ==========================================
        // 冒泡:行为链
        // ==========================================

        private void OnMouseDown(object sender, MouseButtonEventArgs e) => Route(PointerEventKind.Press, MakeInput(e, Map(e.ChangedButton)));
        private void OnMouseUp(object sender, MouseButtonEventArgs e) => Route(PointerEventKind.Release, MakeInput(e, Map(e.ChangedButton)));
        private void OnMouseMove(object sender, MouseEventArgs e) => Route(PointerEventKind.Move, MakeInput(e, PointerButton.None));

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var ev = PointerEvent.Of(PointerEventKind.Wheel, MakeInput(e, PointerButton.None));
            ev.WheelDelta = e.Delta;
            _router.RouteToBehaviors(ref ev);
        }

        private void Route(PointerEventKind kind, in PointerInput input)
        {
            var ev = PointerEvent.Of(kind, input);
            _router.RouteToBehaviors(ref ev);
        }

        private void OnMouseEnter(object sender, MouseEventArgs e) => _router.SetPointerOver(true);

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            var ev = new PointerEvent { Kind = PointerEventKind.Leave };
            _router.RouteToBehaviors(ref ev);
            _router.SetPointerOver(false);

            // 路由器:捕获中离开宿主是"拖出图表外",手势继续;放捕获时同步发的离开,等这一步处理完再看
            if (_ownsCapture) return;
            if (_busy > 0) { _leavePending = true; return; }
            _router.OnLeave();
        }

        private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
        {
            var ev = new PointerEvent { Kind = PointerEventKind.ManipulationStarting };
            _router.RouteToBehaviors(ref ev);
            if (!ev.ManipulationSet) return;   // 没有行为接管:保持平台默认
            var mode = ev.Manipulation;
            if (mode == PointerManipulation.None) { e.Mode = ManipulationModes.None; return; }
            e.ManipulationContainer = _cell.DrawingCanvas;
            var m = ManipulationModes.None;
            if ((mode & PointerManipulation.TranslateX) != 0) m |= ManipulationModes.TranslateX;
            if ((mode & PointerManipulation.Scale) != 0) m |= ManipulationModes.Scale;
            e.Mode = m;
        }

        private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
        {
            var d = e.DeltaManipulation;
            var origin = e.ManipulationOrigin;   // DrawingCanvas 坐标(Container)
            var ev = new PointerEvent
            {
                Kind = PointerEventKind.ManipulationDelta,
                TranslateX = d.Translation.X,
                ScaleX = d.Scale.X,
                Origin = new HevoPoint((float)origin.X, (float)origin.Y),
            };
            _router.RouteToBehaviors(ref ev);
        }

        private void EndBusy()
        {
            if (--_busy > 0 || !_leavePending) return;
            _leavePending = false;
            if (!_ownsCapture && !_cell.IsMouseOver) _router.OnLeave();
        }

        private PointerInput MakeInput(MouseEventArgs e, PointerButton button, int clickCount = 0)
        {
            var dc = e.GetPosition(_cell.DrawingCanvas);
            var host = e.GetPosition(_cell);
            long ms = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
            return new PointerInput(new HevoPoint((float)dc.X, (float)dc.Y), new HevoPoint((float)host.X, (float)host.Y), button, Modifiers(), ms, clickCount);
        }

        private static PointerModifiers Modifiers()
        {
            var o = ModifiersOverride;
            if (o != null) return o();
            var m = Keyboard.Modifiers;
            var r = PointerModifiers.None;
            if ((m & ModifierKeys.Alt) != 0) r |= PointerModifiers.Alt;
            if ((m & ModifierKeys.Control) != 0) r |= PointerModifiers.Control;
            if ((m & ModifierKeys.Shift) != 0) r |= PointerModifiers.Shift;
            if ((m & ModifierKeys.Windows) != 0) r |= PointerModifiers.Meta;
            return r;
        }

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

        public void Focus()
        {
            _cell.Focusable = true;
            _cell.Focus();
        }

        public void Capture()
        {
            _ownsCapture = true;
            if (!_cell.CaptureMouse()) _ownsCapture = false;
        }

        public void ReleaseCapture()
        {
            if (!_ownsCapture) return;
            _ownsCapture = false;   // 先清标志:ReleaseMouseCapture 同步触发的 LostMouseCapture 不当成被打断
            if (Mouse.Captured == _cell) _cell.ReleaseMouseCapture();
        }

        public void HandOverCapture() => _ownsCapture = false;

        public bool IsMouseCapturedByOther => Mouse.Captured != null && !_ownsCapture;

        public void CaptureForBackground()
        {
            _backgroundCaptures++;
            _cell.CaptureMouse();
        }

        public int BackgroundCaptureCount => _backgroundCaptures;

        public void ReleaseBackgroundCapture() => _cell.ReleaseMouseCapture();

        public void AddFrameCallback(Action callback)
        {
            if (_frameCallbacks.Contains(callback)) return;
            if (_frameCallbacks.Count == 0) CompositionTarget.Rendering += _onRendering;
            _frameCallbacks.Add(callback);
        }

        public void RemoveFrameCallback(Action callback)
        {
            if (!_frameCallbacks.Remove(callback)) return;
            if (_frameCallbacks.Count == 0) CompositionTarget.Rendering -= _onRendering;
        }

        // 回调里可能摘掉自己(惯性停下):倒着走
        private void RunFrameCallbacks()
        {
            for (int i = _frameCallbacks.Count - 1; i >= 0; i--)
                if (i < _frameCallbacks.Count) _frameCallbacks[i]();
        }

        public void SetCursor(PointerCursor cursor)
        {
            // 只还原自己设过的:没有区域的图表上,业务自己设的 Cursor 不被碰
            if (cursor == PointerCursor.Default)
            {
                if (_cursorSet) _cell.ClearValue(FrameworkElement.CursorProperty);
                _cursorSet = false;
            }
            else
            {
                _cell.Cursor = Map(cursor);
                _cursorSet = true;
            }
        }

        private static Cursor Map(PointerCursor c) => c switch
        {
            PointerCursor.Arrow => Cursors.Arrow,
            PointerCursor.Hand => Cursors.Hand,
            PointerCursor.Cross => Cursors.Cross,
            PointerCursor.SizeNS => Cursors.SizeNS,
            PointerCursor.SizeWE => Cursors.SizeWE,
            PointerCursor.SizeAll => Cursors.SizeAll,
            PointerCursor.SizeNWSE => Cursors.SizeNWSE,
            PointerCursor.SizeNESW => Cursors.SizeNESW,
            PointerCursor.IBeam => Cursors.IBeam,
            PointerCursor.No => Cursors.No,
            _ => Cursors.Arrow,
        };

        public HevoRect PlotArea => _plotArea();
        public DataBlackboard? Board => _board();
        public double DragThreshold => _dragThreshold();
    }
}
