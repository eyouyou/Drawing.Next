using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.WorkFlow;
using Xunit;
using static Hevo.Charting.Tests.CompositeDataSourceTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 渲染叶的 model 必须是 <c>PickRenderLeaf</c> 选中的那一个。旧实现在 schema 里按运行时类型名反查:
    /// Composite(运行时名 "Composite`1")查不到 → 叶的 OutputBindings 不编译;
    /// 同类型多数据源 → 查到第一个(可能是上游) → 用错 model 的 Properties / OutputBindings。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintRenderLeafModelTests
    {
        public BlueprintRenderLeafModelTests()
        {
            ComponentRegistry.Register<ShardA>();
            ComponentRegistry.Register<ShardB>();
        }

        private static DataSourceModel? RenderLeafModelOf(object schema)
        {
            var field = schema.GetType().GetField("_renderLeafModel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("DynamicChartSchema._renderLeafModel 反射不到。");
            return (DataSourceModel?)field.GetValue(schema);
        }

        private static void DisposeSchema(object schema)
        {
            var field = typeof(Hevo.Charting.Core.ReactiveSchema).GetField(
                "_disposables", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(schema) is List<IDisposable> list)
            {
                foreach (var d in list) try { d?.Dispose(); } catch { }
                list.Clear();
            }
        }

        [Fact]
        public void Composite_Leaf_UsesCompositeModel()
        {
            var merged = new DataSourceModel
            {
                Id = "merged",
                TypeName = "Composite",
                UpstreamRefs = new List<string> { "sh", "sz" },
                OutputBindings = new Dictionary<string, object?> { ["Price"] = "px" },
            };
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA) },
                    new DataSourceModel { Id = "sz", TypeName = nameof(ShardB) },
                    merged,
                },
            };

            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);
            try
            {
                Assert.IsType<Composite<TestRow>>(deferred.LeafInstance);
                Assert.Same(merged, RenderLeafModelOf(deferred.Schema));
            }
            finally { DisposeSchema(deferred.Schema); }
        }

        [Fact]
        public void SameTypeUpstreamAndLeaf_UsesLeafModel()
        {
            // 上游与叶同类型:按类型名反查会拿到排在前面的上游 model。
            var leaf = new DataSourceModel
            {
                Id = "leaf",
                TypeName = nameof(ShardA),
                OutputBindings = new Dictionary<string, object?> { ["Price"] = "leaf_px" },
            };
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel
                    {
                        Id = "up",
                        TypeName = nameof(ShardA),
                        OutputBindings = new Dictionary<string, object?> { ["Price"] = "up_px" },
                    },
                    leaf,
                },
                Cascades = { new CascadeEdge { FromDataSourceId = "up", ToDataSourceId = "leaf" } },
            };

            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);
            try
            {
                Assert.Same(leaf, RenderLeafModelOf(deferred.Schema));
            }
            finally { DisposeSchema(deferred.Schema); }
        }
    }
}
