namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 💥 蓝图编译期透明优化代理。
    /// <para>
    /// 矛盾:图形引擎为 60 FPS 百万级渲染,某些数据源 (如 A 股分时图 240 点固定栅格) 走预分配数组占位,
    /// 而 JSON 蓝图层不应感知“是不是定长”这种性能细节。
    /// </para>
    /// <para>
    /// 解药:特定数据源类型在此登记 <see cref="IPipelinePolicy"/>,蓝图编译期一旦命中,
    /// 整支管线交给策略接管,反射通用拼装彻底绕过。蓝图侧 0 改动。
    /// </para>
    /// </summary>
    public static class ComponentMetadataRegistry
    {
        private static readonly Dictionary<Type, IPipelinePolicy> _policies = new();

        /// <summary>登记某 DataSource 类型对应的自定义管线编译策略。</summary>
        public static void Register<TDataSource>(IPipelinePolicy policy) where TDataSource : class
        {
            _policies[typeof(TDataSource)] = policy;
        }

        /// <summary>
        /// 同上,类型用 <see cref="Type"/> 给:可以登记基类(含开放泛型定义,如 <c>typeof(KLineSourceBase&lt;,&gt;)</c>),
        /// 一次覆盖它的所有子类,不用每个具体数据源各登记一遍。
        /// </summary>
        public static void Register(Type dataSourceType, IPipelinePolicy policy)
        {
            if (dataSourceType == null) throw new ArgumentNullException(nameof(dataSourceType));
            _policies[dataSourceType] = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>
        /// 查询 DataSource 类型的策略:先查精确类型;没有再沿基类链往上找,每一级先查封闭类型、再查它的开放泛型定义。
        /// 都没有返回 null,DynamicChartSchema 走通用反射路径。精确登记永远优先于基类登记。
        /// </summary>
        public static IPipelinePolicy? GetPipelinePolicy(Type dataSourceType)
        {
            for (var t = dataSourceType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (_policies.TryGetValue(t, out var policy)) return policy;
                if (t.IsGenericType && !t.IsGenericTypeDefinition && _policies.TryGetValue(t.GetGenericTypeDefinition(), out policy)) return policy;
            }
            return null;
        }

        /// <summary>清空注册(单元测试用)。</summary>
        public static void Reset() => _policies.Clear();
    }

    /// <summary>
    /// 蓝图自定义管线编译策略契约。
    /// 实现侧应负责把 <paramref>blueprint.DataSource</paramref> 上的 ScalarMappings/VectorMappings
    /// 翻译为对应的 <see cref="IDataIngestor{TItem}"/> 注入到 <paramref>pipe</paramref>。
    /// </summary>
    public interface IPipelinePolicy
    {
        /// <param name="blueprint">蓝图全量,允许策略读 InitialTraits / Features 决定是否额外补管线。</param>
        /// <param name="dataSource">数据源实例(已实例化)。</param>
        /// <param name="pipe">通用数据加工管线;策略通过 <see cref="UniversalDataPipe{TItem}.AddIngestor"/> 注入算子。</param>
        /// <param name="getOrCreatePort">引脚注册器:按 <c>(portDataType, portId)</c> 拿到/创建 <c>DataPort&lt;T&gt;</c> 实例,与 Feature 端口共享。</param>
        void Compile(
            ChartBlueprint blueprint,
            object dataSource,
            object pipe,
            Func<Type, string, object> getOrCreatePort);
    }

    /// <summary>
    /// 💥 强类型策略基类:省掉每个实现都手写一遍 cast 的样板。
    /// 业务策略派生 <see cref="PipelinePolicy{TDataSource, TItem}"/> 即可。
    /// </summary>
    public abstract class PipelinePolicy<TDataSource, TItem> : IPipelinePolicy where TDataSource : class
    {
        public void Compile(ChartBlueprint blueprint, object dataSource, object pipe, Func<Type, string, object> getOrCreatePort)
        {
            CompileTyped(blueprint, (TDataSource)dataSource, (UniversalDataPipe<TItem>)pipe, getOrCreatePort);
        }

        protected abstract void CompileTyped(
            ChartBlueprint blueprint,
            TDataSource dataSource,
            UniversalDataPipe<TItem> pipe,
            Func<Type, string, object> getOrCreatePort);
    }
}
