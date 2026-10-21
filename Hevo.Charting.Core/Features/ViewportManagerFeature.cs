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

        /// <summary>数据/视口变化时的停靠侧。RightEdge = 默认贴右(行情图常态);LeftEdge = 贴左。首屏和双击复原不看它,总是贴最新一根。</summary>
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
        private IChartCell? _cell;

        // ---- 按键锚定(数据源发了 DataExtent 且 Keys 与长度对得上时启用;否则下面四个字段不参与) ----
        // 锚点 = 视口左边那根的键 + 小数偏移(越界留白时为负)。数据怎么变(前插 / 补洞 / 换纪元)都按键找回,不看长度差。
        private long? _anchorKey;
        private double _anchorFrac;
        // ScrollToKey 的目标还没加载进来(早于首根且没到底):裁决后别用最左那根覆盖锚点,等分页补到再落位
        private bool _anchorPending;
        // 上次裁决时的键:用户意图(UserRange)是按上次的下标写的,要先经它换成键;它的末根判断"有没有追加"
        private ReadOnlyMemory<long> _lastKeys;

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            ctx.Shared().PublishData(new ViewportSpanLimitsTrait(Math.Max(0, MinVisibleCount - 1), MaxSpanMultiplier, _pixelMaxSpan));
            ctx.Shared().PublishData(new InitialViewportTrait(DefaultVisibleCount, Alignment));

            _cell = chart;
            _reclamp = Reclamp;
            // Extent:等长替换(补洞 / 换纪元)长度不变也要重新按键找回
            flow.Watch(new object[] { Viewport.LogicalLength, Viewport.UserRange, Viewport.Extent }, Clamp);
        }

        /// <summary>
        /// 跳到某个键(宿主"跳到某天"):把它放到视口中间,跨度不变。
        /// <para>
        /// 只在数据源发了 <see cref="DataExtent"/> 时有效。目标早于已加载的首根时,先停在最左并把目标写进
        /// <see cref="ViewportPorts.AnchorKey"/>;分页模块据此让数据源补到目标,补到后视口按键自然落位。
        /// 期间用户一拖就以拖动为准;目标早于最早数据时(到底)停在第一根。还没有数据时调用无效。
        /// </para>
        /// </summary>
        public void ScrollToKey(long key)
        {
            var board = CurrentBoardOrNull;
            if (board == null || board.IsDisposed) return;
            using (board.UpdateScope())
            {
                var extent = board.Read(Viewport.Extent);
                var active = board.Read(Viewport.ActiveRange);
                int length = board.Read(Viewport.LogicalLength);
                if (!extent.IsTracked || extent.Keys.Length != length || length <= 0 || !active.IsValid) return;

                _anchorKey = key;
                _anchorFrac = -active.Span / 2.0;
                _anchorPending = true;
                // 跳转覆盖之前没处理的拖动
                _lastProcessedUserRange = board.Read(Viewport.UserRange);
                Clamp(board);
            }
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
            double ppd = cell.Engine.PixelsPerDip;
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
            double cap = ComputePixelMaxSpan(ctx.GetPlotArea(), cell.Engine.PixelsPerDip, MinBarSpacingPx);
            if (cap == _pixelMaxSpan) return;
            _pixelMaxSpan = cap;
            ctx.Shared().PublishData(new ViewportSpanLimitsTrait(Math.Max(0, MinVisibleCount - 1), MaxSpanMultiplier, cap));

            var board = CurrentBoardOrNull;
            if (board == null || board.IsDisposed) return;
            Clamp(board);
        }

        /// <summary>
        /// 首屏视口:贴最新一根往前 DefaultVisibleCount 根(null = 全部)。不看 <see cref="Alignment"/>——
        /// 行情图打开时总要看到最新数据;双击复原也调这里,保证回到的就是打开时的样子。
        /// </summary>
        internal static RealRange InitialRange(double? defaultVisibleCount, double minSpan, double maxIndex)
        {
            double initialSpan = defaultVisibleCount.HasValue
                ? Math.Max(minSpan, Math.Min(defaultVisibleCount.Value - 1, maxIndex))
                : Math.Max(minSpan, maxIndex);
            return new RealRange(maxIndex - initialSpan, maxIndex);
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
                    ClearAnchor();
                    using (board.WriteScope())
                    {
                        board.Write(Viewport.ActiveRange, default);
                        board.Write(Viewport.AnchorKey, null);
                    }
                    return;
                }

                double maxIndex = currentLength - 1;
                double minSpan = Math.Max(0, MinVisibleCount - 1);
                RealRange targetRange;

                var extent = board.Read(Viewport.Extent);
                var keys = extent.Keys.Span;
                bool keyed = extent.IsTracked && keys.Length == currentLength;

                if (_lastLogicalLength <= 0)
                {
                    targetRange = InitialRange(DefaultVisibleCount, minSpan, maxIndex);
                    // 按键跟踪时 UserRange 要经上次的键换算,上一个上下文留下的值不能当成新意图
                    if (keyed) _lastProcessedUserRange = userRange;
                }
                else if (keyed && _anchorKey.HasValue && _lastKeys.Length > 0 && activeRange.IsValid)
                {
                    targetRange = KeyedTarget(keys, userRange, activeRange, extent.OlderExhausted, maxIndex);
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

                if (keyed) RecordAnchor(extent.Keys, min);
                else ClearAnchor();

                // 💥 绝对单向流：管家只负责输出现实，不再管意图！打破一切死锁。
                using (board.WriteScope())
                {
                    board.Write(Viewport.ActiveRange, new RealRange(min, max));
                    board.Write(Viewport.AnchorKey, _anchorKey);
                }
            }
        }

        // 按键找回:锚点键在当前键里的位置 + 小数偏移。用户意图先经上次的键换成锚点(UserRange 是按上次的下标写的)。
        // 贴右且末尾来了新的键 → 跟随最新(缓存始终含最新,右侧只有追加)。
        private RealRange KeyedTarget(ReadOnlySpan<long> keys, RealRange userRange, RealRange activeRange, bool olderExhausted, double maxIndex)
        {
            var lastKeys = _lastKeys.Span;
            RealRange basis = activeRange;
            if (userRange.IsValid && !userRange.Equals(_lastProcessedUserRange))
            {
                _lastProcessedUserRange = userRange;
                basis = userRange;
                _anchorPending = false;
                int f = (int)Math.Clamp(Math.Floor(userRange.Min), 0, lastKeys.Length - 1);
                _anchorKey = lastKeys[f];
                _anchorFrac = userRange.Min - f;
            }

            double lastMaxIndex = lastKeys.Length - 1;
            bool appended = keys[keys.Length - 1] > lastKeys[lastKeys.Length - 1];
            if (!_anchorPending && appended && basis.Max >= lastMaxIndex - RightEdgeStickTolerance)
            {
                double newMax = maxIndex + (basis.Max - lastMaxIndex);
                return new RealRange(newMax - basis.Span, newMax);
            }

            long anchor = _anchorKey!.Value;
            int i = LowerBound(keys, anchor);
            // 目标已加载(或更早的已经没有了):落位完成,之后按普通锚点跟踪。到底了还早于首根 → 停在第一根,不留白
            if (_anchorPending && (keys[0] <= anchor || olderExhausted))
            {
                if (keys[0] > anchor) _anchorFrac = Math.Max(_anchorFrac, 0);
                _anchorPending = false;
            }
            return new RealRange(i + _anchorFrac, i + _anchorFrac + basis.Span);
        }

        private void RecordAnchor(ReadOnlyMemory<long> keys, double min)
        {
            if (!_anchorPending)
            {
                var span = keys.Span;
                int f = (int)Math.Clamp(Math.Floor(min), 0, span.Length - 1);
                _anchorKey = span[f];
                _anchorFrac = min - f;
            }
            _lastKeys = keys;
        }

        private void ClearAnchor()
        {
            _anchorKey = null;
            _anchorPending = false;
            _lastKeys = default;
        }

        // 第一个 >= key 的下标;全都更小时取末根(跳到比最新还晚的键 = 看最新)
        internal static int LowerBound(ReadOnlySpan<long> keys, long key)
        {
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (keys[mid] < key) lo = mid + 1;
                else hi = mid;
            }
            return Math.Min(lo, keys.Length - 1);
        }

        public void ResetState()
        {
            _lastLogicalLength = -1;
            _lastProcessedUserRange = new RealRange(double.NaN, double.NaN);
            ClearAnchor();
        }

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