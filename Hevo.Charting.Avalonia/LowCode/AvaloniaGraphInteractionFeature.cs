using Avalonia.Input;
using Avalonia.Interactivity;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 图编辑器的交互层(Avalonia,对应 WPF 的 GraphInteractionFeature)。逻辑全在核心 <see cref="GraphInteractionFeatureBase"/>,
    /// 这里只把 <see cref="AvaloniaChartCell"/> 上的指针 / 滚轮 / 键盘事件翻译过去。
    /// 触屏按鼠标处理(单指 = 左键);双指捏合缩放画布没接,用滚轮缩放。
    /// </summary>
    public sealed class AvaloniaGraphInteractionFeature : GraphInteractionFeatureBase
    {
        private AvaloniaChartCell? _cell;
        private IPointer? _pointer;
        private bool _ownsCapture;

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            var cell = (AvaloniaChartCell)chart;
            _cell = cell;
            cell.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.PointerExitedEvent, OnExited, RoutingStrategies.Direct);
            cell.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Bubble);
            cell.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
            cell.SizeChanged += OnSizeChanged;
        }

        protected override void OnDecompose()
        {
            var cell = _cell;
            if (cell == null) return;
            cell.RemoveHandler(InputElement.PointerPressedEvent, (EventHandler<PointerPressedEventArgs>)OnPressed);
            cell.RemoveHandler(InputElement.PointerMovedEvent, (EventHandler<PointerEventArgs>)OnMoved);
            cell.RemoveHandler(InputElement.PointerReleasedEvent, (EventHandler<PointerReleasedEventArgs>)OnReleased);
            cell.RemoveHandler(InputElement.PointerWheelChangedEvent, (EventHandler<PointerWheelEventArgs>)OnWheel);
            cell.RemoveHandler(InputElement.PointerExitedEvent, (EventHandler<PointerEventArgs>)OnExited);
            cell.RemoveHandler(InputElement.PointerCaptureLostEvent, (EventHandler<PointerCaptureLostEventArgs>)OnCaptureLost);
            cell.RemoveHandler(InputElement.KeyDownEvent, (EventHandler<KeyEventArgs>)OnKeyDown);
            cell.SizeChanged -= OnSizeChanged;
            _cell = null;
        }

        protected override void CaptureInput()
        {
            if (_pointer == null || _cell == null) return;
            _ownsCapture = true;
            _pointer.Capture(_cell);
        }

        protected override void ReleaseInput()
        {
            if (!_ownsCapture) return;
            _ownsCapture = false;   // 先清标志:Capture(null) 同步触发的丢捕获不当成被打断
            if (_pointer != null && ReferenceEquals(_pointer.Captured, _cell)) _pointer.Capture(null);
        }

        protected override void FocusHost() => _cell?.Focus();

        private HevoPoint Pt(PointerEventArgs e)
        {
            var p = e.GetPosition(_cell);
            return new HevoPoint((float)p.X, (float)p.Y);
        }

        private void OnSizeChanged(object? sender, global::Avalonia.Controls.SizeChangedEventArgs e)
            => HandleSizeChanged(e.NewSize.Width, e.NewSize.Height);

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            _pointer = e.Pointer;
            var kind = e.GetCurrentPoint(_cell).Properties.PointerUpdateKind;
            bool handled = kind switch
            {
                PointerUpdateKind.LeftButtonPressed => HandleLeftDown(Pt(e), e.ClickCount,
                    (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0),
                PointerUpdateKind.RightButtonPressed => HandleRightDown(Pt(e)),
                _ => false,
            };
            if (handled) e.Handled = true;
        }

        private void OnMoved(object? sender, PointerEventArgs e) => HandleMove(Pt(e));

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            bool handled = e.InitialPressMouseButton switch
            {
                MouseButton.Left => HandleLeftUp(Pt(e)),
                MouseButton.Right => HandleRightUp(),
                _ => false,
            };
            if (handled) e.Handled = true;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            // 一格滚轮按 WPF 口径记 120(核心的缩放 / 横移系数按这个口径定)
            int delta = (int)Math.Round(e.Delta.Y * 120);
            if (delta == 0) return;
            if (HandleWheel(Pt(e), delta, (e.KeyModifiers & KeyModifiers.Shift) != 0)) e.Handled = true;
        }

        private void OnExited(object? sender, PointerEventArgs e) => HandleLeave();

        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            // 子元素丢的捕获(按下时的隐式捕获换成 cell 的显式捕获)也会冒上来,只认 cell 自己的
            if (!ReferenceEquals(e.Source, _cell) || !_ownsCapture) return;
            _ownsCapture = false;
            HandleLostCapture();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var key = e.Key switch
            {
                Key.Z => GraphKey.Z,
                Key.Y => GraphKey.Y,
                Key.Home => GraphKey.Home,
                Key.Delete => GraphKey.Delete,
                _ => GraphKey.Other,
            };
            // macOS 上撤销是 Cmd+Z:Meta 跟 Control 同等对待
            bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (HandleKey(key, ctrl, shift)) e.Handled = true;
        }
    }

    /// <summary>图编辑器 schema(Avalonia,对应 WPF 的 GraphEditorSchema):装配在核心 <see cref="GraphEditorSchemaBase"/>,这里只给 Avalonia 的交互层。</summary>
    public sealed class AvaloniaGraphEditorSchema : GraphEditorSchemaBase
    {
        public AvaloniaGraphEditorSchema()
            : base((ds, geometry) => new AvaloniaGraphInteractionFeature { DataSource = ds, Geometry = geometry })
        {
        }

        public new AvaloniaGraphInteractionFeature Interaction => (AvaloniaGraphInteractionFeature)base.Interaction;
    }
}
