using System.Text.Json;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 旧格式字段(FeatureModel.PortBindings、DataSourceModel.ScalarMappings / VectorMappings、顶层 DataSource)
    /// 不能被反序列化静默丢掉:能无损翻译的迁到新字段,翻不了的装配期报错;其余不认识的字段 DryRun 提示。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintLegacyFieldTests
    {
        public BlueprintLegacyFieldTests()
        {
            ComponentRegistry.Register<TestDataSource>();
        }

        private static ChartBlueprint Parse(string json)
            => JsonSerializer.Deserialize<ChartBlueprint>(json, BlueprintJsonOptions.Default)!;

        private const string LegacyJson = @"{
            ""DataSource"": { ""TypeName"": ""TestDataSource"", ""VectorMappings"": { ""Value"": ""ds_v"" } },
            ""Features"": [
                { ""TypeName"": ""LineSeriesFeature"", ""PortBindings"": { ""DataPort"": ""ds_v"" } }
            ]
        }";

        [Fact]
        public void LegacyFields_AreMigrated_OnEditorLoad()
        {
            var bp = Parse(LegacyJson);

            var bp2 = GraphSerializer.ToBlueprint(GraphDeserializer.FromBlueprint(bp));

            var ds = Assert.Single(bp2.DataSources);
            Assert.Equal(nameof(TestDataSource), ds.TypeName);
            var line = bp2.Features.Single(f => f.TypeName == nameof(LineSeriesFeature));
            Assert.Equal(
                PortBindingValue.ExtractSingle(ds.OutputBindings["Value"]),
                PortBindingValue.ExtractSingle(line.InputBindings[nameof(LineSeriesFeature.DataPort)]));
        }

        [Fact]
        public void LegacyFields_AreReported_ByDryRun()
        {
            var result = BlueprintLauncher.DryRun(Parse(LegacyJson));

            Assert.Null(result.Error);
            Assert.Contains(result.Diagnostics, d => d.Code == "BP_LEGACY_FIELD_MIGRATED");
        }

        [Fact]
        public void LegacyFeaturePortBindings_AreWiredAtRuntime()
        {
            var bp = Parse(LegacyJson);

            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);

            var line = bp.Features.Single();
            Assert.Equal("ds_v", PortBindingValue.ExtractSingle(line.InputBindings[nameof(LineSeriesFeature.DataPort)]));
            Assert.Equal("ds_v", PortBindingValue.ExtractSingle(bp.DataSources.Single().OutputBindings["Value"]));
            Assert.NotNull(deferred.Schema);
        }

        [Fact]
        public void UnmigratableLegacyField_FailsAtAssembly()
        {
            // 新旧数据源字段同时出现:分不清以哪个为准,不能挑一个静默丢掉另一个。
            var bp = Parse(@"{
                ""DataSource"":  { ""Id"": ""old"", ""TypeName"": ""TestDataSource"" },
                ""DataSources"": [ { ""Id"": ""new"", ""TypeName"": ""TestDataSource"" } ]
            }");

            var ex = Assert.Throws<InvalidOperationException>(() => BlueprintRunner.BuildSchemaDeferred(bp));
            Assert.Contains("DataSource", ex.Message);
        }

        [Fact]
        public void UnknownField_IsReported_ByDryRun()
        {
            var bp = Parse(@"{
                ""DataSources"": [ { ""Id"": ""p"", ""TypeName"": ""TestDataSource"", ""DefaultContxt"": ""typo"" } ]
            }");

            var result = BlueprintLauncher.DryRun(bp);

            Assert.Contains(result.Diagnostics, d => d.Code == "BP_UNKNOWN_FIELD" && d.Message.Contains("DefaultContxt"));
        }

        [Fact]
        public void UnknownField_SurvivesJsonRoundTrip()
        {
            var bp = Parse(@"{ ""DataSources"": [ { ""Id"": ""p"", ""TypeName"": ""TestDataSource"", ""Future"": 1 } ] }");

            var json = JsonSerializer.Serialize(bp, BlueprintJsonOptions.Default);

            Assert.Contains("\"Future\"", json);
        }
    }
}
