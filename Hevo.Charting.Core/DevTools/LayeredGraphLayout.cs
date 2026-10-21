using System;
using System.Collections.Generic;
using System.Linq;

namespace Hevo.Charting.DevTools
{
    /// <summary>
    /// 从左往右的分层布局,思路参照 ELK Layered(Sugiyama 框架)的五个阶段:断环 → 分层 → 长边拆虚拟节点并减少交叉 →
    /// 纵向定位 → 正交走线。拓扑监控和蓝图编辑器这类"数据从左流到右"的图共用。
    /// <para>
    /// 跟 ELK 的差异(图在百节点以内,够用即可):分层用最长路径 + 源头收紧,不做网络单纯形;
    /// 纵向定位用加权有序回归(长边虚拟节点权重高,让长边拉直),不做 Brandes-Köpf。
    /// 只参照算法思路,不搬 ELK 代码(EPL-2.0)。
    /// </para>
    /// <para>
    /// 蓝图编辑器额外用到两项(拓扑监控不设就保持原行为):
    /// 分区(<see cref="Node.Partition"/>,参照 ELK partitioning)把节点按泳道分成连续的列段;
    /// 端口位置(<see cref="Edge.SrcPortY"/> / <see cref="Edge.TgtPortY"/>)让连线从固定端口出入,重心排序也按端口算。
    /// </para>
    /// </summary>
    internal static class LayeredGraphLayout
    {
        internal readonly struct Pt
        {
            public readonly double X, Y;
            public Pt(double x, double y) { X = x; Y = y; }
        }

        internal sealed class Node
        {
            public string Id = "";
            public double Height = 30;
            /// <summary>固定在第 0 层(数据源这类"起点"),不随后继收紧右移。</summary>
            public bool PinFirstLayer;
            /// <summary>
            /// 分区序号。任一节点 ≥ 0 时启用分区:分区 i 的节点全部在分区 j(i &lt; j)的左边,每个分区占一段连续的列;
            /// 从高分区连回低分区的边按回边处理;没有连线的节点放在本分区首列。启用后 &lt; 0 的节点按分区 0 处理。
            /// </summary>
            public int Partition = -1;
        }

        /// <summary>带端口位置的连线。端口位置是相对节点顶边的纵向偏移;null = 由布局沿节点边均匀分布。</summary>
        internal sealed class Edge
        {
            public string Src = "";
            public string Tgt = "";
            public double? SrcPortY;
            public double? TgtPortY;
        }

        internal sealed class Options
        {
            public double Left;
            public double Top;
            public double Width = 1000;
            public double Height = 600;
            public double MaxNodeWidth = 150;
            /// <summary>列再多也不把节点压到比这更窄;总宽放不下时内容超出 Width,由调用方滚动查看。</summary>
            public double MinNodeWidth = 110;
            /// <summary>相邻两列节点之间至少留给连线竖段的宽度。</summary>
            public double MinColumnGap = 36;
            public double NodeSpacing = 12;
            public double DummySpacing = 8;
            public double MaxVerticalExpand = 3;
            public int CrossingSweeps = 8;
            public int PlacementSweeps = 6;
            /// <summary>
            /// true:回边不穿过中间各列,而是从源节点右侧出、沿全部内容下方的走廊绕回目标节点左侧。
            /// 端口固定在节点左右两侧(蓝图)时用它,否则回边会从输出端口的反方向出线。
            /// 进同一个目标端口的回边合成一根总线(共用走廊和最后一段竖线),不再是一捆平行线。
            /// </summary>
            public bool RouteBackEdgesBelow;
            /// <summary>
            /// 跟 <see cref="RouteBackEdgesBelow"/> 一样从源右侧出、目标左侧入、同端口合成总线,但走廊就近找:
            /// 只看源列到目标列之间(含两端)的节点,在它们整体的上方或下方选竖向绕行更短的一侧,
            /// 不再统一绕到全图最底部。设了它就不必再设 RouteBackEdgesBelow。
            /// </summary>
            public bool RouteBackEdgesNear;
            /// <summary>回边走廊离节点的距离,以及相邻走廊之间的间距。</summary>
            public double BackEdgeCorridorGap = 28;
            public double BackEdgeSpacing = 10;
            /// <summary>
            /// true:分区内断环按两节点之间的连线根数加权(Eades 贪心反馈弧集),摘掉根数最少的那一侧;
            /// false:从源头 DFS,回边取决于遍历顺序。蓝图里同一对节点常有多根线(Plot 三根列进 AutoScale、
            /// AutoScale 一根量程回 Plot),DFS 可能把三根都反转成回边。
            /// </summary>
            public bool WeightedCycleBreaking;
            /// <summary>
            /// true:同一个源端口出发的线(扇出)、进同一个目标端口的线(扇入)在每道列缝里共用一根竖线(主干),
            /// 而不是各占一条轨道并排走。同一端口的线流的是同一份数据,画成一根主干 + 分叉更像电路图,
            /// 并排的近距离平行线反而看着像重合。只对给了端口位置的连线生效,拓扑监控不设就保持原样。
            /// </summary>
            public bool MergeSamePortTracks;
        }

        internal sealed class Result
        {
            public readonly Dictionary<string, int> LayerOf = new();
            /// <summary>有连线的节点占的层数;没有任何连线的节点统一放在第 FlowLayerCount 层(未启用分区时)。</summary>
            public int FlowLayerCount;
            public bool HasUnlinkedLayer;
            public int LayerCount => FlowLayerCount + (HasUnlinkedLayer ? 1 : 0);
            public double NodeWidth;
            public double ColumnWidth;
            /// <summary>内容实际占的宽高(从 Options.Left / Top 算起),可能大于可用区域。</summary>
            public double ContentWidth;
            public double ContentHeight;
            public readonly Dictionary<string, (double X, double Y, double W, double H)> Boxes = new();
            /// <summary>按原始方向(Src → Tgt)给出的折线;回边的折线也是从 Src 画到 Tgt。同一对节点多根线时取第一根。</summary>
            public readonly Dictionary<(string Src, string Tgt), List<Pt>> Routes = new();
            /// <summary>跟输入连线一一对应的折线;自环、端点未知的连线为 null。</summary>
            public readonly List<List<Pt>?> EdgeRoutes = new();
            /// <summary>断环时反转的边(原始方向)。</summary>
            public readonly HashSet<(string Src, string Tgt)> ReversedEdges = new();
            /// <summary>去掉回边后的后继 / 前驱表(原始节点之间,不含虚拟节点)。</summary>
            public readonly Dictionary<string, List<string>> Succ = new();
            public readonly Dictionary<string, List<string>> Pred = new();
            /// <summary>启用分区时每个分区占的列段 [First, Last]。</summary>
            public readonly Dictionary<int, (int First, int Last)> PartitionLayers = new();
        }

        private sealed class V
        {
            public string Id = "";
            public bool IsDummy;
            public double H;
            public int Layer;
            public int Order;
            public double Y; // 中心
            public readonly List<Adj> Up = new();   // 上一层邻居(DAG 方向的前驱)
            public readonly List<Adj> Down = new(); // 下一层邻居
        }

        /// <summary>相邻层的一条连接。偏移都相对各自节点中心,虚拟节点为 0。</summary>
        private readonly struct Adj
        {
            public readonly V N;
            public readonly double NOff;
            public readonly double SelfOff;
            public Adj(V n, double nOff, double selfOff) { N = n; NOff = nOff; SelfOff = selfOff; }
        }

        public static Result Compute(IReadOnlyList<Node> nodes, IEnumerable<(string Src, string Tgt)> edges, Options opt)
        {
            // 无端口信息时同一对节点只留一根线,跟拓扑监控"节点对即一条边"的语义一致。
            var list = new List<Edge>();
            var seen = new HashSet<(string, string)>();
            foreach (var e in edges)
                if (seen.Add((e.Src, e.Tgt))) list.Add(new Edge { Src = e.Src, Tgt = e.Tgt });
            return Compute(nodes, list, opt);
        }

        public static Result Compute(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges, Options opt)
        {
            var res = new Result();
            var byId = new Dictionary<string, Node>();
            foreach (var n in nodes) if (!byId.ContainsKey(n.Id)) byId[n.Id] = n;

            bool partitioned = nodes.Any(n => n.Partition >= 0);
            int PartOf(string id) => partitioned ? Math.Max(0, byId[id].Partition) : 0;

            // ── 0. 规整输入:去自环、去未知节点;保留同一对节点之间的多根线(端口不同) ──
            var valid = new List<int>();
            for (int i = 0; i < edges.Count; i++)
            {
                res.EdgeRoutes.Add(null);
                var e = edges[i];
                if (e.Src == e.Tgt || !byId.ContainsKey(e.Src) || !byId.ContainsKey(e.Tgt)) continue;
                valid.Add(i);
            }
            var pairs = new List<(string Src, string Tgt)>();
            var pairSeen = new HashSet<(string, string)>();
            foreach (var i in valid) if (pairSeen.Add((edges[i].Src, edges[i].Tgt))) pairs.Add((edges[i].Src, edges[i].Tgt));

            var linked = new HashSet<string>();
            foreach (var p in pairs) { linked.Add(p.Src); linked.Add(p.Tgt); }
            var order = nodes.Select(n => n.Id).Where(linked.Contains).Distinct().ToList();

            // ── 1. 断环:跨分区的边按分区先后定方向;分区内从没有入边的节点做 DFS,回边反转 ──
            var outAll = new Dictionary<string, List<string>>();
            var hasIn = new HashSet<string>();
            foreach (var p in pairs)
            {
                int ps = PartOf(p.Src), pt = PartOf(p.Tgt);
                if (ps > pt) { res.ReversedEdges.Add(p); continue; }
                if (ps < pt) continue;
                if (!outAll.TryGetValue(p.Src, out var l)) outAll[p.Src] = l = new List<string>();
                l.Add(p.Tgt);
                hasIn.Add(p.Tgt);
            }
            var visited = new HashSet<string>();
            var onPath = new HashSet<string>();
            void Dfs(string v)
            {
                visited.Add(v);
                onPath.Add(v);
                if (outAll.TryGetValue(v, out var outs))
                    foreach (var w in outs)
                    {
                        if (onPath.Contains(w)) res.ReversedEdges.Add((v, w));
                        else if (!visited.Contains(w)) Dfs(w);
                    }
                onPath.Remove(v);
            }
            if (opt.WeightedCycleBreaking)
            {
                var weight = new Dictionary<(string, string), int>();
                foreach (var i in valid)
                {
                    var k = (edges[i].Src, edges[i].Tgt);
                    if (PartOf(k.Item1) == PartOf(k.Item2)) weight[k] = (weight.TryGetValue(k, out var c) ? c : 0) + 1;
                }
                var rank = WeightedOrder(order, weight);
                foreach (var k in weight.Keys) if (rank[k.Item1] > rank[k.Item2]) res.ReversedEdges.Add(k);
            }
            else
            {
                foreach (var id in order) if (!hasIn.Contains(id) && !visited.Contains(id)) Dfs(id);
                foreach (var id in order) if (!visited.Contains(id)) Dfs(id);
            }

            var dSucc = new Dictionary<string, List<string>>();
            var dPred = new Dictionary<string, List<string>>();
            foreach (var p in pairs)
            {
                if (res.ReversedEdges.Contains(p)) { Add(dSucc, p.Tgt, p.Src); Add(dPred, p.Src, p.Tgt); }
                else
                {
                    Add(dSucc, p.Src, p.Tgt); Add(dPred, p.Tgt, p.Src);
                    Add(res.Succ, p.Src, p.Tgt); Add(res.Pred, p.Tgt, p.Src);
                }
            }

            // ── 2. 分层:最长路径(源头在 0 层),再把非固定源头收紧到紧挨后继;分区依次占连续列段 ──
            var indeg = order.ToDictionary(id => id, id => dPred.TryGetValue(id, out var p) ? p.Count : 0);
            var queue = new Queue<string>(order.Where(id => indeg[id] == 0));
            var topo = new List<string>();
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                topo.Add(v);
                if (!dSucc.TryGetValue(v, out var outs)) continue;
                foreach (var w in outs) if (--indeg[w] == 0) queue.Enqueue(w);
            }

            var layer = new Dictionary<string, int>();
            var partIds = partitioned
                ? nodes.Select(n => PartOf(n.Id)).Distinct().OrderBy(p => p).ToList()
                : new List<int> { 0 };
            int lower = 0;
            foreach (var part in partIds)
            {
                var inPart = topo.Where(id => PartOf(id) == part).ToList();
                // 分区里没有连线的节点(纯配置类)单独占分区首列,有连线的从下一列排起。
                // 放末列的话它们会夹在数据流中间:回边要横穿这一列,走廊只能绕到这摞节点下面去。
                var unlinkedInPart = partitioned
                    ? nodes.Where(n => !linked.Contains(n.Id) && PartOf(n.Id) == part).Select(n => n.Id).Distinct().ToList()
                    : new List<string>();
                int flowLower = unlinkedInPart.Count > 0 && inPart.Count > 0 ? lower + 1 : lower;
                foreach (var v in inPart)
                {
                    int l = flowLower;
                    if (dPred.TryGetValue(v, out var ps)) foreach (var u in ps) l = Math.Max(l, layer[u] + 1);
                    layer[v] = l;
                }
                int upper = inPart.Count == 0 ? lower : inPart.Max(id => layer[id]);
                // 源头放在分区首列会拉出很长的边;除非标了固定,挪到最近后继的前一层(不出本分区的列段)
                for (int i = inPart.Count - 1; i >= 0; i--)
                {
                    var v = inPart[i];
                    if (dPred.ContainsKey(v) || byId[v].PinFirstLayer || !dSucc.TryGetValue(v, out var outs)) continue;
                    int want = outs.Min(w => layer.TryGetValue(w, out var lw) ? lw : upper + 1) - 1;
                    layer[v] = Math.Max(flowLower, Math.Min(upper, want));
                }

                if (partitioned)
                {
                    foreach (var id in unlinkedInPart) res.LayerOf[id] = lower;
                    if (inPart.Count > 0 || unlinkedInPart.Count > 0)
                    {
                        res.PartitionLayers[part] = (lower, upper);
                        lower = upper + 1;
                    }
                }
            }

            foreach (var kv in layer) res.LayerOf[kv.Key] = kv.Value;
            if (partitioned)
            {
                res.FlowLayerCount = res.LayerOf.Count == 0 ? 0 : res.LayerOf.Values.Max() + 1;
            }
            else
            {
                res.FlowLayerCount = layer.Count == 0 ? 0 : layer.Values.Max() + 1;
                foreach (var n in nodes)
                {
                    if (res.LayerOf.ContainsKey(n.Id)) continue;
                    res.LayerOf[n.Id] = res.FlowLayerCount;
                    res.HasUnlinkedLayer = true;
                }
            }

            int layerCount = res.LayerCount;
            if (layerCount == 0) return res;

            // ── 3a. 长边拆虚拟节点,每条边只连相邻两层 ──
            var vs = new Dictionary<string, V>();
            foreach (var n in nodes)
                if (!vs.ContainsKey(n.Id))
                    vs[n.Id] = new V { Id = n.Id, H = n.Height, Layer = res.LayerOf[n.Id] };

            double CenterOff(string id, double? portY) => portY.HasValue ? portY.Value - byId[id].Height / 2 : 0;

            var chains = new Dictionary<int, List<V>>();
            var backBelow = new List<int>();
            int dummyNo = 0;
            foreach (var i in valid)
            {
                var e = edges[i];
                bool reversed = res.ReversedEdges.Contains((e.Src, e.Tgt));
                if (reversed && (opt.RouteBackEdgesBelow || opt.RouteBackEdgesNear)) { backBelow.Add(i); continue; }
                // DAG 方向:U → W;回边反过来,端口偏移跟着节点走
                string u = reversed ? e.Tgt : e.Src, w = reversed ? e.Src : e.Tgt;
                double uOff = reversed ? CenterOff(e.Tgt, e.TgtPortY) : CenterOff(e.Src, e.SrcPortY);
                double wOff = reversed ? CenterOff(e.Src, e.SrcPortY) : CenterOff(e.Tgt, e.TgtPortY);
                var chain = new List<V> { vs[u] };
                var prev = vs[u];
                double prevOff = uOff;
                for (int l = prev.Layer + 1; l < vs[w].Layer; l++)
                {
                    var dv = new V { Id = "~d" + (dummyNo++), IsDummy = true, H = 0, Layer = l };
                    vs[dv.Id] = dv;
                    Link(prev, prevOff, dv, 0);
                    chain.Add(dv);
                    prev = dv;
                    prevOff = 0;
                }
                Link(prev, prevOff, vs[w], wOff);
                chain.Add(vs[w]);
                chains[i] = chain;
            }

            var layers = new List<List<V>>();
            for (int l = 0; l < layerCount; l++) layers.Add(new List<V>());
            // 初始顺序:真节点按输入顺序,虚拟节点按创建顺序(跟着各自的源头)
            foreach (var n in nodes) if (vs.TryGetValue(n.Id, out var v) && !layers[v.Layer].Contains(v)) layers[v.Layer].Add(v);
            foreach (var v in vs.Values) if (v.IsDummy) layers[v.Layer].Add(v);
            Renumber(layers);

            // ── 3b. 减少交叉:上下来回扫,按邻居端口位置的重心排序,保留交叉最少的那一版 ──
            var best = layers.Select(l => l.ToList()).ToList();
            int bestCross = CountCrossings(layers);
            for (int it = 0; it < opt.CrossingSweeps && bestCross > 0; it++)
            {
                bool down = it % 2 == 0;
                if (down) for (int l = 1; l < layerCount; l++) SortByBarycenter(layers[l], up: true);
                else for (int l = layerCount - 2; l >= 0; l--) SortByBarycenter(layers[l], up: false);
                int c = CountCrossings(layers);
                if (c < bestCross) { bestCross = c; best = layers.Select(x => x.ToList()).ToList(); }
            }
            layers = best;
            Renumber(layers);

            // ── 4. 纵向定位:对每层做加权有序回归,目标是让两端端口对齐;虚拟节点权重高 ──
            foreach (var lay in layers)
            {
                double y = 0;
                for (int i = 0; i < lay.Count; i++)
                {
                    if (i > 0) y += Sep(lay[i - 1], lay[i], opt);
                    lay[i].Y = y;
                }
            }
            for (int it = 0; it < opt.PlacementSweeps; it++)
            {
                bool down = it % 2 == 0;
                if (down) for (int l = 1; l < layerCount; l++) PlaceLayer(layers[l], up: true, opt);
                else for (int l = layerCount - 2; l >= 0; l--) PlaceLayer(layers[l], up: false, opt);
            }

            // 归一到可用区域:有富余就拉开(最多 MaxVerticalExpand 倍),免得整张图挤成中间一条带;
            // 放不下时不压缩——压缩会让节点叠在一起、长边压到节点上——内容超出部分由调用方滚动。
            double minTop = double.MaxValue, maxBottom = double.MinValue;
            foreach (var v in vs.Values) { minTop = Math.Min(minTop, v.Y - v.H / 2); maxBottom = Math.Max(maxBottom, v.Y + v.H / 2); }
            double span = Math.Max(1, maxBottom - minTop);
            double scale = Math.Max(1.0, Math.Min(opt.Height / span, opt.MaxVerticalExpand));
            double offset = opt.Top + Math.Max(0, (opt.Height - span * scale) / 2);
            foreach (var v in vs.Values) v.Y = offset + (v.Y - v.H / 2 - minTop) * scale + v.H / 2;
            res.ContentHeight = Math.Max(opt.Height, span * scale);

            // ── 横向:等宽列;列太多时保底列宽,宁可横向超出也不让相邻两列的节点重叠 ──
            double colW = Math.Max(opt.Width / layerCount, opt.MinNodeWidth + opt.MinColumnGap);
            res.ColumnWidth = colW;
            res.ContentWidth = colW * layerCount;
            res.NodeWidth = Math.Max(opt.MinNodeWidth, Math.Min(opt.MaxNodeWidth, colW - opt.MinColumnGap));
            double ColCenter(int l) => opt.Left + (l + 0.5) * colW;
            foreach (var v in vs.Values)
            {
                if (v.IsDummy) continue;
                res.Boxes[v.Id] = (ColCenter(v.Layer) - res.NodeWidth / 2, v.Y - v.H / 2, res.NodeWidth, v.H);
            }

            // ── 5. 正交走线:出端在右边、入端在左边;层间竖段分配不重叠的轨道 ──
            RouteOrthogonal(edges, chains, backBelow, vs, res, opt, ColCenter, layerCount);
            for (int i = 0; i < edges.Count; i++)
            {
                var r = res.EdgeRoutes[i];
                var key = (edges[i].Src, edges[i].Tgt);
                if (r != null && !res.Routes.ContainsKey(key)) res.Routes[key] = r;
            }
            return res;
        }

        // Eades 贪心:反复摘汇点放队尾、摘源头放队首;只剩环时挑"出权 - 入权"最大的放队首,它的入边成为回边。
        private static Dictionary<string, int> WeightedOrder(List<string> ids, Dictionary<(string, string), int> weight)
        {
            var alive = new HashSet<string>(ids);
            var outW = ids.ToDictionary(id => id, id => new Dictionary<string, int>());
            var inW = ids.ToDictionary(id => id, id => new Dictionary<string, int>());
            foreach (var kv in weight)
            {
                outW[kv.Key.Item1][kv.Key.Item2] = kv.Value;
                inW[kv.Key.Item2][kv.Key.Item1] = kv.Value;
            }
            int Sum(Dictionary<string, int> d) { int s = 0; foreach (var kv in d) if (alive.Contains(kv.Key)) s += kv.Value; return s; }

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
                string? best = null;
                int bestDelta = int.MinValue;
                foreach (var id in ids)
                {
                    if (!alive.Contains(id)) continue;
                    int d = Sum(outW[id]) - Sum(inW[id]);
                    if (d > bestDelta) { bestDelta = d; best = id; }
                }
                head.Add(best!);
                alive.Remove(best!);
            }
            tail.Reverse();
            head.AddRange(tail);
            var rank = new Dictionary<string, int>();
            for (int i = 0; i < head.Count; i++) rank[head[i]] = i;
            return rank;
        }

        private static void Add(Dictionary<string, List<string>> map, string k, string v)
        {
            if (!map.TryGetValue(k, out var l)) map[k] = l = new List<string>();
            if (!l.Contains(v)) l.Add(v);
        }

        private static void Link(V a, double aOff, V b, double bOff)
        {
            a.Down.Add(new Adj(b, bOff, aOff));
            b.Up.Add(new Adj(a, aOff, bOff));
        }

        private static void Renumber(List<List<V>> layers)
        {
            foreach (var l in layers) for (int i = 0; i < l.Count; i++) l[i].Order = i;
        }

        // 端口在节点内的相对位置折成序号的小数部分:同一节点上靠上的端口排在前面,但不越过相邻节点。
        private static double Frac(V v, double off) => v.H > 0 ? off / v.H * 0.9 : 0;

        private static void SortByBarycenter(List<V> lay, bool up)
        {
            var key = new Dictionary<V, double>(lay.Count);
            foreach (var v in lay)
            {
                var ns = up ? v.Up : v.Down;
                key[v] = ns.Count > 0 ? ns.Average(a => a.N.Order + Frac(a.N, a.NOff)) : v.Order; // 没邻居的留在原位
            }
            var sorted = lay.Select((v, i) => (v, i)).OrderBy(t => key[t.v]).ThenBy(t => t.i).Select(t => t.v).ToList();
            lay.Clear();
            lay.AddRange(sorted);
            for (int i = 0; i < lay.Count; i++) lay[i].Order = i;
        }

        private static int CountCrossings(List<List<V>> layers)
        {
            int total = 0;
            for (int l = 0; l + 1 < layers.Count; l++)
            {
                var segs = new List<(double A, double B)>();
                foreach (var v in layers[l])
                    foreach (var a in v.Down)
                        segs.Add((v.Order + Frac(v, a.SelfOff), a.N.Order + Frac(a.N, a.NOff)));
                for (int i = 0; i < segs.Count; i++)
                    for (int j = i + 1; j < segs.Count; j++)
                        if ((segs[i].A - segs[j].A) * (segs[i].B - segs[j].B) < 0) total++;
            }
            return total;
        }

        private static double Sep(V a, V b, Options opt)
            => a.H / 2 + b.H / 2 + (a.IsDummy && b.IsDummy ? opt.DummySpacing : opt.NodeSpacing);

        // 有序回归:在保持层内顺序和最小间距的前提下,让 Σ w·(y - 目标)² 最小(合并相邻违例块)
        private static void PlaceLayer(List<V> lay, bool up, Options opt)
        {
            int n = lay.Count;
            if (n == 0) return;
            var target = new double[n];
            var weight = new double[n];
            for (int i = 0; i < n; i++)
            {
                var v = lay[i];
                var ns = up ? v.Up : v.Down;
                if (ns.Count == 0) { target[i] = v.Y; weight[i] = 0.2; continue; }
                // 目标:让本节点的端口跟邻居的端口同高
                var ys = ns.Select(a => a.N.Y + a.NOff - a.SelfOff).OrderBy(x => x).ToList();
                target[i] = ys.Count % 2 == 1 ? ys[ys.Count / 2] : (ys[ys.Count / 2 - 1] + ys[ys.Count / 2]) / 2;
                weight[i] = v.IsDummy ? 8 : ns.Count;
            }
            // 把间距约束吸收进偏移:y_i = z_i + off_i,约束变成 z 单调不减
            var off = new double[n];
            for (int i = 1; i < n; i++) off[i] = off[i - 1] + Sep(lay[i - 1], lay[i], opt);

            var blocks = new List<(double Sum, double W, int Count)>();
            for (int i = 0; i < n; i++)
            {
                blocks.Add((weight[i] * (target[i] - off[i]), weight[i], 1));
                while (blocks.Count >= 2)
                {
                    var b = blocks[blocks.Count - 1];
                    var a = blocks[blocks.Count - 2];
                    if (a.Sum / a.W <= b.Sum / b.W) break;
                    blocks.RemoveAt(blocks.Count - 1);
                    blocks[blocks.Count - 1] = (a.Sum + b.Sum, a.W + b.W, a.Count + b.Count);
                }
            }
            int k = 0;
            foreach (var b in blocks)
            {
                double z = b.Sum / b.W;
                for (int j = 0; j < b.Count; j++, k++) lay[k].Y = z + off[k];
            }
        }

        private static void RouteOrthogonal(
            IReadOnlyList<Edge> edges, Dictionary<int, List<V>> chains, List<int> backBelow,
            Dictionary<string, V> vs, Result res, Options opt, Func<int, double> colCenter, int layerCount)
        {
            double halfW = res.NodeWidth / 2;

            // 缝隙 g 是第 g 列和第 g+1 列之间;-1 是首列左边,layerCount-1 是末列右边(回边从那里拐)。
            (double L, double R) GapBounds(int g)
            {
                double l = g < 0 ? colCenter(0) - halfW - opt.MinColumnGap : colCenter(g) + halfW;
                double r = g >= layerCount - 1 ? colCenter(layerCount - 1) + halfW + opt.MinColumnGap : colCenter(g + 1) - halfW;
                return (l, r);
            }

            // 每个真节点的出 / 入连接点:给了端口位置就用端口;没给的按另一端的高度排序后沿边均匀分布
            var outSlots = new Dictionary<V, List<int>>();
            var inSlots = new Dictionary<V, List<int>>();
            var outY = new Dictionary<int, double>();
            var inY = new Dictionary<int, double>();
            foreach (var kv in chains)
            {
                var c = kv.Value;
                var e = edges[kv.Key];
                bool reversed = res.ReversedEdges.Contains((e.Src, e.Tgt));
                double? headPort = reversed ? e.TgtPortY : e.SrcPortY;
                double? tailPort = reversed ? e.SrcPortY : e.TgtPortY;
                var head = c[0];
                var tail = c[c.Count - 1];
                if (headPort.HasValue) outY[kv.Key] = head.Y - head.H / 2 + headPort.Value; else AddSlot(outSlots, head, kv.Key);
                if (tailPort.HasValue) inY[kv.Key] = tail.Y - tail.H / 2 + tailPort.Value; else AddSlot(inSlots, tail, kv.Key);
            }
            foreach (var kv in outSlots) Distribute(kv.Key, kv.Value, i => chains[i][1].Y, outY);
            foreach (var kv in inSlots) Distribute(kv.Key, kv.Value, i => chains[i][chains[i].Count - 2].Y, inY);

            // 先收集每段竖向跳跃的起止高度,再按缝隙分配竖段轨道
            var hops = new List<(int Gap, double Y1, double Y2, int Edge, int Index)>();
            var hopY = new Dictionary<int, List<double>>();
            // 竖段所属的"线网":同一端口的扇出 / 扇入共用一个线网,MergeSamePortTracks 时一个线网在一道缝里只占一条轨道。
            var netOf = new Dictionary<(int, int), string>();
            string PortNet(string kind, string node, double? port)
                => port.HasValue ? kind + "|" + node + "|" + port.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "";
            foreach (var kv in chains)
            {
                var c = kv.Value;
                var ys = new List<double>();
                for (int i = 0; i < c.Count; i++)
                {
                    if (i == 0) ys.Add(outY[kv.Key]);
                    else if (i == c.Count - 1) ys.Add(inY[kv.Key]);
                    else ys.Add(c[i].Y);
                }
                hopY[kv.Key] = ys;
                var ce = edges[kv.Key];
                bool rev = res.ReversedEdges.Contains((ce.Src, ce.Tgt));
                double? headPort = rev ? ce.TgtPortY : ce.SrcPortY;
                double? tailPort = rev ? ce.SrcPortY : ce.TgtPortY;
                for (int i = 0; i + 1 < c.Count; i++)
                {
                    hops.Add((c[i].Layer, ys[i], ys[i + 1], kv.Key, i));
                    if (i == 0) netOf[(kv.Key, i)] = PortNet("S", c[0].Id, headPort);
                    else if (i == c.Count - 2) netOf[(kv.Key, i)] = PortNet("T", c[c.Count - 1].Id, tailPort);
                }
            }

            // 回边走廊:内容底部往下逐条排开,跨度小的在内圈。进同一个目标端口的回边是一组,
            // 共用一条走廊和最后一段竖线(总线);各自只有从源端口落到走廊的那段竖线。
            double contentBottom = res.Boxes.Count == 0 ? opt.Top : res.Boxes.Values.Max(b => b.Y + b.H);
            var backInfo = new Dictionary<int, (double Sy, double Ty, double Corridor, int SrcLayer, int TgtLayer, int Lead)>();
            var groups = backBelow
                .GroupBy(i => (edges[i].Tgt, edges[i].TgtPortY ?? double.NaN))
                .Select(g => g.ToList())
                .OrderBy(g => g.Max(i => vs[edges[i].Src].Layer) - vs[edges[g[0]].Tgt].Layer)
                .ThenBy(g => vs[edges[g[0]].Tgt].Layer)
                .ToList();
            // 就近模式下已经排好的走廊:(高度, 覆盖的列段),同侧、列段重叠的走廊彼此错开一个间距。
            var placed = new List<(double Y, int Lo, int Hi)>();
            double maxCorridor = contentBottom;
            for (int k = 0; k < groups.Count; k++)
            {
                var g = groups[k];
                int lead = g[0];
                var t = vs[edges[lead].Tgt];
                double ty = edges[lead].TgtPortY.HasValue ? t.Y - t.H / 2 + edges[lead].TgtPortY!.Value : t.Y;
                double corridor = opt.RouteBackEdgesNear
                    ? NearCorridor(g, t, ty)
                    : contentBottom + opt.BackEdgeCorridorGap + k * opt.BackEdgeSpacing;
                maxCorridor = Math.Max(maxCorridor, corridor);
                foreach (var i in g)
                {
                    var e = edges[i];
                    var s = vs[e.Src];
                    double sy = e.SrcPortY.HasValue ? s.Y - s.H / 2 + e.SrcPortY.Value : s.Y;
                    backInfo[i] = (sy, ty, corridor, s.Layer, t.Layer, lead);
                    hops.Add((s.Layer, sy, corridor, i, 0));
                    netOf[(i, 0)] = PortNet("S", e.Src, e.SrcPortY);
                }
                hops.Add((t.Layer - 1, corridor, ty, lead, 1));
                netOf[(lead, 1)] = PortNet("T", edges[lead].Tgt, edges[lead].TgtPortY);
            }
            if (groups.Count > 0)
                res.ContentHeight = Math.Max(res.ContentHeight, maxCorridor + opt.BackEdgeSpacing - opt.Top);

            // 源列到目标列之间(含两端)所有节点 —— 包括长边的虚拟节点 —— 的上沿 / 下沿外侧各是一条候选走廊,
            // 横段在这段列里不会压到任何节点或长边;两条竖段落在源右侧、目标左侧的列缝里,也不压节点。
            // 选两端端口竖向绕行总距离短的一侧,然后跟已排好的同侧走廊错开。
            double NearCorridor(List<int> g, V t, double ty)
            {
                int lo = t.Layer, hi = g.Max(i => vs[edges[i].Src].Layer);
                double top = double.MaxValue, bottom = double.MinValue;
                foreach (var v in vs.Values)
                {
                    if (v.Layer < lo || v.Layer > hi) continue;
                    top = Math.Min(top, v.Y - v.H / 2);
                    bottom = Math.Max(bottom, v.Y + v.H / 2);
                }
                var ys = new List<double> { ty };
                foreach (var i in g)
                {
                    var s = vs[edges[i].Src];
                    ys.Add(edges[i].SrcPortY.HasValue ? s.Y - s.H / 2 + edges[i].SrcPortY!.Value : s.Y);
                }
                double above = top - opt.BackEdgeCorridorGap, below = bottom + opt.BackEdgeCorridorGap;
                bool up = ys.Sum(y => Math.Abs(y - above)) < ys.Sum(y => Math.Abs(y - below));
                double dir = up ? -1 : 1;
                double y0 = up ? above : below;
                for (int step = 0; ; step++)
                {
                    double y = y0 + dir * step * opt.BackEdgeSpacing;
                    bool clash = placed.Any(p => p.Lo <= hi && lo <= p.Hi && Math.Abs(p.Y - y) < opt.BackEdgeSpacing - 0.1);
                    if (clash) continue;
                    placed.Add((y, lo, hi));
                    return y;
                }
            }

            var trackX = new Dictionary<(int, int), double>();
            foreach (var g in hops.Where(h => Math.Abs(h.Y1 - h.Y2) > 0.5).GroupBy(h => h.Gap))
            {
                // 同一线网的竖段先并成一段(取高度并集),再参与着色;不合并时每段自成一组。
                var units = g
                    .GroupBy(h => opt.MergeSamePortTracks && netOf.TryGetValue((h.Edge, h.Index), out var n) && n.Length > 0
                        ? n : "#" + h.Edge + "/" + h.Index)
                    .Select(u => (Members: u.ToList(),
                                  Y1: u.First().Y1, Y2: u.First().Y2,
                                  Lo: u.Min(h => Math.Min(h.Y1, h.Y2)), Hi: u.Max(h => Math.Max(h.Y1, h.Y2))))
                    .ToList();
                // 区间着色:竖段高度区间重叠的放不同轨道;先排向下走的,再排向上走的,减少竖段之间交叉
                var segs = units.OrderBy(h => h.Y2 > h.Y1 ? 0 : 1).ThenBy(h => h.Lo).ToList();
                var trackEnds = new List<List<(double Lo, double Hi)>>();
                var assigned = new List<int>();
                foreach (var s in segs)
                {
                    double lo = s.Lo - 2, hi = s.Hi + 2;
                    int t = 0;
                    for (; t < trackEnds.Count; t++)
                        if (trackEnds[t].All(iv => hi < iv.Lo || lo > iv.Hi)) break;
                    if (t == trackEnds.Count) trackEnds.Add(new List<(double, double)>());
                    trackEnds[t].Add((lo, hi));
                    assigned.Add(t);
                }
                var (gapLeft, gapRight) = GapBounds(g.Key);
                int count = trackEnds.Count;
                for (int i = 0; i < segs.Count; i++)
                    foreach (var m in segs[i].Members)
                        trackX[(m.Edge, m.Index)] = gapLeft + (gapRight - gapLeft) * (assigned[i] + 1) / (count + 1);
            }

            foreach (var kv in chains)
            {
                var c = kv.Value;
                var ys = hopY[kv.Key];
                var pts = new List<Pt> { new Pt(colCenter(c[0].Layer) + halfW, ys[0]) };
                for (int i = 0; i + 1 < c.Count; i++)
                {
                    if (trackX.TryGetValue((kv.Key, i), out var tx))
                    {
                        pts.Add(new Pt(tx, ys[i]));
                        pts.Add(new Pt(tx, ys[i + 1]));
                    }
                    var next = c[i + 1];
                    // 虚拟节点处直线穿过整列(它在层内占了位置,不会压到真节点上)
                    pts.Add(next.IsDummy
                        ? new Pt(colCenter(next.Layer) + halfW, ys[i + 1])
                        : new Pt(colCenter(next.Layer) - halfW, ys[i + 1]));
                }
                var route = Simplify(pts);
                // 回边按原始方向返回:从原 Src 画到原 Tgt
                var e = edges[kv.Key];
                if (res.ReversedEdges.Contains((e.Src, e.Tgt))) route.Reverse();
                res.EdgeRoutes[kv.Key] = route;
            }

            foreach (var kv in backInfo)
            {
                var b = kv.Value;
                double x1 = trackX.TryGetValue((kv.Key, 0), out var a) ? a : (GapBounds(b.SrcLayer).L + GapBounds(b.SrcLayer).R) / 2;
                double x2 = trackX.TryGetValue((b.Lead, 1), out var d) ? d : (GapBounds(b.TgtLayer - 1).L + GapBounds(b.TgtLayer - 1).R) / 2;
                res.EdgeRoutes[kv.Key] = Simplify(new List<Pt>
                {
                    new Pt(colCenter(b.SrcLayer) + halfW, b.Sy),
                    new Pt(x1, b.Sy),
                    new Pt(x1, b.Corridor),
                    new Pt(x2, b.Corridor),
                    new Pt(x2, b.Ty),
                    new Pt(colCenter(b.TgtLayer) - halfW, b.Ty),
                });
            }
        }

        private static void AddSlot(Dictionary<V, List<int>> map, V v, int e)
        {
            if (!map.TryGetValue(v, out var l)) map[v] = l = new List<int>();
            l.Add(e);
        }

        private static void Distribute(V v, List<int> edges, Func<int, double> otherY, Dictionary<int, double> into)
        {
            var sorted = edges.OrderBy(otherY).ToList();
            for (int i = 0; i < sorted.Count; i++)
                into[sorted[i]] = v.IsDummy ? v.Y : v.Y - v.H / 2 + v.H * (i + 1) / (sorted.Count + 1);
        }

        // 去掉共线的中间点,折线只留拐点
        private static List<Pt> Simplify(List<Pt> pts)
        {
            var r = new List<Pt>();
            foreach (var p in pts)
            {
                if (r.Count > 0 && Math.Abs(r[r.Count - 1].X - p.X) < 0.01 && Math.Abs(r[r.Count - 1].Y - p.Y) < 0.01) continue;
                if (r.Count >= 2)
                {
                    var a = r[r.Count - 2];
                    var b = r[r.Count - 1];
                    bool collinear = (Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - p.X) < 0.01)
                                  || (Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - p.Y) < 0.01);
                    if (collinear) r.RemoveAt(r.Count - 1);
                }
                r.Add(p);
            }
            return r;
        }
    }
}
