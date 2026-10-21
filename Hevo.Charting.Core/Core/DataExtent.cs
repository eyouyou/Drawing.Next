namespace Hevo.Charting.Core
{
    /// <summary>
    /// 逻辑长度的伴生:每根是谁(<see cref="Keys"/>)、更早的还有没有(<see cref="OlderExhausted"/>)、
    /// 整段换没换(<see cref="Epoch"/>)。数据源随快照发布(<c>BufferedDataSource.Publish(DataExtent)</c>),
    /// <c>ProjectExtent</c> 写进 <c>ViewportPorts.Extent</c>。
    /// <para>
    /// 只描述"现在是什么",不描述"刚才改了什么":通知被合并也不丢信息。<c>default</c> = 不跟踪,
    /// 视口 / 分页 / 增量指标都走原逻辑。
    /// </para>
    /// <para>
    /// 契约:<see cref="Keys"/> 严格递增,长度等于逻辑长度(对不上时视口退回按长度差猜);单位由数据源定,框架只比大小。
    /// 切合约不靠本类型表达,仍走 <c>SwitchContext</c> 的空帧(长度归零 → 视口回首屏)。
    /// </para>
    /// </summary>
    public readonly struct DataExtent : IEquatable<DataExtent>
    {
        /// <param name="epoch">用 <c>new object()</c>;整段替换(复权 / 换月:键没变、值全变)时换一个。</param>
        /// <param name="keys">每根的键,与发布数组下标一一对应。</param>
        /// <param name="olderExhausted">更早的已经没有了(数据源的到底判据)。</param>
        public DataExtent(object epoch, ReadOnlyMemory<long> keys, bool olderExhausted)
        {
            if (epoch == null) throw new ArgumentNullException(nameof(epoch));
            // 按引用比较:值类型每次装箱都是新引用,会被当成每帧整段替换,增量指标每帧全量
            System.Diagnostics.Debug.Assert(!epoch.GetType().IsValueType, "DataExtent.Epoch 用 new object(),不要传数字");
            Epoch = epoch;
            Keys = keys;
            OlderExhausted = olderExhausted;
        }

        /// <summary>纪元,按引用比较。增量指标读:换了 → 清状态全量。<c>null</c> = 不跟踪。</summary>
        public object? Epoch { get; }

        /// <summary>每根的键。视口按锚点键找回位置;分页模块算锚点前还有几根;增量指标核对上次算到的那根。</summary>
        public ReadOnlyMemory<long> Keys { get; }

        /// <summary>更早的没有了。分页模块换算成左墙写 AvailabilityPort,并停止要更早的。</summary>
        public bool OlderExhausted { get; }

        /// <summary>数据源发了 Extent。为 false 时下游走原逻辑。</summary>
        public bool IsTracked => Epoch != null;

        // 黑板 Write 用 EqualityComparer<T>.Default:不实现 IEquatable 会装箱,tick 路径多分配。
        // Keys 按 ReadOnlyMemory 自带语义比(底层数组 + 起点 + 长度),不比内容 —— 末根原地改不算变化。
        public bool Equals(DataExtent other) =>
            ReferenceEquals(Epoch, other.Epoch) && Keys.Equals(other.Keys) && OlderExhausted == other.OlderExhausted;

        public override bool Equals(object? obj) => obj is DataExtent other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Epoch == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Epoch), Keys.Length, OlderExhausted);
    }
}
