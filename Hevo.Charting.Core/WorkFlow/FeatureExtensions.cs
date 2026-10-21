using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.WorkFlow
{
    internal sealed class FeatureComposeScope
    {
        private static readonly AsyncLocal<FeatureComposeScope?> CurrentSlot = new();

        private readonly IRenderFlow<DataBlackboard> _baseFlow;
        private readonly List<Func<IRenderFlow<DataBlackboard>, IRenderFlow<DataBlackboard>>> _transforms = new();

        private FeatureComposeScope(IRenderFlow<DataBlackboard> baseFlow, FeatureLifetime? lifetime)
        {
            _baseFlow = baseFlow;
            Lifetime = lifetime;
        }

        public static FeatureComposeScope? Current => CurrentSlot.Value;

        /// <summary>正在装配的 Feature 本次装配周期的存活令牌;不在 Feature 装配内时为 null。</summary>
        public FeatureLifetime? Lifetime { get; }

        public static IDisposable Enter(IRenderFlow<DataBlackboard> baseFlow, FeatureLifetime? lifetime = null)
        {
            var previous = CurrentSlot.Value;
            CurrentSlot.Value = new FeatureComposeScope(baseFlow, lifetime);
            return new DisposableAction(() => CurrentSlot.Value = previous);
        }

        public void AddTransform(Func<IRenderFlow<DataBlackboard>, IRenderFlow<DataBlackboard>> transform)
        {
            _transforms.Add(transform);
        }

        public IRenderFlow<DataBlackboard> Build()
        {
            var flow = _baseFlow;
            foreach (var transform in _transforms)
            {
                flow = transform(flow);
            }
            return flow;
        }
    }

    /// <summary>
    /// Feature 一个装配周期(InternalCompose → Decompose)的存活令牌。
    /// 装配期 Watch 是串进 schema 主管线的变换,Decompose 时摘不下来;回调经令牌放行,
    /// 周期结束后即使管线还在推送也不再执行。同一实例再次装配拿新令牌,旧链上的回调保持关闭。
    /// </summary>
    internal sealed class FeatureLifetime
    {
        private volatile bool _ended;
        public bool IsAlive => !_ended;
        public void End() => _ended = true;
    }

    public static class FeatureExtensions
    {
        // 把副作用绑到正在装配的 Feature 的存活令牌上(不在 Feature 装配内就原样返回)
        private static Action<DataBlackboard> BindToComposingFeature(Action<DataBlackboard> sideEffect)
        {
            var life = FeatureComposeScope.Current?.Lifetime;
            if (life == null) return sideEffect;
            return b => { if (life.IsAlive) sideEffect(b); };
        }

        // ==========================================
        // ⚡ 算子 1：同步 Watch (排版、切片专用)
        // 语义：在当前线程立刻执行，绝不切后台！
        // ==========================================
        public static IWorkflow<DataBlackboard> Watch(
            this IWorkflow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
            // 同步回调同样可能锁内取列、锁外读(多个数据源从不同线程写同一块黑板时会并发):登记为列读者
            var inner = sideEffect;
            sideEffect = b => { using (LowCode.ColumnReadScope.Begin()) inner(b); };
#if DEBUG
            // 拓扑追踪闭环:跟 WatchAsync 对称,把 sideEffect 包在 EnterScope(owner) 里。
            // 否则 sync Watch 回调里的 board.Read(...) 因为 _currentCaller.Value == null
            // 不会被 RecordRead 记录,导致 TopologyInspector 漏掉这条 feature → port 的连线。
            object owner = DevTools.TopologyTracer.SetupContext.Value ?? "UnknownOwner";
            Action<DataBlackboard> tracedAction = b =>
            {
                using (DevTools.TopologyTracer.EnterScope(owner))
                {
                    sideEffect(b);
                }
            };
            return AttachWatchCore(flow, listenPorts, tracedAction);
#else
            return AttachWatchCore(flow, listenPorts, sideEffect);
#endif
        }

        // ==========================================
        // 🚀 算子 2：异步 WatchAsync (重度指标专用)
        // 语义：自动分发到后台线程池，由开发者自己加锁保护数据！
        // 合并(single-flight + 最新值):同一订阅同时最多一个回调在跑;在跑期间来的通知只记一个"待重跑",
        // 回调结束后再跑一次(回调自己从黑板读最新值,中间版本被跳过是对的)。
        // 以前每次通知都往线程池丢一个任务:慢回调(2 万根纯 Python 指标 ~200 ms)在 100 tick/s 下任务无限堆积、
        // 抢 GIL 谁都算不完;同一订阅并发的任务还会乱序提交,旧结果覆盖新结果。
        // ==========================================
        public static IWorkflow<DataBlackboard> WatchAsync(
            this IWorkflow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
#if DEBUG
            object owner = DevTools.TopologyTracer.SetupContext.Value ?? "UnknownOwner";
#endif
            var gate = new LatestOnlyRunner(b =>
            {
#if DEBUG
                using (DevTools.TopologyTracer.EnterScope(owner))
#endif
                // 回调里读锁内取列引用、锁外读元素(ComputeFeature 的三段式):登记为列读者,
                // 摄入器在本回调结束前不会改写它可能拿到的缓冲
                // releasable:回调里调 Python handler 时,输入已拷成私有副本,长计算期间可让出登记(见 PythonInvokerShim)
                using (LowCode.ColumnReadScope.Begin(releasable: true))
                {
                    sideEffect(b);
                }
            });

            return AttachWatchCore(flow, listenPorts, gate.Notify).DoOnDispose(gate.Dispose);
        }

        /// <summary>
        /// WatchAsync 的单订阅调度:状态 0 空闲 / 1 运行中 / 2 运行中且有新通知。
        /// 空闲时投递一次;运行中只标记待重跑;回调结束后若有标记就清掉再跑一次,否则回到空闲。
        /// 回调抛异常不影响状态复位,循环跑完后再把第一个异常抛给 HevoDispatcher 记录。退订(Dispose)后不再重跑待重跑的那次。
        /// </summary>
        internal sealed class LatestOnlyRunner
        {
            private const int Idle = 0, Running = 1, RunningPending = 2;
            private readonly Action<DataBlackboard> _body;
            private int _state;
            private DataBlackboard? _board;
            private volatile bool _disposed;

            public LatestOnlyRunner(Action<DataBlackboard> body) => _body = body;

            public void Notify(DataBlackboard board)
            {
                // 通知只在订阅着时到来(AttachWatchCore 退订后会复查 currentBoard);流被重新订阅时这里重新放行
                _disposed = false;
                Volatile.Write(ref _board, board);
                while (true)
                {
                    int s = Volatile.Read(ref _state);
                    if (s == Idle)
                    {
                        if (Interlocked.CompareExchange(ref _state, Running, Idle) == Idle) break;
                    }
                    else if (s == Running)
                    {
                        if (Interlocked.CompareExchange(ref _state, RunningPending, Running) == Running) return;
                    }
                    else return; // 已有待重跑
                }
                Core.HevoDispatcher.FireAndForget(RunLoop);
            }

            public void Dispose() => _disposed = true;

            private void RunLoop()
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo? first = null;
                while (true)
                {
                    if (!_disposed)
                    {
                        try { _body(Volatile.Read(ref _board)!); }
                        catch (Exception ex) { first ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
                    }
                    // 没有新通知 → 回到空闲;有 → 清标记再跑一次
                    if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running) break;
                    Volatile.Write(ref _state, Running);
                }
                first?.Throw();
            }
        }

        /// <summary>
        /// IRenderFlow 的安全包装层，提供链式调用
        /// </summary>
        public static IRenderFlow<DataBlackboard> Watch(
            this IRenderFlow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
            sideEffect = BindToComposingFeature(sideEffect);
            IWorkflow<DataBlackboard> safeBaseFlow = ((IWorkflow<DataBlackboard>)flow).Watch(listenPorts, sideEffect);
            var wrapped = flow.Wrap(safeBaseFlow);
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(((IWorkflow<DataBlackboard>)current).Watch(listenPorts, sideEffect)));
            return wrapped;
        }

        /// <summary>
        /// IRenderFlow 的安全包装层，提供链式调用
        /// </summary>
        public static IRenderFlow<DataBlackboard> WatchAsync(
            this IRenderFlow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> sideEffect)
        {
            sideEffect = BindToComposingFeature(sideEffect);
            IWorkflow<DataBlackboard> safeBaseFlow = ((IWorkflow<DataBlackboard>)flow).WatchAsync(listenPorts, sideEffect);
            var wrapped = flow.Wrap(safeBaseFlow);
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(((IWorkflow<DataBlackboard>)current).WatchAsync(listenPorts, sideEffect)));
            return wrapped;
        }

        // ==========================================
        // 🧠 终极 DRY 内核：只处理订阅、防抖与结算！
        // ==========================================
        private static IWorkflow<DataBlackboard> AttachWatchCore(
            IWorkflow<DataBlackboard> flow,
            object[] listenPorts,
            Action<DataBlackboard> executeAction)
        {
            DataBlackboard? currentBoard = null;
            IDisposable? watch = null;
            bool hasInitialized = false;
            object switchLock = new object();

            // 脏标志和"只跑脏的"由黑板的 Watch 表管(提交时按注册顺序扫),这里只负责切板与销毁
            Action<DataBlackboard> onCommit = b =>
            {
                // 修复 M5:退订不拿黑板锁,别的线程正在跑的那次提交可能已经读过 Removed 标志、
                // 还会再调一次本回调,闭包里的 executeAction 可能访问已销毁资源。
                // 在 switchLock 内复查 currentBoard:DoOnDispose 会先置 null / 切换 board,不匹配即安全退出。
                lock (switchLock) { if (currentBoard != b) return; }
                executeAction(b);
            };

            var hookedFlow = flow.Do(board =>
            {
                bool shouldRunInit = false;

                lock (switchLock)
                {
                    if (currentBoard != board)
                    {
                        watch?.Dispose();
                        currentBoard = board;
                        watch = board.AddWatch(listenPorts, onCommit);
                    }

                    if (!hasInitialized)
                    {
                        hasInitialized = true;
                        shouldRunInit = true;
                    }
                }

                if (shouldRunInit) executeAction(board);
            });

            return hookedFlow.DoOnDispose(() =>
            {
                lock (switchLock)
                {
                    watch?.Dispose();
                    watch = null;
                    currentBoard = null;
                }
            });
        }

        // CombineLatest / Merge / Join 的公开重载都要在装配作用域里登记一个变换,Build 时串进 schema 主管线。
        // 变换里只能调不登记的内核再 Wrap:Build 正在遍历变换列表,调回公开重载会再登记一次,遍历期间改列表就抛异常。
        public static IRenderFlow<DataBlackboard> CombineLatest(
            this IRenderFlow<DataBlackboard> flow,
            object[] requiredPorts,
            Action<DataBlackboard> sideEffect)
        {
            sideEffect = BindToComposingFeature(sideEffect);
            var wrapped = flow.Wrap(CombineLatestCore(flow, requiredPorts, sideEffect));
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(CombineLatestCore(current, requiredPorts, sideEffect)));
            return wrapped;
        }

        private static IWorkflow<DataBlackboard> CombineLatestCore(
            IWorkflow<DataBlackboard> flow,
            object[] requiredPorts,
            Action<DataBlackboard> sideEffect)
        {
            return flow.WatchAsync(requiredPorts, b =>
            {
                // 线程池回调不持锁:版本号字典是普通字典,读它要进读锁;副作用在锁外跑(它可能自己拿写锁)
                if (!AllPortsWritten(b, requiredPorts)) return;
                sideEffect(b);
            });
        }

        public static IRenderFlow<DataBlackboard> Merge(
            this IRenderFlow<DataBlackboard> source,
            IRenderFlow<DataBlackboard> other)
        {
            var wrapped = source.Wrap(MergeCore(source, other));
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(MergeCore(current, other)));
            return wrapped;
        }

        private static IWorkflow<DataBlackboard> MergeCore(
            IWorkflow<DataBlackboard> source,
            IWorkflow<DataBlackboard> other)
        {
            return new WorkflowEngine<DataBlackboard>((next, error) =>
            {
                var sub1 = source.Subscribe(next, error);
                var sub2 = other.Subscribe(next, error);
                // CompositeDisposable 多上游 fan-out IPausable
                return new CompositeDisposable(sub1, sub2);
            });
        }

        public static IRenderFlow<DataBlackboard> Join(
            this IRenderFlow<DataBlackboard> source,
            params object[] requiredPorts)
        {
            var wrapped = source.Wrap(JoinCore(source, requiredPorts));
            FeatureComposeScope.Current?.AddTransform(current => current.Wrap(JoinCore(current, requiredPorts)));
            return wrapped;
        }

        // Where 在管线释放黑板锁之后才跑,同样要自己进读锁
        private static IWorkflow<DataBlackboard> JoinCore(IWorkflow<DataBlackboard> source, object[] requiredPorts)
            => source.Where(b => AllPortsWritten(b, requiredPorts));

        // 每个端口都至少写过一次(版本号不是 default)。读锁可重入(持写锁 / 可升级读锁时进读锁也行);
        // 黑板已释放 = 管线正在拆,当作没就绪。
        private static bool AllPortsWritten(DataBlackboard board, object[] ports)
        {
            if (board.IsDisposed) return false;
            try
            {
                using (board.ReadScope())
                {
                    for (int i = 0; i < ports.Length; i++)
                    {
                        if (board.GetVersion(ports[i]) == default) return false;
                    }
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }


        public static IWorkflow<TResult> WithLatestFrom<TSource, TOther, TResult>(
            this IWorkflow<TSource> source,
            IWorkflow<TOther> other,
            Func<TSource, TOther, TResult> selector)
        {
            return new WorkflowEngine<TResult>((onNext, onError) =>
            {
                object gate = new object();
                TOther? latestOther = default;
                bool hasOther = false;

                var otherSub = other.Subscribe(
                    val =>
                    {
                        lock (gate)
                        {
                            latestOther = val;
                            hasOther = true;
                        }
                    },
                    onError
                );

                var sourceSub = source.Subscribe(
                    val =>
                    {
                        TResult result;
                        lock (gate)
                        {
                            if (!hasOther) return;
                            result = selector(val, latestOther!);
                        }
                        onNext(result);
                    },
                    onError
                );

                return new CompositeDisposable(otherSub, sourceSub);
            });
        }

        /// <summary>
        /// 把当前 IDisposable 的完整生命周期托管给 host（schema / feature / session）。
        /// 含义不止 dispose——host 在 Suspend / Resume 时也会级联通知（IPausable），
        /// 业务子类（如 AutoRefreshSchema）还会顺手做 IRefreshable 级联。
        /// </summary>
        public static T OwnedBy<T>(this T subscription, IDisposableHost host) where T : class, IDisposable
        {
            if (subscription is null) throw new ArgumentNullException(nameof(subscription));
            host.RegisterDisposable(subscription);
            return subscription;
        }
    }
}
