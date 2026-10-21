using Hevo.Charting.Core;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 链上第一站,从不拦:做"任何输入都要做的事"。
    /// <list type="bullet">
    ///   <item>按下:拿焦点、停惯性、武装 hover。被区域认领的按下也到这里(焦点由路由器拿),并在按下点就地投影一次十字光标
    ///         (RequireClickToActivate 时单击武装后不挪鼠标也看得到)。</item>
    ///   <item>移动:记下指针位置,后面的平移 / 框选 / hover / 滚轮锚点都用它。</item>
    ///   <item>路由变化:区域手势开始 / 结束、要不要藏十字光标。</item>
    /// </list>
    /// </summary>
    internal sealed class PointerStateBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        public PointerStateBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            var board = _s.Board;
            if (board == null) return false;
            switch (e.Kind)
            {
                case PointerEventKind.Press:
                    if (!route.ClaimedAbove) _s.Host?.Focus();
                    _s.Inertia.Stop();
                    _s.IsHoverArmed = true;
                    if (route.ClaimedAbove)
                    {
                        _s.LastMousePos = e.Input.Position;
                        _s.ProjectHoverAtLastPosition(board);
                    }
                    break;

                case PointerEventKind.Move:
                    _s.LastMousePos = e.Input.Position;
                    break;

                case PointerEventKind.RouteChanged:
                {
                    _s.GestureActive = route.GestureActive;
                    bool hide = route.GestureActive && route.HideCrosshair;
                    if (hide == _s.HideCrosshair) break;
                    _s.HideCrosshair = hide;
                    if (hide) _s.ClearHit(board);
                    else _s.ProjectHoverAtLastPosition(board);   // 恢复十字光标:不用等下一次移动
                    break;
                }
            }
            return false;
        }
    }
}
