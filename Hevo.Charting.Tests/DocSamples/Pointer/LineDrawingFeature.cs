using System;
using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Hevo.Charting.WorkFlow;
using System.Windows.Media;

// 04 号文档「指针区域」:业务画线工具的骨架。点两下画一条线,或按住拖画一条;Esc 退出工具。
namespace Hevo.Charting.DocSamples.Pointer
{
    /// <summary>线的一端:K 线索引 + 价格(跟着数据走,不是屏幕坐标)。</summary>
    public sealed record ChartAnchor(double Index, double Value);

    /// <summary>画好的一条线。</summary>
    public sealed record DrawnLine(ChartAnchor From, ChartAnchor To);

    /// <summary>画线图层的一帧:已投影好的线段端点(两两一对)与橡皮筋。</summary>
    public sealed record DrawingTrait(HevoPoint[] Segments, HevoPoint? RubberFrom, HevoPoint? RubberTo) : IVisualTrait;

    public partial class DrawingLayer : ChartLayer
    {
        private static readonly HevoPen LinePen = new HevoPen(new HevoSolidBrush(Color.FromRgb(0x40, 0x90, 0xF0)), 1.5);
        private static readonly HevoPen RubberPen = new HevoPen(new HevoSolidBrush(Color.FromRgb(0x40, 0x90, 0xF0)), 1.0, new double[] { 4, 3 });

        public DrawingLayer()
        {
            Name = "Drawing";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Selection;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<DrawingTrait>();
            if (t == null) return;
            for (int i = 0; i + 1 < t.Segments.Length; i += 2) draw.DrawLine(LinePen, t.Segments[i], t.Segments[i + 1]);
            if (t.RubberFrom is HevoPoint a && t.RubberTo is HevoPoint b) draw.DrawLine(RubberPen, a, b);
        }
    }

    /// <summary>
    /// 画线工具。只用两样东西:<c>OnProject</c> 里发布区域(选了工具 = 整块绘图区的模态区域;没选 = 每条线一个线段区域),
    /// <c>OnCompose</c> 里 <c>WatchPointer</c> 接手势。没有捕获、Esc、光标、平移冲突的代码 —— 都归路由器。
    /// </summary>
    public sealed class LineDrawingFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>线所在 Y 轴的量程。</summary>
        public DataPort<RealRange> ValueRangePort { get; init; } = null!;

        /// <summary>工具是否选中(业务工具栏写 true;画完一条 / Esc 时本 Feature 写回 false)。</summary>
        public DataPort<bool> ArmedPort { get; init; } = new DataPort<bool>("Drawing_Armed");

        /// <summary>画好的线。每次 new 一份新列表再写,不原地改已发布的。</summary>
        public DataPort<IReadOnlyList<DrawnLine>?> LinesPort { get; init; } = new DataPort<IReadOnlyList<DrawnLine>?>("Drawing_Lines");

        // 私有状态:半截线的起点和橡皮筋终点
        private readonly DataPort<ChartAnchor?> _pendingPort = new DataPort<ChartAnchor?>("Drawing_Pending");
        private readonly DataPort<ChartAnchor?> _rubberPort = new DataPort<ChartAnchor?>("Drawing_Rubber");
        private readonly DrawingLayer _layer = new DrawingLayer();
        private ChartProjection _projection;

        private static readonly PointerRegion ToolRegion = new PointerRegion
        {
            Shape = PointerShape.WholePlot(),
            Accepts = PointerAccepts.Click | PointerAccepts.Drag,   // 点两下画,或按住拖画
            Priority = PointerPriority.Modal,                       // 压过价格线等一切区域,不靠装配顺序
            Modal = true,                                           // 没有手势进行中时 Esc 发给它
            Cursor = PointerCursor.Cross,
        };

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
            flow.WatchPointer(this, OnGesture);
        }

        // 路由器写锁里同步执行:只写端口。几个端口一起写用事务,订阅者只收到一次通知
        private void OnGesture(DataBlackboard board, PointerGesture g, Action<Action> later)
        {
            if (!_projection.IsValid) return;
            switch (g.Phase)
            {
                case PointerPhase.Click:
                    if (board.Read(_pendingPort) == null) SetPending(board, Anchor(g.Position));
                    else Commit(board, Anchor(g.Position));
                    break;
                case PointerPhase.Begin:                         // 按住拖:按下点是起点
                    SetPending(board, Anchor(g.PressPosition));
                    break;
                case PointerPhase.Move:
                case PointerPhase.HoverMove:                     // 两次单击之间指针是抬起的:悬停也拉橡皮筋
                    if (board.Read(_pendingPort) != null) board.WriteIfChanged(_rubberPort, Anchor(g.Position));
                    break;
                case PointerPhase.End:
                    Commit(board, Anchor(g.Position));
                    break;
                case PointerPhase.Cancel:
                case PointerPhase.Escape:                        // 退出工具,丢掉半截线
                    using (board.BeginTransaction())
                    {
                        board.WriteIfChanged(_pendingPort, null);
                        board.WriteIfChanged(_rubberPort, null);
                        board.WriteIfChanged(ArmedPort, false);
                    }
                    break;
            }
        }

        private ChartAnchor Anchor(HevoPoint p) => new ChartAnchor(_projection.ScreenToDomain(p.X), _projection.ScreenToValue(p.Y));

        private void SetPending(DataBlackboard board, ChartAnchor a)
        {
            using (board.BeginTransaction())
            {
                board.WriteIfChanged(_pendingPort, a);
                board.WriteIfChanged(_rubberPort, a);
            }
        }

        private void Commit(DataBlackboard board, ChartAnchor end)
        {
            var start = board.Read(_pendingPort);
            if (start == null) return;
            var old = board.Read(LinesPort);
            var lines = new List<DrawnLine>(old ?? Array.Empty<DrawnLine>()) { new DrawnLine(start, end) };
            using (board.BeginTransaction())
            {
                board.WriteIfChanged(LinesPort, lines);
                board.WriteIfChanged(_pendingPort, null);
                board.WriteIfChanged(_rubberPort, null);
                board.WriteIfChanged(ArmedPort, false);        // 下一帧撤掉模态区域
            }
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (proj, pc) = ctx.UseProjection(ValueRangePort);
            var (armed, ac) = ctx.UsePort(ArmedPort);
            var (lines, lc) = ctx.UsePort(LinesPort);
            var (pending, prc) = ctx.UsePort(_pendingPort);
            var (rubber, rc) = ctx.UsePort(_rubberPort);
            _projection = proj;
            if (!pc && !ac && !lc && !prc && !rc) return;
            if (!proj.IsValid) return;

            int n = lines?.Count ?? 0;
            var segments = new HevoPoint[n * 2];
            for (int i = 0; i < n; i++)
            {
                segments[2 * i] = proj.ToScreen(lines![i].From.Index, lines[i].From.Value);
                segments[2 * i + 1] = proj.ToScreen(lines[i].To.Index, lines[i].To.Value);
            }
            HevoPoint? rubberFrom = pending != null ? proj.ToScreen(pending.Index, pending.Value) : (HevoPoint?)null;
            HevoPoint? rubberTo = rubber != null ? proj.ToScreen(rubber.Index, rubber.Value) : (HevoPoint?)null;
            ctx.For(_layer).PublishData(new DrawingTrait(segments, rubberFrom, rubberTo));

            if (armed)
            {
                ctx.Pointer().Publish(ToolRegion);
                return;
            }

            // 画好的线:每条一个线段区域(Id = 第几条)。真实画线框架还会加两个端点手柄(Part 1/2,Priority = Handle)
            var regions = new PointerRegion[n];
            for (int i = 0; i < n; i++)
                regions[i] = new PointerRegion { Id = i, Shape = PointerShape.Segment(segments[2 * i], segments[2 * i + 1]), Accepts = PointerAccepts.Click, Cursor = PointerCursor.Hand };
            ctx.Pointer().Publish(regions);
        }
    }
}
