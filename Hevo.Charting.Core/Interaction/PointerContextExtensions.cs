using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// <c>ctx.Pointer()</c> 拿到的发布口。只能在 <c>OnProject</c> 帧路径里用(同 <c>UsePort</c>)。
    /// 本帧第一次 Publish 换掉本 Feature 的整张区域表,同一帧再 Publish 往里追加;没发布的帧保持上次的表。
    /// </summary>
    public readonly struct PointerPublisher
    {
        private readonly FeatureContext _ctx;
        internal PointerPublisher(FeatureContext ctx) => _ctx = ctx;

        public void Publish(PointerRegion region)
        {
            _ctx.AssertProjecting("Pointer().Publish");
            Store(out var owner).Publish(owner, _ctx._projectFrame, region);
        }

        /// <summary>多个区域。集合按引用读进框架,发布后不要原地改(画线文档变了 new 一份再发)。</summary>
        public void Publish(IReadOnlyList<PointerRegion> regions)
        {
            _ctx.AssertProjecting("Pointer().Publish");
            Store(out var owner).Publish(owner, _ctx._projectFrame, regions);
        }

        /// <summary>撤掉本 Feature 的全部区域(下一次提交后生效)。</summary>
        public void Clear()
        {
            _ctx.AssertProjecting("Pointer().Clear");
            Store(out var owner).Clear(owner, _ctx._projectFrame);
        }

        private PointerRegionStore Store(out Feature owner)
        {
            owner = _ctx._currentFeature ?? throw new InvalidOperationException("ctx.Pointer() 只能在 OnProject 里用。");
            return owner.Chart.Engine.PointerRegions;
        }
    }

    /// <summary>
    /// 一帧的屏幕 ↔ 值投影快照:(绘图区, 可见索引范围, Y 量程, 比例尺)。值类型,在 <c>OnProject</c> 里拿到后存成字段,
    /// Watch 回调里用它换算指针位置。换算只走比例尺的 Normalize / Denormalize(<see cref="CoordinateExtensions"/> 同一口径)。
    /// </summary>
    public readonly struct ChartProjection
    {
        public ChartProjection(HevoRect plotArea, RealRange domainRange, RealRange valueRange, ScaleStrategyTrait? scale)
        {
            PlotArea = plotArea; DomainRange = domainRange; ValueRange = valueRange; Scale = scale;
        }

        public HevoRect PlotArea { get; }
        /// <summary>可见索引范围(ActiveRange)。</summary>
        public RealRange DomainRange { get; }
        /// <summary>传进来的那根 Y 轴的量程。</summary>
        public RealRange ValueRange { get; }
        public ScaleStrategyTrait? Scale { get; }

        /// <summary>绘图区、两个量程、比例尺都就位。不成立时换算结果没有意义。</summary>
        public bool IsValid => Scale != null && !PlotArea.IsEmpty && DomainRange.IsValid && ValueRange.IsValid;

        /// <summary>屏幕 X(DrawingCanvas)→ 自变量(K 线索引,连续值)。</summary>
        public double ScreenToDomain(double x)
            => Scale!.DomainScale.Denormalize((x - PlotArea.Left) / PlotArea.Width, DomainRange);

        /// <summary>屏幕 Y(DrawingCanvas)→ 因变量(价格)。</summary>
        public double ScreenToValue(double y)
            => Scale!.ValueScale.Denormalize(1.0 - (y - PlotArea.Top) / PlotArea.Height, ValueRange);

        public float DomainToScreenX(double domain) => CoordinateExtensions.ProjectDomainToScreen(PlotArea, DomainRange, Scale!, domain);

        public float ValueToScreenY(double value) => CoordinateExtensions.ProjectValueToScreen(PlotArea, ValueRange, Scale!, value);

        public HevoPoint ToScreen(double domain, double value)
            => CoordinateExtensions.ProjectToScreen(PlotArea, DomainRange, ValueRange, Scale!, domain, value);
    }

    /// <summary>手势回调。<paramref name="later"/>(action) 把 action 放到黑板锁外、本次分发结束后、同一 UI 线程执行。</summary>
    public delegate void PointerGestureHandler(DataBlackboard board, PointerGesture gesture, Action<Action> later);

    public static class PointerContextExtensions
    {
        /// <summary>本 Feature 的指针区域发布口(<c>OnProject</c> 里用)。</summary>
        public static PointerPublisher Pointer(this FeatureContext ctx) => new PointerPublisher(ctx);

        /// <summary>
        /// 屏幕 ↔ 值投影。内部 <c>UsePort(Viewport.ActiveRange)</c> + <c>UsePort(valueRangePort)</c> +
        /// <c>Shared().Use&lt;ScaleStrategyTrait&gt;()</c> + <c>Shared().Use&lt;PlotAreaTrait&gt;()</c>,任一变了 IsChanged = true。
        /// 传 Y 量程是因为一张图可以有多根 Y 轴,框架不知道你画在哪根上。
        /// 同一个 Feature 里别再自己 <c>Shared().Use</c> 这两个 Trait(Use 的"上次读到的"按 Feature 存,读两次只有第一次报变化)。
        /// </summary>
        public static (ChartProjection Projection, bool IsChanged) UseProjection(this FeatureContext ctx, DataPort<RealRange> valueRangePort)
        {
            ctx.AssertProjecting(nameof(UseProjection));
            if (ctx._currentFeature is not ChartFeature feature)
                throw new InvalidOperationException($"UseProjection 只能在 ChartFeature 的 OnProject 里用(要它的 Viewport)。");
            var (domain, dc) = ctx.UsePort(feature.Viewport.ActiveRange);
            var (value, vc) = ctx.UsePort(valueRangePort);
            var (scale, sc) = ctx.Shared().Use<ScaleStrategyTrait>();
            var (_, pc) = ctx.Shared().Use<PlotAreaTrait>();
            return (new ChartProjection(ctx.PlotArea, domain, value, scale), dc || vc || sc || pc);
        }

        /// <summary>
        /// 监听本 Feature 的手势:只在 <see cref="PointerGesture.IsFor"/> 是自己时调 <paramref name="handler"/>。
        /// 回调在路由器写手势端口的写锁里同步执行(不慢一帧):只读手势、写端口;调业务、发请求、弹窗一律放 <c>later(...)</c>。
        /// </summary>
        public static IRenderFlow<DataBlackboard> WatchPointer(this IRenderFlow<DataBlackboard> flow, ChartFeature owner, PointerGestureHandler handler)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var port = owner.Pointer.GesturePort;
            var later = new LaterQueue();
            Action<Action> post = later.Post;
            return flow.Watch(new object[] { port }, board =>
            {
                PointerGesture g;
                using (board.ReadScope()) g = board.Read(port);
                if (!g.IsFor(owner)) return;
                later.Board = board;
                try { handler(board, g, post); }
                finally { later.Board = null; }
            });
        }

        // later(...) 的实现:登记到黑板的锁外队列(internal,不公开到黑板上)。只在分发的写锁内有效。
        private sealed class LaterQueue
        {
            public DataBlackboard? Board;

            public void Post(Action action)
            {
                if (action == null) throw new ArgumentNullException(nameof(action));
                var board = Board ?? throw new InvalidOperationException("later(...) 只能在 WatchPointer 回调执行期间调用。");
                board.PostAfterUnlock(new DelegateAfterUnlock(action));
            }
        }

        private sealed class DelegateAfterUnlock : IAfterUnlockAction
        {
            private readonly Action _action;
            public DelegateAfterUnlock(Action action) => _action = action;
            public void Run(DataBlackboard board) => _action();
        }
    }
}
