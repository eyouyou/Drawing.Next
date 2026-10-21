using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Numerics;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// GraphViewer 调色板。统一在此处注册画刷/画笔常量,
    /// 业务想换皮直接改这里——layer 不需要改一行。
    /// <para>
    /// 配色原则:画布、泳道、节点一律中性石板灰,颜色只留给"数据是什么"(连线 / 端口)和"选中关系"(上下游)。
    /// 颜色一多就分不清哪种颜色在表达什么,这是旧版看起来乱的主因。
    /// </para>
    /// </summary>
    internal static class GraphPalette
    {
        private static HevoColor Rgb(uint hex) => HevoColor.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        private static HevoColor Argb(byte a, uint hex) => HevoColor.FromArgb(a, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

        public static readonly HevoColor CanvasColor = Rgb(0x0F172A);
        public static readonly IHevoBrush CanvasBg     = new HevoSolidBrush(CanvasColor);
        public static readonly IHevoBrush GridBrush    = new HevoSolidBrush(Argb(0x0E, 0x94A3B8));

        // 泳道:相邻泳道明暗交替 + 细分隔线,不染色。
        public static readonly IHevoBrush LaneFillOdd  = new HevoSolidBrush(Argb(0x07, 0xFFFFFF));
        public static readonly HevoPen    LaneDivider  = new(new HevoSolidBrush(Rgb(0x1E293B)), 1.0, IsAntialias: false);
        public static readonly IHevoBrush LaneTitle    = new HevoSolidBrush(Rgb(0x94A3B8));
        public static readonly IHevoBrush LaneCount    = new HevoSolidBrush(Rgb(0x475569));

        public static readonly IHevoBrush NodeFill     = new HevoSolidBrush(Rgb(0x1B2336));
        // 数据源是整张图的起点,底色略偏蓝,一眼能找到。
        public static readonly IHevoBrush NodeFillSrc  = new HevoSolidBrush(Rgb(0x1A2A44));
        public static readonly IHevoBrush NodeShadow   = new HevoSolidBrush(Argb(0x60, 0x020617));
        public static readonly HevoPen NodeBorder      = new(new HevoSolidBrush(Rgb(0x2B3548)), 1.0);
        public static readonly HevoPen NodeDivider     = new(new HevoSolidBrush(Rgb(0x2B3548)), 1.0, IsAntialias: false);
        public static readonly HevoPen NodeBorderSel   = new(new HevoSolidBrush(Rgb(0xFBBF24)), 1.8);

        public static readonly IHevoBrush TitleText    = new HevoSolidBrush(Rgb(0xE2E8F0));
        public static readonly IHevoBrush PortText     = new HevoSolidBrush(Rgb(0x94A3B8));
        public static readonly IHevoBrush PortTextDim  = new HevoSolidBrush(Rgb(0x334155));

        // Edge 衬底:画布底色的粗笔,两根线交叉时下面那根被"切开",交叉关系一目了然,比深色描边干净。
        public static readonly HevoPen EdgeOutlinePen  = new(new HevoSolidBrush(CanvasColor), 4.0);
        public static readonly HevoPen RubberValid     = new(new HevoSolidBrush(Rgb(0x4ADE80)), 2.0);
        public static readonly HevoPen RubberInvalid   = new(new HevoSolidBrush(Rgb(0xF87171)), 2.0,
                                                              DashArray: new double[] { 4, 3 });

        public static readonly HevoPen   BoxSelectPen  = new(new HevoSolidBrush(Rgb(0x38BDF8)), 1.0,
                                                              DashArray: new double[] { 3, 3 });
        public static readonly IHevoBrush BoxSelectFill = new HevoSolidBrush(Argb(0x22, 0x38BDF8));

        public static readonly IHevoBrush  TooltipBg     = new HevoSolidBrush(Argb(0xF5, 0x1E293B));
        public static readonly HevoPen     TooltipBorder = new(new HevoSolidBrush(Rgb(0x334155)), 1.0);
        public static readonly IHevoBrush  TooltipText   = new HevoSolidBrush(Rgb(0xE2E8F0));
        public static readonly IHevoBrush  TooltipDimText = new HevoSolidBrush(Rgb(0x94A3B8));

        // 拖动节点的对齐辅助线:虚线,1px,跟节点边框拉开识别度。
        public static readonly HevoPen AlignGuidePen = new(new HevoSolidBrush(Rgb(0x38BDF8)), 1.0,
                                                            DashArray: new double[] { 4, 3 });

        // Minimap:背景半透明深色 + 边框,内部节点用低饱和填充。
        public static readonly IHevoBrush MinimapBg     = new HevoSolidBrush(Argb(0xE0, 0x0B1120));
        public static readonly HevoPen    MinimapBorder = new(new HevoSolidBrush(Rgb(0x334155)), 1.0);
        public static readonly IHevoBrush MinimapNodeFill = new HevoSolidBrush(Rgb(0x475569));
        public static readonly HevoPen    MinimapViewport = new(new HevoSolidBrush(Rgb(0xFBBF24)), 1.5);

        public static readonly IHevoTypeface Typeface      = new HevoTypeface("Segoe UI", FontWeight: 500);
        public static readonly IHevoTypeface TitleTypeface = new HevoTypeface("Segoe UI", FontWeight: 600);

        // 连线 / 端口颜色只表达"流的是什么数据"。同一档明度(Tailwind 400),色相彼此拉开。
        public static HevoColor EdgeColor(EdgeDataKind kind) => kind switch
        {
            EdgeDataKind.Column   => Rgb(0x38BDF8),
            EdgeDataKind.Range    => Rgb(0xFBBF24),
            EdgeDataKind.HitState => Rgb(0xF472B6),
            EdgeDataKind.Viewport => Rgb(0x4ADE80),
            EdgeDataKind.Stream   => Rgb(0xA78BFA),
            _                     => Rgb(0x94A3B8),
        };

        private static readonly Dictionary<EdgeDataKind, HevoPen> _edgePens = BuildEdgePens(0xE6, 1.5);
        private static readonly Dictionary<EdgeDataKind, HevoPen> _edgePensDim = BuildEdgePens(0x26, 1.2);
        private static readonly Dictionary<EdgeDataKind, IHevoBrush> _portBrushes = BuildPortBrushes();

        private static Dictionary<EdgeDataKind, HevoPen> BuildEdgePens(byte alpha, double thickness)
        {
            var map = new Dictionary<EdgeDataKind, HevoPen>();
            foreach (EdgeDataKind k in Enum.GetValues(typeof(EdgeDataKind)))
            {
                var c = EdgeColor(k);
                map[k] = new HevoPen(new HevoSolidBrush(HevoColor.FromArgb(alpha, c.R, c.G, c.B)), thickness);
            }
            return map;
        }

        private static Dictionary<EdgeDataKind, IHevoBrush> BuildPortBrushes()
        {
            var map = new Dictionary<EdgeDataKind, IHevoBrush>();
            foreach (EdgeDataKind k in Enum.GetValues(typeof(EdgeDataKind)))
                map[k] = new HevoSolidBrush(EdgeColor(k));
            return map;
        }

        public static HevoPen EdgePenFor(EdgeDataKind kind, bool dimmed)
            => (dimmed ? _edgePensDim : _edgePens)[kind];

        public static IHevoBrush PortBrushFor(EdgeDataKind kind) => _portBrushes[kind];

        private static readonly Dictionary<EdgeDataKind, HevoPen> _portRings = BuildEdgePens(0xFF, 1.4);
        public static HevoPen PortRingFor(EdgeDataKind kind) => _portRings[kind];

        // 选中后的上下游:跟拓扑监控同一套语义 —— 蓝 = 数据从哪来,橙 = 数据到哪去。
        public static readonly HevoColor UpstreamColor   = Rgb(0x60A5FA);
        public static readonly HevoColor DownstreamColor = Rgb(0xFB923C);
        public static readonly HevoPen UpstreamEdgePen   = new(new HevoSolidBrush(UpstreamColor), 2.4);
        public static readonly HevoPen DownstreamEdgePen = new(new HevoSolidBrush(DownstreamColor), 2.4);
        public static readonly HevoPen UpstreamNodeBorder   = new(new HevoSolidBrush(UpstreamColor), 1.6);
        public static readonly HevoPen DownstreamNodeBorder = new(new HevoSolidBrush(DownstreamColor), 1.6);
        public static readonly IHevoBrush NodeDimOverlay = new HevoSolidBrush(Argb(0xB0, 0x0F172A));

        public static readonly IHevoBrush LegendBg     = new HevoSolidBrush(Argb(0xF0, 0x111A2E));
        public static readonly HevoPen    LegendBorder = new(new HevoSolidBrush(Rgb(0x1E293B)), 1.0);
    }

    /// <summary>
    /// 选中节点的上下游快照。节点层、连线层、图例层各自从同一份 GraphState 算出,结论一致。
    /// </summary>
    internal sealed class GraphFocus
    {
        public IReadOnlySet<string> Selected { get; }
        public HashSet<string> Upstream { get; }
        public HashSet<string> Downstream { get; }
        public HashSet<(string From, string To)> BackEdges { get; }

        private GraphFocus(IReadOnlySet<string> selected, HashSet<string> up, HashSet<string> down,
                           HashSet<(string, string)> back)
        {
            Selected = selected; Upstream = up; Downstream = down; BackEdges = back;
        }

        /// <summary>没选中节点时返回 null —— 调用方据此走"不压暗"的常规绘制。</summary>
        public static GraphFocus? From(GraphState s)
        {
            if (s.SelectedNodeIds.Count == 0) return null;
            var back = GraphFlowAnalysis.FindBackEdges(s.Nodes, s.Edges);
            var (up, down) = GraphFlowAnalysis.ComputeLineage(s.Edges, s.SelectedNodeIds, back);
            return new GraphFocus(s.SelectedNodeIds, up, down, back);
        }

        public bool InLineage(string nodeId) => Upstream.Contains(nodeId) || Downstream.Contains(nodeId);

        /// <summary>
        /// 连线在焦点下的角色:+1 下游,-1 上游,0 无关(压暗)。
        /// 回边不参与链路传递,只在直接连着选中节点时亮,否则环里的线会把整圈都染上。
        /// </summary>
        public int RoleOf(Edge e)
        {
            if (BackEdges.Contains((e.FromNodeId, e.ToNodeId)))
            {
                if (Selected.Contains(e.FromNodeId)) return 1;
                if (Selected.Contains(e.ToNodeId)) return -1;
                return 0;
            }
            if (Downstream.Contains(e.FromNodeId) && Downstream.Contains(e.ToNodeId)) return 1;
            if (Upstream.Contains(e.FromNodeId) && Upstream.Contains(e.ToNodeId)) return -1;
            return 0;
        }
    }

    /// <summary>
    /// 画布背景层:纯色底 + 缩放感知的网格。
    /// 仅监听 GraphRenderTrait;CanvasTransform 变化(平移/缩放)时引擎引用判脏触发重绘。
    /// </summary>
    internal sealed class GraphCanvasLayer : ChartLayer
    {
        public GraphCanvasLayer()
        {
            Name = "GraphCanvasLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Background;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            var size = data.Get<ViewportSizeTrait>();
            if (t == null || size == null) return;

            float w = (float)size.Width;
            float h = (float)size.Height;
            draw.DrawRectangle(GraphPalette.CanvasBg, null, new HevoRect(0, 0, w, h));

            var tr = t.State.Transform;

            // 1) 网格:很淡,只给拖拽对齐一点参照;间距随 zoom 扩缩,屏幕步长保持在 [16, 64] 内
            float baseStep = 40f;
            float screenStep = baseStep * tr.Scale;
            while (screenStep < 16f) screenStep *= 2f;
            while (screenStep > 64f) screenStep /= 2f;

            float ox = ((tr.OffsetX % screenStep) + screenStep) % screenStep;
            float oy = ((tr.OffsetY % screenStep) + screenStep) % screenStep;
            var gridPen = new HevoPen(GraphPalette.GridBrush, 1.0, IsAntialias: false);
            for (float x = ox; x < w; x += screenStep) draw.DrawLine(gridPen, new HevoPoint(x, 0), new HevoPoint(x, h));
            for (float y = oy; y < h; y += screenStep) draw.DrawLine(gridPen, new HevoPoint(0, y), new HevoPoint(w, y));

            // 2) 泳道 —— 竖向列带,从左到右就是数据流方向。不染色,只靠明暗交替 + 细分隔线区分:
            //    颜色留给连线表达数据类型,泳道再染一套色会互相抢。
            //    相邻泳道在两者之间的缝隙中线分界,色带连成一片,不留黑缝。
            //    标题固定贴在色带顶部(图例那一行下面),上下平移时不会滚出视野。
            var bands = ComputeCategoryBands(t.State).OrderBy(b => b.left).ToList();
            for (int i = 0; i < bands.Count; i++)
            {
                var (category, left, right, count) = bands[i];
                float l = i == 0 ? left : (bands[i - 1].right + left) / 2f;
                float r = i == bands.Count - 1 ? right : (right + bands[i + 1].left) / 2f;
                float screenLeft  = l * tr.Scale + tr.OffsetX;
                float screenRight = r * tr.Scale + tr.OffsetX;
                if (screenRight < 0 || screenLeft > w) continue;

                if (i % 2 == 1)
                    draw.DrawRectangle(GraphPalette.LaneFillOdd, null, new HevoRect(screenLeft, 0, screenRight - screenLeft, h));
                if (i > 0)
                    draw.DrawLine(GraphPalette.LaneDivider, new HevoPoint(screenLeft, 0), new HevoPoint(screenLeft, h));

                var title = FeatureCategoryStyle.DisplayName(category);
                float tx = Math.Max(screenLeft, 0) + 12f;
                draw.DrawText(title, GraphPalette.TitleTypeface,
                    GraphPalette.LaneTitle, 12f, new HevoPoint(tx, LaneTitleY), TextAlignX.Left, TextAlignY.Top);
                draw.DrawText(count.ToString(), GraphPalette.Typeface, GraphPalette.LaneCount, 11f,
                    new HevoPoint(tx + EstimateWidth(title, 12f) + 8f, LaneTitleY + 1f),
                    TextAlignX.Left, TextAlignY.Top);
            }
        }

        // 没有文本测量接口,按 CJK 全角 / 拉丁半角粗估。
        internal static float EstimateWidth(string text, float fontSize)
        {
            float wsum = 0f;
            foreach (var ch in text) wsum += ch >= 0x2E80 ? fontSize : fontSize * 0.58f;
            return wsum;
        }

        // 泳道标题的屏幕 Y:让开顶部图例那一行。
        private const float LaneTitleY = 42f;

        /// <summary>
        /// 把当前画布上同泳道的节点聚成 X 范围列带。泳道归属跟 <see cref="AutoLayout.LaneFor"/> 一致,
        /// 自动布局排出来的列段和这里画的色带才能对上。
        /// </summary>
        private static IEnumerable<(FeatureCategory category, float left, float right, int count)> ComputeCategoryBands(GraphState state)
        {
            const float pad = 24f;
            var groups = new Dictionary<FeatureCategory, (float min, float max, int count)>();
            foreach (var n in state.Nodes)
            {
                var lane = AutoLayout.LaneFor(n);
                if (lane == FeatureCategory.Unknown) continue;
                float l = n.Position.X - pad, r = n.Position.X + n.Size.X + pad;
                groups[lane] = groups.TryGetValue(lane, out var range)
                    ? (Math.Min(range.min, l), Math.Max(range.max, r), range.count + 1)
                    : (l, r, 1);
            }
            foreach (var kv in groups)
                yield return (kv.Key, kv.Value.min, kv.Value.max, kv.Value.count);
        }
    }

    /// <summary>
    /// 节点层:渲染所有节点 (圆角矩形 + 标题栏 + 端口圆点 + 端口标签)。
    /// 全程 PushTransform 一次,后续按画布坐标系直接画——把 pan/zoom 交给底层渲染器。
    /// </summary>
    internal sealed class GraphNodeLayer : ChartLayer
    {
        public GraphNodeLayer()
        {
            Name = "GraphNodeLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            if (t == null) return;
            var s = t.State;
            var tr = s.Transform;
            var matrix = new Matrix3x2(tr.Scale, 0, 0, tr.Scale, tr.OffsetX, tr.OffsetY);
            using var _ = draw.PushTransform(matrix);
            var focus = GraphFocus.From(s);

            // 端口颜色跟连在它上面的线一致;没连线的端口按自身类型取色、画空心圈。
            var portKind = new Dictionary<(string, string, bool), EdgeDataKind>();
            foreach (var e in s.Edges)
            {
                var k = EdgeDataKindClassifier.Classify(s, e);
                portKind[(e.FromNodeId, e.FromPortId, false)] = k;
                portKind[(e.ToNodeId, e.ToPortId, true)] = k;
            }

            const float radius = 8f, headerH = 24f;
            foreach (var node in s.Nodes)
            {
                var bounds = node.GetBounds();
                var fill = node.Kind == NodeKind.DataSource ? GraphPalette.NodeFillSrc : GraphPalette.NodeFill;
                var border = GraphPalette.NodeBorder;
                if (s.SelectedNodeIds.Contains(node.Id)) border = GraphPalette.NodeBorderSel;
                else if (focus != null && focus.Upstream.Contains(node.Id)) border = GraphPalette.UpstreamNodeBorder;
                else if (focus != null && focus.Downstream.Contains(node.Id)) border = GraphPalette.DownstreamNodeBorder;

                // 一层下移 3px 的深色投影,让卡片从画布上浮起来,不用边框加粗也能分出层次。
                var shadow = new HevoRect(bounds.X, bounds.Y + 3f, bounds.Width, bounds.Height);
                draw.DrawRoundedRectangle(GraphPalette.NodeShadow, null, shadow, radius, radius);
                draw.DrawRoundedRectangle(fill, border, bounds, radius, radius);

                draw.DrawText(node.Title, GraphPalette.TitleTypeface, GraphPalette.TitleText, 13f,
                              new HevoPoint(bounds.X + 10f, bounds.Y + headerH / 2f + 1f),
                              TextAlignX.Left, TextAlignY.Center);
                if (node.InputPorts.Count + node.OutputPorts.Count > 0)
                    draw.DrawLine(GraphPalette.NodeDivider,
                        new HevoPoint(bounds.X + 1f, bounds.Y + headerH + 2f),
                        new HevoPoint(bounds.X + bounds.Width - 1f, bounds.Y + headerH + 2f));

                bool dimmed = focus != null && !focus.InLineage(node.Id);
                // 不在链路上的节点盖一层半透明底色压暗,比逐个元素换暗色笔刷改动小、观感一致。
                // 端口圆点骑在节点边框上、一半露在遮罩外,所以端口在遮罩之后单独按压暗样式画。
                if (dimmed) draw.DrawRoundedRectangle(GraphPalette.NodeDimOverlay, null, bounds, radius, radius);
                DrawPorts(draw, node, isInput: true, portKind, dimmed);
                DrawPorts(draw, node, isInput: false, portKind, dimmed);
            }
        }

        private static void DrawPorts(IDrawingSink draw, Node node, bool isInput,
                                      Dictionary<(string, string, bool), EdgeDataKind> portKind, bool dimmed)
        {
            var ports = isInput ? node.InputPorts : node.OutputPorts;
            for (int i = 0; i < ports.Count; i++)
            {
                var port = ports[i];
                var c = node.GetPortPosition(port);
                bool connected = portKind.TryGetValue((node.Id, port.Id, isInput), out var kind);
                if (!connected)
                    kind = EdgeDataKindClassifier.Classify(port.DataTypeName, EdgeKind.DataPort, node.Kind, null);
                var fill = connected && !dimmed ? GraphPalette.PortBrushFor(kind) : GraphPalette.NodeFill;
                var ring = dimmed ? GraphPalette.EdgePenFor(kind, dimmed: true) : GraphPalette.PortRingFor(kind);

                if (port.IsArray)
                {
                    // 扇入端口画圆角方块,跟单端口圆点拉开视觉差。
                    draw.DrawRoundedRectangle(fill, ring, new HevoRect(c.X - 4.5f, c.Y - 4.5f, 9f, 9f), 2f, 2f);
                }
                else
                {
                    draw.DrawEllipse(fill, ring, c, 4.5f, 4.5f);
                }

                var labelX = isInput ? c.X + 11f : c.X - 11f;
                var alignX = isInput ? TextAlignX.Left : TextAlignX.Right;
                var label  = port.IsArray ? port.Name + " []" : port.Name;
                draw.DrawText(label, GraphPalette.Typeface, dimmed ? GraphPalette.PortTextDim : GraphPalette.PortText, 11f,
                              new HevoPoint(labelX, c.Y),
                              alignX, TextAlignY.Center);
            }
        }
    }

    /// <summary>
    /// 连线层:优先按自动布局给的横竖折线画;没有折线或节点被拖动过的连线退回贝塞尔曲线连接 from-output 到 to-input。
    /// 用 Geometry SVG path,稳定 0-GC——cubic 控制点按节点距离半量水平偏移即可。
    /// </summary>
    internal sealed class GraphEdgeLayer : ChartLayer
    {
        public GraphEdgeLayer()
        {
            Name = "GraphEdgeLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            if (t == null) return;
            var s = t.State;
            var tr = s.Transform;
            var matrix = new Matrix3x2(tr.Scale, 0, 0, tr.Scale, tr.OffsetX, tr.OffsetY);
            using var _ = draw.PushTransform(matrix);

            // 同 source-port 多次出线时,按出线序号微微纵向偏移,避免曲线 100% 重叠看起来像一根。
            // 缓存 (fromNodeId, fromPortId) → 已绘制次数。
            var siblingCounter = new Dictionary<(string, string), int>();
            var focus = GraphFocus.From(s);

            // 焦点下先画压暗的、再画链路上的,保证高亮线不被暗线盖住。
            IEnumerable<Edge> ordered = s.Edges;
            if (focus != null) ordered = s.Edges.OrderBy(e => focus.RoleOf(e) != 0 ? 1 : 0);

            // 走折线的连线按源端口 / 目标端口分组,最后在主干的分叉处画圆点。
            var bySource = new Dictionary<(string, string), List<(IReadOnlyList<HevoPoint> Route, HevoPen Pen, bool Dimmed)>>();
            var byTarget = new Dictionary<(string, string), List<(IReadOnlyList<HevoPoint> Route, HevoPen Pen, bool Dimmed)>>();

            // 回绕总线:进同一个输入端口的回边,收集起来最后统一标注来源。
            var buses = new Dictionary<(string, string), List<(Edge Edge, IReadOnlyList<HevoPoint> Route, bool Dimmed, EdgeDataKind Kind)>>();

            foreach (var edge in ordered)
            {
                var from = s.FindNode(edge.FromNodeId);
                var to = s.FindNode(edge.ToNodeId);
                if (from == null || to == null) continue;
                var fp = from.FindPort(edge.FromPortId, isInput: false);
                var tp = to.FindPort(edge.ToPortId, isInput: true);
                if (fp == null || tp == null) continue;

                var p0 = from.GetPortPosition(fp);
                var p3 = to.GetPortPosition(tp);

                int siblingIdx = siblingCounter.GetValueOrDefault((edge.FromNodeId, edge.FromPortId), 0);
                siblingCounter[(edge.FromNodeId, edge.FromPortId)] = siblingIdx + 1;

                int role = focus?.RoleOf(edge) ?? 0;
                bool dimmed = focus != null && role == 0;
                var pen = role > 0 ? GraphPalette.DownstreamEdgePen
                        : role < 0 ? GraphPalette.UpstreamEdgePen
                        : GraphPalette.EdgePenFor(EdgeDataKindClassifier.Classify(s, edge), dimmed);

                // 自动布局给过折线、且首尾还贴着端口(节点没被拖走)→ 按折线画:横竖走线不穿节点、不重合。
                if (TryGetRoute(s, edge, p0, p3, out var route))
                {
                    if (!dimmed) DrawRoundedPolyline(draw, route, GraphPalette.EdgeOutlinePen);
                    DrawRoundedPolyline(draw, route, pen);
                    AddTo(bySource, (edge.FromNodeId, edge.FromPortId), (route, pen, dimmed));
                    AddTo(byTarget, (edge.ToNodeId, edge.ToPortId), (route, pen, dimmed));
                    if (p3.X < p0.X)
                    {
                        var key = (edge.ToNodeId, edge.ToPortId);
                        if (!buses.TryGetValue(key, out var list)) buses[key] = list = new List<(Edge, IReadOnlyList<HevoPoint>, bool, EdgeDataKind)>();
                        list.Add((edge, route, dimmed, EdgeDataKindClassifier.Classify(s, edge)));
                    }
                    continue;
                }

                // 终点在起点左边:贝塞尔会从节点身上横穿回去。改走两端节点下方绕回,跟正向线分开。
                if (p3.X < p0.X + BackRouteMinDx)
                {
                    float bottom = Math.Max(from.Position.Y + from.Size.Y, to.Position.Y + to.Size.Y);
                    float routeY = bottom + 24f + siblingIdx * 6f;
                    if (!dimmed) DrawBackRoute(draw, p0, p3, routeY, GraphPalette.EdgeOutlinePen);
                    DrawBackRoute(draw, p0, p3, routeY, pen);
                    continue;
                }

                // 避障:扫所有非端点节点,挡道的把控制点 Y 推开。
                float vAvoid = ComputeAvoidanceVOffset(p0, p3, s.Nodes, edge.FromNodeId, edge.ToNodeId);

                // 双笔:深色描边在底层兜底"过节点也能看到",上层亮线表达连接关系;压暗的线不要衬底,免得暗线反而显眼。
                if (!dimmed) DrawBezier(draw, p0, p3, GraphPalette.EdgeOutlinePen, siblingIdx, vAvoid);
                DrawBezier(draw, p0, p3, pen, siblingIdx, vAvoid);
            }

            foreach (var g in bySource.Values) DrawJunctions(draw, g);
            foreach (var g in byTarget.Values) DrawJunctions(draw, g);
            foreach (var kv in buses) DrawBusLabel(draw, s, kv.Key.Item2, kv.Value);
        }

        private static void AddTo<TKey, TItem>(Dictionary<TKey, List<TItem>> map, TKey key, TItem item) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<TItem>();
            list.Add(item);
        }

        /// <summary>
        /// 同一端口的几根折线共用主干时,在分叉处画圆点:某根线的拐点落在同组另一根线的线段内部
        /// (另一根在这里直行、它在这里拐走)就是分叉。没有圆点的十字交叉才是真的两根线交叉。
        /// </summary>
        private static void DrawJunctions(IDrawingSink draw, List<(IReadOnlyList<HevoPoint> Route, HevoPen Pen, bool Dimmed)> group)
        {
            if (group.Count < 2) return;
            var drawn = new HashSet<(int, int)>();
            foreach (var a in group)
            {
                for (int i = 1; i < a.Route.Count - 1; i++)
                {
                    var v = a.Route[i];
                    foreach (var b in group)
                    {
                        if (ReferenceEquals(a.Route, b.Route)) continue;
                        if (!LiesInsideSegment(v, b.Route)) continue;
                        if (!drawn.Add(((int)MathF.Round(v.X), (int)MathF.Round(v.Y)))) break;
                        var brush = a.Pen.Brush;
                        draw.DrawEllipse(brush, null, v, a.Dimmed ? 2f : 3f, a.Dimmed ? 2f : 3f);
                        break;
                    }
                }
            }

            static bool LiesInsideSegment(HevoPoint p, IReadOnlyList<HevoPoint> route)
            {
                const float eps = 0.5f;
                for (int j = 0; j + 1 < route.Count; j++)
                {
                    var s = route[j];
                    var t = route[j + 1];
                    if (Math.Abs(s.X - t.X) < eps && Math.Abs(p.X - s.X) < eps
                        && p.Y > Math.Min(s.Y, t.Y) + eps && p.Y < Math.Max(s.Y, t.Y) - eps) return true;
                    if (Math.Abs(s.Y - t.Y) < eps && Math.Abs(p.Y - s.Y) < eps
                        && p.X > Math.Min(s.X, t.X) + eps && p.X < Math.Max(s.X, t.X) - eps) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 回绕总线的来源标注,贴在总线走廊(折线里最长的横段)左端、靠节点的一侧:"Compute.OutputPort, Plot.macd/signal/hist → ValuePorts"。
        /// 回边要绕到画布底部再回来,单看线很难追到两头;把两头写在线上,不用追线也知道谁连谁。
        /// </summary>
        private static void DrawBusLabel(IDrawingSink draw, GraphState s,
            string toPortId, List<(Edge Edge, IReadOnlyList<HevoPoint> Route, bool Dimmed, EdgeDataKind Kind)> bus)
        {
            if (bus.All(b => b.Dimmed)) return;

            // 同一个源节点的多个端口合并写:Plot.macd/signal/hist
            var parts = new List<string>();
            foreach (var g in bus.GroupBy(b => b.Edge.FromNodeId))
            {
                var node = s.FindNode(g.Key);
                if (node == null) continue;
                var ports = g.Select(b => node.FindPort(b.Edge.FromPortId, isInput: false)?.Name ?? b.Edge.FromPortId).Distinct();
                parts.Add(ShortTitle(node.Title) + "." + string.Join("/", ports));
            }
            var target = s.FindNode(bus[0].Edge.ToNodeId);
            var toName = target?.FindPort(toPortId, isInput: true)?.Name ?? toPortId;
            var text = string.Join(", ", parts) + "  →  " + (target != null ? ShortTitle(target.Title) + "." : "") + toName;

            // 走廊在节点上方或下方都有可能,取折线里最长的横段;标签写在走廊靠节点的一侧
            //(走廊在两端端口上方就写在线下面),不跟顶部泳道标题或外圈的走廊抢位置。
            var route = bus[0].Route;
            float corridorY = route[0].Y, left = route[0].X, longest = -1f;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                var a = route[i];
                var b = route[i + 1];
                if (Math.Abs(a.Y - b.Y) > 0.5f) continue;
                float len = Math.Abs(a.X - b.X);
                if (len <= longest) continue;
                longest = len;
                corridorY = a.Y;
                left = Math.Min(a.X, b.X);
            }

            const float fontSize = 12f, padX = 6f, h = 17f;
            float w = GraphCanvasLayer.EstimateWidth(text, fontSize) + padX * 2;
            bool corridorAbove = corridorY < Math.Min(route[0].Y, route[route.Count - 1].Y);
            var rect = new HevoRect(left + 10f, corridorAbove ? corridorY + 3f : corridorY - h - 3f, w, h);
            draw.DrawRoundedRectangle(GraphPalette.CanvasBg, null, rect, 3f, 3f);
            draw.DrawText(text, GraphPalette.Typeface, GraphPalette.PortBrushFor(bus[0].Kind), fontSize,
                new HevoPoint(rect.X + padX, rect.Y + h / 2f), TextAlignX.Left, TextAlignY.Center);
        }

        // 节点标题去掉 Feature 后缀,标注里省地方:UniversalAutoScaleFeature → UniversalAutoScale。
        private static string ShortTitle(string title)
            => title.EndsWith("Feature", StringComparison.Ordinal) && title.Length > 7 ? title.Substring(0, title.Length - 7) : title;

        private static bool TryGetRoute(GraphState s, Edge edge, HevoPoint p0, HevoPoint p3, out IReadOnlyList<HevoPoint> route)
        {
            route = Array.Empty<HevoPoint>();
            if (s.EdgeRoutes == null || !s.EdgeRoutes.TryGetValue(edge.Id, out var r) || r == null || r.Count < 2) return false;
            const float tol = 1f;
            var a = r[0];
            var b = r[r.Count - 1];
            if (Math.Abs(a.X - p0.X) > tol || Math.Abs(a.Y - p0.Y) > tol) return false;
            if (Math.Abs(b.X - p3.X) > tol || Math.Abs(b.Y - p3.Y) > tol) return false;
            route = r;
            return true;
        }

        /// <summary>
        /// 横竖折线,拐角用小圆角过渡:直角拐点在缩放后显得生硬,圆角也让并行的线在拐弯处更好分辨。
        /// 圆角半径不超过相邻两段各自的一半,短段不会被圆角吃掉方向。
        /// </summary>
        public static void DrawRoundedPolyline(IDrawingSink draw, IReadOnlyList<HevoPoint> pts, HevoPen pen, float radius = 6f)
        {
            if (pts.Count < 2) return;
            var cursor = pts[0];
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var prev = pts[i - 1];
                var corner = pts[i];
                var next = pts[i + 1];
                float inLen = Dist(prev, corner), outLen = Dist(corner, next);
                float r = Math.Min(radius, Math.Min(inLen, outLen) / 2f);
                if (r < 0.5f)
                {
                    draw.DrawLine(pen, cursor, corner);
                    cursor = corner;
                    continue;
                }
                var enter = Toward(corner, prev, r);
                var leave = Toward(corner, next, r);
                draw.DrawLine(pen, cursor, enter);
                // 控制点取在拐点与两端点连线的 0.55 处,近似四分之一圆。
                draw.DrawCubicBezier(pen, enter, Lerp(enter, corner, 0.55f), Lerp(leave, corner, 0.55f), leave);
                cursor = leave;
            }
            draw.DrawLine(pen, cursor, pts[pts.Count - 1]);

            static float Dist(HevoPoint a, HevoPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            static HevoPoint Toward(HevoPoint from, HevoPoint to, float d)
            {
                float len = Dist(from, to);
                return len < 1e-3f ? from : new HevoPoint(from.X + (to.X - from.X) / len * d, from.Y + (to.Y - from.Y) / len * d);
            }
            static HevoPoint Lerp(HevoPoint a, HevoPoint b, float t) => new HevoPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        // 两端口水平距离不足这个值也按回绕处理:几乎竖直的 S 形线会贴着节点边框走,看不清。
        private const float BackRouteMinDx = 24f;

        /// <summary>
        /// 回绕走线:出口向右拐下 → 沿 <paramref name="routeY"/> 水平向左 → 向上拐进入口。
        /// 两段拐角用 cubic,中段直线,端口处仍保持水平切线,跟正向边的接入方式一致。
        /// </summary>
        public static void DrawBackRoute(IDrawingSink draw, HevoPoint a, HevoPoint b, float routeY, HevoPen pen)
        {
            const float k = 40f;
            var a1 = new HevoPoint(a.X + k, routeY);
            var b1 = new HevoPoint(b.X - k, routeY);
            draw.DrawCubicBezier(pen, a, new HevoPoint(a.X + k * 1.4f, a.Y), new HevoPoint(a.X + k * 1.4f, routeY), a1);
            draw.DrawLine(pen, a1, b1);
            draw.DrawCubicBezier(pen, b1, new HevoPoint(b.X - k * 1.4f, routeY), new HevoPoint(b.X - k * 1.4f, b.Y), b);
        }

        /// <summary>
        /// 直线连接 from→to 在中段是否被某个非端点节点 AABB 阻挡;返回需要施加的 Y 偏移
        /// (>0 = 把曲线腹部往下推,<0 = 往上推)。多障碍叠加取绝对值最大方向。
        /// 简化判定:1) 节点 AABB 在 X 方向跟边的 [minX, maxX] 重叠,
        ///         2) 节点中心 Y 与"端点 Y 线性插值在该 X 处的值"距离小于安全垫 → 算阻挡。
        /// </summary>
        private static float ComputeAvoidanceVOffset(
            HevoPoint p0, HevoPoint p3, IReadOnlyList<Node> nodes, string fromId, string toId)
        {
            float minX = Math.Min(p0.X, p3.X);
            float maxX = Math.Max(p0.X, p3.X);
            float dx = p3.X - p0.X;
            float pushUp = 0f, pushDown = 0f;

            foreach (var n in nodes)
            {
                if (n.Id == fromId || n.Id == toId) continue;
                var b = n.GetBounds();
                if (b.X + b.Width < minX || b.X > maxX) continue;

                // 直线在节点中心 X 处的预期 Y
                float nodeMidX = b.X + b.Width / 2f;
                float t = Math.Abs(dx) < 1e-3f ? 0.5f : Math.Clamp((nodeMidX - p0.X) / dx, 0f, 1f);
                float lineYAtNode = p0.Y + (p3.Y - p0.Y) * t;
                float nodeMidY = b.Y + b.Height / 2f;
                float halfH = b.Height / 2f + 16f;  // 16px 安全垫

                if (Math.Abs(lineYAtNode - nodeMidY) > halfH) continue;  // 不挡道

                // 决定推哪个方向:朝远离节点中心的方向。推力 = 把端点连线推到节点边外所需 Y。
                if (lineYAtNode <= nodeMidY)
                {
                    // 直线在节点中心或上方 → 把曲线往上推到节点上方
                    float need = (nodeMidY - halfH) - lineYAtNode;  // ≤0
                    if (need < pushUp) pushUp = need;
                }
                else
                {
                    float need = (nodeMidY + halfH) - lineYAtNode;  // ≥0
                    if (need > pushDown) pushDown = need;
                }
            }

            // 同时被上下挤的少见,先按"推力大的方向"走;限幅避免曲线腹部飞太远
            float deflection = Math.Abs(pushDown) >= Math.Abs(pushUp) ? pushDown : pushUp;
            return Math.Clamp(deflection, -180f, 180f);
        }

        // 走原生 <see cref="DrawingExtensions.DrawCubicBezier"/> —— WPF 用 StreamGeometry.BezierTo 渲染,
        // 比"自己采样成 N 段 polyline"既丝滑又便宜:N 段法在长边上每段都是直线,折角能用肉眼看见;
        // 原生 cubic bezier 走 GPU 抗锯齿曲线管线,任何长度都丝滑无折。
        //
        // <para>控制点策略:</para>
        // <list type="bullet">
        ///   <item>正向边 (b.X >= a.X):标准向右伸出 + 向左伸入,水平切线接入端口。</item>
        ///   <item>反向边 (b.X &lt; a.X):水平 dx 按节点距离反推,加大力度避免控制点穿过端点形成 S 太紧。</item>
        ///   <item>陡边 (|dy| > |dx|*1.5):dx 拉大避免视觉直角。</item>
        ///   <item><c>siblingIdx</c> ≥ 1:同一 source 的第 N 条边纵向偏移 ±8*N,防止彻底重叠。</item>
        ///   <item><c>vAvoid</c> ≠ 0:避障偏移,直接加到控制点 Y 上让曲线腹部绕开障碍节点。</item>
        /// </list>
        public static void DrawBezier(IDrawingSink draw, HevoPoint a, HevoPoint b, HevoPen pen, int siblingIdx = 0, float vAvoid = 0f)
        {
            float dxAbs = Math.Abs(b.X - a.X);
            float dyAbs = Math.Abs(b.Y - a.Y);
            float dx;
            if (b.X >= a.X)
            {
                dx = Math.Max(60f, dxAbs * 0.5f);
                if (dyAbs > dxAbs * 1.5f) dx = Math.Max(dx, dyAbs * 0.45f);
            }
            else
            {
                dx = Math.Max(120f, dyAbs * 0.6f + dxAbs * 0.5f);
            }

            // 同 source 的 sibling:从 0 开始,每多一条往下错 8px(交替正负避免单边堆积)
            float siblingOffset = siblingIdx == 0 ? 0f
                : ((siblingIdx % 2 == 1 ? 1 : -1) * ((siblingIdx + 1) / 2) * 8f);

            // 避障 + sibling 共同形成控制点 Y 偏移
            float vOff = siblingOffset + vAvoid;
            var c1 = new HevoPoint(a.X + dx, a.Y + vOff);
            var c2 = new HevoPoint(b.X - dx, b.Y + vOff);
            draw.DrawCubicBezier(pen, a, c1, c2, b);
        }
    }

    /// <summary>
    /// 选中高亮层:已选节点的外发光边框 + 框选橡皮筋。
    /// 跟节点层分开,频繁切换选中时只重画这一层。
    /// </summary>
    internal sealed class GraphSelectionLayer : ChartLayer
    {
        public GraphSelectionLayer()
        {
            Name = "GraphSelectionLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Selection;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            if (t == null) return;
            var s = t.State;

            if (s.BoxSelection != null)
            {
                var rect = s.Transform.CanvasToScreen(s.BoxSelection.ToRect());
                draw.DrawRectangle(GraphPalette.BoxSelectFill, GraphPalette.BoxSelectPen, rect);
            }
        }
    }

    /// <summary>
    /// 连线颜色图例,固定在画布左上角横排一条(右下角归 minimap)。
    /// 只列当前画布实际出现的数据类型;选中节点时追加上下游两行,解释高亮色。
    /// </summary>
    internal sealed class GraphLegendLayer : ChartLayer
    {
        public GraphLegendLayer()
        {
            Name = "GraphLegendLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Selection;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            var size = data.Get<ViewportSizeTrait>();
            if (t == null || size == null) return;
            var rows = BuildRows(t.State);
            if (rows.Count == 0) return;

            // 横排一条贴画布顶部:AutoLayout 第一条泳道从 y=60 起,顶上这条带子默认是空的,不压节点。
            const float pad = 6f, rowH = 18f, swatchW = 18f, gap = 6f, itemGap = 16f, fontSize = 11f;
            float maxW = Math.Max(120f, (float)size.Width - 24f);
            var placed = new List<(float X, int Row, string Label, HevoPen Pen)>(rows.Count);
            float x = 0f, widest = 0f;
            int row = 0;
            foreach (var (label, pen) in rows)
            {
                float itemW = swatchW + gap + EstimateTextWidth(label, fontSize);
                if (x > 0f && x + itemW > maxW - pad * 2) { row++; x = 0f; }
                placed.Add((x, row, label, pen));
                x += itemW + itemGap;
                widest = Math.Max(widest, x - itemGap);
            }

            var rect = new HevoRect(12f, 8f, widest + pad * 2, (row + 1) * rowH + pad * 2 - 4f);
            draw.DrawRoundedRectangle(GraphPalette.LegendBg, GraphPalette.LegendBorder, rect, 4f, 4f);
            foreach (var (ix, r, label, pen) in placed)
            {
                float cy = rect.Y + pad - 2f + rowH * (r + 0.5f);
                float sx = rect.X + pad + ix;
                draw.DrawLine(pen, new HevoPoint(sx, cy), new HevoPoint(sx + swatchW, cy));
                draw.DrawText(label, GraphPalette.Typeface, GraphPalette.TooltipText, fontSize,
                    new HevoPoint(sx + swatchW + gap, cy), TextAlignX.Left, TextAlignY.Center);
            }
        }

        private static List<(string Label, HevoPen Pen)> BuildRows(GraphState s)
        {
            var present = new HashSet<EdgeDataKind>();
            foreach (var e in s.Edges) present.Add(EdgeDataKindClassifier.Classify(s, e));

            var rows = new List<(string, HevoPen)>();
            foreach (EdgeDataKind k in Enum.GetValues(typeof(EdgeDataKind)))
            {
                if (present.Contains(k))
                    rows.Add((EdgeDataKindClassifier.DisplayName(k), GraphPalette.EdgePenFor(k, dimmed: false)));
            }
            if (s.SelectedNodeIds.Count > 0 && rows.Count > 0)
            {
                rows.Add(("上游 · 数据从哪来", GraphPalette.UpstreamEdgePen));
                rows.Add(("下游 · 数据到哪去", GraphPalette.DownstreamEdgePen));
            }
            return rows;
        }

        // 没有文本测量接口,按 CJK 全角 / 拉丁半角粗估;宁宽勿窄,背景框不至于截字。
        private static float EstimateTextWidth(string text, float fontSize)
        {
            float w = 0f;
            foreach (var ch in text) w += ch >= 0x2E80 ? fontSize : fontSize * 0.6f;
            return w;
        }
    }

    // GraphMinimapLayer 已合并到通用 Charting.Layers.MinimapLayer ——
    // GraphSchema.PushTraitToLayers 自己负责把 GraphState 翻译成 MinimapTrait(ContentBounds + ContentItems + Viewport)。
    // 旧的 ComputeContentBounds 静态方法搬到 GraphSchema.ComputeNodesAabb,跟 ComputeMinimapMapping 共用。

    /// <summary>
    /// 交互预览层:橡皮筋拖出连线 + 端口悬停 tooltip。
    /// 落在 Interaction 槽位,单独一个 _overlayCanvas,跟主图 dirty 隔离 ——
    /// hover 这种高频事件不会触发主画布 (节点/连线) 的重画。
    /// </summary>
    internal sealed class GraphPreviewLayer : ChartLayer
    {
        public GraphPreviewLayer()
        {
            Name = "GraphPreviewLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var t = data.Get<GraphRenderTrait>();
            var size = data.Get<ViewportSizeTrait>();
            if (t == null || size == null) return;
            var s = t.State;

            // 1. 橡皮筋 + 对齐辅助线(画布坐标系,需要 transform)
            if (s.RubberBand != null || s.AlignmentGuides != null)
            {
                var tr = s.Transform;
                var matrix = new Matrix3x2(tr.Scale, 0, 0, tr.Scale, tr.OffsetX, tr.OffsetY);
                using var _ = draw.PushTransform(matrix);

                if (s.RubberBand != null)
                {
                    var from = s.FindNode(s.RubberBand.FromNodeId);
                    if (from != null)
                    {
                        var fp = from.FindPort(s.RubberBand.FromPortId, isInput: false);
                        if (fp != null)
                        {
                            var p0 = from.GetPortPosition(fp);
                            var pen = s.RubberBand.IsValidTarget ? GraphPalette.RubberValid : GraphPalette.RubberInvalid;
                            GraphEdgeLayer.DrawBezier(draw, p0, s.RubberBand.MousePosCanvas, pen);
                        }
                    }
                }

                if (s.AlignmentGuides != null)
                {
                    // 把屏幕边界反算到画布坐标,作为辅助线的两端
                    float canvasLeft   = -tr.OffsetX / tr.Scale;
                    float canvasRight  = ((float)size.Width - tr.OffsetX) / tr.Scale;
                    float canvasTop    = -tr.OffsetY / tr.Scale;
                    float canvasBottom = ((float)size.Height - tr.OffsetY) / tr.Scale;
                    foreach (var x in s.AlignmentGuides.VerticalLines)
                        draw.DrawLine(GraphPalette.AlignGuidePen, new HevoPoint(x, canvasTop), new HevoPoint(x, canvasBottom));
                    foreach (var y in s.AlignmentGuides.HorizontalLines)
                        draw.DrawLine(GraphPalette.AlignGuidePen, new HevoPoint(canvasLeft, y), new HevoPoint(canvasRight, y));
                }
            }

            // 2. 端口悬停 tooltip(屏幕坐标系,无需 transform)
            if (s.HoveredPort != null)
            {
                var node = s.FindNode(s.HoveredPort.NodeId);
                var port = node?.FindPort(s.HoveredPort.PortId, s.HoveredPort.IsInput);
                if (node != null && port != null)
                {
                    DrawPortTooltip(draw, port, s.HoveredPort.AnchorScreen);
                }
            }
        }

        // 端口悬停浮窗:显示 [in/out] PortName : DataType + 描述行(可选)。
        // 排版用 DrawText 多次绘制(每行一次),底色用 DrawRectangle 兜底。
        // 绘制位置以 anchor (端口圆点屏幕坐标) 为锚,自动避免出画布。
        private static void DrawPortTooltip(IDrawingSink draw, Port port, HevoPoint anchor)
        {
            string head = port.IsInput ? "◀ Input  " : "▶ Output ";
            string line1 = head + port.Name;
            string line2 = port.DataTypeName;
            string? line3 = string.IsNullOrEmpty(port.Description) ? null : port.Description;

            const float pad = 6f, lineH = 14f, fontSize = 11f;
            // 估算宽度:取最长行 × 平均字宽
            int maxLen = Math.Max(Math.Max(line1.Length, line2.Length), line3?.Length ?? 0);
            float w = pad * 2 + maxLen * 6.4f;
            float h = pad * 2 + (line3 != null ? lineH * 3 : lineH * 2);

            // 锚点放在端口右上方;input 端口往左挪
            float ox = port.IsInput ? anchor.X - w - 12f : anchor.X + 12f;
            float oy = anchor.Y - h - 8f;
            if (oy < 4f) oy = anchor.Y + 12f; // 顶部不够就往下放

            var rect = new HevoRect(ox, oy, w, h);
            draw.DrawRoundedRectangle(GraphPalette.TooltipBg, GraphPalette.TooltipBorder, rect, 4f, 4f);

            float tx = rect.X + pad;
            float ty = rect.Y + pad;
            var headBrush = GraphPalette.PortBrushFor(
                EdgeDataKindClassifier.Classify(port.DataTypeName, EdgeKind.DataPort, null, null));
            draw.DrawText(line1, GraphPalette.Typeface, headBrush, fontSize,
                new HevoPoint(tx, ty), TextAlignX.Left, TextAlignY.Top);
            draw.DrawText(line2, GraphPalette.Typeface, GraphPalette.TooltipDimText, fontSize - 1f,
                new HevoPoint(tx, ty + lineH), TextAlignX.Left, TextAlignY.Top);
            if (line3 != null)
            {
                draw.DrawText(line3, GraphPalette.Typeface, GraphPalette.TooltipText, fontSize - 1f,
                    new HevoPoint(tx, ty + lineH * 2), TextAlignX.Left, TextAlignY.Top);
            }
        }
    }
}
