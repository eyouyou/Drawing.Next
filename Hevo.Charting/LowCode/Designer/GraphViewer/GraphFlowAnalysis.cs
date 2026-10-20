namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 连线承载的数据语义。画布按它给连线上色,让人一眼分清"这根线流的是列数据还是量程 / 命中 / 视口"。
    /// </summary>
    public enum EdgeDataKind
    {
        /// <summary>数值列(<c>ReadOnlyMemory&lt;T&gt;</c> 等按 bar 对齐的向量)。</summary>
        Column,
        /// <summary>量程(<c>RealRange</c>),典型是 AutoScale → Axis / Series。</summary>
        Range,
        /// <summary>光标命中状态(<c>PointerHitState</c>),交互 → Crosshair / Tooltip。</summary>
        HitState,
        /// <summary>进出 Viewport 节点的连线(逻辑长度 / 可见范围)。</summary>
        Viewport,
        /// <summary>整条 snapshot 订阅或 Cascade 推送(DS 之间的 push 路径)。</summary>
        Stream,
        /// <summary>其它标量 / 对象。</summary>
        Other,
    }

    /// <summary>
    /// 连线语义判定。只看端口声明的 T(<see cref="Port.DataTypeName"/>)和两端节点身份,
    /// 不依赖具体 Feature 类型,业务自定义 Feature 无需登记就能拿到正确颜色。
    /// </summary>
    public static class EdgeDataKindClassifier
    {
        public static EdgeDataKind Classify(GraphState state, Edge edge)
        {
            var from = state.FindNode(edge.FromNodeId);
            var to = state.FindNode(edge.ToNodeId);
            var fp = from?.FindPort(edge.FromPortId, isInput: false);
            var tp = to?.FindPort(edge.ToPortId, isInput: true);
            // 输出端口的 T 是数据的真实形态;输入端可能是扇入数组 T[],只在输出端缺失时兜底。
            var typeName = fp?.DataTypeName ?? tp?.DataTypeName;
            return Classify(typeName, edge.Kind, from?.Kind, to?.Kind);
        }

        public static EdgeDataKind Classify(string? dataTypeName, EdgeKind edgeKind, NodeKind? fromKind, NodeKind? toKind)
        {
            if (edgeKind != EdgeKind.DataPort) return EdgeDataKind.Stream;
            // 视口优先于类型:ActiveRange 也是 RealRange,但读者关心的是"这根线来自视口协议"。
            if (fromKind == NodeKind.Viewport || toKind == NodeKind.Viewport) return EdgeDataKind.Viewport;
            if (string.IsNullOrEmpty(dataTypeName)) return EdgeDataKind.Other;

            var t = dataTypeName!;
            if (t.StartsWith("DataSnapshot", StringComparison.Ordinal) || t == "TContext") return EdgeDataKind.Stream;
            if (t.StartsWith("ReadOnlyMemory", StringComparison.Ordinal)
                || t.StartsWith("Memory<", StringComparison.Ordinal)
                || t.EndsWith("[]", StringComparison.Ordinal)) return EdgeDataKind.Column;
            if (t.IndexOf("HitState", StringComparison.Ordinal) >= 0) return EdgeDataKind.HitState;
            if (t.IndexOf("Range", StringComparison.Ordinal) >= 0) return EdgeDataKind.Range;
            return EdgeDataKind.Other;
        }

        public static string DisplayName(EdgeDataKind kind) => kind switch
        {
            EdgeDataKind.Column   => "数值列",
            EdgeDataKind.Range    => "量程 RealRange",
            EdgeDataKind.HitState => "命中状态",
            EdgeDataKind.Viewport => "视口",
            EdgeDataKind.Stream   => "数据流 / Cascade",
            _                     => "其它标量",
        };
    }

    /// <summary>
    /// 画布数据流分析:环路回边识别 + 选中节点的上下游。
    /// 这里的回边只看数据依赖,不看泳道 —— 布局里"Series 连回 Environment"算回边是为了排版,
    /// 但 AutoScale 收 Series 的列在语义上仍是 Series 的下游,高亮时要沿它走。
    /// </summary>
    public static class GraphFlowAnalysis
    {
        /// <summary>
        /// 找出让图变成 DAG 需要摘掉的节点对 (from, to)。
        /// <para>
        /// 用 Eades 贪心反馈弧集,按节点对之间的连线根数加权:同一对节点之间连线越多,越不该被当成回边。
        /// 典型场景 AutoScale ⇄ PlotFeature —— Plot 有 3 根列流进 AutoScale,AutoScale 只回 1 根量程,
        /// 摘掉那 1 根量程,3 根列就能正向排开;纯 DFS 回边取决于遍历顺序,可能反过来摘掉 3 根。
        /// </para>
        /// </summary>
        public static HashSet<(string From, string To)> FindBackEdges(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
        {
            var order = ComputeFlowOrder(nodes, edges);
            var rank = new Dictionary<string, int>(order.Count);
            for (int i = 0; i < order.Count; i++) rank[order[i]] = i;

            var back = new HashSet<(string, string)>();
            foreach (var e in edges)
            {
                if (!rank.TryGetValue(e.FromNodeId, out var rf) || !rank.TryGetValue(e.ToNodeId, out var rt)) continue;
                if (rf >= rt) back.Add((e.FromNodeId, e.ToNodeId));
            }
            return back;
        }

        /// <summary>
        /// 节点的数据流线性序:正向边尽量从前指向后。节点顺序先按 <paramref name="nodes"/> 原序,保证结果确定。
        /// </summary>
        public static List<string> ComputeFlowOrder(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
        {
            var alive = new HashSet<string>();
            var ids = new List<string>(nodes.Count);
            foreach (var n in nodes) if (alive.Add(n.Id)) ids.Add(n.Id);

            // 节点对 → 连线根数;自环不参与排序。
            var outW = new Dictionary<string, Dictionary<string, int>>();
            var inW = new Dictionary<string, Dictionary<string, int>>();
            foreach (var id in ids) { outW[id] = new Dictionary<string, int>(); inW[id] = new Dictionary<string, int>(); }
            foreach (var e in edges)
            {
                if (e.FromNodeId == e.ToNodeId) continue;
                if (!alive.Contains(e.FromNodeId) || !alive.Contains(e.ToNodeId)) continue;
                outW[e.FromNodeId][e.ToNodeId] = outW[e.FromNodeId].GetValueOrDefault(e.ToNodeId) + 1;
                inW[e.ToNodeId][e.FromNodeId] = inW[e.ToNodeId].GetValueOrDefault(e.FromNodeId) + 1;
            }

            int Sum(Dictionary<string, int> d)
            {
                int s = 0;
                foreach (var kv in d) if (alive.Contains(kv.Key)) s += kv.Value;
                return s;
            }

            var head = new List<string>();
            var tail = new List<string>();
            while (alive.Count > 0)
            {
                bool progressed = true;
                while (progressed)
                {
                    progressed = false;
                    foreach (var id in ids)
                    {
                        if (!alive.Contains(id)) continue;
                        if (Sum(outW[id]) == 0) { tail.Add(id); alive.Remove(id); progressed = true; }
                        else if (Sum(inW[id]) == 0) { head.Add(id); alive.Remove(id); progressed = true; }
                    }
                }
                if (alive.Count == 0) break;

                // 只剩环:挑"出 - 入"最大的节点放前面,它的入边成为回边,被摘的根数最少。
                string? best = null;
                int bestDelta = int.MinValue;
                foreach (var id in ids)
                {
                    if (!alive.Contains(id)) continue;
                    int delta = Sum(outW[id]) - Sum(inW[id]);
                    if (delta > bestDelta) { bestDelta = delta; best = id; }
                }
                head.Add(best!);
                alive.Remove(best!);
            }

            tail.Reverse();
            head.AddRange(tail);
            return head;
        }

        /// <summary>
        /// 选中集合的完整上游(数据从哪来)与下游(数据到哪去),都包含种子自身。
        /// 沿摘掉回边后的图走,否则环里的节点会同时出现在上下游,高亮失去方向感。
        /// </summary>
        public static (HashSet<string> Upstream, HashSet<string> Downstream) ComputeLineage(
            IReadOnlyList<Edge> edges, IEnumerable<string> seeds, ISet<(string From, string To)> backEdges)
        {
            var pred = new Dictionary<string, List<string>>();
            var succ = new Dictionary<string, List<string>>();
            foreach (var e in edges)
            {
                if (backEdges.Contains((e.FromNodeId, e.ToNodeId))) continue;
                Add(succ, e.FromNodeId, e.ToNodeId);
                Add(pred, e.ToNodeId, e.FromNodeId);
            }

            var up = new HashSet<string>();
            var down = new HashSet<string>();
            foreach (var s in seeds)
            {
                Walk(s, pred, up);
                Walk(s, succ, down);
            }
            return (up, down);

            static void Add(Dictionary<string, List<string>> map, string k, string v)
            {
                if (!map.TryGetValue(k, out var list)) { list = new List<string>(); map[k] = list; }
                list.Add(v);
            }

            static void Walk(string start, Dictionary<string, List<string>> next, HashSet<string> seen)
            {
                var stack = new Stack<string>();
                seen.Add(start);
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var v = stack.Pop();
                    if (!next.TryGetValue(v, out var ns)) continue;
                    foreach (var w in ns) if (seen.Add(w)) stack.Push(w);
                }
            }
        }
    }
}
