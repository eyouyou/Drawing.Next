using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 双击复原视口(<see cref="ChartInteractionMode.DoubleClickReset"/>)。在框选 / 平移之前判定,避免被它们吞掉。
    /// 被区域认领的按下不计入。目标 = <see cref="DoubleClickResetOptions.CustomReset"/>,否则首屏视口(InitialViewportTrait)。
    /// </summary>
    internal sealed class DoubleClickResetBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        private long _lastClickTimeMs;
        private HevoPoint _lastClickPos;

        public DoubleClickResetBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            if (e.Kind != PointerEventKind.Press || route.ClaimedAbove) return false;
            var board = _s.Board;
            if (board == null) return false;

            var config = _s.F.DoubleClickResetConfig;
            var pos = e.Input.HostPosition;
            long now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
            bool isDouble = (now - _lastClickTimeMs) <= config.MaxInterval.TotalMilliseconds
                            && Math.Abs(pos.X - _lastClickPos.X) <= config.MaxMovementPx
                            && Math.Abs(pos.Y - _lastClickPos.Y) <= config.MaxMovementPx;
            _lastClickTimeMs = now;
            _lastClickPos = pos;
            if (!isDouble) return false;

            Reset(board, config);
            _lastClickTimeMs = 0; // 防止三连击退化成第二个双击
            return true;
        }

        private void Reset(LowCode.DataBlackboard board, DoubleClickResetOptions config)
        {
            _s.Inertia.Stop();
            var viewport = _s.Viewport;
            using (board.UpdateScope())
            {
                int logicalLength = board.Read(viewport.LogicalLength);
                if (logicalLength <= 0) return;

                RealRange resetRange;
                if (config.CustomReset != null)
                {
                    resetRange = config.CustomReset(logicalLength);
                }
                else
                {
                    // 走 ViewportManager publish 的 InitialViewportTrait,与首屏视口完全一致
                    var initial = _s.Ctx.Shared().Read<InitialViewportTrait>();
                    double maxIndex = Math.Max(0, logicalLength - 1);
                    double initialSpan = initial?.DefaultVisibleCount.HasValue == true
                        ? Math.Min(initial.DefaultVisibleCount.Value - 1, maxIndex)
                        : maxIndex;
                    initialSpan = Math.Max(0, initialSpan);

                    var alignment = initial?.Alignment ?? ViewportAlignment.RightEdge;
                    resetRange = alignment == ViewportAlignment.RightEdge
                        ? new RealRange(maxIndex - initialSpan, maxIndex)
                        : new RealRange(0, initialSpan);
                }

                if (!resetRange.IsValid) return;
                using (board.WriteScope()) board.Write(viewport.UserRange, resetRange);
            }
        }
    }
}
