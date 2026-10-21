namespace Hevo.Charting.Core
{
    /// <summary>
    /// 一次发布相对上一次发布改了什么(按逻辑长度计的根数)。沿用 hevo.drawing Foundation 的 KLineChange 设计,上提到框架:
    /// 黑板 / 快照以前只有长度和版本,视口只能按长度差猜"前插还是追加"。
    /// <para>
    /// 几次修改合并成一次发布 / 一次通知时用 <see cref="Merge"/> 累加:<see cref="IsReset"/> 吞掉其它一切,其余逐项累加。
    /// 发布方不描述变化时,快照上是 null,下游按长度差处理(原行为)。
    /// </para>
    /// </summary>
    public readonly struct DataChange : IEquatable<DataChange>
    {
        private DataChange(bool isReset, int prepended, int appended, bool tailUpdated, bool historyUpdated)
        {
            IsReset = isReset;
            Prepended = prepended;
            Appended = appended;
            TailUpdated = tailUpdated;
            HistoryUpdated = historyUpdated;
        }

        public static readonly DataChange None = default;
        public static readonly DataChange Reset = new DataChange(true, 0, 0, false, false);

        public static DataChange Prepend(int count) => new DataChange(false, count, 0, false, false);
        public static DataChange Append(int count) => new DataChange(false, 0, count, false, false);
        public static readonly DataChange UpdateTail = new DataChange(false, 0, 0, true, false);
        public static readonly DataChange UpdateHistory = new DataChange(false, 0, 0, false, true);

        /// <summary>整段换掉(切合约 / 切周期 / 全量重拉)。下游当成全新数据。</summary>
        public bool IsReset { get; }

        /// <summary>左侧新插入的根数(翻历史)。</summary>
        public int Prepended { get; }

        /// <summary>右侧新追加的根数(新开一根 / 心跳补齐)。</summary>
        public int Appended { get; }

        /// <summary>最后一根原地改了(实时 tick)。</summary>
        public bool TailUpdated { get; }

        /// <summary>最后一根之外的历史改了(收盘后修正等),已按写时复制换了新数组。</summary>
        public bool HistoryUpdated { get; }

        public bool IsEmpty => !IsReset && Prepended == 0 && Appended == 0 && !TailUpdated && !HistoryUpdated;

        /// <summary>只有最后一根变了:依赖历史的缓存可以保留,只重算尾部。</summary>
        public bool IsTailOnly => !IsReset && Prepended == 0 && Appended == 0 && !HistoryUpdated && TailUpdated;

        public DataChange Merge(DataChange next)
        {
            if (IsReset || next.IsReset) return Reset;
            return new DataChange(false, Prepended + next.Prepended, Appended + next.Appended,
                TailUpdated || next.TailUpdated, HistoryUpdated || next.HistoryUpdated);
        }

        public bool Equals(DataChange other) =>
            IsReset == other.IsReset && Prepended == other.Prepended && Appended == other.Appended
            && TailUpdated == other.TailUpdated && HistoryUpdated == other.HistoryUpdated;

        public override bool Equals(object? obj) => obj is DataChange other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(IsReset, Prepended, Appended, TailUpdated, HistoryUpdated);

        public override string ToString()
        {
            if (IsReset) return "Reset";
            if (IsEmpty) return "None";
            return $"Prepend={Prepended} Append={Appended} Tail={TailUpdated} History={HistoryUpdated}";
        }
    }

    /// <summary>
    /// 写在黑板端口上的变化累计:每次发布记一笔,值类型、不分配。
    /// 只为一件事:Watch 回调会被合并(WatchAsync 只跑最新一次、联动镜像转写),只存"最近一次变化"会丢掉中间几次;
    /// 消费者留一份上次看到的值,用 <see cref="Since"/> 取合并后的 <see cref="DataChange"/>(合并规则同 <see cref="DataChange.Merge"/>)。
    /// 中间有没描述变化的发布,或长度对不上(有人只写了长度),返回 null,消费者按长度差处理。
    /// </summary>
    public readonly struct DataChangeLog : IEquatable<DataChangeLog>
    {
        private readonly long _lastReset, _lastUnknown, _lastTail, _lastHistory;

        private DataChangeLog(long sequence, int length, long prepended, long appended,
            long lastReset, long lastUnknown, long lastTail, long lastHistory)
        {
            Sequence = sequence;
            Length = length;
            TotalPrepended = prepended;
            TotalAppended = appended;
            _lastReset = lastReset;
            _lastUnknown = lastUnknown;
            _lastTail = lastTail;
            _lastHistory = lastHistory;
        }

        /// <summary>记了几笔。0 = 从没记过。</summary>
        public long Sequence { get; }

        /// <summary>最近一笔发布后的逻辑长度。</summary>
        public int Length { get; }

        public long TotalPrepended { get; }
        public long TotalAppended { get; }

        /// <summary>记一笔发布;<paramref name="change"/> 为 null = 发布方没描述变化。返回新值。</summary>
        public DataChangeLog Record(DataChange? change, int lengthAfter)
        {
            long seq = Sequence + 1;
            var c = change.GetValueOrDefault();
            return new DataChangeLog(seq, lengthAfter,
                TotalPrepended + c.Prepended, TotalAppended + c.Appended,
                c.IsReset ? seq : _lastReset,
                change.HasValue ? _lastUnknown : seq,
                c.TailUpdated ? seq : _lastTail,
                c.HistoryUpdated ? seq : _lastHistory);
        }

        /// <summary>从 <paramref name="earlier"/> 到本值之间合并后的变化;说不清时返回 null。</summary>
        public DataChange? Since(DataChangeLog earlier)
        {
            if (earlier.Sequence > Sequence) return null;
            if (earlier.Sequence == Sequence) return DataChange.None;
            if (_lastReset > earlier.Sequence) return DataChange.Reset;
            if (_lastUnknown > earlier.Sequence) return null;
            var change = DataChange.None;
            long pre = TotalPrepended - earlier.TotalPrepended, app = TotalAppended - earlier.TotalAppended;
            if (pre > 0) change = change.Merge(DataChange.Prepend((int)Math.Min(pre, int.MaxValue)));
            if (app > 0) change = change.Merge(DataChange.Append((int)Math.Min(app, int.MaxValue)));
            if (_lastTail > earlier.Sequence) change = change.Merge(DataChange.UpdateTail);
            if (_lastHistory > earlier.Sequence) change = change.Merge(DataChange.UpdateHistory);
            return change;
        }

        public bool Equals(DataChangeLog other) =>
            Sequence == other.Sequence && Length == other.Length && TotalPrepended == other.TotalPrepended
            && TotalAppended == other.TotalAppended && _lastReset == other._lastReset && _lastUnknown == other._lastUnknown
            && _lastTail == other._lastTail && _lastHistory == other._lastHistory;

        public override bool Equals(object? obj) => obj is DataChangeLog other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Sequence, Length, TotalPrepended, TotalAppended);
        public override string ToString() => $"#{Sequence} len={Length}";
    }
}
