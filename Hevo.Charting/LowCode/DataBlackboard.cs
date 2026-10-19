using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 💥 现代高性能状态黑板 (精确制导 + 事务合并 + 0-GC 存储 + 无限纪元)
    /// 💥 [读写分离最终版] 完美支持并发读、独占写、以及牛逼的“读锁原地升级写锁”！
    /// </summary>
    public class DataBlackboard : IDisposable
    {
        // 关门标志:Dispose 第一步就置位,之后新来的拿锁 / 订阅在碰锁之前就能看到
        private volatile bool _isDisposed;
        public bool IsDisposed => _isDisposed;
        private readonly TypedBucketMap _memory = new();
        private readonly StateClock _boardClock = new();
        // 只在写锁内写、持锁读(GetVersion 有 Debug 断言),普通字典即可(00 号文档:内部不用 ConcurrentDictionary)
        private readonly Dictionary<object, VersionToken> _portTokens = new();

        // ==========================================
        // 🛡️ 核心装甲升级：工业级读写分离锁
        // 💥 必须开启 SupportsRecursion！这是支持 WriteScope 嵌套、以及 UpdateScope 里升级写锁的物理基础！
        // ==========================================
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);

        private int _transactionDepth = 0;
        private readonly HashSet<object> _dirtyPorts = new();

        // 修复 H3：预分配快照缓冲，替代 EndWriteScope 里每次 ToList() 的堆分配。
        // _portsSnapshot 在首次事务提交后增长到足够容量，之后复用，热路径零分配。
        private readonly List<object> _portsSnapshot = new();

        /// <summary>端口最新写入的版本令牌;没写过返回 default。须持锁调用(同 <see cref="Read{T}"/>)。</summary>
        internal VersionToken GetVersion(object port)
        {
            AssertLockHeld(port);
            return _portTokens.TryGetValue(port, out var token) ? token : default;
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertLockHeld(object port)
        {
            if (!_rwLock.IsReadLockHeld && !_rwLock.IsWriteLockHeld && !_rwLock.IsUpgradeableReadLockHeld)
                throw new InvalidOperationException($"[黑板越权访问] 读引脚 {port} 的版本号失败！必须在 ReadScope() / UpdateScope() / WriteScope() 内读！");
        }

        // 列端口(ReadOnlyMemory<T>)的"引用版本":只在新值的底层引用(数组 / 起点 / 长度)跟当前值不同时才变。
        // 实时 tick 原地改最后一根再 ForceWrite 同一块 ROM → 版本号(GetVersion)变、引用版本不变;
        // 追加 / 写时复制 / 缓冲池换块(包括 A → B → A 换回同一块数组)→ 引用版本变。
        // 消费者只存这个令牌、不存列引用,不违反"列只在当帧有效"(缓冲会被纪元回收重写)。
        // 跟 _portTokens 分开存:实时 tick 原地改最后一根只写 _portTokens,合并成一本这条最热路径要多一次查找
        private readonly Dictionary<object, VersionToken> _identityTokens = new();

        /// <summary>
        /// 列端口的引用版本:底层引用(数组 / 起点 / 长度)换了才变。非列端口、没写过的端口返回 default。
        /// 用于"版本变了但引用没变 = 只有最后一根被原地改"这类判断(如 CandleFeature 静态层)。
        /// </summary>
        internal VersionToken GetIdentityVersion(object port)
        {
            AssertLockHeld(port);
            return _identityTokens.TryGetValue(port, out var token) ? token : default;
        }

        // 只有列端口才记引用版本;按 T 静态缓存,JIT 对其它端口类型折叠掉这段判断
        private static class ColumnType<T>
        {
            public static readonly bool IsColumn =
                typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>);
        }

        // ==========================================
        // 读写作用域(都配 using):
        //   ReadScope   只读;连读几个端口时拿到的是同一时刻的值
        //   UpdateScope 先读、看情况再写;读的这段不挡普通读者,要写时在里面嵌套 WriteScope
        //   WriteScope  写;作用域里的写入先攒着,最外层 WriteScope 结束时统一通知订阅者(一次)
        // 返回 readonly struct:每次 using 零堆分配(交互 / 视口 / 分页热路径每个鼠标事件都要开一次)。
        // ==========================================

        /// <summary>只读作用域。连读几个端口要求同一时刻时用;OnProject / 手势回调里框架已持锁,不用再开。</summary>
        public BlackboardScope ReadScope()
        {
            ThrowIfDisposed();
            _rwLock.EnterReadLock();
            return new BlackboardScope(this, ScopeKind.Read);
        }

        /// <summary>
        /// 读后写作用域:先读、看情况再写。读的这段不挡普通读者,同一时刻只有一个 UpdateScope / WriteScope。
        /// 要写时在里面嵌套 <see cref="WriteScope"/>;内层结束就通知订阅者,外层后面接着读能读到 Watch 算出的派生值。
        /// </summary>
        public BlackboardScope UpdateScope()
        {
            ThrowIfDisposed();
            _rwLock.EnterUpgradeableReadLock();
            return new BlackboardScope(this, ScopeKind.Update);
        }

        /// <summary>
        /// 写作用域:独占写锁;作用域里的 <see cref="Write{T}"/> / <see cref="ForceWrite{T}"/> 先攒着,
        /// 最外层 WriteScope 结束时按端口统一通知,几个端口一起写订阅者也只看到一次。
        /// 可嵌套;在 <see cref="UpdateScope"/> 里开即为升级。
        /// </summary>
        public BlackboardScope WriteScope()
        {
            ThrowIfDisposed();
            _rwLock.EnterWriteLock();
            _transactionDepth++;
            return new BlackboardScope(this, ScopeKind.Write);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(DataBlackboard));
        }

        internal enum ScopeKind : byte { Read, Update, Write }

        /// <summary><see cref="ReadScope"/> / <see cref="UpdateScope"/> / <see cref="WriteScope"/> 的返回值,只用于 using。</summary>
        public readonly struct BlackboardScope : IDisposable
        {
            private readonly DataBlackboard _b;
            private readonly ScopeKind _kind;
            internal BlackboardScope(DataBlackboard b, ScopeKind kind) { _b = b; _kind = kind; }
            public void Dispose()
            {
                switch (_kind)
                {
                    case ScopeKind.Write: _b.EndWriteScope(); break;
                    case ScopeKind.Update: _b.ExitScope(ScopeKind.Update); break;
                    default: _b.ExitScope(ScopeKind.Read); break;
                }
            }
        }

        private void ExitScope(ScopeKind kind)
        {
            var pending = TakeAfterUnlockIfLastHold();
            if (kind == ScopeKind.Update) _rwLock.ExitUpgradeableReadLock();
            else _rwLock.ExitReadLock();
            RunAfterUnlock(pending);
        }

        // ==========================================
        // 锁外回调:写锁内登记,本线程彻底退出这块板的锁之后再执行。
        // 给联动镜像这类"要去写别的板"的订阅者用 —— 在本板锁内拿别的板的锁,
        // 两块板在两个线程上反向镜像就互相等死。
        // 登记只发生在写锁内、取出发生在最后一层锁退出前,所以列表始终只被持锁线程碰。
        // ==========================================
        private readonly List<IAfterUnlockAction> _afterUnlock = new();

        internal void PostAfterUnlock(IAfterUnlockAction action)
        {
            if (!_rwLock.IsWriteLockHeld)
                throw new InvalidOperationException("PostAfterUnlock 只能在写锁内调用。");
            if (!_afterUnlock.Contains(action)) _afterUnlock.Add(action);
        }

        // 返回 null / 单个 IAfterUnlockAction / IAfterUnlockAction[]:常见情况只登记一项,不为它分配数组
        private object? TakeAfterUnlockIfLastHold()
        {
            if (_afterUnlock.Count == 0) return null;
            int holds = _rwLock.RecursiveReadCount + _rwLock.RecursiveWriteCount + _rwLock.RecursiveUpgradeCount;
            if (holds != 1) return null;
            object pending = _afterUnlock.Count == 1 ? _afterUnlock[0] : _afterUnlock.ToArray();
            _afterUnlock.Clear();
            return pending;
        }

        private void RunAfterUnlock(object? pending)
        {
            if (pending is IAfterUnlockAction single) single.Run(this);
            else if (pending is IAfterUnlockAction[] many)
                for (int i = 0; i < many.Length; i++) many[i].Run(this);
        }

        // ==========================================
        // Watch 表:按注册顺序的一张表 + 每项一个脏标志。
        // 写入时把关心该端口的项标脏,提交时(深度为零时的单次写入末尾 / 最外层 WriteScope 结束)按注册顺序只跑脏的。
        // 顺序、重入(回调里再写再提交 = 嵌套再扫一遍,深度优先)、异常(标志调用前清,后面的留到下次提交)
        // 都跟以前"每次提交把所有 Watch 的提交回调调一遍、各自看自己脏没脏"一致,只是不再进两把锁。
        // 脏标志、_dirtyCount、两张表只在写锁内碰;退订只置 Removed 不拿锁,写锁内不在扫表时再压缩。
        // ==========================================
        private sealed class WatchEntry : IDisposable
        {
            public readonly DataBlackboard Board;
            public readonly object[] Ports;
            public readonly Action<DataBlackboard> OnCommit;
            public bool Dirty;
            public volatile bool Removed;

            public WatchEntry(DataBlackboard board, object[] ports, Action<DataBlackboard> onCommit)
            {
                Board = board;
                Ports = ports;
                OnCommit = onCommit;
            }

            // 不拿锁:持读锁时、在别的 Watch 回调里、黑板已释放时退订都不会撞锁。
            // 别的线程正在跑的那次提交仍可能再调一次(调用方自己复查,见 AttachWatchCore)。
            public void Dispose()
            {
                if (Removed) return;
                Removed = true;
                Board._hasRemovedWatches = true;
            }
        }

        private readonly List<WatchEntry> _watches = new();                         // 注册顺序
        private readonly Dictionary<object, WatchEntry[]> _watchersByPort = new();   // 增删时整组换新数组
        private int _dirtyCount;
        private int _commitDepth;                                                    // >0 = 正在扫表,不能压缩
        private volatile bool _hasRemovedWatches;

        /// <summary>
        /// 登记一个 Watch:<paramref name="ports"/> 里任一端口被写过,下一次提交时调用一次 <paramref name="onCommit"/>
        /// (一次提交里写了它关心的多个端口也只调一次)。返回的句柄 Dispose 即退订,不拿锁。
        /// 已释放的黑板上登记静默返回空句柄:管线销毁时订阅链从上游往下游释放,
        /// pipe(连同黑板)先于下游 Watch 的清理被释放,这里抛异常会截断后面的清理。
        /// </summary>
        internal IDisposable AddWatch(object[] ports, Action<DataBlackboard> onCommit)
        {
            if (!TryEnterWriteLockUnlessDisposed()) return DisposableAction.Empty;
            try
            {
                if (_commitDepth == 0) CompactWatches();
                var entry = new WatchEntry(this, ports, onCommit);
                _watches.Add(entry);
                for (int i = 0; i < ports.Length; i++) AppendWatcher(ports[i], entry);
                return entry;
            }
            finally
            {
                var pending = TakeAfterUnlockIfLastHold();
                _rwLock.ExitWriteLock();
                RunAfterUnlock(pending);
            }
        }

        private void AppendWatcher(object port, WatchEntry entry)
        {
            if (!_watchersByPort.TryGetValue(port, out var old))
            {
                _watchersByPort[port] = new[] { entry };
                return;
            }
            if (Array.IndexOf(old, entry) >= 0) return; // 同一端口在 ports 里出现两次
            var next = new WatchEntry[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = entry;
            _watchersByPort[port] = next;
        }

        // 写锁内、不在扫表时调用:把已退订的项从两张表里拿掉
        private void CompactWatches()
        {
            if (!_hasRemovedWatches) return;
            _hasRemovedWatches = false;
            int kept = 0;
            for (int i = 0; i < _watches.Count; i++)
            {
                var e = _watches[i];
                if (e.Removed)
                {
                    if (e.Dirty) { e.Dirty = false; _dirtyCount--; }
                    continue;
                }
                _watches[kept++] = e;
            }
            if (kept == _watches.Count) return;
            _watches.RemoveRange(kept, _watches.Count - kept);

            _watchersByPort.Clear();
            for (int i = 0; i < _watches.Count; i++)
            {
                var e = _watches[i];
                for (int j = 0; j < e.Ports.Length; j++) AppendWatcher(e.Ports[j], e);
            }
        }

        // 写锁内:深度为零时的单次写入末尾、最外层 WriteScope 结束
        private void Commit()
        {
            if (_dirtyCount > 0)
            {
                _commitDepth++;
                try
                {
                    // 每轮重读 Count:回调里登记的新 Watch 追加在末尾,本轮照样扫到
                    for (int i = 0; i < _watches.Count; i++)
                    {
                        var e = _watches[i];
                        if (!e.Dirty) continue;
                        e.Dirty = false;            // 调用前清:抛异常也不会下次重跑
                        _dirtyCount--;
                        if (!e.Removed) e.OnCommit(this);
                    }
                }
                finally
                {
                    _commitDepth--;
                }
            }
            if (_hasRemovedWatches && _commitDepth == 0) CompactWatches();
        }

        // ==========================================
        // 端口写入观察者:Schema 判脏占固定槽,DevTools 内存雷达等其余观察者进可选数组。
        // 写入热路径只多一次字段读 + 空检查。增删很少(切板 / 挂卸雷达),在 _observerGate 里换新数组。
        // ==========================================
        private volatile IBoardObserver? _observer;
        private volatile IBoardObserver[]? _extraObservers;
        private readonly object _observerGate = new();

        internal void AddObserver(IBoardObserver observer)
        {
            lock (_observerGate)
            {
                if (_observer == null) { _observer = observer; return; }
                var old = _extraObservers;
                if (old == null) { _extraObservers = new[] { observer }; return; }
                var next = new IBoardObserver[old.Length + 1];
                Array.Copy(old, next, old.Length);
                next[old.Length] = observer;
                _extraObservers = next;
            }
        }

        internal void RemoveObserver(IBoardObserver observer)
        {
            lock (_observerGate)
            {
                if (ReferenceEquals(_observer, observer))
                {
                    _observer = null;
                    return;
                }
                var old = _extraObservers;
                if (old == null) return;
                int idx = Array.IndexOf(old, observer);
                if (idx < 0) return;
                if (old.Length == 1) { _extraObservers = null; return; }
                var next = new IBoardObserver[old.Length - 1];
                Array.Copy(old, 0, next, 0, idx);
                Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
                _extraObservers = next;
            }
        }

        private bool TryEnterWriteLockUnlessDisposed()
        {
            if (_isDisposed) return false;
            try { _rwLock.EnterWriteLock(); return true; }
            catch (ObjectDisposedException) { return false; }
        }

        // ==========================================
        // 📖 极速裸读：没有任何性能损耗！
        // ==========================================
        public T Read<T>(DataPort<T> port)
        {
#if DEBUG
            DevTools.TracerRegistry.Get(this)?.RecordRead(port);

            // 💥 引擎级防弹衣：你敢不带套（不加锁）就来读黑板？
            // 强制约束业务层:必须在 ReadScope / UpdateScope / WriteScope 内调用 Read
            if (!_rwLock.IsReadLockHeld && !_rwLock.IsWriteLockHeld && !_rwLock.IsUpgradeableReadLockHeld)
            {
                throw new InvalidOperationException($"[黑板越权访问] 读引脚 {port.Id} 失败！必须在 ReadScope() / UpdateScope() / WriteScope() 内读！");
            }
#endif
            if (_memory.TryGetValue<DataPort<T>, T>(port, out var val)) return val;
            return default!;
        }

        // ==========================================
        // ✍️ 极速裸写：把升级和加锁的权利还给外层！
        // ==========================================
        /// <summary>值变了才写(<see cref="EqualityComparer{T}.Default"/> 比较);须在 <see cref="WriteScope"/> 内。</summary>
        public void Write<T>(DataPort<T> port, T value)
        {
            // 1. 极速查脏
            if (_memory.TryGetValue<DataPort<T>, T>(port, out var oldVal))
            {
                if (EqualityComparer<T>.Default.Equals(oldVal, value)) return;
            }
            ForceWrite(port, value);
        }

        /// <summary>跳过查脏直接写(如 K 线最后一根原地改后重发同一块列);须在 <see cref="WriteScope"/> 内。</summary>
        public void ForceWrite<T>(DataPort<T> port, T value)
        {
#if DEBUG
            // 用 RecordWriteWithValue 而非 RecordWrite —— 顺手把 value 缓存进 tracer.LastValues。
            // Inspector dump 走 tracer 这条路,不再依赖 board 是否还活着,绕开蓝图 one-shot 数据源
            // pipe.Dispose 后 _latestBoard 悬挂的 lifecycle 坑。
            DevTools.TracerRegistry.Get(this)?.RecordWriteWithValue(port, value);

            // 💥 引擎级防弹衣：必须持有写锁才能修改数据！
            if (!_rwLock.IsWriteLockHeld)
            {
                throw new InvalidOperationException($"[黑板越权篡改] 写引脚 {port.Id} 失败！必须在 WriteScope() 内写！");
            }
#endif
            // 列端口:先跟当前值比引用(ROM.Equals 比数组 / 起点 / 长度,不比内容)
            bool identityChanged = ColumnType<T>.IsColumn
                && !(_memory.TryGetValue<DataPort<T>, T>(port, out var current) && EqualityComparer<T>.Default.Equals(current, value));

            // 2. 0-GC 裸写物理桶
            _memory.Set<DataPort<T>, T>(port, value);

            // 3. 拨动时钟(融合 Advance+Snapshot,单次 Interlocked.Increment 取新值,省一次 barrier)
            var token = _boardClock.AdvanceAndSnapshot();
            _portTokens[port] = token;
            if (identityChanged) _identityTokens[port] = token;

            // 4. 路由拦截:WriteScope 内攒着,结束时统一通知;Watch 回调里(深度已归零)的写入当下通知
            if (_transactionDepth > 0)
            {
                _dirtyPorts.Add(port);
            }
            else
            {
                NotifyPort(port);
                Commit();
            }
        }

        private void NotifyPort(object port)
        {
            if (_watchersByPort.TryGetValue(port, out var watchers))
            {
                for (int i = 0; i < watchers.Length; i++)
                {
                    var e = watchers[i];
                    if (!e.Dirty) { e.Dirty = true; _dirtyCount++; }
                }
            }
            _observer?.OnPortWritten(port);
            var extra = _extraObservers;
            if (extra != null)
                for (int i = 0; i < extra.Length; i++) extra[i].OnPortWritten(port);
        }

#if DEBUG
        /// <summary>
        /// DEBUG dump 入口:把当前所有 port 的 (Id, DisplayName, T, value) 抓成快照,供 Topology Inspector
        /// 之类的工具一键导出。读锁内迭代,迭代期间不会跟事务写并发。
        /// 仅枚举 key 实现 IDataPort 的桶 —— 黑板里其它非 DataPort 的 key(如果有)不应当出现在这层视图。
        /// </summary>
        internal List<(string Id, string DisplayName, string TypeName, object? Value)> DumpAllPortValues()
        {
            var result = new List<(string, string, string, object?)>();
            // Inspector 寿命可能比 board 长(切 template、关图时 schema.Decompose 会先 Dispose 旧 board);
            // dispose 后 _rwLock 已经被 Dispose,再 ReadScope 会抛 ObjectDisposedException。
            // 这里直接短路返回空列表,UI 端拿到的 ports 段就是空数组,语义清晰。
            if (IsDisposed) return result;
            using (ReadScope())
            {
                foreach (var (key, value) in _memory.EnumerateAll())
                {
                    if (key is IDataPort port)
                    {
                        var t = key.GetType();
                        var typeName = t.IsGenericType ? t.GetGenericArguments()[0].Name : t.Name;
                        result.Add((port.Id, port.DisplayName, typeName, value));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// DEBUG 诊断:统计 _memory 里的 (总 entry, IDataPort entry, 非 IDataPort entry)。
        /// dump 出来 ports 是空数组、又看到拓扑链路有 hits 时,就靠这套数字判断:
        ///   - TotalEntries=0 → 这块 board 真的没人写过 → 数据写在别的 board(常见于 dashboard 多 board 拓扑)
        ///   - TotalEntries>0 且 PortEntries=0 → key 不是 IDataPort,DumpAllPortValues 的过滤把它筛掉了
        ///   - TotalEntries>0 且 PortEntries>0 → DumpAllPortValues 应该有内容,如果空说明枚举链路出 bug
        /// </summary>
        internal (int TotalEntries, int PortEntries, int NonPortEntries) DumpMemoryStats()
        {
            if (IsDisposed) return (0, 0, 0);
            int total = 0, port = 0, nonPort = 0;
            using (ReadScope())
            {
                foreach (var (key, _) in _memory.EnumerateAll())
                {
                    total++;
                    if (key is IDataPort) port++;
                    else nonPort++;
                }
            }
            return (total, port, nonPort);
        }
#endif

        // 最外层 WriteScope 结束:逐端口通知,再跑一遍脏 Watch
        private void EndWriteScope()
        {
            try
            {
                _transactionDepth--;
                if (_transactionDepth == 0)
                {
                    if (_dirtyPorts.Count > 0)
                    {
                        // 修复 H3：用预分配的 _portsSnapshot 替代 ToList()，热路径零堆分配。
                        // _portsSnapshot 充当临时快照缓冲：
                        //   - _dirtyPorts 可能在 NotifyPort 的回调中被重新写入（递归事务），
                        //     因此必须先快照、再清空、再通知，不能边遍历边清空。
                        //   - _portsSnapshot 复用跨事务增长的内部数组，无 GC 压力。
                        _portsSnapshot.Clear();
                        foreach (var p in _dirtyPorts) _portsSnapshot.Add(p);
                        _dirtyPorts.Clear();
                        foreach (var port in _portsSnapshot) NotifyPort(port);
                    }
                    // 💥 事务所有引脚通知完毕，触发最终结算！
                    Commit();
                }
            }
            finally
            {
                var pending = TakeAfterUnlockIfLastHold();
                _rwLock.ExitWriteLock(); // 💥 释放排他写锁
                RunAfterUnlock(pending);
            }
        }

        private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(1);

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // 先等在途读写者退出再拆锁:别的线程正持锁时拆掉,它退出时会撞上锁内部状态异常。
            // 本线程自己持锁(在回调里释放自己的板)就不等,也不拆 —— 拆持有中的锁会抛,留给 GC。
            bool heldHere = _rwLock.IsReadLockHeld || _rwLock.IsUpgradeableReadLockHeld || _rwLock.IsWriteLockHeld;
            if (!heldHere && _rwLock.TryEnterWriteLock(DisposeDrainTimeout)) _rwLock.ExitWriteLock();
            if (!heldHere)
            {
                try { _rwLock.Dispose(); }
                catch (SynchronizationLockException) { /* 仍有线程排队等锁:不拆,留给 GC */ }
            }

            _watches.Clear();
            _watchersByPort.Clear();
            _dirtyCount = 0;
            _afterUnlock.Clear();
            _dirtyPorts.Clear();
            _portTokens.Clear();
            _identityTokens.Clear();
            lock (_observerGate)
            {
                _observer = null;
                _extraObservers = null;
            }
        }
    }

    /// <summary>
    /// 黑板端口写入观察者(框架内部:Schema 判脏 Feature、DevTools 内存雷达)。
    /// 在写入线程上、持写锁时调用(WriteScope 结束时逐端口;Watch 回调里的写入当下),实现里不要去拿别的板的锁。
    /// </summary>
    internal interface IBoardObserver
    {
        void OnPortWritten(object port);
    }

    /// <summary>见 <see cref="DataBlackboard.PostAfterUnlock"/>:登记它的板作参数,在锁外、同一线程上执行。</summary>
    internal interface IAfterUnlockAction
    {
        void Run(DataBlackboard board);
    }

    // ==========================================
    // 💥 3. 定向花名册订阅系统 (双缓冲安全版)
    // 单锁守护 _portSubscribers + _dirtyFeatures：
    //   - NotifyPortUpdated 是 ForceWrite 后的高频路径,原双锁嵌套(先 _portSubscribers
    //     再 _dirtyLock)每次进出两套 monitor,合并后省一半 lock 开销。
    //   - Subscribe / PopDirtyFeatures / UnsubscribeAll 几乎都在 UI 线程上,合锁后
    //     竞争面没变化。
    // ==========================================
    public class SubscriptionRegistry
    {
        private readonly object _gate = new object();
        private readonly HashSet<Feature> _dirtyFeatures = new();

        // 记录引脚与 Feature 的订阅关系 (通常在 UI 线程建树时操作)
        private readonly Dictionary<object, HashSet<Feature>> _portSubscribers = new();

        /// <summary>隐式订阅:将 Feature 登记为引脚的观察者</summary>
        public void Subscribe<T>(DataPort<T> port, Feature feature)
        {
#if DEBUG
            DevTools.TracerRegistry.Get(this)?.RecordSubscribe(port, feature);
#endif

            // 简单加锁防止建树期的并发
            lock (_gate)
            {
                if (!_portSubscribers.TryGetValue(port, out var features))
                {
                    features = new HashSet<Feature>();
                    _portSubscribers[port] = features;
                }
                features.Add(feature);
            }
        }

        /// <summary>
        /// 联动查脏:将所有订阅了该引脚的 Feature 打入冷宫 (脏列表)
        /// 💥 注意:此方法由黑板在后台线程触发!
        /// </summary>
        public bool NotifyPortUpdated(object port)
        {
            lock (_gate)
            {
                if (_portSubscribers.TryGetValue(port, out var features) && features.Count > 0)
                {
                    foreach (var f in features) _dirtyFeatures.Add(f);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 💥 工业级引擎的核心交接法:填充模式弹出 (Fill-Pattern Pop)
        /// 修复 H3:原实现每次返回新 List,热路径每帧产生堆分配。
        /// 新 API 直接填充调用方预分配的 HashSet,零堆分配。
        /// 返回 true 代表有脏 Feature 已填入 target;false 代表脏名单为空。
        /// </summary>
        public bool PopDirtyFeatures(HashSet<Feature> target)
        {
            lock (_gate)
            {
                if (_dirtyFeatures.Count == 0) return false;

                // 直接填充目标集合(调用方负责 Clear),避免任何中间 List/HashSet 分配
                foreach (var f in _dirtyFeatures) target.Add(f);
                _dirtyFeatures.Clear();
                return true;
            }
        }

        /// <summary>彻底抹除某个 Feature 的所有订阅记录</summary>
        public void UnsubscribeAll(Feature feature)
        {
            lock (_gate)
            {
                _dirtyFeatures.Remove(feature);
                foreach (var subscribers in _portSubscribers.Values)
                {
                    subscribers.Remove(feature);
                }
            }
        }
    }

    /// <summary>
    /// 💥 统一数据加工管线 (具备自我清理能力的加工厂)
    /// 核心职责：持有所有摄入器规则，复用唯一的黑板，并在销毁时通知下属释放内存池！
    /// </summary>
    public class UniversalDataPipe<TItem> : IDisposable
    {
        private readonly List<IDataIngestor<TItem>> _ingestors = new();

        // 💥 跨帧复用的持久化黑板，避免每帧 new DataBlackboard
        private readonly DataBlackboard _persistentBoard = new();

        /// <summary>
        /// 暴露内部持久化黑板,给 DEBUG 路径里的 TopologyTracer.Attach 用 ——
        /// 必须在 first Process() 之前 attach,否则 first-publish 的 DataPipe 写入(Value/Time/Index 等)
        /// 因为 board 还没 tracer 而进不了 LinkHits → inspector 看不到 DataPipe → 端口的连线。
        /// 业务侧别用,只有诊断工具读。
        /// </summary>
        public DataBlackboard Board => _persistentBoard;

        public void AddIngestor(IDataIngestor<TItem> ingestor) => _ingestors.Add(ingestor);

        /// <summary>
        /// 💥 核心加工马达：每当新行情到来，遍历执行加工规则
        /// </summary>
        public DataBlackboard Process(DataSnapshot<TItem> snapshot)
        {
            // 销毁与推送并发:板已关门就丢掉这次推送(管线正在拆,没有下游要看了)
            if (_persistentBoard.IsDisposed) return _persistentBoard;
            using (_persistentBoard.WriteScope())
            {
                // 按添加顺序执行，确保标量先写入，向量后写入
                for (int i = 0; i < _ingestors.Count; i++)
                {
                    // 💥 极简投喂：直接扔快照！脏判断由各个 Ingestor 自己负责
                    _ingestors[i].Process(snapshot, _persistentBoard);
                }
            }
            return _persistentBoard;
        }

        /// <summary>
        /// 定向输出管：允许双引擎管线在鼠标拖拽时，把最新的数据快照强行压入黑板！
        /// </summary>
        /// <param name="items"></param>
        /// <param name="board"></param>
        public void ProcessTo(DataSnapshot<TItem> snapshot, DataBlackboard board)
        {
            using (board.WriteScope())
            {
                for (int i = 0; i < _ingestors.Count; i++)
                {
                    _ingestors[i].Process(snapshot, board);
                }
            }
        }

        /// <summary>
        /// 💥 生命周期终点：必须被调用以释放 ArrayPool！
        /// </summary>
        public void Dispose()
        {
            _persistentBoard.Dispose();
            for (int i = 0; i < _ingestors.Count; i++)
            {
                if (_ingestors[i] is IDisposable d) d.Dispose();
            }
            _ingestors.Clear();
        }
    }
}