using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>一次指针输入,已从平台事件换算好。</summary>
    internal readonly struct PointerInput
    {
        public PointerInput(HevoPoint position, HevoPoint hostPosition, PointerButton button, PointerModifiers modifiers, long timestampMs, int clickCount = 0)
        {
            Position = position; HostPosition = hostPosition; Button = button; Modifiers = modifiers; TimestampMs = timestampMs; ClickCount = clickCount;
        }

        /// <summary>平台的连击计数(按下时有值,WPF ClickCount)。</summary>
        public int ClickCount { get; }

        /// <summary>DrawingCanvas 坐标(DIP),区域与十字光标的口径。</summary>
        public HevoPoint Position { get; }

        /// <summary>ChartCell 坐标(DIP),平移的口径(ChartInteractionFeature 的 _lastPanPos)。</summary>
        public HevoPoint HostPosition { get; }

        /// <summary>按下 / 松开的那个键;移动时为 None。</summary>
        public PointerButton Button { get; }
        public PointerModifiers Modifiers { get; }
        public long TimestampMs { get; }
    }

    /// <summary>路由器关心的键。</summary>
    internal enum PointerKey { Other, Escape, Left, Right, Up, Down }

    /// <summary>触屏 Manipulation 要开的模式(对应 WPF ManipulationModes 的子集)。</summary>
    [Flags]
    internal enum PointerManipulation { None = 0, TranslateX = 1, Scale = 2 }

    /// <summary>路由器和兜底处理器对宿主(ChartCell)要的能力。WPF 实现在 <see cref="WpfPointerAdapter"/>。</summary>
    internal interface IPointerHost
    {
        bool HasPendingFrame { get; }
        void RunPendingFrame();
        void Focus();
        void Capture();
        void ReleaseCapture();
        /// <summary>捕获转给图表平移:不释放,但此后丢捕获不再算路由器的。</summary>
        void HandOverCapture();
        /// <summary>鼠标被宿主以外的元素捕获,或被宿主捕获但不是路由器发起的。</summary>
        bool IsMouseCapturedByOther { get; }
        void SetCursor(PointerCursor cursor);
        HevoRect PlotArea { get; }
        DataBlackboard? Board { get; }

        /// <summary>兜底处理器(平移 / 框选)抓鼠标。</summary>
        void CaptureForBackground();
        void ReleaseBackgroundCapture();

        /// <summary>每个显示帧回调一次(惯性);null = 停。同一时间只有一个回调。</summary>
        void SetFrameCallback(Action? callback);
    }

    /// <summary>
    /// 兜底处理器(<see cref="Features.ChartInteractionFeature"/>):路由器 → 兜底的单向分发,业务看不到。
    /// 没被区域认领的按下 / 移动 / 松开 / 离开、全部滚轮、键盘、触屏都交给它;区域认领了按下时,
    /// "任意按下都要做的事"(停惯性、武装 hover)也由路由器调它完成。
    /// </summary>
    internal interface IPointerBackground
    {
        void OnPointerDown(in PointerInput input);
        void OnPointerUp(in PointerInput input);
        void OnPointerMove(in PointerInput input);
        void OnPointerLeave();
        void OnWheel(in PointerInput input, int delta);
        /// <summary>返回 true = 处理了(调用方标 Handled)。</summary>
        bool OnKeyDown(PointerKey key, PointerModifiers modifiers);
        PointerManipulation OnManipulationStarting();
        void OnManipulationDelta(double translateX, double scaleX, HevoPoint origin);

        /// <summary>认领的按下:停惯性、武装 hover,并在按下点就地投影一次十字光标。</summary>
        void OnPressClaimed(HevoPoint canvasPosition);

        /// <summary>只接受点击的区域被拖出阈值:从按下点开始平移(宿主坐标)。返回 false = 没开平移(Pan 关闭)。</summary>
        bool BeginPanFrom(HevoPoint hostPosition);

        /// <summary>手势进行中:屏蔽触屏 Manipulation、鼠标离开不清 hit;hideCrosshair 时清掉并暂停十字光标。</summary>
        void SetGestureActive(bool active, bool hideCrosshair);

        float DragThreshold { get; }
        TimeSpan DoubleClickInterval { get; }
        double DoubleClickMaxMovement { get; }
    }

    /// <summary>
    /// 指针路由器的状态机(不含任何平台类型)。按下时裁决点中了谁,之后的移动 / 松开 / 取消都发给它;
    /// 没点中就什么都不做,事件照常流到 <see cref="Features.ChartInteractionFeature"/>。
    /// <para>
    /// 每一步往 <see cref="PointerPorts.GesturePort"/> 写一条 <see cref="PointerGesture"/>:持写锁 ForceWrite,
    /// 同步 Watch 在写的当下、同一调用栈里跑完(不慢一帧)。分发途中不允许再分发(防重入);
    /// 结束时先回到空闲、再释放捕获、最后分发,释放捕获同步触发的丢捕获通知看到的已是空闲。
    /// </para>
    /// </summary>
    internal sealed class PointerRouter : IPointerRegionListener
    {
        private enum State { Idle, Pressed, Dragging }

        private readonly PointerRegionStore _store;
        private readonly PointerPorts _ports;
        private IPointerHost? _host;

        private State _state;
        private Feature? _owner;
        private PointerRegion? _region;
        private int _regionId;
        private PointerButton _button;
        private HevoPoint _pressPos;
        private HevoPoint _pressHostPos;
        private bool _pressIsSecondClick;
        private bool _capturing;

        // 双击计数:只数被认领的按下,跟 ChartInteractionFeature 的双击复原互不相干
        private Feature? _lastPressOwner;
        private int _lastPressId, _lastPressPart;
        private long _lastPressMs;
        private HevoPoint _lastPressPos;

        // 悬停
        private Feature? _hoverOwner;
        private int _hoverId, _hoverPart;
        private HevoPoint? _lastPos;
        private PointerModifiers _lastModifiers;
        private PointerCursor _appliedCursor;

        private long _sequence;
        private bool _dispatching;
        private bool _cancelPending;

        public PointerRouter(PointerRegionStore store, PointerPorts ports)
        {
            _store = store;
            _ports = ports;
        }

        private float DragThreshold => _store.Background?.DragThreshold ?? PointerRouterFeature.DragThreshold;
        private static bool FlushPendingFrameOnPress => PointerRouterFeature.FlushPendingFrameOnPress;

        internal bool IsActive => _state != State.Idle;

        internal void AttachHost(IPointerHost? host) => _host = host;

        // ==========================================
        // 输入
        // ==========================================

        /// <summary>按下。返回 true = 被区域认领(调用方标 Handled)。</summary>
        public bool OnPress(in PointerInput input)
        {
            var host = _host;
            if (host == null) return false;

            // 手势中按了另一个键:取消(拖画线时右键 = 取消),这次按下也吃掉,不让图表半路开始平移
            if (_state != State.Idle)
            {
                CancelActive(PointerCancelReason.OtherButton, input.Position, input.Modifiers);
                return true;
            }

            if (!_store.EverPublished) return false;
            if (FlushPendingFrameOnPress && host.HasPendingFrame) host.RunPendingFrame();
            if (_store.Count == 0) return false;

            int hit = _store.HitTestPress(input.Position, input.Button, input.Modifiers, host.PlotArea, out int hitId);
            if (hit < 0) return false;

            ref readonly var f = ref _store[hit];
            var owner = f.Owner;
            var region = f.Region;

            var fallback = _store.Background;
            var interval = fallback?.DoubleClickInterval ?? TimeSpan.FromMilliseconds(400);
            double maxMove = fallback?.DoubleClickMaxMovement ?? 6.0;
            bool isSecond = input.Button != PointerButton.Right
                && _lastPressOwner != null && ReferenceEquals(_lastPressOwner, owner)
                && _lastPressId == hitId && _lastPressPart == region.Part
                && input.TimestampMs - _lastPressMs <= interval.TotalMilliseconds
                && Math.Abs(input.Position.X - _lastPressPos.X) <= maxMove
                && Math.Abs(input.Position.Y - _lastPressPos.Y) <= maxMove;
            _lastPressOwner = owner; _lastPressId = hitId; _lastPressPart = region.Part;
            _lastPressMs = input.TimestampMs; _lastPressPos = input.Position;

            // Q12:只接受双击的区域,第一次按下不认领(照常平移),只记位置;第二次落在同一区域才认领
            const PointerAccepts clickOrDrag = PointerAccepts.Click | PointerAccepts.Drag;
            bool doubleClickOnly = input.Button != PointerButton.Right
                && (region.Accepts & clickOrDrag) == 0 && (region.Accepts & PointerAccepts.DoubleClick) != 0;
            if (doubleClickOnly)
            {
                if (region.PassThrough)
                {
                    // 旁观:平台判定是第二击就发 DoubleClick,按下照常交给兜底
                    _lastPressOwner = null;
                    if (input.ClickCount == 2)
                        Dispatch(new PointerGesture(++_sequence, owner, hitId, region.Part, PointerPhase.DoubleClick, PointerCancelReason.None,
                            input.Position, input.Position, input.Button, input.Modifiers));
                    return false;
                }
                if (!isSecond) return false;
            }

            _lastPos = input.Position;
            _lastModifiers = input.Modifiers;

            // 悬停在别的区域上:先让它离开
            if (_hoverOwner != null && !SameHover(owner, hitId, region))
                LeaveHover(input.Position, input.Modifiers);

            host.Focus();
            fallback?.OnPressClaimed(input.Position);

            _pressIsSecondClick = isSecond;
            _state = State.Pressed;
            _owner = owner;
            _region = region;
            _regionId = hitId;
            _button = input.Button;
            _pressPos = input.Position;
            _pressHostPos = input.HostPosition;

            // 认领的按下一律捕获:拖出图表外仍收得到 Move / 松开,点击也不会因为在外面松手而悬空
            host.Capture();
            _capturing = true;
            fallback?.SetGestureActive(true, false);

            DispatchGuarded(PointerPhase.Press, PointerCancelReason.None, input.Position, input.Modifiers);
            return true;
        }

        /// <summary>移动。永不标 Handled:十字光标照常跟手。</summary>
        public void OnMove(in PointerInput input)
        {
            var host = _host;
            if (host == null) return;
            _lastPos = input.Position;
            _lastModifiers = input.Modifiers;

            if (_state == State.Idle)
            {
                UpdateHover(host, input.Position, input.Modifiers);
                return;
            }

            if (_state == State.Pressed)
            {
                if (_button == PointerButton.Right) return;   // 右键只认点击
                float dx = input.Position.X - _pressPos.X, dy = input.Position.Y - _pressPos.Y;
                if (dx * dx + dy * dy < DragThreshold * DragThreshold) return;

                var region = _region!;
                if ((region.Accepts & PointerAccepts.Drag) == 0)
                {
                    // 只接受点击的区域被拖走:交给平移,从按下点接着走
                    SupersedeByPan(input);
                    return;
                }

                _state = State.Dragging;
                _lastPressOwner = null;    // 拖过的按下不算双击的第一下
                ApplyCursor(region.DragCursor != PointerCursor.Default ? region.DragCursor : region.Cursor);
                if (region.HideCrosshairWhileActive) _store.Background?.SetGestureActive(true, true);
                DispatchGuarded(PointerPhase.Begin, PointerCancelReason.None, input.Position, input.Modifiers);
                return;
            }

            DispatchGuarded(PointerPhase.Move, PointerCancelReason.None, input.Position, input.Modifiers);
        }

        /// <summary>松开。返回 true = 属于进行中的手势(调用方标 Handled)。</summary>
        public bool OnRelease(in PointerInput input)
        {
            if (_state == State.Idle || input.Button != _button) return false;

            var region = _region!;
            PointerPhase phase;
            if (_state == State.Dragging) phase = PointerPhase.End;
            else if (_button == PointerButton.Right) phase = PointerPhase.ContextClick;
            else if (_pressIsSecondClick && (region.Accepts & PointerAccepts.DoubleClick) != 0) { phase = PointerPhase.DoubleClick; _lastPressOwner = null; }
            else phase = PointerPhase.Click;

            FinishAndDispatch(phase, PointerCancelReason.None, input.Position, input.Modifiers);
            _lastPos = input.Position;
            if (_host != null && !_host.IsMouseCapturedByOther) UpdateHover(_host, input.Position, input.Modifiers);
            return true;
        }

        /// <summary>鼠标捕获丢了。自己释放引起的(已回到空闲)直接忽略。</summary>
        public void OnLostCapture()
        {
            if (_state == State.Idle || !_capturing) return;
            _capturing = false;
            CancelActive(PointerCancelReason.CaptureLost, _lastPos ?? _pressPos, _lastModifiers);
        }

        /// <summary>Esc:先取消进行中的手势;否则发给模态区域;都没有返回 false,交给 DismissOnEscape。</summary>
        public bool OnEscape(PointerModifiers modifiers)
        {
            if (_state != State.Idle)
            {
                CancelActive(PointerCancelReason.Escape, _lastPos ?? _pressPos, modifiers);
                return true;
            }

            int modal = _store.FindModal();
            if (modal < 0) return false;
            ref readonly var f = ref _store[modal];
            var pos = _lastPos ?? default;
            Dispatch(new PointerGesture(++_sequence, f.Owner, f.Region.Id, f.Region.Part, PointerPhase.Escape, PointerCancelReason.None,
                pos, pos, PointerButton.None, modifiers));
            return true;
        }

        /// <summary>鼠标离开宿主(没在捕获时)。</summary>
        public void OnLeave()
        {
            _lastPos = null;
            if (_state != State.Idle) return;
            LeaveHover(default, _lastModifiers);
        }

        /// <summary>业务请求取消(<see cref="PointerPorts.RequestCancel"/>)。分发途中先记下,这次分发结束后再取消。</summary>
        internal void RequestCancel()
        {
            if (_state == State.Idle) return;
            if (_dispatching) { _cancelPending = true; return; }
            CancelActive(PointerCancelReason.Requested, _lastPos ?? _pressPos, _lastModifiers);
        }

        /// <summary>图表暂停(切走 Tab、最小化):拖动中取消,清悬停。</summary>
        public void OnSuspend()
        {
            if (_state != State.Idle) CancelActive(PointerCancelReason.ChartUnloaded, _lastPos ?? _pressPos, _lastModifiers);
            LeaveHover(_lastPos ?? default, _lastModifiers);
            _lastPos = null;
        }

        // ==========================================
        // 区域表与生命周期
        // ==========================================

        void IPointerRegionListener.OnRegionsCommitted()
        {
            if (_state != State.Idle || _hoverOwner == null && _appliedCursor == PointerCursor.Default && _lastPos == null) return;
            var host = _host;
            if (host == null) return;
            // 区域换了位置 / 撤掉了:按最后的指针位置重算悬停,光标跟着对
            if (_lastPos is HevoPoint p && !host.IsMouseCapturedByOther) UpdateHover(host, p, _lastModifiers);
            else if (_hoverOwner != null && _store.Find(_hoverOwner, _hoverId, _hoverPart) < 0) LeaveHover(default, _lastModifiers);
        }

        void IPointerRegionListener.OnOwnerRemoving(Feature owner)
        {
            // 此时 Feature 的 Watch 还活着(生命周期令牌在 Decompose 稍后才结束),Cancel 能让它回滚
            if (_state != State.Idle && ReferenceEquals(_owner, owner))
                CancelActive(PointerCancelReason.OwnerRemoved, _lastPos ?? _pressPos, _lastModifiers);
            if (ReferenceEquals(_hoverOwner, owner)) LeaveHover(_lastPos ?? default, _lastModifiers);
            if (ReferenceEquals(_lastPressOwner, owner)) _lastPressOwner = null;
        }

        void IPointerRegionListener.OnUnloading() => OnSuspend();

        /// <summary>路由器自己卸载:复位,不再分发(其它 Feature 此时可能已经卸载)。</summary>
        internal void Detach()
        {
            ResetToIdle();
            LeaveHoverSilently();
            _host = null;
        }

        // ==========================================
        // 内部
        // ==========================================

        private void SupersedeByPan(in PointerInput input)
        {
            var fallback = _store.Background;
            var pressHost = _pressHostPos;
            var press = _pressPos;
            var button = _button;
            var owner = _owner; var region = _region!; int regionId = _regionId;
            // 先回空闲再分发。捕获先不放:平移接手就转给它(由 ChartInteractionFeature 松手时释放),接不了再释放
            bool hadCapture = _capturing;
            _state = State.Idle; _owner = null; _region = null; _capturing = false;
            fallback?.SetGestureActive(false, false);
            ApplyCursor(PointerCursor.Default);
            _lastPressOwner = null;

            try
            {
                Dispatch(new PointerGesture(++_sequence, owner, regionId, region.Part, PointerPhase.Cancel, PointerCancelReason.SupersededByPan,
                    input.Position, press, button, input.Modifiers));
            }
            catch
            {
                if (hadCapture) _host?.ReleaseCapture();
                throw;
            }

            if (fallback != null && fallback.BeginPanFrom(pressHost)) _host?.HandOverCapture();
            else if (hadCapture) _host?.ReleaseCapture();
        }

        private void CancelActive(PointerCancelReason reason, HevoPoint position, PointerModifiers modifiers)
            => FinishAndDispatch(PointerPhase.Cancel, reason, position, modifiers);

        // 结束顺序:先回到空闲,再释放捕获(同步触发的丢捕获通知看到空闲,忽略),再恢复光标,最后分发
        private void FinishAndDispatch(PointerPhase phase, PointerCancelReason reason, HevoPoint position, PointerModifiers modifiers)
        {
            var owner = _owner;
            var region = _region;
            int regionId = _regionId;
            var button = _button;
            var press = _pressPos;
            ResetToIdle();
            if (owner == null || region == null) return;
            Dispatch(new PointerGesture(++_sequence, owner, regionId, region.Part, phase, reason, position, press, button, modifiers));
        }

        private void ResetToIdle()
        {
            bool wasActive = _state != State.Idle;
            _state = State.Idle;
            _owner = null;
            _region = null;
            if (_capturing)
            {
                _capturing = false;
                _host?.ReleaseCapture();
            }
            if (wasActive) _store.Background?.SetGestureActive(false, false);
            ApplyCursor(_hoverOwner != null ? HoverCursor() : PointerCursor.Default);
        }

        private PointerCursor HoverCursor()
        {
            int i = _hoverOwner != null ? _store.Find(_hoverOwner, _hoverId, _hoverPart) : -1;
            return i >= 0 ? _store[i].Region.Cursor : PointerCursor.Default;
        }

        // 按下 / 移动阶段的分发:回调抛异常时路由器先复位(释放捕获、回到空闲、恢复光标)再往外抛
        private void DispatchGuarded(PointerPhase phase, PointerCancelReason reason, HevoPoint position, PointerModifiers modifiers)
        {
            var region = _region!;
            var g = new PointerGesture(++_sequence, _owner, _regionId, region.Part, phase, reason, position, _pressPos, _button, modifiers);
            try
            {
                Dispatch(g);
            }
            catch
            {
                ResetToIdle();
                throw;
            }
        }

        private void UpdateHover(IPointerHost host, HevoPoint p, PointerModifiers modifiers)
        {
            if (_store.Count == 0)
            {
                if (_hoverOwner != null || _appliedCursor != PointerCursor.Default) LeaveHover(p, modifiers);
                return;
            }
            // 别人(图表平移、框选)抓着鼠标时不改悬停
            if (host.IsMouseCapturedByOther) return;

            int hit = _store.HitTestHover(p, modifiers, host.PlotArea, out int hitId);
            if (hit < 0)
            {
                LeaveHover(p, modifiers);
                return;
            }

            ref readonly var f = ref _store[hit];
            var owner = f.Owner;
            var region = f.Region;
            if (_hoverOwner != null && SameHover(owner, hitId, region))
            {
                _hoverId = hitId;
                ApplyCursor(region.Cursor);
                Dispatch(new PointerGesture(++_sequence, owner, hitId, region.Part, PointerPhase.HoverMove, PointerCancelReason.None, p, p, PointerButton.None, modifiers));
                return;
            }

            LeaveHover(p, modifiers);
            _hoverOwner = owner; _hoverId = hitId; _hoverPart = region.Part;
            ApplyCursor(region.Cursor);
            Dispatch(new PointerGesture(++_sequence, owner, hitId, region.Part, PointerPhase.HoverEnter, PointerCancelReason.None, p, p, PointerButton.None, modifiers));
        }

        // 同一个悬停对象:自定义形状(ElementPlot)在元素之间移动算同一个区域里移动,编号随 HoverMove 带出,
        // 跟原来"每次移动报一次命中元素"一致,中间不插 HoverLeave
        private bool SameHover(Feature owner, int id, PointerRegion region)
            => ReferenceEquals(_hoverOwner, owner) && _hoverPart == region.Part && (region.Shape.IsCustom || _hoverId == id);

        private void LeaveHover(HevoPoint p, PointerModifiers modifiers)
        {
            var owner = _hoverOwner;
            int id = _hoverId, part = _hoverPart;
            LeaveHoverSilently();
            if (owner != null)
                Dispatch(new PointerGesture(++_sequence, owner, id, part, PointerPhase.HoverLeave, PointerCancelReason.None, p, p, PointerButton.None, modifiers));
        }

        private void LeaveHoverSilently()
        {
            _hoverOwner = null;
            if (_state == State.Idle) ApplyCursor(PointerCursor.Default);
        }

        private void ApplyCursor(PointerCursor cursor)
        {
            if (cursor == _appliedCursor) return;
            _appliedCursor = cursor;
            _host?.SetCursor(cursor);
        }

        // 分发 = 持写锁写一次端口。同步 Watch 在锁内、同一调用栈里跑完;Watch 里经 later(...) 登记的锁外回调
        // 在最后一层锁退出后执行。Sequence 每次递增,值总会变,WriteIfChanged 一定触发 Watch。
        private void Dispatch(in PointerGesture g)
        {
            var board = _host?.Board;
            if (board == null || board.IsDisposed) return;
            if (_dispatching)
            {
                // 悬停是平台事件顺带触发的(放捕获时的离开 / 补发的移动),不是业务在回调里碰了路由器:不报错,只是不发
                if (g.Phase is PointerPhase.HoverEnter or PointerPhase.HoverMove or PointerPhase.HoverLeave) return;
                throw new InvalidOperationException(
                    "Pointer.GesturePort 只能由路由器写;Watch 回调里不要写它,也不要碰鼠标捕获(会同步触发路由器)。");
            }
            _dispatching = true;
#if DEBUG
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            try
            {
                using (board.AcquireWriteLock()) board.WriteIfChanged(_ports.GesturePort, g);
            }
            finally
            {
                _dispatching = false;
            }
            if (_cancelPending)
            {
                _cancelPending = false;
                if (_state != State.Idle) CancelActive(PointerCancelReason.Requested, _lastPos ?? _pressPos, _lastModifiers);
            }
#if DEBUG
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 4) System.Diagnostics.Debug.WriteLine($"[Pointer] {g.Phase} 的 Watch 回调(含锁外回调)用了 {ms:F1}ms;重活放 later(...)。");
#endif
        }
    }
}
