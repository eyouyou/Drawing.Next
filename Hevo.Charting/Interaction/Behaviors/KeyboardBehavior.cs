using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 键盘(Esc 归 <see cref="HoverBehavior"/>):
    /// <list type="bullet">
    ///   <item>↑ / ↓ 缩放一档(<see cref="ChartInteractionMode.KeyboardZoom"/>,默认不开)。</item>
    ///   <item><see cref="ChartInteractionMode.Keyboard"/> 开着时任意键都停惯性;← / → 把十字光标移一根 K 线,出了视口就带着视口走。</item>
    /// </list>
    /// </summary>
    internal sealed class KeyboardBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        public KeyboardBehavior(ChartInteractionState state) => _s = state;

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            if (e.Kind != PointerEventKind.Key) return false;
            var board = _s.Board;
            if (board == null) return false;

            // ↑ / ↓ 缩放:单独的开关,不在 All / Default 里(默认不抢上下键)
            if ((e.Key == PointerKey.Up || e.Key == PointerKey.Down) && _s.Has(ChartInteractionMode.KeyboardZoom))
            {
                _s.Inertia.Stop();
                Zoom(board, e.Key == PointerKey.Up);
                return true;
            }

            if (!_s.Has(ChartInteractionMode.Keyboard)) return false;
            _s.Inertia.Stop();

            if (e.Key == PointerKey.Left) { Navigate(board, -1); return true; }
            if (e.Key == PointerKey.Right) { Navigate(board, 1); return true; }
            return false;
        }

        /// <summary>放大 / 缩小一档(同一次滚轮刻度),锚点跟滚轮一样取最后的指针位置。</summary>
        private void Zoom(DataBlackboard board, bool zoomIn)
        {
            var plotArea = _s.Ctx.GetPlotArea();
            if (plotArea.IsEmpty) return;
            float anchorX = _s.LastMousePos?.X ?? plotArea.Right;
            int delta = (int)ChartInteractionState.StandardWheelDelta;
            _s.HandleZoom(board, new HevoPoint(anchorX, 0), zoomIn ? delta : -delta);
        }

        /// <summary>方向键导航:移动十字光标,目标出了视口就平移视口跟上。</summary>
        private void Navigate(DataBlackboard board, int indexDelta)
        {
            var hitPort = _s.F.PointerHitPort;
            var viewport = _s.Viewport;
            using (board.AcquireUpgradeableReadLock())
            {
                var hit = board.Read(hitPort);
                var baseRange = board.Read(viewport.ActiveRange);
                var plotArea = _s.Ctx.GetPlotArea();
                var scaleStrategy = _s.Ctx.Shared().Read<ScaleStrategyTrait>();

                if (hit == null || !baseRange.IsValid || plotArea.IsEmpty || scaleStrategy == null) return;

                // 1. 不越界到没有数据的地方:数据世界范围 [0, validCount-1]
                int validCount = _s.ValidCount(board);
                int targetIndex = Math.Clamp(hit.Value.Hit.LogicalIndex + indexDelta, 0, validCount - 1);
                if (targetIndex == hit.Value.Hit.LogicalIndex) return;   // 到底了

                // 2. 目标在视口外:视口跟随平移
                bool isRangeChanged = false;
                if (targetIndex < baseRange.Min)
                {
                    using (board.AcquireWriteLock()) board.WriteIfChanged(viewport.UserRange, new RealRange(targetIndex, targetIndex + baseRange.Span));
                    isRangeChanged = true;
                }
                else if (targetIndex > baseRange.Max)
                {
                    using (board.AcquireWriteLock()) board.WriteIfChanged(viewport.UserRange, new RealRange(targetIndex - baseRange.Span, targetIndex));
                    isRangeChanged = true;
                }

                // 3. 用真实比例尺反推物理坐标;视口刚平移过就用平移后的
                var currentRange = isRangeChanged ? board.Read(viewport.ActiveRange) : baseRange;
                double centerRelativeX = scaleStrategy.DomainScale.Normalize(targetIndex, currentRange);
                double targetPhysicalX = plotArea.Left + (plotArea.Width * centerRelativeX);

                // 4. 合成鼠标位置(Y 不变),走同一条命中管道(吸附、边界、写回黑板)
                _s.LastMousePos = new HevoPoint((float)targetPhysicalX, hit.Value.MousePos.Y);
                _s.UpdatePointerStateFromMouse(plotArea, scaleStrategy, board, currentRange, _s.LastMousePos.Value);
            }
        }
    }
}
