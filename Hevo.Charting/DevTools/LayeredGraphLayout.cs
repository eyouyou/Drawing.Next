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
        }

        internal sealed class Result
        {
            public readonly Dictionary<string, int> LayerOf = new();
            /// <summary>有连线的节点占的层数;没有任何连线的节点统一放在第 FlowLayerCount 层。</summary>
            public int FlowLayerCount;
            public bool HasUnlinkedLayer;
            public int LayerCount => FlowLayerCount + (HasUnlinkedLayer ? 1 : 0);
            public double NodeWidth;
            public double ColumnWidth;
            /// <summary>内容实际占的宽高(从 Options.Left / Top 算起),可能大于可用区域。</summary>
            public double ContentWidth;
            public double ContentHeight;
            public readonly Dictionary<string, (double X, double Y, double W, double H)> Boxes = new();
            /// <summary>按原始方向(Src → Tgt)给出的折线;回边的折线也是从 Src 画到 Tgt。</summary>
            public readonly Dictionary<(string Src, string Tgt), List<Pt>> Routes = new();
            /// <summary>断环时反转的边(原始方向)。</summary>
            public readonly HashSet<(string Src, string Tgt)> ReversedEdges = new();
            /// <summary>去掉回边后的后继 / 前驱表(原始节点之间,不含虚拟节点)。</summary>
            public readonly Dictionary<string, List<string>> Succ = new();
            public readonly Dictionary<string, List<string>> Pred = new();
        }

        private sealed class V
        {
            public string Id = "";
            public bool IsDummy;
            public double H;
            public int Layer;
            public int Order;
            public double Y; // 中心
            public readonly List<V> Up = new();   // 上一层邻居(DAG 方向的前驱)
            public readonly List<V> Down = new(); // 下一层邻居
        }

        public static Result Compute(IReadOnlyList<Node> nodes, IEnumerable<(string Src, string Tgt)> edges, Options opt)
        {
            var res = new Result();
            var byId = new Dictionary<string, Node>();
            foreach (var n in nodes) if (!byId.ContainsKey(n.Id)) byId[n.Id] = n;

            // ── 0. 规整输入:去重、去自环、去未知节点 ──
            var edgeList = new List<(string Src, string Tgt)>();
            var seen = new HashSet<(string, string)>();
            foreach (var e in edges)
            {
                if (e.Src == e.Tgt || !byId.ContainsKey(e.Src) || !byId.ContainsKey(e.Tgt)) continue;
                if (seen.Add((e.Src, e.Tgt))) edgeList.Add(e);
            }

            var linked = new HashSet<string>();
            foreach (var e in edgeList) { linked.Add(e.Src); linked.Add(e.Tgt); }
            var order = nodes.Select(n => n.Id).Where(linked.Contains).Distinct().ToList();

            // ── 1. 断环:从没有入边的节点做 DFS,回边反转 ──
            var outAll = new Dictionary<string, List<string>>();
            var hasIn = new HashSet<string>();
            foreach (var e in edgeList)
            {
                if (!outAll.TryGetValue(e.Src, out var l)) outAll[e.Src] = l = new List<string>();
                l.Add(e.Tgt);
                hasIn.Add(e.Tgt);
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
            foreach (var id in order) if (!hasIn.Contains(id) && !visited.Contains(id)) Dfs(id);
            foreach (var id in order) if (!visited.Contains(id)) Dfs(id);

            // DAG 边(回边反过来)
            var dag = new List<(string U, string W, (string, string) Orig)>();
            foreach (var e in edgeList)
            {
                if (res.ReversedEdges.Contains(e)) dag.Add((e.Tgt, e.Src, e));
                else
                {
                    dag.Add((e.Src, e.Tgt, e));
                    Add(res.Succ, e.Src, e.Tgt);
                    Add(res.Pred, e.Tgt, e.Src);
                }
            }
            var dSucc = new Dictionary<string, List<string>>();
            var dPred = new Dictionary<string, List<string>>();
            foreach (var d in dag) { Add(dSucc, d.U, d.W); Add(dPred, d.W, d.U); }

            // ── 2. 分层:最长路径(源头在 0 层),再把非固定源头收紧到紧挨后继 ──
            var layer = new Dictionary<string, int>();
            var indeg = order.ToDictionary(id => id, id => dPred.TryGetValue(id, out var p) ? p.Count : 0);
            var queue = new Queue<string>(order.Where(id => indeg[id] == 0));
            var topo = new List<string>();
            foreach (var id in queue) layer[id] = 0;
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                topo.Add(v);
                if (!dSucc.TryGetValue(v, out var outs)) continue;
                foreach (var w in outs)
                {
                    layer[w] = Math.Max(layer.TryGetValue(w, out var lw) ? lw : 0, layer[v] + 1);
                    if (--indeg[w] == 0) queue.Enqueue(w);
                }
            }
            // 源头(没有前驱)放在 0 层会拉出很长的边;除非标了固定,挪到最近后继的前一层
            for (int i = topo.Count - 1; i >= 0; i--)
            {
                var v = topo[i];
                if (dPred.ContainsKey(v) || byId[v].PinFirstLayer || !dSucc.TryGetValue(v, out var outs)) continue;
                layer[v] = Math.Max(0, outs.Min(w => layer[w]) - 1);
            }

            res.FlowLayerCount = layer.Count == 0 ? 0 : layer.Values.Max() + 1;
            foreach (var kv in layer) res.LayerOf[kv.Key] = kv.Value;
            foreach (var n in nodes)
            {
                if (res.LayerOf.ContainsKey(n.Id)) continue;
                res.LayerOf[n.Id] = res.FlowLayerCount;
                res.HasUnlinkedLayer = true;
            }

            int layerCount = res.LayerCount;
            if (layerCount == 0) return res;

            // ── 3a. 长边拆虚拟节点,每条边只连相邻两层 ──
            var vs = new Dictionary<string, V>();
            foreach (var n in nodes)
                if (!vs.ContainsKey(n.Id))
                    vs[n.Id] = new V { Id = n.Id, H = n.Height, Layer = res.LayerOf[n.Id] };
            var chains = new Dictionary<(string, string), List<V>>();
            int dummyNo = 0;
            foreach (var d in dag)
            {
                var chain = new List<V> { vs[d.U] };
                var prev = vs[d.U];
                for (int l = prev.Layer + 1; l < vs[d.W].Layer; l++)
                {
                    var dv = new V { Id = "~d" + (dummyNo++), IsDummy = true, H = 0, Layer = l };
                    vs[dv.Id] = dv;
                    Link(prev, dv);
                    chain.Add(dv);
                    prev = dv;
                }
                Link(prev, vs[d.W]);
                chain.Add(vs[d.W]);
                chains[d.Orig] = chain;
            }

            var layers = new List<List<V>>();
            for (int l = 0; l < layerCount; l++) layers.Add(new List<V>());
            // 初始顺序:真节点按输入顺序,虚拟节点按创建顺序(跟着各自的源头)
            foreach (var n in nodes) if (vs.TryGetValue(n.Id, out var v) && !layers[v.Layer].Contains(v)) layers[v.Layer].Add(v);
            foreach (var v in vs.Values) if (v.IsDummy) layers[v.Layer].Add(v);
            Renumber(layers);

            // ── 3b. 减少交叉:上下来回扫,按邻居重心排序,保留交叉最少的那一版 ──
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

            // ── 4. 纵向定位:对每层做加权有序回归,目标是邻居中心的中位数;虚拟节点权重高 ──
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

            // ── 5. 正交走线:出端在右边、入端在左边,同侧多条线均匀分布;层间竖段分配不重叠的轨道 ──
            RouteOrthogonal(chains, layers, res, colW, ColCenter, opt);
            return res;
        }

        private static void Add(Dictionary<string, List<string>> map, string k, string v)
        {
            if (!map.TryGetValue(k, out var l)) map[k] = l = new List<string>();
            if (!l.Contains(v)) l.Add(v);
        }

        private static void Link(V a, V b) { a.Down.Add(b); b.Up.Add(a); }

        private static void Renumber(List<List<V>> layers)
        {
            foreach (var l in layers) for (int i = 0; i < l.Count; i++) l[i].Order = i;
        }

        private static void SortByBarycenter(List<V> lay, bool up)
        {
            var key = new Dictionary<V, double>(lay.Count);
            foreach (var v in lay)
            {
                var ns = up ? v.Up : v.Down;
                key[v] = ns.Count > 0 ? ns.Average(n => n.Order) : v.Order; // 没邻居的留在原位
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
                var segs = new List<(int A, int B)>();
                foreach (var v in layers[l]) foreach (var w in v.Down) segs.Add((v.Order, w.Order));
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
                var ys = ns.Select(x => x.Y).OrderBy(x => x).ToList();
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
            Dictionary<(string, string), List<V>> chains, List<List<V>> layers, Result res,
            double colW, Func<int, double> colCenter, Options opt)
        {
            double halfW = res.NodeWidth / 2;

            // 每个真节点的出 / 入连接点:按另一端的高度排序后沿边均匀分布,减少在节点边上交叉
            var outSlots = new Dictionary<V, List<(string, string)>>();
            var inSlots = new Dictionary<V, List<(string, string)>>();
            foreach (var kv in chains)
            {
                var c = kv.Value;
                AddSlot(outSlots, c[0], kv.Key);
                AddSlot(inSlots, c[c.Count - 1], kv.Key);
            }
            var outY = new Dictionary<((string, string), V), double>();
            var inY = new Dictionary<((string, string), V), double>();
            foreach (var kv in outSlots) Distribute(kv.Key, kv.Value, e => chains[e][1].Y, outY);
            foreach (var kv in inSlots) Distribute(kv.Key, kv.Value, e => chains[e][chains[e].Count - 2].Y, inY);

            // 先收集每段层间跳跃的起止高度,再按层间缝隙分配竖段轨道
            var hops = new List<(int Gap, double Y1, double Y2, (string, string) Edge, int Index)>();
            var hopY = new Dictionary<(string, string), List<double>>();
            foreach (var kv in chains)
            {
                var c = kv.Value;
                var ys = new List<double>();
                for (int i = 0; i < c.Count; i++)
                {
                    if (i == 0) ys.Add(outY[(kv.Key, c[0])]);
                    else if (i == c.Count - 1) ys.Add(inY[(kv.Key, c[i])]);
                    else ys.Add(c[i].Y);
                }
                hopY[kv.Key] = ys;
                for (int i = 0; i + 1 < c.Count; i++)
                    hops.Add((c[i].Layer, ys[i], ys[i + 1], kv.Key, i));
            }

            var trackX = new Dictionary<((string, string), int), double>();
            foreach (var g in hops.Where(h => Math.Abs(h.Y1 - h.Y2) > 0.5).GroupBy(h => h.Gap))
            {
                // 区间着色:竖段高度区间重叠的放不同轨道;先排向下走的,再排向上走的,减少竖段之间交叉
                var segs = g.OrderBy(h => h.Y2 > h.Y1 ? 0 : 1).ThenBy(h => Math.Min(h.Y1, h.Y2)).ToList();
                var trackEnds = new List<List<(double Lo, double Hi)>>();
                var assigned = new List<int>();
                foreach (var s in segs)
                {
                    double lo = Math.Min(s.Y1, s.Y2) - 2, hi = Math.Max(s.Y1, s.Y2) + 2;
                    int t = 0;
                    for (; t < trackEnds.Count; t++)
                        if (trackEnds[t].All(iv => hi < iv.Lo || lo > iv.Hi)) break;
                    if (t == trackEnds.Count) trackEnds.Add(new List<(double, double)>());
                    trackEnds[t].Add((lo, hi));
                    assigned.Add(t);
                }
                double gapLeft = colCenter(g.Key) + halfW, gapRight = colCenter(g.Key + 1) - halfW;
                int count = trackEnds.Count;
                for (int i = 0; i < segs.Count; i++)
                    trackX[(segs[i].Edge, segs[i].Index)] = gapLeft + (gapRight - gapLeft) * (assigned[i] + 1) / (count + 1);
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
                if (res.ReversedEdges.Contains(kv.Key)) route.Reverse();
                res.Routes[kv.Key] = route;
            }
        }

        private static void AddSlot(Dictionary<V, List<(string, string)>> map, V v, (string, string) e)
        {
            if (!map.TryGetValue(v, out var l)) map[v] = l = new List<(string, string)>();
            l.Add(e);
        }

        private static void Distribute(V v, List<(string, string)> edges, Func<(string, string), double> otherY,
            Dictionary<((string, string), V), double> into)
        {
            var sorted = edges.OrderBy(otherY).ToList();
            for (int i = 0; i < sorted.Count; i++)
                into[(sorted[i], v)] = v.IsDummy ? v.Y : v.Y - v.H / 2 + v.H * (i + 1) / (sorted.Count + 1);
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
