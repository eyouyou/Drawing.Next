namespace Hevo.Charting.Core
{
public readonly struct DataSnapshot<TItem>
    {
        /// <summary>原始数据数组引用 (允许在异步流中跨线程安全传递)</summary>
        public readonly TItem[] Items;
        
        /// <summary>当前有效的数据长度</summary>
        public readonly int Count;
        
        /// <summary>数据纪元令牌：查脏的绝对真理！</summary>
        public readonly VersionToken Version;

        /// <summary>每根是谁 / 更早的还有没有 / 纪元。原构造下为 default(不跟踪);ProjectExtent 转写进视口端口。</summary>
        public readonly DataExtent Extent;

        public DataSnapshot(TItem[] items, int count, VersionToken version)
            : this(items, count, version, default)
        {
        }

        public DataSnapshot(TItem[] items, int count, VersionToken version, DataExtent extent)
        {
            Items = items;
            Count = count;
            Version = version;
            Extent = extent;
        }

        /// <summary>
        /// 💥 性能核弹：瞬间将堆数组转化为栈上的连续内存指针！
        /// 只有在 Ingestor 内部开始跑 for 循环计算时才调用，零拷贝！
        /// </summary>
        public ReadOnlySpan<TItem> AsSpan() => Items.AsSpan(0, Count);

        public static DataSnapshot<TItem> Empty => new(Array.Empty<TItem>(), 0, default);
    }
}
