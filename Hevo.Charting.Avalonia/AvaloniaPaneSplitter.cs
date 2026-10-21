using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// <see cref="AvaloniaLinkedChartDashboard"/> 相邻两个 pane 之间的拖拽分隔条(对应 WPF 的 PaneSplitter)。
    /// 放在 chart cell 外面:拖拽不经过图表交互管线,不和平移 / 十字光标抢事件。
    /// 不用 Avalonia 自带 GridSplitter:它直接改写行高,没有"比例状态"可读写,也没法按拖拽起点快照做钳制。
    /// </summary>
    public sealed class AvaloniaPaneSplitter : Border
    {
        private static readonly Cursor s_cursor = new(StandardCursorType.SizeNorthSouth);

        private readonly AvaloniaLinkedChartDashboard _owner;
        private readonly Border _line = new() { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        private IPointer? _pointer;
        private double _dragStartY;
        private bool _dragging;

        internal AvaloniaPaneSplitter(AvaloniaLinkedChartDashboard owner, int boundaryIndex)
        {
            _owner = owner;
            BoundaryIndex = boundaryIndex;
            // 透明底让整条都能命中,可见的只是中间 1px 线
            Background = Brushes.Transparent;
            Cursor = s_cursor;
            Focusable = false;
            Child = _line;
            ApplyLook();
        }

        /// <summary>分隔条序号:0 = 第 0 个 pane(主图)与第 1 个 pane 之间。</summary>
        public int BoundaryIndex { get; internal set; }

        internal void ApplyLook()
        {
            Height = _owner.SplitterThickness;
            _line.Background = IsPointerOver || _dragging ? _owner.SplitterHoverBrush : _owner.SplitterBrush;
        }

        internal void CancelDrag()
        {
            if (_dragging && _pointer?.Captured == this) _pointer.Capture(null);
        }

        protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); ApplyLook(); }
        protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); ApplyLook(); }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                _owner.ResetPaneRatios();
                return;
            }
            // 位移相对拖拽起点算(不是逐帧增量):拖过最小高度再拖回来时,分隔条跟手而不是提前回弹
            _dragStartY = e.GetPosition(_owner).Y;
            if (!_owner.BeginSplitterDrag(BoundaryIndex)) return;
            _dragging = true;
            _pointer = e.Pointer;
            e.Pointer.Capture(this);
            ApplyLook();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;
            _owner.UpdateSplitterDrag(e.GetPosition(_owner).Y - _dragStartY);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;
            e.Handled = true;
            e.Pointer.Capture(null);   // 走 OnPointerCaptureLost 收尾
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (!_dragging) return;
            _dragging = false;
            _pointer = null;
            _owner.EndSplitterDrag();
            ApplyLook();
        }
    }
}
