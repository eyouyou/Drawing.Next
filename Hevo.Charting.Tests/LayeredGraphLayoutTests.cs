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
    }
}
