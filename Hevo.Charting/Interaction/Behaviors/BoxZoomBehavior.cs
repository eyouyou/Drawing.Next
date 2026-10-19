using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 框选缩放(<see cref="ChartInteractionMode.BoxZoom"/>):按着 <see cref="BoxZoomOptions.Activation"/>(默认 Shift)拖出选框,
    /// 松手把选框的 X 范围设为视口。选框矩形写进 Feature 的框选状态端口,由 Feature 的 OnProject 搬给图层。
    /// </summary>
    internal sealed class BoxZoomBehavior : IPointerBehavior
    {
        private readonly ChartInteractionState _s;
        private readonly DataPort<BoxZoomTrait> _statePort;
        private HevoPoint _start;
        private HevoPoint _current;

        public BoxZoomBehavior(ChartInteractionState state, DataPort<BoxZoomTrait> statePort)
        {
            _s = state;
            _statePort = statePort;
        }

        public bool OnInput(ref PointerEvent e, in PointerRoute route)
        {
            var board = _s.Board;
            if (board == null) return false;
            var config = _s.F.BoxZoomConfig;
            switch (e.Kind)
            {
                case PointerEventKind.Press:
                {
                    if (route.ClaimedAbove) return false;
                    // Activation=None 任意按下进入,否则修饰键命中才进入
                    var activation = config.Activation;
                    if (activation != PointerModifiers.None && (e.Input.Modifiers & activation) != activation) return false;
                    _s.IsBoxActive = true;
                    _start = _current = e.Input.Position;
                    using (board.AcquireWriteLock())
                    {
                        board.WriteIfChanged(_s.F.PointerHitPort, null);
                        board.WriteIfChanged(_statePort, ActiveTrait(config));
                    }
                    _s.Host?.CaptureForBackground();
                    return true;
                }
                case PointerEventKind.Move:
                    if (!_s.IsBoxActive) return false;
                    _current = e.Input.Position;
                    using (board.AcquireWriteLock()) board.WriteIfChanged(_statePort, ActiveTrait(config));
                    return true;

                case PointerEventKind.Release:
                    if (!_s.IsBoxActive) return false;
                    _s.IsBoxActive = false;
                    _s.Host?.ReleaseBackgroundCapture();
                    Finalize(board, config);
                    return true;
            }
            return false;
        }

        /// <summary>Feature 卸载时:选框清掉。</summary>
        internal void Clear(DataBlackboard board)
        {
            using (board.AcquireWriteLock()) board.WriteIfChanged(_statePort, BoxZoomTrait.Inactive);
        }

        private BoxZoomTrait ActiveTrait(BoxZoomOptions config) => new(true, ComputeRect(), config.BorderStyle, config.FillBrush);

        private void Finalize(DataBlackboard board, BoxZoomOptions config)
        {
            // 不管下面成不成,选框矩形必须立刻清掉
            using (board.AcquireWriteLock()) board.WriteIfChanged(_statePort, BoxZoomTrait.Inactive);

            using (board.AcquireUpgradeableReadLock())
            {
                var plotArea = _s.Ctx.GetPlotArea();
                var scaleStrategy = _s.Ctx.Shared().Read<ScaleStrategyTrait>();
                var baseRange = board.Read(_s.Viewport.ActiveRange);
                if (plotArea.IsEmpty || scaleStrategy == null || !baseRange.IsValid) return;

                var projected = ProjectToDomainRange(config, plotArea, scaleStrategy.DomainScale, baseRange);
                if (projected is not RealRange newRange) return;

                if (scaleStrategy.DomainScale is ISnappableScale snappable)
                {
                    double sMin = snappable.Snap(newRange.Min);
                    double sMax = snappable.Snap(newRange.Max);
                    if (sMax - sMin >= 1.0) newRange = new RealRange(sMin, sMax);
                }

                using (board.AcquireWriteLock()) board.WriteIfChanged(_s.Viewport.UserRange, newRange);
            }
        }

        /// <summary>反投影选框 X 范围到 domain。返回 null = 选框过小或与 plot 区无交集(误操作)。</summary>
        private RealRange? ProjectToDomainRange(BoxZoomOptions config, HevoRect plotArea, IScale scale, RealRange baseRange)
        {
            float left = Math.Min(_start.X, _current.X);
            float right = Math.Max(_start.X, _current.X);
            if (right - left < config.MinPixelSize) return null;

            double clampedLeft = Math.Max(left, plotArea.Left);
            double clampedRight = Math.Min(right, plotArea.Right);
            if (clampedRight - clampedLeft < config.MinPixelSize) return null;

            double normLeft = (clampedLeft - plotArea.Left) / plotArea.Width;
            double normRight = (clampedRight - plotArea.Left) / plotArea.Width;
            double newMin = scale.Denormalize(normLeft, baseRange);
            double newMax = scale.Denormalize(normRight, baseRange);
            return newMax > newMin ? new RealRange(newMin, newMax) : (RealRange?)null;
        }

        private HevoRect ComputeRect()
        {
            float left = Math.Min(_start.X, _current.X);
            float right = Math.Max(_start.X, _current.X);
            float top = Math.Min(_start.Y, _current.Y);
            float bottom = Math.Max(_start.Y, _current.Y);
            return new HevoRect(left, top, right - left, bottom - top);
        }
    }
}
