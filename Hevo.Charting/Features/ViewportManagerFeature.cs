using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    public enum ViewportAlignment { RightEdge, LeftEdge }

    /// <summary>
    /// 视口越界策略：用户拖到数据边界外时怎么处理。
    /// </summary>
    public enum OverscrollPolicy
    {
        /// <summary>硬钳位：UserRange 越界时强制拉回数据范围。简单稳定，适合数据量固定的业务。</summary>
        Hard,
        /// <summary>越界留白：允许 UserRange 超出数据范围，超出部分显示为空白。
        /// 适合分页业务（K 线）：用户的拖拽意图直接保留，分页加载完成后由真实数据填充空白。</summary>
        Overscroll
    }

    /// <summary>
    /// 视口跨度限制 trait（视图层产生，交互层消费）：
    /// <list type="bullet">
    ///   <item>生产者：<see cref="ViewportManagerFeature"/> —— 它持有 <see cref="ViewportManagerFeature.MinVisibleCount"/> 与 <see cref="ViewportManagerFeature.MaxSpanMultiplier"/> 配置</item>
    ///   <item>消费者：<see cref="ChartInteractionFeature"/>.HandleZoom —— 构造 <c>ZoomContext.Limits</c> 喂给策略 clamp</item>
    /// </list>
    /// 注意：MaxSpanMultiplier 是相对乘数，需要结合当前 LogicalLength 才能解析为绝对 MaxSpan。
    /// 最终 MaxSpan = min(maxIndex × MaxSpanMultiplier, MaxSpanByPixels)。
    /// </summary>
    /// <param name="MaxSpanByPixels">按绘图区物理宽度折算的跨度上限(见 <see cref="ViewportManagerFeature.MinBarSpacingPx"/>);
    /// 未开启 / 尚未布局时为 +∞。</param>
    public record ViewportSpanLimitsTrait(double MinSpan, double MaxSpanMultiplier, double MaxSpanByPixels = double.PositiveInfinity) : IVisualTrait;

    /// <summary>
    /// 视口初始配置 trait（视图层产生，交互层消费）：
    /// 双击复原等"回到原点"语义需要知道 ViewportManager 的首屏视口配置才能复算 reset 目标。
    /// 单独抽 trait 避免 InteractionFeature 直接持有 ViewportManager 引用。
    /// </summary>
    /// <param name="DefaultVisibleCount">首屏显示根数；null = 显示全部</param>
    /// <param name="Alignment">数据/视口变化时的停靠侧</param>
    public record InitialViewportTrait(double? DefaultVisibleCount, ViewportAlignment Alignment) : IVisualTrait;

    /// <summary>
    /// 视口管家:把 UserRange(用户意图) + LogicalLength(数据现实) 钳制成最终的 ActiveRange(渲染真相)。
    /// <para>
    /// <b>典型用法</b>(K 线分页业务):
    /// <code>
    /// canvas.Add(new ViewportManagerFeature
    /// {
    ///     Alignment           = ViewportAlignment.RightEdge,
    ///     DefaultVisibleCount = 120,                // 首屏 120 根
    ///     MinVisibleCount     = 5,
    ///     MaxSpanMultiplier   = 1.5,                // 允许缩到 1.5 倍数据宽度
    ///     OverscrollMin       = OverscrollPolicy.Overscroll, // 左侧允许越界(给分页留意图)
    ///     OverscrollMax       = OverscrollPolicy.Hard,
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:Viewport.UserRange + LogicalLength 任一变化 → Watch → 钳位算法 → 写 Viewport.ActiveRange;
    /// 同时 publish ViewportSpanLimitsTrait + InitialViewportTrait 给 ChartInteractionFeature 消费。
    /// </para>
    /// </summary>
    public class ViewportManagerFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Layout;

        // 一个 schema 只能有一套视口钳制策略;多实例会导致 ActiveRange 被互相覆写,
        // 写一帧 valid 一帧 default 的抖动。Add 检测同类型已存在时自动替换。
        public override bool IsSingleton => true;

        // Ports 字段已迁出:持有职责拆给 ViewportPortsFeature(框架强制 ensure)。VPM 内部读写 Ports 改用
        // base ChartFeature.Viewport 字段(InternalCompose 时从 PortsFeature.Ports 自动注入)。
        // 联动 dashboard 改 SharedViewport 由 LinkedMaster/Pane 在 PortsFeature.Ports 上 mutate,
        // VPM 不再参与那条路径(纯钳制业务,联动副图根本不挂 VPM 避免双写竞态)。

        /// <summary>数据/视口变化时的停靠侧。RightEdge = 默认贴右(行情图常态);LeftEdge = 贴左。也用于双击复原方向。</summary>
        public ViewportAlignment Alignment { get; init; } = ViewportAlignment.RightEdge;

        /// <summary>视口最少需要包含几根数据（限制最大放大倍数）。同时也是 SafeBuffer 的下限。</summary>
        public double MinVisibleCount { get; init; } = 3;

        /// <summary>初次布局时显示几根数据（null = 显示全部）。分页业务必传，否则视图贴死数据两端导致 pan 无反应。</summary>
        public double? DefaultVisibleCount { get; init; }

        /// <summary>视口最大可缩放至数据量的几倍（限制最大缩小倍数）。
        /// 注意：这是**视口跨度的硬性约束**，应用于所有 UserRange 写入（Pan/Zoom 都会被钳制），
        /// **不是缩放算法参数**。缩放具体行为由 <see cref="Buildin.IZoomStrategy"/> 决定。</summary>
        public double MaxSpanMultiplier { get; init; } = 1.5;

        /// <summary>
        /// 每根数据至少占几个物理像素(限制最大缩小倍数):可见根数 ≤ 绘图区物理宽度 / 该值。默认 0 = 不限制(原行为)。
        /// <para>
        /// 设 1 即一个物理像素列里最多一根。比这更密时逐根画只是在同一列像素上反复覆盖,绘制记录却随根数线性涨;
        /// 没做按像素列合并的图层(如成交量 BarLayer)亚像素宽的柱子在 EdgeMode.Aliased 下只剩零星竖条(满屏白缝)。
        /// 在视口这一处卡住,任何图层(含外部图层)拿到的视口都不会过密。代价是缩不到"一屏看全量历史"。
        /// </para>
        /// <para>开启后:窗口缩放 / 换屏 DPI 时按新宽度重算;收窄时贴停靠边的视图保持贴边;缩放到上限时按鼠标锚点停住;
        /// BarLayer 柱宽至少 1px。</para>
        /// </summary>
        public double MinBarSpacingPx { get; init; }

        public OverscrollPolicy OverscrollMin { get; init; } = OverscrollPolicy.Hard;
        public OverscrollPolicy OverscrollMax { get; init; } = OverscrollPolicy.Hard;
        public double HistoryLoadTolerance { get; init; } = 30;
        public double RightEdgeStickTolerance { get; init; } = 2;

        // 上次见到的 LogicalLength。-1 = 首次进入,走"首屏初始化"分支;长大 = 心跳/拉历史,走"Stick to edge"分支。
        private int _lastLogicalLength = -1;
        // 上次处理过的 UserRange,防止 ActiveRange 被反复回写到 UserRange 时形成死锁。
        private RealRange _lastProcessedUserRange = new RealRange(double.NaN, double.NaN);

        // 按像素算出的跨度上限(MinBarSpacingPx > 0 时才有限)。只在 UI 线程(帧首的 RequestUpdate)更新;Watch 可能跑在数据线程,只读这个缓存。
        private double _pixelMaxSpan = double.PositiveInfinity;
        // 上次排重算时看到的视口宽 + DPI。窗口缩放 / 换屏时变化,OnProject 据此排一次重算。
        private double _seenViewportWidth = double.NaN;
        private double _seenPixelsPerDip = double.NaN;
        private Action<RenderContext>? _reclamp;
        // 排进帧队列的重算可能在 Decompose 之后才执行,用自己的引用判活,不碰会抛异常的 Chart / Schema
        private ChartCell? _cell;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            ctx.Shared().PublishData(new ViewportSpanLimitsTrait(Math.Max(0, MinVisibleCount - 1), MaxSpanMultiplier, _pixelMaxSpan));
            ctx.Shared().PublishData(new InitialViewportTrait(DefaultVisibleCount, Alignment));

            _cell = chart;
            _reclamp = Reclamp;
            flow.Watch(new object[] { Viewport.LogicalLength, Viewport.UserRange }, Clamp);
        }

        // 只在环境纪元变化(尺寸 / DPI / 首次挂载)时被调到:视口宽或 DPI 变了就排一次重算。
        // 不在这里重算:绘图区由同为 Layout 阶段的 GridLayoutFeature 算,本帧未必已更新,
        // 放到下一帧帧首(那时 PlotAreaTrait 已是新的),而且 OnProject 不写黑板。
        protected override void OnProject(FeatureContext ctx)
        {
            if (MinBarSpacingPx <= 0) return;
            var size = ctx.Shared().Read<ViewportSizeTrait>();
            if (size == null) return;
            var cell = _cell;
            if (cell == null) return;
            double ppd = cell.PixelsPerDip;
            if (size.Width == _seenViewportWidth && ppd == _seenPixelsPerDip) return;
            _seenViewportWidth = size.Width;
            _seenPixelsPerDip = ppd;
            cell.RequestUpdate(_reclamp!);
        }

        protected override void OnDecompose()
        {
            base.OnDecompose();
            _cell = null;
            _seenViewportWidth = _seenPixelsPerDip = double.NaN;
        }

        private void Reclamp(RenderContext ctx)
        {
            var cell = _cell;
            if (cell == null) return;
            double cap = ComputePixelMaxSpan(ctx.GetPlotArea(), cell.PixelsPerDip, MinBarSpacingPx);
            if (cap == _pixelMaxSpan) return;
            _pixelMaxSpan = cap;
            ctx.Shared().PublishData(new ViewportSpanLimitsTrait(Math.Max(0, MinVisibleCount - 1), MaxSpanMultiplier, cap));

            var board = CurrentBoardOrNull;
            if (board == null || board.IsDisposed) return;
            Clamp(board);
        }

        /// <summary>
        /// 绘图区物理宽度能容下的最大跨度:可见根数 ≈ Span + 1(居中刻度两端各留半根),
        /// 每根至少 <paramref name="minBarSpacingPx"/> 物理像素。绘图区未布局 / 不限制时为 +∞。
        /// </summary>
        internal static double ComputePixelMaxSpan(HevoRect plotArea, double pixelsPerDip, double minBarSpacingPx)
        {
            if (minBarSpacingPx <= 0 || plotArea.IsEmpty || plotArea.Width <= 0 || pixelsPerDip <= 0) return double.PositiveInfinity;
            return Math.Max(0, plotArea.Width * pixelsPerDip / minBarSpacingPx - 1);
        }

        private void Clamp(DataBlackboard board)
        {
            using (board.UpdateScope())
            {
                int currentLength = board.Read(Viewport.LogicalLength);
                var userRange = board.Read(Viewport.UserRange);
                var activeRange = board.Read(Viewport.ActiveRange);

                if (currentLength <= 0)
                {
                    // 归零等价于"上下文重置"（SwitchContext 清 buffer + Publish 会经过这条路径）。
                    // 把内部 last 状态一并清空，下次有数据进来走"首次 layout"分支，
                    // 避免 _lastLogicalLength 泄漏旧上下文（导致 shrink 分支用陈旧 activeRange 卡死）。
                    //
                    // 💥 写 default(RealRange) 而非 (0,1)：长度归 0 但 candle/value 端口写入非原子，
                    // 端口可能仍残留旧数据。若写 (0,1) 这种 IsValid=true 的窄区间，CandleFeature 会用
                    // xRange.Span=1 渲染旧蜡烛 → 每根宽度=整个画布 → tab 切回首帧出现超大蜡烛闪烁。
                    // 写 default 让所有消费者通过 !IsValid 跳过该帧，等新数据到位后走首次 layout。
                    _lastLogicalLength = -1;
                    _lastProcessedUserRange = new RealRange(double.NaN, double.NaN);
                    using (board.WriteScope()) board.Write(Viewport.ActiveRange, default);
                    return;
                }

                double maxIndex = currentLength - 1;
                double minSpan = Math.Max(0, MinVisibleCount - 1);
                RealRange targetRange;

                if (_lastLogicalLength <= 0)
                {
                    double initialSpan = DefaultVisibleCount.HasValue ? Math.Max(minSpan, Math.Min(DefaultVisibleCount.Value - 1, maxIndex)) : Math.Max(minSpan, maxIndex);
                    targetRange = new RealRange(maxIndex - initialSpan, maxIndex);
                }
                else if (currentLength > _lastLogicalLength)
                {
                    int delta = currentLength - _lastLogicalLength;
                    double lastMaxIndex = _lastLogicalLength - 1;

                    if (activeRange.IsValid && activeRange.Max >= lastMaxIndex - RightEdgeStickTolerance) targetRange = new RealRange(activeRange.Min + delta, activeRange.Max + delta);
                    else if (activeRange.IsValid && activeRange.Min <= HistoryLoadTolerance) targetRange = new RealRange(activeRange.Min + delta, activeRange.Max + delta);
                    else targetRange = activeRange;
                }
                else
                {
                    bool isNewUserIntent = userRange.IsValid && !userRange.Equals(_lastProcessedUserRange);
                    if (isNewUserIntent) { targetRange = userRange; _lastProcessedUserRange = userRange; }
                    else targetRange = activeRange;
                }

                _lastLogicalLength = currentLength;

                // 💥 物理钳制：重心锁定 + 终极虚空法则
                double targetSpan = targetRange.Span;
                double targetCenter = targetRange.Min + targetSpan / 2.0;
                double maxSpan = Math.Min(maxIndex * MaxSpanMultiplier, _pixelMaxSpan);
                double clampedSpan = Math.Clamp(targetSpan, minSpan, Math.Max(minSpan, maxSpan));

                double min = targetCenter - clampedSpan / 2.0;
                double max = targetCenter + clampedSpan / 2.0;

                // 被像素上限收窄时(典型:窗口变窄后重算)贴着停靠边的视图保持贴边,
                // 否则居中收缩会把盯盘时的最新一根挤出右边。只看像素上限:倍数上限的收窄保持原来的居中行为。
                if (clampedSpan < targetSpan && _pixelMaxSpan < maxIndex * MaxSpanMultiplier)
                {
                    if (Alignment == ViewportAlignment.RightEdge && targetRange.Max >= maxIndex - RightEdgeStickTolerance)
                    {
                        max = targetRange.Max;
                        min = max - clampedSpan;
                    }
                    else if (Alignment == ViewportAlignment.LeftEdge && targetRange.Min <= RightEdgeStickTolerance)
                    {
                        min = targetRange.Min;
                        max = min + clampedSpan;
                    }
                }

                // 💥 边界处理：分两步走
                //   Step 1：Span 超过数据时（极致缩小），按 Alignment 选择停靠边
                //   Step 2：Span 在数据范围内，对各边界按 OverscrollPolicy 决定钳/越
                if (clampedSpan > maxIndex)
                {
                    if (Alignment == ViewportAlignment.RightEdge) { max = maxIndex; min = max - clampedSpan; }
                    else { min = 0; max = clampedSpan; }
                }
                else
                {
                    // 钳位顺序：Alignment 决定哪边优先被检查（避免两边同时越界时反向覆盖）
                    if (Alignment == ViewportAlignment.RightEdge)
                    {
                        ApplyMaxBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMax);
                        ApplyMinBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMin);
                    }
                    else
                    {
                        ApplyMinBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMin);
                        ApplyMaxBoundary(ref min, ref max, maxIndex, clampedSpan, OverscrollMax);
                    }
                }

                // 💥 绝对单向流：管家只负责输出现实，不再管意图！打破一切死锁。
                using (board.WriteScope()) board.Write(Viewport.ActiveRange, new RealRange(min, max));
            }
        }

        public void ResetState() { _lastLogicalLength = -1; _lastProcessedUserRange = new RealRange(double.NaN, double.NaN); }

        // 💥 边界判定原子操作：Hard 钳回 maxIndex/0；Overscroll 直接放行（保留越界 UserRange 给 series/axis 渲染）。
        private static void ApplyMaxBoundary(ref double min, ref double max, double maxIndex, double clampedSpan, OverscrollPolicy policy)
        {
            if (max > maxIndex && policy == OverscrollPolicy.Hard)
            {
                max = maxIndex;
                min = max - clampedSpan;
            }
        }

        private static void ApplyMinBoundary(ref double min, ref double max, double maxIndex, double clampedSpan, OverscrollPolicy policy)
        {
            if (min < 0 && policy == OverscrollPolicy.Hard)
            {
                min = 0;
                max = clampedSpan;
            }
        }
    }
}