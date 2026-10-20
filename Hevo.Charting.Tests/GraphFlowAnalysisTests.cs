using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 蓝图编辑器数据流可读性的纯逻辑部分:连线语义上色、环路回边、选中上下游、自动布局列序。
    /// </summary>
    public sealed class GraphFlowAnalysisTests
    {
        private static Node N(string id, FeatureCategory cat = FeatureCategory.Series, NodeKind kind = NodeKind.Feature,
                              Port[]? outs = null, Port[]? ins = null)
            => new Node(id, id, id, kind, default, new HevoPoint(200f, 80f),
                        ins ?? Array.Empty<Port>(), outs ?? Array.Empty<Port>(),
                        new Dictionary<string, object?>(), cat);

        private static Edge E(string from, string to, string fromPort = "out", string toPort = "in")
            => new Edge(Guid.NewGuid().ToString("N"), from, fromPort, to, toPort);

        [Theory]
        [InlineData("ReadOnlyMemory<double>", EdgeDataKind.Column)]
        [InlineData("ReadOnlyMemory<double>[]", EdgeDataKind.Column)]
        [InlineData("RealRange", EdgeDataKind.Range)]
        [InlineData("PointerHitState?", EdgeDataKind.HitState)]
        [InlineData("DataSnapshot<SinWavePoint>", EdgeDataKind.Stream)]
        [InlineData("TContext", EdgeDataKind.Stream)]
        [InlineData("int", EdgeDataKind.Other)]
        [InlineData(null, EdgeDataKind.Other)]
        public void Classify_ByPortType(string? typeName, EdgeDataKind expected)
        {
            Assert.Equal(expected, EdgeDataKindClassifier.Classify(typeName, EdgeKind.DataPort, NodeKind.Feature, NodeKind.Feature));
        }

        [Fact]
        public void Classify_ViewportNodeWinsOverType()
        {
            // ActiveRange 是 RealRange,但它出自 Viewport 节点,读者关心的是视口协议。
            Assert.Equal(EdgeDataKind.Viewport,
                EdgeDataKindClassifier.Classify("RealRange", EdgeKind.DataPort, NodeKind.Viewport, NodeKind.Feature));
            Assert.Equal(EdgeDataKind.Viewport,
                EdgeDataKindClassifier.Classify("int", EdgeKind.DataPort, NodeKind.DataSource, NodeKind.Viewport));
        }

        [Fact]
        public void Classify_CascadeAndStreamEdges()
        {
            Assert.Equal(EdgeDataKind.Stream, EdgeDataKindClassifier.Classify("int", EdgeKind.Cascade, null, null));
            Assert.Equal(EdgeDataKind.Stream, EdgeDataKindClassifier.Classify("int", EdgeKind.Stream, null, null));
        }

        [Fact]
        public void Classify_FromStateUsesOutputPortType()
        {
            var src = N("scale", outs: new[] { new Port("YRangePort", "YRangePort", "RealRange", false) });
            var dst = N("line", ins: new[] { new Port("YRangePort", "YRangePort", "RealRange", true) });
            var e = E("scale", "line", "YRangePort", "YRangePort");
            var s = GraphState.Empty with { Nodes = new[] { src, dst }, Edges = new[] { e } };
            Assert.Equal(EdgeDataKind.Range, EdgeDataKindClassifier.Classify(s, e));
        }

        // AutoScale ⇄ Plot:Plot 三根列进 AutoScale,AutoScale 一根量程回 Plot。
        private static (List<Node> nodes, List<Edge> edges) ScalePlotCycle()
        {
            var nodes = new List<Node>
            {
                N("ds", FeatureCategory.DataSource, NodeKind.DataSource),
                N("scale", FeatureCategory.Environment),
                N("plot"),
                N("line"),
                N("decor", FeatureCategory.Environment),
            };
            var edges = new List<Edge>
            {
                E("ds", "plot"),
                E("ds", "scale"),
                E("plot", "scale", "macd"),
                E("plot", "scale", "signal"),
                E("plot", "scale", "hist"),
                E("scale", "plot", "YRangePort", "YRangePort"),
                E("scale", "line", "YRangePort", "YRangePort"),
                E("ds", "line"),
            };
            return (nodes, edges);
        }

        [Fact]
        public void BackEdges_DropFewestParallelEdges()
        {
            var (nodes, edges) = ScalePlotCycle();
            var back = GraphFlowAnalysis.FindBackEdges(nodes, edges);
            Assert.Equal(new[] { ("scale", "plot") }, back.ToArray());
        }

        [Fact]
        public void BackEdges_EmptyForDag()
        {
            var nodes = new List<Node> { N("a"), N("b"), N("c") };
            var edges = new List<Edge> { E("a", "b"), E("b", "c"), E("a", "c") };
            Assert.Empty(GraphFlowAnalysis.FindBackEdges(nodes, edges));
        }

        [Fact]
        public void Lineage_FollowsFullChainAndIgnoresBackEdge()
        {
            var (nodes, edges) = ScalePlotCycle();
            var back = GraphFlowAnalysis.FindBackEdges(nodes, edges);
            var (up, down) = GraphFlowAnalysis.ComputeLineage(edges, new[] { "scale" }, back);

            Assert.Equal(new[] { "ds", "plot", "scale" }, up.OrderBy(x => x).ToArray());
            // plot 只经回边可达,不算下游 —— 否则环里节点同时出现在上下游。
            Assert.Equal(new[] { "line", "scale" }, down.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Lineage_MultipleSeedsUnion()
        {
            var nodes = new List<Node> { N("a"), N("b"), N("c"), N("d") };
            var edges = new List<Edge> { E("a", "b"), E("c", "d") };
            var (up, down) = GraphFlowAnalysis.ComputeLineage(edges, new[] { "b", "c" }, new HashSet<(string, string)>());
            Assert.Equal(new[] { "a", "b", "c" }, up.OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "b", "c", "d" }, down.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void AutoLayout_LanesRunLeftToRight()
        {
            var (nodes, edges) = ScalePlotCycle();
            AutoLayout.Apply(nodes, edges);
            float X(string id) => nodes.Single(n => n.Id == id).Position.X;
            // 数据源 → Environment → Series:泳道是竖向列带,先后跟数据流一致;泳道严格按类别,不随连线挪。
            Assert.True(X("ds") < X("scale"));
            Assert.True(X("scale") < X("plot"));
            Assert.True(X("decor") < X("plot"));
            Assert.True(X("plot") <= X("line"));
        }

        private static Port Out(string id, string type = "ReadOnlyMemory<double>") => new Port(id, id, type, false);
        private static Port In(string id, string type = "ReadOnlyMemory<double>") => new Port(id, id, type, true);

        [Fact]
        public void AutoLayout_RoutesStartAndEndAtPorts_AvoidNodes()
        {
            var nodes = new List<Node>
            {
                N("ds", FeatureCategory.DataSource, NodeKind.DataSource, outs: new[] { Out("Value"), Out("Time") }),
                N("scale", FeatureCategory.Environment, ins: new[] { In("ValuePorts", "ReadOnlyMemory<double>[]") },
                  outs: new[] { Out("YRangePort", "RealRange") }),
                N("plot", outs: new[] { Out("macd"), Out("signal"), Out("hist") },
                  ins: new[] { In("InputPort"), In("YRangePort", "RealRange") }),
                N("line", ins: new[] { In("DataPort"), In("YRangePort", "RealRange") }),
                N("hit", FeatureCategory.Interaction, ins: new[] { In("XAxisDataPort") }),
            };
            var edges = new List<Edge>
            {
                E("ds", "plot", "Value", "InputPort"),
                E("ds", "scale", "Value", "ValuePorts"),
                E("plot", "scale", "macd", "ValuePorts"),
                E("plot", "scale", "signal", "ValuePorts"),
                E("plot", "scale", "hist", "ValuePorts"),
                E("scale", "plot", "YRangePort", "YRangePort"),
                E("scale", "line", "YRangePort", "YRangePort"),
                E("ds", "line", "Value", "DataPort"),
                E("ds", "hit", "Time", "XAxisDataPort"),
            };
            var state = AutoLayout.Apply(GraphState.Empty with { Nodes = nodes, Edges = edges });

            Assert.NotNull(state.EdgeRoutes);
            foreach (var e in state.Edges)
            {
                var route = state.EdgeRoutes![e.Id];
                var from = state.FindNode(e.FromNodeId)!;
                var to = state.FindNode(e.ToNodeId)!;
                var p0 = from.GetPortPosition(from.FindPort(e.FromPortId, false)!);
                var p3 = to.GetPortPosition(to.FindPort(e.ToPortId, true)!);
                Assert.Equal(p0.X, route[0].X, 1);
                Assert.Equal(p0.Y, route[0].Y, 1);
                Assert.Equal(p3.X, route[route.Count - 1].X, 1);
                Assert.Equal(p3.Y, route[route.Count - 1].Y, 1);

                for (int i = 0; i + 1 < route.Count; i++)
                {
                    var a = route[i];
                    var b = route[i + 1];
                    Assert.True(Math.Abs(a.X - b.X) < 0.01f || Math.Abs(a.Y - b.Y) < 0.01f, "折线应只有横竖段");
                    foreach (var n in state.Nodes)
                    {
                        // 线段不能从节点内部穿过(端点所在节点只看内部,不看边框)
                        var bx = n.GetBounds();
                        float lo = Math.Min(a.X, b.X), hi = Math.Max(a.X, b.X);
                        float top = Math.Min(a.Y, b.Y), bottom = Math.Max(a.Y, b.Y);
                        bool overlap = hi > bx.X + 1 && lo < bx.X + bx.Width - 1
                                    && bottom > bx.Y + 1 && top < bx.Y + bx.Height - 1;
                        Assert.False(overlap, $"{e.FromNodeId}.{e.FromPortId}->{e.ToNodeId}.{e.ToPortId} 穿过了 {n.Id}");
                    }
                }
            }
        }

        [Fact]
        public void FitTransform_NeverZoomsBelowReadableFloor()
        {
            // 内容远宽于视口:按宽度适配会缩到 0.3,但下限是 0.9,宁可横向超出由用户平移
            var nodes = Enumerable.Range(0, 12)
                .Select(i => N("n" + i) with { Position = new HevoPoint(i * 300f, 100f) })
                .ToList();
            var state = GraphState.Empty with { Nodes = nodes };
            var t = GraphInteractionFeature.ComputeFitTransform(state, 1200f, 800f, 0.9f);
            Assert.Equal(0.9f, t.Scale, 3);
            // 左上角对齐:最左节点落在视口左边距处
            Assert.Equal(20f, t.CanvasToScreen(nodes[0].Position).X, 1);

            // 内容小:不放大超过 1
            var small = GraphState.Empty with { Nodes = new[] { N("a") with { Position = new HevoPoint(0f, 0f) } } };
            Assert.Equal(1f, GraphInteractionFeature.ComputeFitTransform(small, 1200f, 800f, 0.9f).Scale, 3);
        }
    }
}
