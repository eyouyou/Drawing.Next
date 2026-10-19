using Hevo.Charting.Abstractions;
using Hevo.Charting.Features;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 触屏(Manipulation):单指横移平移(受 <see cref="ChartInteractionMode.Pan"/> 控)、捏合缩放(受 <see cref="ChartInteractionMode.Zoom"/> 控)。
    /// 两样都没开时不接管,让平台把触摸提升成鼠标。区域手势进行中不动。
    /// </summary>
    internal sealed class TouchBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        public TouchBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            switch (e.Kind)
            {
                case PointerEventKind.ManipulationStarting:
                {
                    bool wantPan = _s.Has(ChartInteractionMode.Pan);
                    bool wantZoom = _s.Has(ChartInteractionMode.Zoom);
                    var mode = PointerManipulation.None;
                    if ((wantPan || wantZoom) && !_s.GestureActive)
                    {
                        if (wantPan) mode |= PointerManipulation.TranslateX;
                        if (wantZoom) mode |= PointerManipulation.Scale;
                    }
                    e.Manipulation = mode;
                    e.ManipulationSet = true;
                    return true;
                }
                case PointerEventKind.ManipulationDelta:
                {
                    if (_s.GestureActive) return true;
                    var board = _s.Board;
                    if (board == null) return false;
                    _s.Inertia.Stop();
                    _s.ClearHit(board);

                    if (e.TranslateX != 0 && _s.Has(ChartInteractionMode.Pan))
                        _s.ApplyPanDelta(board, e.TranslateX);

                    if (Math.Abs(e.ScaleX - 1.0) > MathTolerance.ManipulationScale && _s.Has(ChartInteractionMode.Zoom))
                        _s.ApplyZoom(board, 1.0 / e.ScaleX, e.Origin.X);
                    return true;
                }
            }
            return false;
        }
    }
}
