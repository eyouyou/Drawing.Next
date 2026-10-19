using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// <see cref="LinkedChartDashboard"/> 相邻两个 pane 之间的拖拽分隔条。
    /// <para>
    /// 刻意做成 dashboard 自己的 WPF 元素、放在 chart cell 外面:拖拽不经过图表交互管线,
    /// 不会和平移 / 十字光标抢事件,也不受交互层改造影响。
    /// 不用 WPF 自带 GridSplitter:它直接改写行高,没有"比例状态"可读写,也没法按拖拽起点快照做钳制。
    /// </para>
    /// </summary>
    public sealed class PaneSplitter : Border
    {
        private readonly LinkedChartDashboard _owner;
        private readonly Border _line = new() { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        private double _dragStartY;
        private bool _dragging;

        internal PaneSplitter(LinkedChartDashboard owner, int boundaryIndex)
        {
            _owner = owner;
            BoundaryIndex = boundaryIndex;
            // 透明底让整条都能命中,可见的只是中间 1px 线
            Background = Brushes.Transparent;
            Cursor = Cursors.SizeNS;
            Focusable = false;
            Child = _line;
            ApplyLook();
        }

        /// <summary>分隔条序号:0 = 第 0 个 pane(主图)与第 1 个 pane 之间。</summary>
        public int BoundaryIndex { get; internal set; }

        internal void ApplyLook()
        {
            Height = _owner.SplitterThickness;
            _line.Background = IsMouseOver || _dragging ? _owner.SplitterHoverBrush : _owner.SplitterBrush;
        }

        protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); ApplyLook(); }
        protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); ApplyLook(); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
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
            CaptureMouse();
            ApplyLook();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            _owner.UpdateSplitterDrag(e.GetPosition(_owner).Y - _dragStartY);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;
            e.Handled = true;
            ReleaseMouseCapture();   // 走 OnLostMouseCapture 收尾
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (!_dragging) return;
            _dragging = false;
            _owner.EndSplitterDrag();
            ApplyLook();
        }
    }
}
