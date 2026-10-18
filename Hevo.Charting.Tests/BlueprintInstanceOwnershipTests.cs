using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.LowCode.Designer;
using Xunit;
using static Hevo.Charting.Tests.CompositeDataSourceTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 框架替蓝图 new 出来的数据源归 schema 所有:schema 卸载时一起 Dispose;装配中途失败时立即 Dispose。
    /// 旧实现只托管渲染叶,Composite 的上游等其它自建实例没人释放。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintInstanceOwnershipTests
    {
        public sealed class OwnedProbeDs : ReactiveDataSource<OwnedProbeDs, string, TestRow>
        {
            private static readonly object _gate = new();
            public static readonly List<OwnedProbeDs> Created = new();
            public bool Disposed { get; private set; }

            public OwnedProbeDs() { lock (_gate) Created.Add(this); }

            protected override Task<int> OnFetchAsync(string? context, CancellationToken token) => Task.FromResult(0);
            public override int LogicalLength => 0;

            public override void Dispose()
            {
                Disposed = true;
                base.Dispose();
            }

            public static void Reset() { lock (_gate) Created.Clear(); }
        }

        public BlueprintInstanceOwnershipTests()
        {
            ComponentRegistry.Register<OwnedProbeDs>();
            OwnedProbeDs.Reset();
        }

        private static void DisposeSchema(object schema)
        {
            var field = typeof(Hevo.Charting.Core.ReactiveSchema).GetField(
                "_disposables", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(schema) is List<IDisposable> list)
            {
                foreach (var d in list) d?.Dispose();
                list.Clear();
            }
        }

        [Fact]
        public void FrameworkCreatedUpstreams_AreDisposedWithSchema()
        {
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "a", TypeName = nameof(OwnedProbeDs) },
                    new DataSourceModel { Id = "b", TypeName = nameof(OwnedProbeDs) },
                    new DataSourceModel { Id = "merged", TypeName = "Composite", UpstreamRefs = new List<string> { "a", "b" } },
                },
            };

            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);
            Assert.Equal(2, OwnedProbeDs.Created.Count);

            DisposeSchema(deferred.Schema);

            Assert.All(OwnedProbeDs.Created, ds => Assert.True(ds.Disposed));
        }

        [Fact]
        public void SuppliedInstances_AreNotDisposedBySchema()
        {
            // 调用方自己传进来的实例归调用方,schema 不越权释放。
            var supplied = new OwnedProbeDs();
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "a", TypeName = nameof(OwnedProbeDs) },
                    new DataSourceModel { Id = "merged", TypeName = "Composite", UpstreamRefs = new List<string> { "a" } },
                },
            };

            var deferred = BlueprintRunner.BuildSchemaDeferred(bp, new Dictionary<string, object> { ["a"] = supplied });
            DisposeSchema(deferred.Schema);

            Assert.False(supplied.Disposed);
        }

        [Fact]
        public void AssemblyFailure_DisposesFrameworkCreatedInstances()
        {
            // 两个互不引用的数据源 → 多渲染叶,装配失败;已经 new 出来的实例不能就这么丢着。
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "a", TypeName = nameof(OwnedProbeDs) },
                    new DataSourceModel { Id = "b", TypeName = nameof(OwnedProbeDs) },
                },
            };

            Assert.Throws<InvalidOperationException>(() => BlueprintRunner.BuildSchemaDeferred(bp));

            Assert.Equal(2, OwnedProbeDs.Created.Count);
            Assert.All(OwnedProbeDs.Created, ds => Assert.True(ds.Disposed));
        }
    }
}
