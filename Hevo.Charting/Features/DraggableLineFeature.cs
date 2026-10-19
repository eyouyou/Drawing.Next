using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary><see cref="DraggableLineFeature"/> 的配置。</summary>
    public sealed record DraggableLineOptions
    {
        /// <summary>松手时调一次(锁外、同一 UI 线程,业务在里面发请求 / 弹框都不卡黑板)。Esc / 丢捕获 / 被拔掉时回滚,不调。</summary>
        public Action<double>? OnCommitted { get; init; }

        /// <summary>
        /// true(默认)= 拖动中实时写 PricePort(界面别处跟着变);false = 拖动中只动草稿,松手才写。
        /// PricePort 参与自动量程、或者下单界面不想被拖动过程刷新时用 false。
        /// </summary>
        public bool LiveWrite { get; init; } = true;

        /// <summary>命中容差(DIP)。</summary>
        public float Tolerance { get; init; } = 4;

        /// <summary>常态样式;null = 橙色 1px。</summary>
        public LineStyle? Style { get; init; }

        /// <summary>悬停 / 拖动中的样式;null = 橙色 2px。</summary>
        public LineStyle? HotStyle { get; init; }

        /// <summary>右侧价格标签的格式;null = 不画标签。</summary>
        public string? LabelFormat { get; init; } = "F2";

        /// <summary>true = 只画不能拖(不发布区域)。</summary>
        public bool ReadOnly { get; init; }
    }

    /// <summary>
    /// 可拖水平线:委托价、止损、预警线这类"一条横线,能拖,松手提交"。价格存在业务给的 <see cref="PricePort"/>。
    /// 悬停变上下箭头、线加粗;按住拖动改价;松手调 <see cref="DraggableLineOptions.OnCommitted"/>;
    /// Esc / 丢失捕获 / 被拔掉时回滚到按下前的价格。装配用 <c>i.AddDraggableLine(...)</c>。
    /// </summary>
    public sealed class DraggableLineFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>价格(业务端口)。</summary>
        public DataPort<double> PricePort { get; init; } = null!;

        /// <summary>线所在 Y 轴的量程(同序列 Feature 的 YRangePort)。</summary>
        public DataPort<RealRange> ValueRangePort { get; init; } = null!;

        public DraggableLineOptions Options { get; init; } = new DraggableLineOptions();

        // 私有状态:悬停高亮、拖动草稿(LiveWrite = false 时用;null = 不在拖)
        private readonly DataPort<bool> _hotPort = new("DraggableLine_Hot");
        private readonly DataPort<double?> _draftPort = new("DraggableLine_Draft");
        private readonly DraggableLineLayer _layer = new();

        // OnProject 存下、Watch 回调里用:上一帧的投影和画出来的价格
        private ChartProjection _projection;
        private double _drawnPrice = double.NaN;
        private double _startPrice, _grabOffset, _dragPrice;
        // 拖动中最后的指针 Y:拖着时量程变了(滚轮缩放、自动量程),线按新投影仍画在指针下,松手按它提交
        private float _dragPointerY;
        private bool _dragging;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
            flow.WatchPointer(this, OnGesture);
        }

        // 在路由器分发的写锁里执行:只写端口;业务回调放 later(...)
        private void OnGesture(DataBlackboard board, PointerGesture g, Action<Action> later)
        {
            switch (g.Phase)
            {
                case PointerPhase.HoverEnter:
                    board.Write(_hotPort, true);
                    break;
                case PointerPhase.HoverLeave:
                    if (!_dragging) board.Write(_hotPort, false);
                    break;
                case PointerPhase.Press:
                    // 按用户看到的位置抓线:抓点跟线的偏移在拖动中保持不变,线不会跳到指针下
                    _startPrice = _dragPrice = _drawnPrice;
                    _grabOffset = _projection.IsValid && !double.IsNaN(_drawnPrice) ? g.PressPosition.Y - _projection.ValueToScreenY(_drawnPrice) : 0;
                    break;
                case PointerPhase.Begin:
                case PointerPhase.Move:
                    // 用最新投影:拖动中 Y 量程自己在变时,线也始终在指针下
                    if (!_projection.IsValid || double.IsNaN(_startPrice)) break;
                    _dragging = true;
                    _dragPointerY = g.Position.Y;
                    _dragPrice = _projection.ScreenToValue(g.Position.Y - _grabOffset);
                    using (board.WriteScope())
                    {
                        board.Write(_hotPort, true);
                        if (Options.LiveWrite) board.Write(PricePort, _dragPrice);
                        else board.Write(_draftPort, _dragPrice);
                    }
                    break;
                case PointerPhase.End:
                {
                    if (!_dragging) break;
                    _dragging = false;
                    // 按最新投影换算松手点:最后一次移动之后量程可能又变了(拖着线滚轮)
                    double committed = _projection.IsValid ? _projection.ScreenToValue(g.Position.Y - _grabOffset) : _dragPrice;
                    using (board.WriteScope())
                    {
                        board.Write(PricePort, committed);
                        board.Write(_draftPort, null);
                    }
                    var onCommitted = Options.OnCommitted;
                    if (onCommitted != null) later(() => onCommitted(committed));
                    break;
                }
                case PointerPhase.Cancel:
                    if (!_dragging) break;
                    _dragging = false;
                    using (board.WriteScope())
                    {
                        if (Options.LiveWrite) board.Write(PricePort, _startPrice);
                        board.Write(_draftPort, null);
                        board.Write(_hotPort, false);
                    }
                    break;
            }
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (proj, pc) = ctx.UseProjection(ValueRangePort);
            var (price, prc) = ctx.UsePort(PricePort);
            var (draft, dc) = ctx.UsePort(_draftPort);
            var (hot, hc) = ctx.UsePort(_hotPort);
            // 拖动中按本帧投影从指针位置换算:量程变了而指针没动时,端口里的价格要等下一次移动才更新,线不能跟着跑开
            double shown = _dragging && proj.IsValid ? proj.ScreenToValue(_dragPointerY - _grabOffset) : draft ?? price;
            _projection = proj;
            _drawnPrice = shown;
            if (!pc && !prc && !dc && !hc) return;

            var plot = proj.PlotArea;
            float y = proj.IsValid && !double.IsNaN(shown) ? proj.ValueToScreenY(shown) : float.NaN;
            if (float.IsNaN(y) || y < plot.Top || y > plot.Bottom)
            {
                ctx.For(_layer).PublishData(DraggableLineTrait.Hidden);
                ctx.Pointer().Clear();
                return;
            }

            string? label = Options.LabelFormat != null ? shown.ToString(Options.LabelFormat) : null;
            ctx.For(_layer).PublishData(new DraggableLineTrait(true, plot.Left, plot.Right, y, hot,
                Options.Style ?? DraggableLineTrait.DefaultStyle, Options.HotStyle ?? DraggableLineTrait.DefaultHotStyle, label));

            if (Options.ReadOnly) ctx.Pointer().Clear();
            else ctx.Pointer().Publish(new PointerRegion
            {
                Shape = PointerShape.HLine(y, plot.Left, plot.Right),
                Accepts = PointerAccepts.Drag,
                Cursor = PointerCursor.SizeNS,
                Tolerance = Options.Tolerance,
            });
        }

        protected override void OnDecompose()
        {
            _dragging = false;
            _drawnPrice = double.NaN;
            _projection = default;
            base.OnDecompose();
        }
    }
}
