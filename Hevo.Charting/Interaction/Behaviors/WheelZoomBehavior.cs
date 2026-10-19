using Hevo.Charting.Features;

namespace Hevo.Charting.Interaction
{
    /// <summary>滚轮缩放(<see cref="ChartInteractionMode.Zoom"/>):锚点 = 指针位置,算法见 <see cref="ChartInteractionFeature.ZoomStrategy"/>。区域手势进行中也照常缩放。</summary>
    internal sealed class WheelZoomBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        public WheelZoomBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            if (e.Kind != PointerEventKind.Wheel) return false;
            var board = _s.Board;
            if (board == null) return false;
            _s.Inertia.Stop();
            _s.LastMousePos = e.Input.Position;
            _s.HandleZoom(board, e.Input.Position, e.WheelDelta);
            return true;
        }
    }
}
