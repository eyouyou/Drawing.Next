using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 指针路由器。由 <see cref="ChartReactiveSchema"/> 强制挂上(同 <see cref="Features.ViewportPortsFeature"/>):
    /// 每个图表 schema 一份,业务不用 Add,也拔不掉;端口注入到每个 <see cref="ChartFeature.Pointer"/>。
    /// <para>
    /// 没有任何区域时它什么都不做:不标 Handled、不抓鼠标、不设光标、不写手势端口,图表自带的平移 / 框选 / 双击复原跟原来一样。
    /// 有区域时按"点中了谁"裁决(优先级 → 图层 → 距离),把这一下的整个过程写进 <see cref="PointerPorts.GesturePort"/>。
    /// 见 04 号文档「指针区域」一节。
    /// </para>
    /// </summary>
    public sealed class PointerRouterFeature : Feature
    {
        // 跟视口端口同一阶段、更早装配:它的 Preview 键盘处理器要先于 ChartInteractionFeature 的(Esc 先给手势)
        public override FeaturePhase Phase => FeaturePhase.PreLayout;
        public override bool IsSingleton => true;

        /// <summary>拖动阈值(DIP),全局。按下后移动不到这个距离松开算点击。默认 4(WPF 默认拖动距离)。</summary>
        public static float DragThreshold { get; set; } = 4f;

        /// <summary>
        /// 按下时如果有排队没跑的帧,先同步跑掉再命中(默认开),保证命中用的区域 = 状态最新的区域。
        /// 只在发布过区域的图表上生效。
        /// </summary>
        public static bool FlushPendingFrameOnPress { get; set; } = true;

        private readonly PointerPorts _ports = new();
        private PointerRouter? _router;
        private WpfPointerAdapter? _adapter;
        private ChartCell? _cell;

        public PointerPorts Ports => _ports;

        protected override void OnAttached()
        {
            Chart.PointerRegions.Ports = _ports;
        }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            var store = chart.PointerRegions;
            store.Ports = _ports;
            _cell = chart;
            _router = new PointerRouter(store, _ports);
            store.Listener = _router;
            _adapter = new WpfPointerAdapter(chart, _router, () => CurrentBoardOrNull(), () => ctx.GetPlotArea());
            RegisterDisposable(_adapter);
        }

        // 帧外(输入事件)取当前黑板;卸载后为 null,路由器据此不分发
        private DataBlackboard? CurrentBoardOrNull() => _cell != null ? CurrentBoard : null;

        protected override void OnProject(FeatureContext ctx) { }

        protected override void OnSuspend() => _router?.OnSuspend();

        protected override void OnDecompose()
        {
            var cell = _cell;
            if (cell != null)
            {
                var store = cell.PointerRegions;
                if (ReferenceEquals(store.Listener, _router)) store.Listener = null;
                if (ReferenceEquals(store.Ports, _ports)) store.Ports = null;
            }
            _router = null;
            _adapter = null;
            _cell = null;
        }

        /// <summary>测试与诊断用:当前是否有手势进行中。</summary>
        internal bool IsGestureActive => _router?.IsActive ?? false;
    }
}
