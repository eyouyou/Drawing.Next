using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 蓝图管线策略按数据源基类回退:在基类(含开放泛型定义)上登记一次,所有子类都命中;
    /// 精确登记优先;都没登记时返回 null(走通用反射路径)。登记表是进程级静态的,这里只用本文件私有的类型,不清表。
    /// </summary>
    public sealed class PipelinePolicyBaseTypeFallbackTests
    {
        private sealed class Policy : IPipelinePolicy
        {
            public Policy(string name) { Name = name; }
            public string Name { get; }
            public void Compile(ChartBlueprint blueprint, object dataSource, object pipe, Func<Type, string, object> getOrCreatePort) { }
        }

        // 开放泛型基类(仿 Foundation 的 KLineSourceBase<TSelf, TContext>)
        private abstract class FrameSourceBase<TSelf, TContext> where TSelf : FrameSourceBase<TSelf, TContext> { }
        private sealed class ConcreteA : FrameSourceBase<ConcreteA, string> { }
        private class ConcreteB : FrameSourceBase<ConcreteB, int> { }
        private sealed class DerivedFromB : ConcreteB { }

        // 普通(非泛型)基类
        private abstract class PlainBase { }
        private sealed class PlainChild : PlainBase { }
        private sealed class ExactlyRegistered : PlainBase { }

        private sealed class Unregistered { }

        static PipelinePolicyBaseTypeFallbackTests()
        {
            ComponentMetadataRegistry.Register(typeof(FrameSourceBase<,>), new Policy("frame-base"));
            ComponentMetadataRegistry.Register(typeof(PlainBase), new Policy("plain-base"));
            ComponentMetadataRegistry.Register<ExactlyRegistered>(new Policy("exact"));
            ComponentMetadataRegistry.Register<ConcreteB>(new Policy("concrete-b"));
        }

        private static string? NameOf(Type t) => (ComponentMetadataRegistry.GetPipelinePolicy(t) as Policy)?.Name;

        [Fact]
        public void Subclass_WithoutOwnRegistration_UsesOpenGenericBasePolicy()
        {
            Assert.Equal("frame-base", NameOf(typeof(ConcreteA)));
        }

        [Fact]
        public void Subclass_WithoutOwnRegistration_UsesPlainBasePolicy()
        {
            Assert.Equal("plain-base", NameOf(typeof(PlainChild)));
        }

        [Fact]
        public void ExactRegistration_WinsOverBase()
        {
            Assert.Equal("exact", NameOf(typeof(ExactlyRegistered)));
            Assert.Equal("concrete-b", NameOf(typeof(ConcreteB)));
            // 子类的子类:先碰到最近的那一级登记(ConcreteB),而不是更上面的开放泛型基类
            Assert.Equal("concrete-b", NameOf(typeof(DerivedFromB)));
        }

        [Fact]
        public void NoRegistrationAnywhere_ReturnsNull_SoGenericPathIsUsed()
        {
            Assert.Null(ComponentMetadataRegistry.GetPipelinePolicy(typeof(Unregistered)));
            Assert.Null(ComponentMetadataRegistry.GetPipelinePolicy(typeof(object)));
        }
    }
}
