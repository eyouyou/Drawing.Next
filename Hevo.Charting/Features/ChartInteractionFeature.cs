using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Hevo.Charting.Interaction;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 缩放配置参数
    /// </summary>
    public record ZoomOptions
    {
        /// <summary> 缩放灵敏度 (默认 0.15 即每次滚轮缩放 15% 的视口) </summary>
        public double Sensitivity { get; init; } = 0.15;
    }

    /// <summary>
    /// 惯性 Pan 配置 (kinetic scrolling).
    /// 抬手时基于最近一段窗口的平均速度启动衰减动画,直至速度低于阈值或被新事件中断。
    /// </summary>
    public record InertiaPanOptions
    {
        /// <summary>每帧速度衰减系数 (∈ (0,1))。0.92 ≈ 半秒衰减到 ~10%。</summary>
        public double Friction { get; init; } = 0.92;

        /// <summary>低于此速度即停止 (像素 / 秒)。</summary>
        public double MinVelocityPxPerSec { get; init; } = 30.0;

        /// <summary>抬手前用于估算初速度的滑动窗口长度 (毫秒)。</summary>
        public double VelocityWindowMs { get; init; } = 60.0;
    }

    /// <summary>
    /// 双击复原配置。
    /// 默认重置目标 = ViewportManager 的首屏视口 (DefaultVisibleCount + Alignment),
    /// 业务可通过 <see cref="CustomReset"/> 注入自己的"原点"。
    /// </summary>
    public record DoubleClickResetOptions
    {
        /// <summary>两次按下的最大时间间隔。</summary>
        public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMilliseconds(400);

        /// <summary>两次按下的最大像素位移 (超出则视为拖拽,不算双击)。</summary>
        public double MaxMovementPx { get; init; } = 6.0;

        /// <summary>自定义重置回调。入参 = LogicalLength,出参 = 复原后的视口。null = 走框架默认。</summary>
        public Func<int, RealRange>? CustomReset { get; init; }
    }

    // ==========================================
    // 💥 交互状态载荷 (彻底更新为 0-GC 的值类型及纯 float 坐标 HevoPoint)
    // ==========================================

    /// <summary> X 轴逻辑域碰撞结果 </summary>
    public record struct DomainHitResult(int LogicalIndex, double SnapX, double CenterRelativeX);

    /// <summary> 全局指针 (鼠标/十字光标) 命中状态 </summary>
    public record struct PointerHitState(HevoPoint MousePos, DomainHitResult Hit, double CenterX, int LocalIndex, bool IsOutOfBounds);

    /// <summary> 指针越界策略 </summary>
    public enum PointerOutOfBoundsStrategy
    {
        /// <summary> 自由模式：超出数据区依然显示光标 </summary>
        Free,
        /// <summary> 强吸附模式：超出数据区时，光标强制吸附在最后一根或第一根有效 K 线上 </summary>
        SnapToValidData
    }

    /// <summary> 图表支持的交互模式标记 (支持位运算组合) </summary>
    /// <remarks>
    /// 触屏 (WPF Manipulation 通道) 默认无条件挂载;捏合的 scale 段受 <see cref="Zoom"/> flag 控,
    /// 平移段受 <see cref="Pan"/> flag 控,与鼠标行为对称。
    /// </remarks>
    [Flags]
    public enum ChartInteractionMode
    {
        None = 0,
        Hover = 1 << 0,
        Pan = 1 << 1,
        Zoom = 1 << 2,
        Keyboard = 1 << 3,
        /// <summary>框选缩放 (默认 Shift+Drag,可配)。配套 <see cref="BoxZoomOptions"/>。</summary>
        BoxZoom = 1 << 4,
        /// <summary>抬手后惯性平移。叠加于 <see cref="Pan"/> 之上;Pan 关闭则惯性自然失效。配套 <see cref="InertiaPanOptions"/>。</summary>
        InertiaPan = 1 << 5,
        /// <summary>双击复原视口。配套 <see cref="DoubleClickResetOptions"/>。</summary>
        DoubleClickReset = 1 << 6,
        /// <summary>↑ 放大 / ↓ 缩小(跟一格滚轮同幅度)。不在 <see cref="All"/> / <see cref="Default"/> 里:默认不抢上下键,要用显式加上。</summary>
        KeyboardZoom = 1 << 7,
        All = Hover | Pan | Zoom | Keyboard | BoxZoom | InertiaPan | DoubleClickReset,
        TimeShareDefault = Hover,
        Default = Hover | Keyboard
    }

    /// <summary>
    /// 💥 图表交互总枢纽大脑
    /// 职责:按 <see cref="SupportedModes"/> 往本图表的行为链上装内置交互(平移、框选、双击复原、滚轮、键盘、惯性、触屏、十字光标)。
    /// 输入由指针路由器统一分发:没被指针区域拿走的才到行为链(见 04 号文档「指针区域」一节)。本 Feature 不订平台事件。
    /// 分页/无缝加载已拆为独立的 <see cref="DataPagingFeature"/>，本 Feature 只关心手势翻译。
    /// <para>
    /// <b>典型用法</b>:
    /// <code>
    /// var hitPort = new DataPort&lt;PointerHitState?&gt;("Hit");
    /// interactions.Add(new ChartInteractionFeature
    /// {
    ///     PointerHitPort  = hitPort,
    ///     SupportedModes  = ChartInteractionMode.All,
    ///     PointerSnapMode = PointerOutOfBoundsStrategy.SnapToValidData,
    ///     ZoomStrategy    = new SmartAdaptiveZoomStrategy(),
    /// });
    /// // 把 hitPort 传给 Crosshair / Tooltip / Header 即可全联动
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:输入(鼠标/触屏/键盘) → 路由器 → 行为链 → 写 <see cref="Viewport.UserRange"/> / <see cref="PointerHitPort"/> → ViewportManager 钳制成 ActiveRange → 全图 OnProject 重算。
    /// </para>
    /// </summary>
    public class ChartInteractionFeature : ChartFeature
    {
        // 铁律：交互运算必须在布局之前完成，确保后续的图层拿到的是最新视口
        public override FeaturePhase Phase => FeaturePhase.PreLayout;

        // --- 核心引脚（Viewport 由 ChartFeature 基类自动注入）---
        /// <summary> 暴露给外部层(如 Tooltip, Crosshair) 订阅的光标全局状态 </summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<PointerHitState?> PointerHitPort { get; init; } = new("GlobalPointerHit");

        /// <summary>有效数据计数端口(可选)。业务侧若部分数据是 NaN 占位,可写入真实可命中根数,影响键盘导航与吸附边界。null 时回退到 LogicalLength。</summary>
        public DataPort<int>? ValidDataCountPort { get; init; }

        /// <summary>
        /// 数据可用性端口：由 DSL 注入，与 <see cref="DataPagingFeature.AvailabilityPort"/> 共用同一实例。
        /// 仅 HandleZoom 在锁内 Read 一次喂给 ZoomContext。未挂载分页 Feature 时为 null。
        /// </summary>
        public DataPort<DataAvailability>? AvailabilityPort { get; init; }

        // --- 行为配置 ---
        /// <summary>启用的交互通道(位运算组合)。默认 All;TimeShareDefault 仅 hover;Default 仅 hover+键盘。</summary>
        public ChartInteractionMode SupportedModes { get; init; } = ChartInteractionMode.All;

        /// <summary>光标越界策略。Free = 越界自由;SnapToValidData = 强制吸附到首/末根有效数据。</summary>
        public PointerOutOfBoundsStrategy PointerSnapMode { get; init; } = PointerOutOfBoundsStrategy.Free;

        /// <summary>滚轮缩放算法实现。SmartAdaptive 默认会根据 hit 锚点 / 撞墙状态动态切策略。
        /// <para>默认用 <see cref="SmartAdaptiveZoomStrategy.Instance"/> 单例,这样蓝图编辑器的
        /// 接口下拉(NodeEditorWindow.MakeInterfaceInstancePicker)按 ReferenceEquals 找选中项能命中。</para></summary>
        public IZoomStrategy ZoomStrategy { get; init; } = SmartAdaptiveZoomStrategy.Instance;

        /// <summary>滚轮缩放参数(灵敏度等)。</summary>
        public ZoomOptions ZoomConfig { get; init; } = new();

        /// <summary>框选缩放参数(激活键 / 最小像素 / 选框样式)。需 SupportedModes 启用 BoxZoom。</summary>
        public BoxZoomOptions BoxZoomConfig { get; init; } = new();

        /// <summary>惯性 Pan 参数(摩擦/阈值/采样窗口)。需 SupportedModes 启用 InertiaPan。</summary>
        public InertiaPanOptions InertiaPanConfig { get; init; } = new();

        /// <summary>双击复原参数(时间间隔 / 像素位移 / 自定义重置回调)。需 SupportedModes 启用 DoubleClickReset。</summary>
        public DoubleClickResetOptions DoubleClickResetConfig { get; init; } = new();

        /// <summary>plotArea 边缘命中容差(像素)。鼠标越界但在容差内仍判定为有效命中,防止贴边光标闪烁。</summary>
        public double EdgeHitTolerance { get; init; } = 1.0;

        /// <summary>
        /// true = hover 通道默认未武装,需要用户在 chart 内单击一次后才会写 PointerHitPort。
        /// 影响所有读 hit 的下游 (Crosshair / Tooltip / 业务自定义)。
        /// 用于"chart 与其他 widget 共栖、不希望鼠标飘过就闪十字光标"的场景。
        /// </summary>
        public bool RequireClickToActivate { get; init; } = false;

        /// <summary>
        /// true = 按下 Esc 立即解除 hover 武装并清空 PointerHitPort,直至下次单击重新武装。
        /// 与 <see cref="RequireClickToActivate"/> 独立可用 (即便默认武装,Esc 也能临时藏 hover)。
        /// </summary>
        public bool DismissOnEscape { get; init; } = false;

        // 框选矩形:行为写端口,本 Feature 在 OnProject 里搬给图层(Interaction 层级,与 CrosshairLayer 共栖)
        private readonly BoxZoomLayer _boxLayer = new();
        private readonly DataPort<BoxZoomTrait> _boxStatePort = new("BoxZoomState");

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_boxLayer);

            // 按 SupportedModes 装行为,顺序即优先级(同原来按下时的分流顺序:双击复原 → 框选 → 平移)。
            // 输入由路由器交给行为链,本 Feature 不订任何平台事件。
            var state = new ChartInteractionState(this, chart, ctx);
            var hover = new HoverBehavior(state);
            BoxZoomBehavior? box = SupportedModes.HasFlag(ChartInteractionMode.BoxZoom) ? new BoxZoomBehavior(state, _boxStatePort) : null;

            var behaviors = new List<IPointerBehavior> { new PointerStateBehavior(state) };
            if (SupportedModes.HasFlag(ChartInteractionMode.DoubleClickReset)) behaviors.Add(new DoubleClickResetBehavior(state));
            if (box != null) behaviors.Add(box);
            if (SupportedModes.HasFlag(ChartInteractionMode.Pan)) behaviors.Add(new PanBehavior(state));
            behaviors.Add(hover);
            if (SupportedModes.HasFlag(ChartInteractionMode.Zoom)) behaviors.Add(new WheelZoomBehavior(state));
            if ((SupportedModes & (ChartInteractionMode.Keyboard | ChartInteractionMode.KeyboardZoom)) != 0) behaviors.Add(new KeyboardBehavior(state));
            behaviors.Add(new TouchBehavior(state));

            // 卸载时(Feature.Decompose 第一步)整组摘掉:停惯性、放掉平移 / 框选抓着的鼠标、清选框
            chart.PointerRegions.Behaviors.Add(this, behaviors.ToArray(), state,
                DoubleClickResetConfig.MaxInterval, DoubleClickResetConfig.MaxMovementPx,
                onRemoved: () =>
                {
                    state.Detach();
                    var board = CurrentBoardOrNull;
                    if (box != null && board != null && !board.IsDisposed) box.Clear(board);
                });

            // 视口变更后刷新十字光标位置(分页判定已交给 DataPagingFeature 独立 Watch)
            flow.Watch(new object[] { Viewport.ActiveRange }, hover.OnActiveRangeChanged);
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 框选 trait 透传:状态端口由框选行为写入,这里只搬运到图层。
            // 端口未写过(默认 null)时退回 Inactive,避免 layer 看到 null trait。
            var (state, _) = ctx.UsePort(_boxStatePort);
            ctx.For(_boxLayer).PublishData(state ?? BoxZoomTrait.Inactive);
        }
    }
}
