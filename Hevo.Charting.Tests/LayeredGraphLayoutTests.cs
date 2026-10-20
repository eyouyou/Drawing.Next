using System;
using System.Collections.Generic;
using System.Linq;
using Hevo.Charting.DevTools;
using Xunit;
using L = Hevo.Charting.DevTools.LayeredGraphLayout;

namespace Hevo.Charting.Tests
{
    /// <summary>分层布局(参照 ELK Layered)的各阶段行为。</summary>
    public sealed class LayeredGraphLayoutTests
    {
        private static L.Result Run(string[] nodes, (string, string)[] edges, Action<L.Node>? tweak = null)
        {
            var ns = nodes.Select(id => { var n = new L.Node { Id = id, Height = 30 }; tweak?.Invoke(n); return n; }).ToList();
            return L.Compute(ns, edges, new L.Options { Width = 1200, Height = 800 });
        }

        private static bool Orthogonal(List<L.Pt> r)
        {
            for (int i = 0; i + 1 < r.Count; i++)
                if (Math.Abs(r[i].X - r[i + 1].X) > 0.01 && Math.Abs(r[i].Y - r[i + 1].Y) > 0.01) return false;
            return true;
        }

        [Fact]
        public void ForwardEdges_GoLeftToRight_AndRoutesAreOrthogonal()
        {
            var r = Run(new[] { "A", "B", "C", "D" }, new[] { ("A", "B"), ("B", "C"), ("A", "C"), ("C", "D") });
            Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { "A", "B", "C", "D" }.Select(x => r.LayerOf[x]));
            foreach (var kv in r.Routes)
            {
                Assert.True(Orthogonal(kv.Value), $"{kv.Key} 不是横竖折线");
                Assert.Equal(r.Boxes[kv.Key.Src].X + r.Boxes[kv.Key.Src].W, kv.Value[0].X, 3); // 从源节点右边出
                Assert.Equal(r.Boxes[kv.Key.Tgt].X, kv.Value[^1].X, 3);                        // 到目标节点左边
            }
        }

        [Fact]
        public void LongEdge_PassesBetweenNodesOfIntermediateLayers()
        {
            // A→X 跨两层;中间层有 M1..M3 三个节点,长边的水平段不能压在它们身上
            var r = Run(new[] { "A", "B", "M1", "M2", "M3", "X" },
                new[] { ("A", "B"), ("B", "M1"), ("B", "M2"), ("B", "M3"), ("M1", "X"), ("M2", "X"), ("M3", "X"), ("A", "M2"), ("A", "X") });
            var route = r.Routes[("A", "X")];
            foreach (var m in new[] { "M1", "M2", "M3", "B" })
            {
                var b = r.Boxes[m];
                for (int i = 0; i + 1 < route.Count; i++)
                {
                    var p = route[i];
                    var q = route[i + 1];
                    bool horizontal = Math.Abs(p.Y - q.Y) < 0.01;
                    if (!horizontal) continue;
                    double lo = Math.Min(p.X, q.X), hi = Math.Max(p.X, q.X);
                    bool overlapsX = hi > b.X + 0.5 && lo < b.X + b.W - 0.5;
                    bool insideY = p.Y > b.Y + 0.5 && p.Y < b.Y + b.H - 0.5;
                    Assert.False(overlapsX && insideY, $"A→X 的水平段穿过了 {m}");
                }
            }
        }

        [Fact]
        public void CrossingReduction_UntanglesTwistedPairs()
        {
            // 输入顺序故意交叉:A→Y、B→X,而输入顺序是 X 在 Y 前面
            var r = Run(new[] { "A", "B", "X", "Y" }, new[] { ("A", "Y"), ("B", "X") });
            bool aAboveB = r.Boxes["A"].Y < r.Boxes["B"].Y;
            bool yAboveX = r.Boxes["Y"].Y < r.Boxes["X"].Y;
            Assert.Equal(aAboveB, yAboveX);
        }

        [Fact]
        public void Cycle_ReversesOneBackEdge_FromTheSourceSide()
        {
            var r = Run(new[] { "S", "P", "Q" }, new[] { ("S", "P"), ("P", "Q"), ("Q", "P") }, n => n.PinFirstLayer = n.Id == "S");
            Assert.Equal(new[] { ("Q", "P") }, r.ReversedEdges.ToArray());
            Assert.True(r.LayerOf["P"] < r.LayerOf["Q"]);
            var back = r.Routes[("Q", "P")];
            Assert.Equal(r.Boxes["P"].X + r.Boxes["P"].W, back[^1].X, 3); // 回边画到 P 的右侧,箭头朝左
            Assert.DoesNotContain("Q", r.Succ.TryGetValue("Q", out var s) ? s : new List<string>());
        }

        [Fact]
        public void UnpinnedSource_IsPulledNextToItsSuccessor_PinnedSourceStays()
        {
            var r = Run(new[] { "PIPE", "A", "B", "C", "Hit" },
                new[] { ("PIPE", "A"), ("A", "B"), ("B", "C"), ("Hit", "C") },
                n => n.PinFirstLayer = n.Id == "PIPE");
            Assert.Equal(0, r.LayerOf["PIPE"]);
            Assert.Equal(r.LayerOf["C"] - 1, r.LayerOf["Hit"]);
        }

        [Fact]
        public void IsolatedNodes_GoToTrailingLayer()
        {
            var r = Run(new[] { "A", "B", "Lonely" }, new[] { ("A", "B") });
            Assert.True(r.HasUnlinkedLayer);
            Assert.Equal(r.FlowLayerCount, r.LayerOf["Lonely"]);
            Assert.Equal(3, r.LayerCount);
        }
        private static void AssertNoOverlap(L.Result r)
        {
            var boxes = r.Boxes.ToList();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i].Value;
                    var b = boxes[j].Value;
                    bool overlap = a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
                    Assert.False(overlap, $"{boxes[i].Key} 与 {boxes[j].Key} 重叠");
                }
        }

        [Fact]
        public void TallLayer_DoesNotOverlap_ContentGrowsInstead()
        {
            var ids = new[] { "S" }.Concat(Enumerable.Range(0, 40).Select(i => "N" + i)).ToArray();
            var edges = Enumerable.Range(0, 40).Select(i => ("S", "N" + i)).ToArray();
            var ns = ids.Select(id => new L.Node { Id = id, Height = 30 }).ToList();
            var r = L.Compute(ns, edges, new L.Options { Width = 800, Height = 200 });
            AssertNoOverlap(r);
            Assert.True(r.ContentHeight > 200, "放不下时内容高度应超出可用高度(交给滚动)");
        }

        [Fact]
        public void ManyLayers_InNarrowWidth_DoNotOverlap_ContentGrowsInstead()
        {
            var ids = Enumerable.Range(0, 20).Select(i => "L" + i).ToArray();
            var edges = Enumerable.Range(0, 19).Select(i => ("L" + i, "L" + (i + 1))).ToArray();
            var ns = ids.Select(id => new L.Node { Id = id, Height = 30 }).ToList();
            var r = L.Compute(ns, edges, new L.Options { Width = 600, Height = 400 });
            AssertNoOverlap(r);
            Assert.True(r.ContentWidth > 600, "列太多时内容宽度应超出可用宽度(交给滚动)");
            Assert.True(r.Boxes["L1"].X - (r.Boxes["L0"].X + r.Boxes["L0"].W) >= 30, "相邻两列之间要留出走线的空隙");
        }

        // ── 分区 ──

        private static L.Result RunParts((string Id, int Part)[] nodes, (string, string)[] edges, bool backBelow = false)
        {
            var ns = nodes.Select(n => new L.Node { Id = n.Id, Height = 30, Partition = n.Part }).ToList();
            var es = edges.Select(e => new L.Edge { Src = e.Item1, Tgt = e.Item2 }).ToList();
            return L.Compute(ns, es, new L.Options { Width = 1200, Height = 800, RouteBackEdgesBelow = backBelow });
        }

        [Fact]
        public void Partitions_OccupyContiguousColumns_InOrder()
        {
            var nodes = new[] { ("A", 0), ("B", 1), ("C", 1), ("Lonely", 1), ("D", 2) };
            var r = RunParts(nodes, new[] { ("A", "B"), ("B", "C"), ("C", "D"), ("A", "D") });

            Assert.Equal(new[] { 0, 1, 2 }, r.PartitionLayers.Keys.OrderBy(k => k));
            int prevLast = -1;
            foreach (var p in r.PartitionLayers.OrderBy(kv => kv.Key))
            {
                Assert.Equal(prevLast + 1, p.Value.First); // 连续且紧挨着上一个分区
                prevLast = p.Value.Last;
            }
            foreach (var (id, part) in nodes)
            {
                var range = r.PartitionLayers[part];
                Assert.InRange(r.LayerOf[id], range.First, range.Last);
            }
            Assert.Equal(r.PartitionLayers[1].First, r.LayerOf["Lonely"]); // 无连线节点单独占分区首列
            Assert.True(r.LayerOf["B"] > r.LayerOf["Lonely"]);
            Assert.False(r.HasUnlinkedLayer);
        }

        [Fact]
        public void Partitions_EdgeFromLaterPartition_IsBackEdge()
        {
            // 交互(2)回写视口(0):按分区判回边,不靠 DFS 顺序
            var r = RunParts(new[] { ("VP", 0), ("Axis", 1), ("Hit", 2) },
                new[] { ("VP", "Axis"), ("Axis", "Hit"), ("Hit", "VP") });
            Assert.Equal(new[] { ("Hit", "VP") }, r.ReversedEdges.ToArray());
            Assert.True(r.LayerOf["VP"] < r.LayerOf["Axis"] && r.LayerOf["Axis"] < r.LayerOf["Hit"]);
        }

        [Fact]
        public void Partitions_PartitionWithoutLinks_StillGetsItsOwnColumn()
        {
            var r = RunParts(new[] { ("A", 0), ("Cfg", 1), ("Z", 2) }, new[] { ("A", "Z") });
            Assert.True(r.LayerOf["A"] < r.LayerOf["Cfg"] && r.LayerOf["Cfg"] < r.LayerOf["Z"]);
        }

        [Fact]
        public void BackEdgesBelow_LeaveFromSourceRight_RunUnderAllNodes_EnterTargetLeft()
        {
            var r = RunParts(new[] { ("Scale", 0), ("Mid", 1), ("Plot", 2) },
                new[] { ("Scale", "Mid"), ("Mid", "Plot"), ("Plot", "Scale") }, backBelow: true);
            var route = r.Routes[("Plot", "Scale")];
            Assert.True(Orthogonal(route));
            Assert.Equal(r.Boxes["Plot"].X + r.Boxes["Plot"].W, route[0].X, 3);
            Assert.Equal(r.Boxes["Scale"].X, route[route.Count - 1].X, 3);
            double bottom = r.Boxes.Values.Max(b => b.Y + b.H);
            Assert.Contains(route, p => p.Y > bottom);
        }

        // ── 端口位置 ──

        [Fact]
        public void Ports_RoutesLeaveAndEnterAtPortOffsets_MultiEdgesKeptApart()
        {
            var ns = new List<L.Node>
            {
                new L.Node { Id = "Plot", Height = 100 },
                new L.Node { Id = "Scale", Height = 60 },
            };
            var es = new List<L.Edge>
            {
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 20, TgtPortY = 30 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 50, TgtPortY = 30 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 80, TgtPortY = 30 },
            };
            var r = L.Compute(ns, es, new L.Options { Width = 800, Height = 400 });

            Assert.Equal(3, r.EdgeRoutes.Count);
            var plot = r.Boxes["Plot"];
            var scale = r.Boxes["Scale"];
            for (int i = 0; i < 3; i++)
            {
                var route = r.EdgeRoutes[i]!;
                Assert.True(Orthogonal(route));
                Assert.Equal(plot.Y + es[i].SrcPortY!.Value, route[0].Y, 3);
                Assert.Equal(scale.Y + 30, route[route.Count - 1].Y, 3);
            }
            // 三根线的竖段各占一条轨道,不叠在一起
            var verticalX = r.EdgeRoutes.Select(rt => rt!.Count > 2 ? rt[1].X : double.NaN).Where(x => !double.IsNaN(x)).ToList();
            Assert.Equal(verticalX.Count, verticalX.Distinct().Count());
        }

        [Fact]
        public void Ports_BarycenterUsesPortPosition()
        {
            // S 的上端口连 X、下端口连 Y;输入顺序故意让 Y 在前,排完后 X 应在 Y 上面,连线不交叉
            var ns = new List<L.Node>
            {
                new L.Node { Id = "S", Height = 100 },
                new L.Node { Id = "Y", Height = 30 },
                new L.Node { Id = "X", Height = 30 },
            };
            var es = new List<L.Edge>
            {
                new L.Edge { Src = "S", Tgt = "X", SrcPortY = 10 },
                new L.Edge { Src = "S", Tgt = "Y", SrcPortY = 90 },
            };
            var r = L.Compute(ns, es, new L.Options { Width = 800, Height = 400 });
            Assert.True(r.Boxes["X"].Y < r.Boxes["Y"].Y);
        }

        [Fact]
        public void WeightedCycleBreaking_ReversesTheThinnerSide()
        {
            // Plot 三根列进 Scale,Scale 一根量程回 Plot:应摘掉那一根量程,三根列保持正向
            var ns = new[] { "Src", "Plot", "Scale" }.Select(id => new L.Node { Id = id, Height = 40 }).ToList();
            var es = new List<L.Edge>
            {
                new L.Edge { Src = "Src", Tgt = "Scale" },
                new L.Edge { Src = "Src", Tgt = "Plot" },
                new L.Edge { Src = "Scale", Tgt = "Plot" },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 10 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 20 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 30 },
            };
            var r = L.Compute(ns, es, new L.Options { Width = 900, Height = 400, WeightedCycleBreaking = true });
            Assert.Equal(new[] { ("Scale", "Plot") }, r.ReversedEdges.ToArray());
            Assert.True(r.LayerOf["Plot"] < r.LayerOf["Scale"]);
        }

        [Fact]
        public void BackEdgesBelow_IntoSamePort_MergeIntoOneBus()
        {
            // Plot 的三根列都回连 Scale 的同一个扇入端口:共用一条走廊和最后一段竖线
            var ns = new List<L.Node>
            {
                new L.Node { Id = "Scale", Height = 60, Partition = 0 },
                new L.Node { Id = "Plot", Height = 90, Partition = 1 },
            };
            var es = new List<L.Edge>
            {
                new L.Edge { Src = "Scale", Tgt = "Plot", SrcPortY = 30, TgtPortY = 70 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 30, TgtPortY = 30 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 50, TgtPortY = 30 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 70, TgtPortY = 30 },
            };
            var r = L.Compute(ns, es, new L.Options { Width = 800, Height = 400, RouteBackEdgesBelow = true });
            var buses = Enumerable.Range(1, 3).Select(i => r.EdgeRoutes[i]!).ToList();
            double corridor = buses[0].Max(p => p.Y);
            foreach (var b in buses)
            {
                Assert.True(Orthogonal(b));
                Assert.Equal(corridor, b.Max(p => p.Y), 3);                     // 同一条走廊
                Assert.Equal(buses[0][buses[0].Count - 2].X, b[b.Count - 2].X, 3); // 同一段上行竖线
            }
            Assert.Equal(3, buses.Select(b => b[1].X).Distinct().Count());      // 各自从源端口落下的竖线不重叠
        }

        [Fact]
        public void BackEdgesNear_StayCloseToTheirColumns_AndAvoidNodes()
        {
            // Scale(0) ← Plot(2) 的回边;另有一个很高的节点 Tall 在 Plot 之后的列,它不该把走廊拖到它下面去
            var ns = new List<L.Node>
            {
                new L.Node { Id = "Scale", Height = 60, Partition = 0 },
                new L.Node { Id = "Mid", Height = 40, Partition = 1 },
                new L.Node { Id = "Plot", Height = 80, Partition = 2 },
                new L.Node { Id = "Tall", Height = 600, Partition = 3 },
            };
            var es = new List<L.Edge>
            {
                new L.Edge { Src = "Scale", Tgt = "Mid", SrcPortY = 30, TgtPortY = 20 },
                new L.Edge { Src = "Mid", Tgt = "Plot", SrcPortY = 20, TgtPortY = 20 },
                new L.Edge { Src = "Plot", Tgt = "Tall", SrcPortY = 20, TgtPortY = 300 },
                new L.Edge { Src = "Plot", Tgt = "Scale", SrcPortY = 60, TgtPortY = 30 },
            };
            var r = L.Compute(ns, es, new L.Options { Width = 1000, Height = 800, RouteBackEdgesNear = true });
            var route = r.EdgeRoutes[3]!;
            Assert.True(Orthogonal(route));
            Assert.Equal(r.Boxes["Plot"].X + r.Boxes["Plot"].W, route[0].X, 3);
            Assert.Equal(r.Boxes["Scale"].X, route[route.Count - 1].X, 3);

            // 走廊只需躲开 Scale..Plot 这几列,不该落到 Tall 的底部之下
            double tallBottom = r.Boxes["Tall"].Y + r.Boxes["Tall"].H;
            Assert.All(route, p => Assert.True(p.Y < tallBottom, "回边绕到了全图最底部"));

            for (int i = 0; i + 1 < route.Count; i++)
                foreach (var b in r.Boxes.Values)
                {
                    double lo = Math.Min(route[i].X, route[i + 1].X), hi = Math.Max(route[i].X, route[i + 1].X);
                    double top = Math.Min(route[i].Y, route[i + 1].Y), bottom = Math.Max(route[i].Y, route[i + 1].Y);
                    bool overlap = hi > b.X + 0.5 && lo < b.X + b.W - 0.5 && bottom > b.Y + 0.5 && top < b.Y + b.H - 0.5;
                    Assert.False(overlap, "回边压到了节点");
                }
        }

        [Fact]
        public void BackEdgesNear_ParallelCorridorsDoNotOverlap()
        {
            var ns = new List<L.Node>
            {
                new L.Node { Id = "A", Height = 60, Partition = 0 },
                new L.Node { Id = "B", Height = 60, Partition = 0 },
                new L.Node { Id = "P", Height = 80, Partition = 1 },
            };
            var es = new List<L.Edge>
            {
                new L.Edge { Src = "A", Tgt = "P", SrcPortY = 30, TgtPortY = 20 },
                new L.Edge { Src = "P", Tgt = "A", SrcPortY = 40, TgtPortY = 30 },
                new L.Edge { Src = "P", Tgt = "B", SrcPortY = 60, TgtPortY = 30 },
            };
            var r = L.Compute(ns, es, new L.Options { Width = 800, Height = 600, RouteBackEdgesNear = true });
            double CorridorY(List<L.Pt> rt)
            {
                double y = 0, best = -1;
                for (int i = 0; i + 1 < rt.Count; i++)
                    if (Math.Abs(rt[i].Y - rt[i + 1].Y) < 0.01 && Math.Abs(rt[i].X - rt[i + 1].X) > best)
                    { best = Math.Abs(rt[i].X - rt[i + 1].X); y = rt[i].Y; }
                return y;
            }
            Assert.NotEqual(CorridorY(r.EdgeRoutes[1]!), CorridorY(r.EdgeRoutes[2]!));
        }

        [Fact]
        public void MergeSamePortTracks_FanOutSharesOneTrunk_OtherwiseSeparate()
        {
            // S 的同一个端口扇出到 A / B / C;另有 T 的端口连 B。合并时 S 的三根线共用一根竖线,T 的线单独一条轨道
            List<L.Node> Nodes() => new List<L.Node>
            {
                new L.Node { Id = "S", Height = 60 },
                new L.Node { Id = "T", Height = 60 },
                new L.Node { Id = "A", Height = 40 },
                new L.Node { Id = "B", Height = 40 },
                new L.Node { Id = "C", Height = 40 },
            };
            List<L.Edge> Edges() => new List<L.Edge>
            {
                new L.Edge { Src = "S", Tgt = "A", SrcPortY = 30, TgtPortY = 20 },
                new L.Edge { Src = "S", Tgt = "B", SrcPortY = 30, TgtPortY = 10 },
                new L.Edge { Src = "S", Tgt = "C", SrcPortY = 30, TgtPortY = 20 },
                new L.Edge { Src = "T", Tgt = "B", SrcPortY = 30, TgtPortY = 30 },
            };
            double TrunkX(List<L.Pt> r) => r.Count > 2 ? r[1].X : double.NaN;

            var merged = L.Compute(Nodes(), Edges(), new L.Options { Width = 800, Height = 400, MergeSamePortTracks = true });
            var fan = merged.EdgeRoutes.Take(3).Select(r => TrunkX(r!)).Where(x => !double.IsNaN(x)).Distinct().ToList();
            Assert.Single(fan);
            // T 的竖线可以跟主干同一个 x,但高度区间不能重叠,否则就是两根不同来源的线叠在一起
            var t = merged.EdgeRoutes[3]!;
            if (t.Count > 2 && Math.Abs(t[1].X - fan[0]) < 0.01)
            {
                double tLo = Math.Min(t[1].Y, t[2].Y), tHi = Math.Max(t[1].Y, t[2].Y);
                foreach (var r in merged.EdgeRoutes.Take(3))
                {
                    if (r!.Count <= 2) continue;
                    double lo = Math.Min(r[1].Y, r[2].Y), hi = Math.Max(r[1].Y, r[2].Y);
                    Assert.True(tHi < lo || tLo > hi, "不同端口的竖线叠在一起");
                }
            }

            var apart = L.Compute(Nodes(), Edges(), new L.Options { Width = 800, Height = 400 });
            var sep = apart.EdgeRoutes.Take(3).Select(r => TrunkX(r!)).Where(x => !double.IsNaN(x)).ToList();
            Assert.Equal(sep.Count, sep.Distinct().Count()); // 默认行为不变:各占一条轨道
        }
    }
}
