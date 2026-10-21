using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 一个 <see cref="ChartInteractionFeature"/> 装上的那组行为共用的状态和视口算法。
    /// <para>
    /// 状态:最后的指针位置、hover 是否武装、在平移 / 框选中、区域手势是否进行中,惯性。行为之间靠它配合
    /// (平移中 hover 不投影、鼠标离开时平移中不清十字光标……),跟拆开之前同一个 Feature 里的字段一一对应。
    /// </para>
    /// <para>
    /// 算法(平移换算、缩放、指针命中写 <see cref="ChartInteractionFeature.PointerHitPort"/>)原样搬自拆开之前的
    /// ChartInteractionFeature;选项直接读 Feature 的 init-only 属性,装配后不会变。
    /// </para>
    /// </summary>
    internal sealed class ChartInteractionState
    {
        internal const double StandardWheelDelta = 120.0;

        public readonly ChartInteractionFeature F;
        private readonly RenderContext _ctx;
        private readonly IChartCell _chart;

        // 上一次鼠标物理坐标(DrawingCanvas 系)。视口刷新 / 抬手补投影 / 滚轮缩放都靠这个回放命中。
        public HevoPoint? LastMousePos;
        // RequireClickToActivate = true 时初值 false(等单击武装);任意按下武装;Esc(DismissOnEscape)解除。
        public bool IsHoverArmed;
        // 平移与框选互斥,任意一个为 true 时 hover 不投影
        public bool IsPanning;
        public bool IsBoxActive;
        // 区域手势进行中:触屏不动、鼠标离开不清 hit;HideCrosshair 时十字光标暂停
        public bool GestureActive;
        public bool HideCrosshair;

        public readonly InertiaPanRunner Inertia = new();

        // 整根对齐时连续缩放的未取整视口:每步在上一步的精确结果上算,只在写出时取整。
        // 若每步都从取整后的视口重算,Min / Max 各自四舍五入的误差会一步步累积,鼠标下那根 K 线越缩越偏。
        // 视口被别人改了(平移 / 追加数据 / 撞墙钳制)或锚点挪了就作废,从当前视口重新起算。
        // 锚十字光标那根的策略,十字光标换了根也算锚点挪了:新那根在未取整视口里的位置跟画面上差最多半根,接着算会跳一根。
        private RealRange _zoomExact;
        private RealRange _zoomWritten;
        private double _zoomAnchorPx = double.NaN;
        private int? _zoomHitIndex;

        public ChartInteractionState(ChartInteractionFeature feature, IChartCell chart, RenderContext ctx)
        {
            F = feature;
            _chart = chart;
            _ctx = ctx;
            IsHoverArmed = !feature.RequireClickToActivate;
        }

        public ViewportPorts Viewport => F.Viewport;
        public DataBlackboard? Board => F.CurrentBoardOrNull;
        public IPointerHost? Host => _chart.Engine.PointerRegions.Host;
        public RenderContext Ctx => _ctx;
        public bool Has(ChartInteractionMode mode) => F.SupportedModes.HasFlag(mode);

        /// <summary>整组摘掉(Feature 卸载):停惯性;平移 / 框选中抓着的鼠标放掉。</summary>
        public void Detach()
        {
            Inertia.Stop();
            if (IsPanning || IsBoxActive) Host?.ReleaseBackgroundCapture();
            IsPanning = IsBoxActive = false;
        }

        // ==========================================
        // 平移
        // ==========================================

        /// <summary>
        /// 通用平移内核:把物理像素位移换算成 logical delta 并写 UserRange,返回真实消费的像素量。
        /// 鼠标 / Manipulation / 惯性帧 共用同一份算法。
        /// 返回值:对 ISnappableScale 表示成功消费的像素;非 snap 场景永远等于入参 deltaPx。
        /// </summary>
        public double ApplyPanDelta(DataBlackboard board, double deltaPx)
        {
            if (deltaPx == 0) return 0;
            using (board.UpdateScope())
            {
                var plotArea = _ctx.GetPlotArea();
                var scaleStrategy = _ctx.Shared().Read<ScaleStrategyTrait>();
                var baseRange = board.Read(Viewport.ActiveRange);

                if (plotArea.IsEmpty || scaleStrategy == null || !baseRange.IsValid || baseRange.Span <= 0) return 0;

                // expandedSpan: plot 整个宽度对应的 domain 跨度
                //   Edge scale = Span; Centered scale = Span + 1。
                //   Denormalize(1)-Denormalize(0) 通用兜底,对任何 IScale 实现都正确。
                double expandedSpan = scaleStrategy.DomainScale.Denormalize(1.0, baseRange) - scaleStrategy.DomainScale.Denormalize(0.0, baseRange);
                double logicalDelta = (deltaPx / plotArea.Width) * expandedSpan;
                double consumedPx = deltaPx;

                // ISnappableScale: 整数索引 ratcheting,Math.Truncate 防止"过度消费"
                if (scaleStrategy.DomainScale is ISnappableScale snappable && snappable.SnapEnabled)
                {
                    double snappedDelta = Math.Truncate(logicalDelta);
                    if (snappedDelta == 0) return 0;
                    consumedPx = (snappedDelta / expandedSpan) * plotArea.Width;
                    logicalDelta = snappedDelta;
                }

                using (board.WriteScope())
                    board.Write(Viewport.UserRange, new RealRange(baseRange.Min - logicalDelta, baseRange.Max - logicalDelta));

                return consumedPx;
            }
        }

        // ==========================================
        // 缩放
        // ==========================================

        /// <summary>滚轮刻度 → zoomFactor,委派给 <see cref="ApplyZoom"/>。</summary>
        public void HandleZoom(DataBlackboard board, HevoPoint pos, int delta)
        {
            double steps = delta / StandardWheelDelta;
            double zoomFactor = Math.Pow(1.0 - F.ZoomConfig.Sensitivity, steps);
            ApplyZoom(board, zoomFactor, pos.X);
        }

        /// <summary>
        /// 通用缩放内核:鼠标滚轮 / 键盘 / 双指捏合 共用。
        /// 构造 ZoomContext → IZoomStrategy.Calculate → ISnappableScale snap → 写 UserRange。
        /// </summary>
        /// <param name="zoomFactor">目标 Span 相对当前 Span 的比例(>1 缩小、&lt;1 放大)</param>
        /// <param name="anchorPxX">缩放锚点的物理 X 坐标(DrawingCanvas 系)</param>
        public void ApplyZoom(DataBlackboard board, double zoomFactor, double anchorPxX)
        {
            if (zoomFactor <= 0 || double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor)) return;
            using (board.UpdateScope())
            {
                var activeRange = board.Read(Viewport.ActiveRange);
                int logicalLength = board.Read(Viewport.LogicalLength);
                var plotArea = _ctx.GetPlotArea();
                var scaleStrategy = _ctx.Shared().Read<ScaleStrategyTrait>();

                if (!activeRange.IsValid || plotArea.Width <= 0 || logicalLength <= 0) return;

                var hitState = board.Read(F.PointerHitPort);
                int? hitIndex = F.ZoomStrategy is CrosshairAnchorZoomStrategy ? hitState?.Hit.LogicalIndex : null;
                var snappable = scaleStrategy?.DomainScale as ISnappableScale;
                bool continuing = snappable != null
                    && activeRange.Equals(_zoomWritten)
                    && anchorPxX.Equals(_zoomAnchorPx)
                    && hitIndex == _zoomHitIndex;
                var baseRange = continuing ? _zoomExact : activeRange;

                double rawTargetSpan = baseRange.Span * zoomFactor;

                // 解析视图层 trait → 绝对 SpanLimits
                var limitsTrait = _ctx.Shared().Read<ViewportSpanLimitsTrait>();
                double maxIndex = Math.Max(0, logicalLength - 1);
                double minSpan = limitsTrait?.MinSpan ?? 2.0;
                // 像素上限(MinBarSpacingPx)也在这里带上,缩到底时仍按鼠标锚点停住,而不是被 VPM 事后居中收回
                double maxSpan = limitsTrait != null
                    ? Math.Min(maxIndex * limitsTrait.MaxSpanMultiplier, limitsTrait.MaxSpanByPixels)
                    : maxIndex * 2.0;
                var limits = new SpanLimits(minSpan, Math.Max(minSpan, maxSpan));

                // 锚点相对位置:保留越界(不 Clamp 到 [0,1]),让策略判断锚点是否在 plot 区。
                // 未武装的 hover 状态(requireClickToActivate=true 未点击 / Esc 解除武装)下鼠标位置不可信,
                // 强制写成越界值 -1,让 SmartAdaptive / CrosshairAnchor / Directional 走各自的 out-of-plot 分支
                // (默认右缘锚定),严守"未武装前不采信鼠标"的契约。
                double relativeX = IsHoverArmed
                    ? (anchorPxX - plotArea.Left) / plotArea.Width
                    : -1.0;

                // 数据可用性(分页墙状态):未启用分页时一律视为未耗尽
                var availability = F.AvailabilityPort != null ? board.Read(F.AvailabilityPort) : default;

                var zoomCtx = new ZoomContext(
                    BaseRange: baseRange,
                    RawTargetSpan: rawTargetSpan,
                    MouseRelativeX: relativeX,
                    HitState: hitState,
                    LogicalLength: logicalLength,
                    Limits: limits,
                    DomainScale: scaleStrategy?.DomainScale,
                    LeftDataExhausted: availability.LeftExhausted,
                    RightDataExhausted: availability.RightExhausted);

                RealRange newRange = F.ZoomStrategy.Calculate(in zoomCtx);

                // 视口与上一帧无变化就退帧,省一次完整 ProjectAll。判等用 NumericEqual,跟撞墙判定同口径。
                if (Math.Abs(newRange.Span - baseRange.Span) < MathTolerance.NumericEqual
                    && Math.Abs(newRange.Min - baseRange.Min) < MathTolerance.NumericEqual) return;

                var exactRange = newRange;
                if (snappable != null)
                {
                    double snappedMin = snappable.Snap(newRange.Min);
                    double snappedMax = snappable.Snap(newRange.Max);
                    if (snappedMax - snappedMin >= 1.0)
                        newRange = new RealRange(snappedMin, snappedMax);
                }

                using (board.WriteScope())
                    board.Write(Viewport.UserRange, newRange);

                _zoomExact = exactRange;
                _zoomWritten = newRange;
                _zoomAnchorPx = anchorPxX;
                _zoomHitIndex = hitIndex;
            }
        }

        // ==========================================
        // 指针命中(十字光标)
        // ==========================================

        /// <summary>在最后的指针位置投影一次十字光标(要求 hover 开着且已武装)。</summary>
        public void ProjectHoverAtLastPosition(DataBlackboard board)
        {
            if (!LastMousePos.HasValue || !IsHoverArmed || !Has(ChartInteractionMode.Hover)) return;
            ProjectHover(board, LastMousePos.Value);
        }

        /// <summary>在给定位置投影一次十字光标(不看武装,调用方负责)。</summary>
        public void ProjectHover(DataBlackboard board, HevoPoint pos)
        {
            using (board.UpdateScope())
            {
                var activeRange = board.Read(Viewport.ActiveRange);
                var scaleStrategy = _ctx.Shared().Read<ScaleStrategyTrait>();
                var plotArea = _ctx.GetPlotArea();
                if (activeRange.IsValid && scaleStrategy != null && !plotArea.IsEmpty)
                    UpdatePointerStateFromMouse(plotArea, scaleStrategy, board, activeRange, pos);
            }
        }

        public void ClearHit(DataBlackboard board)
        {
            using (board.WriteScope()) board.Write(F.PointerHitPort, null);
        }

        /// <summary>
        /// 指针碰撞检测:将鼠标物理位置投射至黑板的数学模型中,并触发吸附判定
        /// </summary>
        public void UpdatePointerStateFromMouse(HevoRect plotArea, ScaleStrategyTrait scaleStrategy, DataBlackboard board, RealRange activeRange, HevoPoint pos)
        {
            double margin = F.EdgeHitTolerance;
            // 若超出容差边缘,判定为离场,清空光标
            if (pos.X < plotArea.Left - margin || pos.X > plotArea.Right + margin || pos.Y < plotArea.Top - margin || pos.Y > plotArea.Bottom + margin)
            {
                using (board.WriteScope()) board.Write(F.PointerHitPort, null);
                return;
            }

            // 1. 根据鼠标相对物理位移,反向 Denormalize 查找到准确的逻辑索引
            double relativeX = Math.Clamp((pos.X - plotArea.Left) / plotArea.Width, 0.0, 1.0);
            double logicalValue = scaleStrategy.DomainScale.Denormalize(relativeX, activeRange);
            int globalIndex = (int)Math.Round(logicalValue); // 吸附到最近的 K线索引

            // 收进"可见整数索引"范围,保证正向投影回来的 centerX ∈ [plot.Left, plot.Right]。
            // 指针压在绘图区边沿时 logicalValue 恰为 Min-0.5 / Max+0.5,Math.Round 银行家舍入按奇偶决定方向,
            // 不收的话会命中视口外那根,十字线画到绘图区外。
            int visibleLeft = (int)Math.Ceiling(scaleStrategy.DomainScale.Denormalize(0.0, activeRange));
            int visibleRight = (int)Math.Floor(scaleStrategy.DomainScale.Denormalize(1.0, activeRange));
            if (visibleLeft <= visibleRight) globalIndex = Math.Clamp(globalIndex, visibleLeft, visibleRight);

            // 2. 校验有效性边界
            int validCount = ValidCount(board);

            // 数据世界范围 [0, validCount-1](删 Slicer 后,世界索引 == 数组下标)
            bool isOutOfBounds = globalIndex < 0 || globalIndex >= validCount;

            // 3. 吸附防线:是否强制把越界的光标拉回合法数据区
            if (F.PointerSnapMode == PointerOutOfBoundsStrategy.SnapToValidData)
            {
                // 再收进数据范围,让竖线与交点圆点和 series 画出来的柱子/点位严格对齐。
                int lo = Math.Max(0, visibleLeft);
                int hi = Math.Min(validCount - 1, visibleRight);
                // 可见范围与数据范围无交集时回退到数据范围(极端缩放场景)
                if (hi < lo) { lo = 0; hi = Math.Max(0, validCount - 1); }
                globalIndex = Math.Clamp(globalIndex, lo, hi);
                isOutOfBounds = false;
            }

            // 4. 正向投影:根据吸附后的标准逻辑索引,重新计算出它绝对居中的物理屏幕 X 坐标
            double centerRelativeX = scaleStrategy.DomainScale.Normalize(globalIndex, activeRange);
            double centerX = CoordinateExtensions.ProjectDomainToScreen(plotArea, activeRange, scaleStrategy, globalIndex);
            var hit = new DomainHitResult(globalIndex, centerX, centerRelativeX);

            using (board.WriteScope())
            {
                board.Write(F.PointerHitPort, new PointerHitState(
                    new HevoPoint((float)centerX, pos.Y),
                    hit,
                    centerX,
                    globalIndex,    // 删 Slicer 后世界索引 == 数组下标,LocalIndex 无意义,直接用 world
                    isOutOfBounds));
            }
        }

        /// <summary>可命中根数:业务给了有效计数端口就用它,否则 LogicalLength。</summary>
        public int ValidCount(DataBlackboard board)
        {
            int logicalLength = board.Read(Viewport.LogicalLength);
            int validCount = F.ValidDataCountPort != null ? board.Read(F.ValidDataCountPort) : logicalLength;
            if (validCount <= 0) validCount = logicalLength;
            return validCount;
        }
    }
}
