using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 十字光标跟手:把指针位置投影成 <see cref="ChartInteractionFeature.PointerHitPort"/>(Crosshair / Tooltip 读它)。
    /// <list type="bullet">
    ///   <item>移动:<see cref="ChartInteractionMode.Hover"/> 开、已武装、没被区域手势藏起时投影;平移 / 框选中不到这里(前面的行为拦了)。</item>
    ///   <item>离开:清 hit(平移、框选、区域手势进行中不清)。</item>
    ///   <item>Esc:<see cref="ChartInteractionFeature.DismissOnEscape"/> 时解除武装并清 hit,不受 Keyboard 开关影响。</item>
    ///   <item>视口变了:在最后的指针位置重投(Feature 的 ActiveRange Watch 调 <see cref="OnActiveRangeChanged"/>)。</item>
    /// </list>
    /// </summary>
    internal sealed class HoverBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        public HoverBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            var board = _s.Board;
            if (board == null) return false;
            switch (e.Kind)
            {
                case PointerEventKind.Move:
                    if (_s.Has(ChartInteractionMode.Hover) && _s.IsHoverArmed && !_s.HideCrosshair && _s.LastMousePos is HevoPoint p)
                        _s.ProjectHover(board, p);
                    return false;

                case PointerEventKind.Leave:
                    _s.LastMousePos = null;
                    if (!_s.IsPanning && !_s.IsBoxActive && !_s.GestureActive) _s.ClearHit(board);
                    return false;

                case PointerEventKind.Key:
                    if (e.Key != PointerKey.Escape || !_s.F.DismissOnEscape) return false;
                    _s.IsHoverArmed = false;
                    _s.ClearHit(board);
                    return true;
            }
            return false;
        }

        /// <summary>视口变更后刷新十字光标位置。</summary>
        public void OnActiveRangeChanged(DataBlackboard board)
        {
            using (board.UpdateScope())
            {
                var activeRange = board.Read(_s.Viewport.ActiveRange);
                var plotArea = _s.Ctx.GetPlotArea();
                var scaleStrategy = _s.Ctx.Shared().Read<ScaleStrategyTrait>();

                if (!activeRange.IsValid || plotArea.IsEmpty || scaleStrategy == null) return;

                if (_s.LastMousePos.HasValue && !_s.IsPanning && !_s.IsBoxActive && _s.IsHoverArmed && !_s.HideCrosshair)
                    _s.UpdatePointerStateFromMouse(plotArea, scaleStrategy, board, activeRange, _s.LastMousePos.Value);
            }
        }
    }
}
