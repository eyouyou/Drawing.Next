using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 编辑器改线 / 断线 / 删上游节点后保存,必须反映画布上看到的连线,而不是载入时记在
    /// <see cref="Port.BindingId"/> 上的旧 binding。旧实现下 BindingId 优先于入边,改线后保存仍写旧值。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintEditorEdgeRewireTests
    {
        public BlueprintEditorEdgeRewireTests()
        {
            ComponentRegistry.Register<TestDataSource>();
            ComponentRegistry.Register<ComputeFeature>();
        }

        // ds.Value → A(sig_a)、ds.Value → B(sig_b);Line.DataPort 载入时接 sig_a。
        private static ChartBlueprint MakeBlueprint() => new ChartBlueprint
        {
            DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(TestDataSource),
                OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" } } },
            Features = new List<FeatureModel>
            {
                new FeatureModel
                {
                    TypeName       = nameof(ComputeFeature),
                    InputBindings  = new Dictionary<string, object?> { ["InputPort"]  = "ds_v" },
                    OutputBindings = new Dictionary<string, object?> { ["OutputPort"] = "sig_a" },
                },
                new FeatureModel
                {
                    TypeName       = nameof(ComputeFeature),
                    InputBindings  = new Dictionary<string, object?> { ["InputPort"]  = "ds_v" },
                    OutputBindings = new Dictionary<string, object?> { ["OutputPort"] = "sig_b" },
                },
                new FeatureModel
                {
                    TypeName      = nameof(LineSeriesFeature),
                    InputBindings = new Dictionary<string, object?> { [nameof(LineSeriesFeature.DataPort)] = "sig_a" },
                },
            },
        };

        private static (GraphState state, Node line, Node a, Node b) Load()
        {
            var state = GraphDeserializer.FromBlueprint(MakeBlueprint());
            var line = state.Nodes.Single(n => n.TypeName == nameof(LineSeriesFeature));
            var computes = state.Nodes.Where(n => n.TypeName == nameof(ComputeFeature)).ToList();
            var a = computes.Single(n => n.OutputPorts.Any(p => p.BindingId == "sig_a"));
            var b = computes.Single(n => n.OutputPorts.Any(p => p.BindingId == "sig_b"));
            return (state, line, a, b);
        }

        private static object? LineDataBinding(ChartBlueprint bp)
        {
            var line = bp.Features.Single(f => f.TypeName == nameof(LineSeriesFeature));
            return line.InputBindings.TryGetValue(nameof(LineSeriesFeature.DataPort), out var v) ? v : null;
        }

        [Fact]
        public void Rewire_ThenSave_WritesNewSource()
        {
            var (state, line, _, b) = Load();
            var rewired = state.AddEdge(new Edge("e_new", b.Id, "OutputPort", line.Id, nameof(LineSeriesFeature.DataPort)));

            var bp2 = GraphSerializer.ToBlueprint(rewired);

            Assert.Equal("sig_b", PortBindingValue.ExtractSingle(LineDataBinding(bp2)));
        }

        [Fact]
        public void Rewire_ByReplacingEdgesDirectly_StillWritesNewSource()
        {
            // 防御:有人绕过 AddEdge 直接改 Edges,编码侧也要以入边为准。
            var (state, line, _, b) = Load();
            var edges = state.Edges
                .Where(e => !(e.ToNodeId == line.Id && e.ToPortId == nameof(LineSeriesFeature.DataPort)))
                .Append(new Edge("e_new", b.Id, "OutputPort", line.Id, nameof(LineSeriesFeature.DataPort)))
                .ToList();

            var bp2 = GraphSerializer.ToBlueprint(state with { Edges = edges });

            Assert.Equal("sig_b", PortBindingValue.ExtractSingle(LineDataBinding(bp2)));
        }

        [Fact]
        public void Disconnect_ThenSave_DropsBinding()
        {
            var (state, line, _, _) = Load();
            var cut = state.DisconnectInput(line.Id, nameof(LineSeriesFeature.DataPort));

            var bp2 = GraphSerializer.ToBlueprint(cut);

            Assert.True(string.IsNullOrEmpty(PortBindingValue.ExtractSingle(LineDataBinding(bp2))),
                "断线后保存仍写着旧 binding");
        }

        [Fact]
        public void DeleteUpstreamNode_ThenSave_DropsBinding()
        {
            var (state, line, a, _) = Load();
            var removed = state.WithoutNode(a.Id);

            var bp2 = GraphSerializer.ToBlueprint(removed);

            Assert.True(string.IsNullOrEmpty(PortBindingValue.ExtractSingle(LineDataBinding(bp2))),
                "上游节点删掉后保存仍写着指向它的旧 binding");
        }

        [Fact]
        public void Untouched_ExternalBinding_IsKeptVerbatim()
        {
            // 画布外引用(dashboard 共享端口)载入后没有入边,不动它时保存必须原样写回。
            var bp = MakeBlueprint();
            bp.Features[2].InputBindings[nameof(LineSeriesFeature.DataPort)] = "dashboard:shared_close";
            var state = GraphDeserializer.FromBlueprint(bp);

            var bp2 = GraphSerializer.ToBlueprint(state);

            Assert.Equal("dashboard:shared_close", PortBindingValue.ExtractSingle(LineDataBinding(bp2)));
        }
    }
}
