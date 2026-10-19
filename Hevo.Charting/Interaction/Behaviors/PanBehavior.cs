using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 拖拽平移(<see cref="ChartInteractionMode.Pan"/>),松手按速度起惯性(<see cref="ChartInteractionMode.InertiaPan"/>)。
    /// 不看按键:右键、中键拖也平移(同原来)。按下即进入平移,没有拖动阈值。
    /// </summary>
    internal sealed class PanBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        // 平移锚点(ChartCell 坐标):每次按下重置,移动时 (current - _lastPanPos) 算出位移;ISnappableScale 下兼做未消费像素的余额
        private HevoPoint _lastPanPos;

        public PanBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            var board = _s.Board;
            if (board == null) return false;
            switch (e.Kind)
            {
                case PointerEventKind.Press:
                    if (route.ClaimedAbove) return false;
                    _s.IsPanning = true;
                    _lastPanPos = e.Input.HostPosition;
                    _s.Inertia.ResetSamples();
                    _s.ClearHit(board);
                    _s.Host?.CaptureForBackground();
                    return true;

                case PointerEventKind.Move:
                    if (!_s.IsPanning) return false;
                    Pan(board, e.Input.HostPosition);
                    return true;

                case PointerEventKind.Release:
                    if (!_s.IsPanning) return false;
                    End(board);
                    return true;
            }
            return false;
        }

        private void Pan(DataBlackboard board, HevoPoint currentPos)
        {
            double deltaX = currentPos.X - _lastPanPos.X;
            if (deltaX == 0) return;

            // ISnappableScale 模式下返回真实"消费"的像素量(可能少于 deltaX),残留余额留到下一次移动累积,防止小幅拖动卡死
            double consumedPx = _s.ApplyPanDelta(board, deltaX);
            if (consumedPx == 0) return;

            _s.Inertia.RecordSample(consumedPx);
            _lastPanPos = new HevoPoint((float)(_lastPanPos.X + consumedPx), _lastPanPos.Y);
        }

        private void End(DataBlackboard board)
        {
            _s.IsPanning = false;
            var host = _s.Host;
            host?.ReleaseBackgroundCapture();
            if (_s.Has(ChartInteractionMode.InertiaPan) && host != null)
            {
                _s.Inertia.Start(_s.F.InertiaPanConfig, host,
                    applyPxDelta: dx => _s.ApplyPanDelta(board, dx),
                    readActiveRange: () =>
                    {
                        using (board.UpdateScope()) return board.Read(_s.Viewport.ActiveRange);
                    });
            }

            // 单击-武装-松手不挪鼠标:按下时清了 hit 准备平移,其实没拖、松手也不动鼠标的话没人写新的 hit,
            // 永远看不到光标。hover 已武装且无惯性接管时,就地投影一次。
            if (!_s.Inertia.IsRunning) _s.ProjectHoverAtLastPosition(board);
        }
    }
}
