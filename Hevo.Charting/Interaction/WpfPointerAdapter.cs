using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Windows;
using System.Windows.Input;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 路由器的 WPF 适配器:全仓指针交互里唯一出现 WPF 输入类型的地方。
    /// 在 ChartCell 上订 Preview(隧道)事件,先于 <see cref="Features.ChartInteractionFeature"/> 的冒泡处理器看到输入;
    /// 区域认领了按下 / 松开时标 Handled,冒泡处理器就收不到这一次。移动从不标 Handled。
    /// </summary>
    internal sealed class WpfPointerAdapter : IPointerHost, IDisposable
    {
        /// <summary>测试用:替代 <see cref="Keyboard.Modifiers"/>(注入原始键盘报告太重)。</summary>
        internal static Func<PointerModifiers>? ModifiersOverride;

        private readonly ChartCell _cell;
        private readonly PointerRouter _router;
        private readonly Func<DataBlackboard?> _board;
        private readonly Func<HevoRect> _plotArea;
        private bool _ownsCapture;
        private bool _cursorSet;
        // 抓 / 放鼠标捕获时 WPF 会同步补发一次按系统光标位置算的移动。它不是用户的移动:
        // 处理按下 / 松开 / Esc / 丢捕获期间收到的移动一律不算,路由器自己在这些步骤后按真实位置更新悬停
        private int _busy;
        private bool _leavePending;
        private bool _disposed;

        private readonly MouseButtonEventHandler _onDown;
        private readonly MouseButtonEventHandler _onUp;
        private readonly MouseEventHandler _onMove;
        private readonly MouseEventHandler _onLeave;
        private readonly MouseEventHandler _onLostCapture;
        private readonly KeyEventHandler _onKey;

        public WpfPointerAdapter(ChartCell cell, PointerRouter router, Func<DataBlackboard?> board, Func<HevoRect> plotArea)
        {
            _cell = cell;
            _router = router;
            _board = board;
            _plotArea = plotArea;
            _onDown = OnPreviewMouseDown;
            _onUp = OnPreviewMouseUp;
            _onMove = OnPreviewMouseMove;
            _onLeave = OnMouseLeave;
            _onLostCapture = OnLostMouseCapture;
            _onKey = OnPreviewKeyDown;

            cell.AddHandler(UIElement.PreviewMouseDownEvent, _onDown);
            cell.AddHandler(UIElement.PreviewMouseUpEvent, _onUp);
            cell.AddHandler(UIElement.PreviewMouseMoveEvent, _onMove);
            cell.AddHandler(UIElement.MouseLeaveEvent, _onLeave);
            cell.AddHandler(UIElement.LostMouseCaptureEvent, _onLostCapture);
            cell.AddHandler(UIElement.PreviewKeyDownEvent, _onKey);
            router.AttachHost(this);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cell.RemoveHandler(UIElement.PreviewMouseDownEvent, _onDown);
            _cell.RemoveHandler(UIElement.PreviewMouseUpEvent, _onUp);
            _cell.RemoveHandler(UIElement.PreviewMouseMoveEvent, _onMove);
            _cell.RemoveHandler(UIElement.MouseLeaveEvent, _onLeave);
            _cell.RemoveHandler(UIElement.LostMouseCaptureEvent, _onLostCapture);
            _cell.RemoveHandler(UIElement.PreviewKeyDownEvent, _onKey);
            _router.Detach();
            if (_ownsCapture && Mouse.Captured == _cell) { _ownsCapture = false; _cell.ReleaseMouseCapture(); }
            if (_cursorSet) _cell.ClearValue(FrameworkElement.CursorProperty);
        }

        // ==========================================
        // WPF 事件 → 路由器
        // ==========================================

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _busy++;
            try { if (_router.OnPress(MakeInput(e, Map(e.ChangedButton)))) e.Handled = true; }
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

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            // 捕获中离开宿主是"拖出图表外",手势继续
            if (_ownsCapture) return;
            // 放捕获时 WPF 同步发的离开:等这一步处理完再看指针是不是真的在外面
            if (_busy > 0) { _leavePending = true; return; }
            _router.OnLeave();
        }

        private void EndBusy()
        {
            if (--_busy > 0 || !_leavePending) return;
            _leavePending = false;
            if (!_ownsCapture && !_cell.IsMouseOver) _router.OnLeave();
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
            if (e.Key != Key.Escape) return;
            _busy++;
            try { if (_router.OnEscape(Modifiers())) e.Handled = true; }
            finally { EndBusy(); }
        }

        private PointerInput MakeInput(MouseEventArgs e, PointerButton button)
        {
            var dc = e.GetPosition(_cell.DrawingCanvas);
            var host = e.GetPosition(_cell);
            long ms = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
            return new PointerInput(new HevoPoint((float)dc.X, (float)dc.Y), new HevoPoint((float)host.X, (float)host.Y), button, Modifiers(), ms);
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
    }
}
