using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 行式 element-plot 一等 Feature 基类 —— 把 ScatterPlotFeature / ArrowMarkerFeature 重复的
    /// hover/双击订阅 + spec 缓存 + hit-test 调度收口。派生类填:
    /// <list type="bullet">
    ///   <item><see cref="CreateLayer"/>:实例化对应的 layer 类型</item>
    ///   <item><see cref="PublishSpecTrait"/>:OnProject 时把 ROM&lt;TSpec&gt; 包成 trait publish 给自己的 layer</item>
    ///   <item><see cref="ProjectToScreen"/>:把单个 spec 元素投到屏幕坐标</item>
    /// </list>
    /// 鼠标订阅 + ElementHovered/ElementDoubleClicked 事件 + <see cref="HitStatePort"/> 写入完全在基类。
    /// 事件 args 用 <see cref="PlotElementEvent{TSpec}"/> 强类型携带 spec 元素本身(struct 零分配),
    /// 框架不解释字段 — 业务侧用 Index 反查自家数据。
    ///
    /// <para>
    /// <b>跟 <see cref="TooltipWidgetFeature{TX}"/> 联动</b>:配置 <see cref="HitStatePort"/> 后,基类在 hover 命中时
    /// 写入 <see cref="PointerHitState"/>(<c>LocalIndex = element idx</c>, <c>MousePos = 命中屏幕点</c>),离开时写
    /// <c>IsOutOfBounds=true</c>。Tooltip 的 <c>HitStatePort</c> 接到这个端口即可显示 tooltip;layer 侧 publish 的
    /// <see cref="MetaTrait"/> + <see cref="DoubleSeriesDataTrait"/> 自动被 tooltip 抓取成行(field name + value)。
    /// </para>
    /// </summary>
    public abstract class ElementPlotFeatureBase<TSpec, TLayer> : ChartFeature
        where TSpec : struct
        where TLayer : ChartLayer
    {
        public override FeaturePhase Phase => FeaturePhase.Series;

        /// <summary>spec 输入端口 —— 上游 Feature 写,Feature OnProject 读 + publish 给 layer。</summary>
        public DataPort<ReadOnlyMemory<TSpec>> Spec { get; init; } = null!;

        /// <summary>
        /// 可选 hit state 输出端口 —— 配置后,hover 命中时基类写 <c>PointerHitState{LocalIndex=idx, MousePos=hit}</c>;
        /// 离开时写 <c>IsOutOfBounds=true</c> 的状态。<see cref="TooltipWidgetFeature{TX}"/> 可订阅这个端口。
        /// 不需要 tooltip 的场景留 null。
        /// </summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<PointerHitState?>? HitStatePort { get; init; }

        /// <summary>
        /// Feature identifier —— 业务侧反查实例用(<c>schema.ListFeatures().OfType&lt;ScatterPlotFeature&gt;().First(f =&gt; f.Name == "...")</c>)。
        /// </summary>
        public string Name { get; init; } = "";

        /// <summary>底层 layer 名(调试用)。</summary>
        public string LayerName { get; init; } = "";

        /// <summary>Hover 命中容差(像素²)。默认 16²。</summary>
        public double HoverHitToleranceSq { get; init; } = 16.0 * 16.0;

        /// <summary>Hover 命中事件。args 携带强类型 spec 元素 + Index,业务侧自管 hover UI(WPF popup 等)。</summary>
        public event EventHandler<PlotElementEvent<TSpec>>? ElementHovered;

        /// <summary>双击命中事件(第二次按下时,按平台的连击判定)。</summary>
        public event EventHandler<PlotElementEvent<TSpec>>? ElementDoubleClicked;

        /// <summary>
        /// true = 元素上的双击只归元素:发 <see cref="ElementDoubleClicked"/>,不再同时触发图表的双击复原(第二次按下被认领)。
        /// 默认 false,跟原来一样两者都触发。第一次按下始终不认领,元素上按住拖照常平移。
        /// </summary>
        public bool DoubleClickClaimsPress { get; init; }

        // ── runtime ─────────────────────────────────────────────
        private TLayer? _layer;
        private VisualProxy<TLayer> _proxy;

        private readonly object _specLock = new();
        private ReadOnlyMemory<TSpec> _cachedSpec;
        private int _lastHoverIdx = -1;   // 上一帧命中 idx,用于"hit→no-hit"边界写 out-of-bounds 状态

        // 指针:一块"现算命中"的区域(命中那一刻按当前投影找最近元素,跟原来在 MouseMove 里现算一致)
        private ElementHitTester? _tester;
        private PointerRegion? _region;

        protected TLayer? Layer => _layer;
        protected VisualProxy<TLayer> Proxy => _proxy;

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            if (Spec == null) return;

            _layer = CreateLayer();
            _proxy = AttachLayer(_layer);

            OnAfterAttachLayer(ctx);
            _tester = new ElementHitTester(this, ctx);
            _region = null;
            flow.WatchPointer(this, OnGesture);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            if (_layer == null) return;
            var (spec, _) = ctx.UsePort(Spec);
            lock (_specLock) _cachedSpec = spec;
            // ⚠️ 不能用 OnCompose 时缓存的 _proxy —— ChartCell.AddUnmanagedLayer 返回的 proxy 是
            // "直写 live bag" 模式(base=draft=同一 localBag),Publish 跳过 RenderContext._transactionMap
            // 的 transaction draft,SubmitSync 时不会标 layer dirty → layer.OnUpdate 不被调 → 不渲染。
            // ctx.For(_layer) 返回的才是 base=live + draft=transaction 的正确 proxy,Publish 走 transaction,
            // SubmitSync 合并 draft 时检测到 trait 变化 → MarkDirty → 下一帧 OnUpdate 被调。
            PublishSpecTrait(ctx.For(_layer), spec);

            // 区域只发一次:命中是现算的,数据 / 视口变了不用重发
            if (_region == null && _tester != null)
            {
                _region = new PointerRegion
                {
                    Shape = PointerShape.Custom(_tester),
                    Accepts = PointerAccepts.DoubleClick,
                    PassThrough = !DoubleClickClaimsPress,
                };
                ctx.Pointer().Publish(_region);
            }
        }

        protected override void OnDecompose()
        {
            // 同一实例拔了再插:区域表已随卸载清掉,重装后首帧重发
            _region = null;
            _tester = null;
            _lastHoverIdx = -1;
            base.OnDecompose();
        }

        // ── 派生类钩子 ────────────────────────────────────────────────────

        /// <summary>实例化具体 layer。</summary>
        protected abstract TLayer CreateLayer();

        /// <summary>把当前 spec 包成 trait publish 给 layer。</summary>
        protected abstract void PublishSpecTrait(VisualProxy<TLayer> proxy, ReadOnlyMemory<TSpec> spec);

        /// <summary>
        /// 把单个 spec 元素投到屏幕。返 null = 该元素被裁(出 plot area / NaN);非 null = 屏幕点。
        /// </summary>
        protected abstract HevoPoint? ProjectToScreen(RenderContext ctx, in TSpec elem);

        /// <summary>OnCompose layer attach 后调,派生类可在此 publish 一次性 layer-local trait(显式 axes 等)。</summary>
        protected virtual void OnAfterAttachLayer(RenderContext ctx) { }

        // ── 鼠标 / hit-test(完全通用)─────────────────────────────────────────

        // 悬停命中 → 发 ElementHovered + 写 HitStatePort(命中);离开 → 写 HitStatePort(IsOutOfBounds=true)。
        // 回调在路由器的写锁里:端口直接写,业务事件放 later(...) 到锁外。
        private void OnGesture(DataBlackboard board, PointerGesture g, Action<Action> later)
        {
            if (_layer == null) return;
            switch (g.Phase)
            {
                case PointerPhase.HoverEnter:
                case PointerPhase.HoverMove:
                {
                    int idx = g.RegionId;
                    var hit = _tester != null ? _tester.LastHit : g.Position;
                    var element = GetCachedElement(idx);
                    later(() => ElementHovered?.Invoke(this, new PlotElementEvent<TSpec>(idx, element)));
                    WriteHitState(board, new PointerHitState(
                        MousePos: hit,
                        Hit: default,
                        CenterX: hit.X,
                        LocalIndex: idx,
                        IsOutOfBounds: false));
                    _lastHoverIdx = idx;
                    break;
                }
                case PointerPhase.HoverLeave:
                    if (_lastHoverIdx < 0) break;
                    // 上次命中,这次 no-hit / 鼠标离开 chart → 下发"已离开" 状态让 tooltip 隐藏
                    WriteHitState(board, new PointerHitState(
                        MousePos: g.Position,
                        Hit: default,
                        CenterX: g.Position.X,
                        LocalIndex: -1,
                        IsOutOfBounds: true));
                    _lastHoverIdx = -1;
                    break;
                case PointerPhase.DoubleClick:
                {
                    int idx = g.RegionId;
                    var element = GetCachedElement(idx);
                    later(() => ElementDoubleClicked?.Invoke(this, new PlotElementEvent<TSpec>(idx, element)));
                    break;
                }
            }
        }

        private sealed class ElementHitTester : IPointerHitTester
        {
            private readonly ElementPlotFeatureBase<TSpec, TLayer> _owner;
            private readonly RenderContext _ctx;
            public HevoPoint LastHit;

            public ElementHitTester(ElementPlotFeatureBase<TSpec, TLayer> owner, RenderContext ctx) { _owner = owner; _ctx = ctx; }

            public float HitTest(HevoPoint p, out int id)
            {
                var (idx, hit, d2) = _owner.HitTest(_ctx, p);
                id = idx;
                if (idx < 0) return float.NaN;
                LastHit = hit;
                return (float)Math.Sqrt(d2);
            }
        }

        private void WriteHitState(DataBlackboard board, PointerHitState? state)
        {
            if (HitStatePort == null) return;
            using (board.WriteScope())
                board.Write(HitStatePort, state);
        }

        private (int Index, HevoPoint Hit, double DistSq) HitTest(RenderContext ctx, HevoPoint screen)
        {
            ReadOnlyMemory<TSpec> spec;
            lock (_specLock) spec = _cachedSpec;
            if (spec.Length == 0) return (-1, default, 0);

            var span = spec.Span;
            int bestIdx = -1;
            double bestDist = HoverHitToleranceSq;
            var bestHit = default(HevoPoint);
            for (int i = 0; i < span.Length; i++)
            {
                var p = ProjectToScreen(ctx, in span[i]);
                if (p == null) continue;
                double dx = p.Value.X - screen.X, dy = p.Value.Y - screen.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist) { bestDist = d2; bestIdx = i; bestHit = p.Value; }
            }
            return (bestIdx, bestHit, bestDist);
        }

        private TSpec GetCachedElement(int idx)
        {
            ReadOnlyMemory<TSpec> spec;
            lock (_specLock) spec = _cachedSpec;
            return (uint)idx < (uint)spec.Length ? spec.Span[idx] : default;
        }
    }
}
