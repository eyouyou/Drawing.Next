using System.Runtime.CompilerServices;

namespace Hevo.Charting.LowCode
{
    using Hevo.Charting.Core;
    /// <summary>
    /// 寻址系统
    /// </summary>
    public interface IDataPort
    {
        string Id { get; }
        string DisplayName { get; }
    }

    /// <summary>
    /// 💥 强类型物理线缆：贯穿数据管线与特征组件的唯一凭证
    /// </summary>
    public record DataPort<T> : IDataPort
    {
        public string Id { get; }
        public string DisplayName { get; }

        public DataPort([CallerMemberName] string name = "")
        {
            DisplayName = string.IsNullOrEmpty(name) ? "AnonymousPort" : name;
            // 逻辑唯一标识：真名 + GUID
            Id = $"{DisplayName}_{Guid.NewGuid().GetHashCode():X8}";
        }

        public T? Value { get; set; }

        public override string ToString() => DisplayName; // 方便调试器查看


        /// <summary>
        /// 获取一个全局唯一的、类型为 T 的空壳引脚 (Dummy Port)。
        /// 用于可选端口:业务没接线时垫一个永远不写入的端口,OnProject 照常 UsePort,不用到处判 null(UsePort 不接受 null 端口)。
        /// </summary>
        public static DataPort<T> Empty => EmptyPortCache.Instance;

        // 💥 魔法：利用 C# 泛型静态类的特性，每种类型 T 只会实例化一次！
        private static class EmptyPortCache
        {
            // 给 Id 加上特殊的 "__EMPTY__" 前缀，方便底层调试时一眼看穿这是个假引脚
            public static readonly DataPort<T> Instance = new($"__EMPTY_{typeof(T).Name}__");
        }
    }

    /// <summary>
    /// 💥 0-GC 摄入期上下文载体 (Ingestion Payload)。
    /// 意图：将数据源与物理时钟打包，一次性传递给管线中的所有 Ingestor。
    /// 铁律：必须保持为 readonly struct，绝对禁止在管线高频分发时产生任何堆分配。
    /// </summary>
    public readonly struct IngestContext<TItem>
    {
        /// <summary>
        /// 原始数据源。
        /// 💥 架构提示：虽然签名是接口，但底层实际载体通常是 DataSnapshot 结构体。
        /// Ingestor 内部必须使用模式匹配 (is DataSnapshot) 提取 Span，严禁直接 foreach 导致隐式装箱。
        /// </summary>
        public IReadOnlyList<TItem> Source { get; }

        /// <summary>
        /// 数据源当前的物理纪元 (时钟)。
        /// 意图：当数据确实发生变更时，用于向下游黑板引脚 (DataPort) 写入新纪元，从而驱动底层 DAG 算子更新。
        /// </summary>
        public VersionToken SourceVersion { get; }

        /// <summary>
        /// 💥 核心防线：预计算脏标记 (Dirty Flag)。
        /// 意图：由管线头部 (UniversalDataPipe) 统一比对时钟后下发，避免 N 个 Ingestor 分别去重复比对。
        /// 约束：当此标记为 false 时，业务 Ingestor 的第一行代码必须是 if (!context.SourceChanged) return; 以实现 0 开销短路！
        /// </summary>
        public bool SourceChanged { get; }

        public IngestContext(IReadOnlyList<TItem> source, VersionToken sourceVersion, bool sourceChanged)
        {
            Source = source;
            SourceVersion = sourceVersion;
            SourceChanged = sourceChanged;
        }
    }

    /// <summary>
    /// 💥 数据摄入器底层接口
    /// </summary>
    public interface IDataIngestor<TItem>
    {
        void Process(DataSnapshot<TItem> snapshot, DataBlackboard board);
    }

    /// <summary>
    /// 阶段二：万能计算节点。泛型无关，只对黑板进行纯粹的数组运算。
    /// </summary>
    public interface IComputeNode
    {
        /// <summary>
        /// 冷启动：解析引脚映射
        /// 手动代码 不需要通过这个加载
        /// </summary>
        void Initialize(Dictionary<string, string> mappings, Dictionary<string, object> activePorts);
        /// <summary>热更新：执行 0 GC 核心计算</summary>
        void Execute(DataBlackboard board);
    }
}
