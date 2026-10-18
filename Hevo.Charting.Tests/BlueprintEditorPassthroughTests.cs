using System.Text.Json;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 编辑器载入再保存不能丢掉画布不表达的内容:未登记类型的节点、Triggers / TriggerBindings、
    /// 扇入端口上指向画布外的引用、顶层不认识的字段。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintEditorPassthroughTests
    {
        public BlueprintEditorPassthroughTests()
        {
            ComponentRegistry.Register<TestDataSource>();
            ComponentRegistry.Register<UniversalAutoScaleFeature>();
        }

        private static ChartBlueprint RoundTrip(ChartBlueprint bp)
            => GraphSerializer.ToBlueprint(GraphDeserializer.FromBlueprint(bp));

        private static ChartBlueprint Base() => new ChartBlueprint
        {
            DataSources = { new DataSourceModel { Id = "p", TypeName = nameof(TestDataSource),
                OutputBindings = new Dictionary<string, object?> { ["Value"] = "ds_v" } } },
        };

        [Fact]
        public void UnregisteredFeature_SurvivesRoundTrip()
        {
            var bp = Base();
            bp.Features.Add(new FeatureModel
            {
                TypeName = "PluginOnlyFeature",
                Properties = { ["Period"] = 14 },
                InputBindings = { ["DataPort"] = "ds_v" },
            });

            var bp2 = RoundTrip(bp);

            var f = Assert.Single(bp2.Features, x => x.TypeName == "PluginOnlyFeature");
            Assert.Equal(14, f.Properties["Period"]);
            Assert.Equal("ds_v", PortBindingValue.ExtractSingle(f.InputBindings["DataPort"]));
        }

        [Fact]
        public void UnregisteredDataSourceAndTrait_SurviveRoundTrip_WithTheirCascade()
        {
            var bp = Base();
            bp.DataSources.Add(new DataSourceModel { Id = "plug", TypeName = "PluginOnlyDataSource" });
            bp.InitialTraits.Add(new StyleModel { TraitTypeName = "PluginOnlyTrait", Preset = "Dark" });
            bp.Cascades.Add(new CascadeEdge { FromDataSourceId = "plug", ToDataSourceId = "p", ContextDriver = "drv" });

            var bp2 = RoundTrip(bp);

            Assert.Contains(bp2.DataSources, d => d.Id == "plug" && d.TypeName == "PluginOnlyDataSource");
            Assert.Contains(bp2.InitialTraits, t => t.TraitTypeName == "PluginOnlyTrait" && t.Preset == "Dark");
            Assert.Contains(bp2.Cascades, c => c.FromDataSourceId == "plug" && c.ToDataSourceId == "p" && c.ContextDriver == "drv");
        }

        [Fact]
        public void TriggersAndBindings_SurviveRoundTrip()
        {
            var bp = Base();
            bp.Triggers.Add(new TriggerModel { Id = "t1", IntervalSeconds = 3 });
            bp.TriggerBindings.Add(new TriggerBinding { TriggerId = "t1", TargetDataSourceId = "p", Verb = "Refresh" });

            var bp2 = RoundTrip(bp);

            Assert.Contains(bp2.Triggers, t => t.Id == "t1" && t.IntervalSeconds == 3);
            Assert.Contains(bp2.TriggerBindings, b => b.TriggerId == "t1" && b.TargetDataSourceId == "p");
        }

        [Fact]
        public void FanInPort_ExternalReference_SurvivesRoundTrip()
        {
            var bp = Base();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(UniversalAutoScaleFeature),
                InputBindings = { ["ValuePorts"] = new List<string> { "ds_v", "dashboard:other_cell_signal" } },
                OutputBindings = { ["YRangePort"] = "yr" },
            });

            var bp2 = RoundTrip(bp);

            var ids = PortBindingValue.ExtractList(bp2.Features.Single().InputBindings["ValuePorts"]);
            Assert.Contains("dashboard:other_cell_signal", ids);
            Assert.Contains(PortBindingValue.ExtractSingle(bp2.DataSources[0].OutputBindings["Value"]), ids);
        }

        [Fact]
        public void FanInPort_Disconnect_DropsExternalReferenceToo()
        {
            var bp = Base();
            bp.Features.Add(new FeatureModel
            {
                TypeName = nameof(UniversalAutoScaleFeature),
                InputBindings = { ["ValuePorts"] = new List<string> { "ds_v", "dashboard:other_cell_signal" } },
                OutputBindings = { ["YRangePort"] = "yr" },
            });
            var state = GraphDeserializer.FromBlueprint(bp);
            var node = state.Nodes.Single(n => n.TypeName == nameof(UniversalAutoScaleFeature));

            var bp2 = GraphSerializer.ToBlueprint(state.DisconnectInput(node.Id, "ValuePorts"));

            var f = bp2.Features.Single();
            Assert.True(!f.InputBindings.TryGetValue("ValuePorts", out var v) || PortBindingValue.ExtractList(v).Count == 0);
        }

        [Fact]
        public void TopLevelUnknownField_SurvivesRoundTrip()
        {
            var bp = JsonSerializer.Deserialize<ChartBlueprint>(
                @"{ ""DataSources"": [ { ""Id"": ""p"", ""TypeName"": ""TestDataSource"" } ], ""Annotations"": { ""author"": ""x"" } }",
                BlueprintJsonOptions.Default)!;

            var json = JsonSerializer.Serialize(RoundTrip(bp), BlueprintJsonOptions.Default);

            Assert.Contains("\"Annotations\"", json);
        }
    }
}
