using System.Reflection;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// 多 ChartCell 列式联动 dashboard 的共享状态容器(领域无关)。
    ///
    /// <para>
    /// 现实情况:每个 ChartCell 自带独立 <see cref="DataBlackboard"/>(由 schema 的 pipe new 出来)。
    /// 仅"两 schema 持有同一个 <see cref="DataPort{T}"/> 实例"无法跨 cell 同步状态——port 只是键,
    /// blackboard 才是值容器。本类内置端口镜像桥,把指定的几个 port 在所有
    /// attached board 之间双向同步,达成跨 cell 联动。
    /// </para>
    ///
    /// <para>
    /// 业务方有两种用法:
    /// <list type="number">
    ///   <item>直接 new <see cref="LinkedChartContext"/>:已经够用 4 个默认镜像 port</item>
    ///   <item>派生子类(如 <c>LinkedKLineContext : LinkedChartContext</c>),在子类里
    ///         挂业务自有的数据源/扩展端口,用 <see cref="RegisterMirror{T}"/> 加更多镜像 port</item>
    /// </list>
    /// </para>
    /// </summary>
    public class LinkedChartContext
    {
        /// <summary>
        /// 共享视口端口集——所有 schema 的 viewport 指向同一对 UserRange/ActiveRange/LogicalLength。
        /// </summary>
        public ViewportPorts SharedViewport { get; } = new();

        /// <summary>
        /// dashboard 级共享端口注册表 —— Key = 用户起的 shared name(如 "linkedHit"),
        /// Value = 对应 <see cref="DataPort{T}"/> 实例(用作镜像桥的物理载体)。
        /// <para>
        /// ChartBlueprint.DefineFeatures 时按 <c>"dashboard:{name}"</c> 前缀注入 <c>_portRegistry</c>,
        /// feature 蓝图 PortBinding 引用同名拿到同一实例。AttachBoard 自动给每个 board 挂 mirror 订阅。
        /// </para>
        /// <para>
        /// ctor 兜底建一个 <c>linkedHit</c> (DataPort&lt;PointerHitState?&gt;) —— 向后兼容旧 LinkedHit 用法。
        /// 业务侧典型扩展:DashboardLauncher 启动期从 Dashboard.SharedPorts 配置批量 <see cref="RegisterFromConfig"/>。
        /// </para>
        /// </summary>
        public Dictionary<string, IDataPort> SharedPorts { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// <b>向后兼容</b>:旧 API alias,等价 <c>SharedPorts["linkedHit"]</c>。新代码用 SharedPorts dict。
        /// </summary>
        public DataPort<PointerHitState?> SharedHit => (DataPort<PointerHitState?>)SharedPorts["linkedHit"];

        /// <summary>
        /// 所有 pane 必须使用相同的水平 layout 边距,crosshair 才能在垂直方向对齐。
        /// </summary>
        public ChartLength HorizontalLeft { get; init; } = ChartLength.Pixel(60);
        public ChartLength HorizontalRight { get; init; } = ChartLength.Pixel(0);

        // ── 端口镜像桥 ─────────────────────────────────────────────────────
        // 列表锁定哪些 port 跨 cell 镜像。默认 5 个(viewport 4 + linkedHit);业务方可通过 RegisterMirror / RegisterFromConfig 扩展。
        private readonly List<IPortMirror> _mirrors;
        // 写时复制:镜像分发可能在任意线程上遍历,AttachBoard 在 UI 线程上换新数组
        private DataBlackboard[] _boards = Array.Empty<DataBlackboard>();

        internal DataBlackboard[] AttachedBoards => Volatile.Read(ref _boards);

        public LinkedChartContext()
        {
            // 兜底:linkedHit shared port —— 跟旧 SharedHit 字段语义一致,JSON 没声明 SharedPorts 时仍 work。
            var defaultHit = new DataPort<PointerHitState?>("linkedHit");
            SharedPorts["linkedHit"] = defaultHit;

            _mirrors = new List<IPortMirror>
            {
                PortMirror.For(SharedViewport.UserRange),
                PortMirror.For(SharedViewport.ActiveRange),
                // Extent 排在长度前:分发按此顺序,对端先拿到键再拿到长度,两者对得上
                PortMirror.For(SharedViewport.Extent),
                PortMirror.For(SharedViewport.LogicalLength),
                PortMirror.For(defaultHit),
            };
        }

        /// <summary>
        /// 业务方扩展点:把自定义 port 加入跨 cell 镜像列表。
        /// 如果调用时已有 board attached,会立即对其订阅(确保后续写入也镜像)。
        /// </summary>
        public void RegisterMirror<T>(DataPort<T> port)
        {
            if (port is null) throw new ArgumentNullException(nameof(port));
            var mirror = PortMirror.For(port);
            _mirrors.Add(mirror);
            var boards = AttachedBoards;
            for (int i = 0; i < boards.Length; i++)
                mirror.SubscribeOn(boards[i], this);
        }

        // 旧 JSON 里的程序集名可能已过时(类型从 Hevo.Charting 挪进了 Hevo.Charting.Core):
        // 按写的程序集找不到时,在已加载程序集里按全名再找一遍
        private static Type? ResolveConfigType(string typeName)
        {
            var direct = Type.GetType(typeName);
            if (direct != null) return direct;
            return Type.GetType(typeName,
                // 找不到的程序集给个占位,让 typeResolver 照样被调到、去已加载程序集里按全名找
                assemblyResolver: an => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == an.Name)
                                        ?? typeof(object).Assembly,
                typeResolver: (asm, name, ignoreCase) =>
                    asm?.GetType(name, false, ignoreCase)
                    ?? Type.GetType(name, false, ignoreCase)
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType(name, false, ignoreCase))
                        .FirstOrDefault(t => t != null),
                throwOnError: false);
        }

        /// <summary>
        /// 从 <see cref="Dashboard.SharedPorts"/> 配置批量建 DataPort + 注册 mirror。
        /// 由 <see cref="LowCode.Designer.GraphViewer.DashboardLauncher.LaunchEx"/> 启动期调用。
        /// 同名重复声明 → silently 跳过(ctor 默认建的 linkedHit 不会被覆盖,业务想换类型显式删 ctor 默认即可)。
        /// </summary>
        public void RegisterFromConfig(IReadOnlyList<LowCode.Designer.SharedPortModel> models)
        {
            if (models == null) return;
            foreach (var m in models)
            {
                if (string.IsNullOrEmpty(m.Name)) continue;
                if (SharedPorts.ContainsKey(m.Name)) continue; // 防 ctor 默认 linkedHit 被覆盖 / 重复声明

                var dataType = ResolveConfigType(m.DataTypeName)
                    ?? throw new InvalidOperationException(
                        $"Dashboard.SharedPorts['{m.Name}'] DataTypeName='{m.DataTypeName}' 反射解析失败 —— " +
                        "请用 AssemblyQualifiedName 或确保 Type.GetType 能解析的形态。");
                var portType = typeof(DataPort<>).MakeGenericType(dataType);
                var port = (IDataPort)Activator.CreateInstance(portType, m.Name)!;
                SharedPorts[m.Name] = port;

                // 反射调 PortMirror.For<T>(DataPort<T>) → 拿回 IPortMirror。
                var portMirrorForMethod = typeof(PortMirror)
                    .GetMethod(nameof(PortMirror.For), BindingFlags.Public | BindingFlags.Static)!
                    .MakeGenericMethod(dataType);
                var mirror = (IPortMirror)portMirrorForMethod.Invoke(null, new object[] { port })!;
                _mirrors.Add(mirror);
                var boards = AttachedBoards;
                for (int i = 0; i < boards.Length; i++)
                    mirror.SubscribeOn(boards[i], this);
            }
        }

        /// <summary>
        /// 由 <see cref="LinkedChartDashboard"/> 在每个 schema 的 board 就绪时调用一次。
        /// 同一 board 多次 attach 直接 short-circuit,幂等。
        /// </summary>
        internal void AttachBoard(DataBlackboard board)
        {
            if (board is null || Array.IndexOf(AttachedBoards, board) >= 0) return;
            var next = new DataBlackboard[_boards.Length + 1];
            Array.Copy(_boards, next, _boards.Length);
            next[_boards.Length] = board;
            Volatile.Write(ref _boards, next);

            // 关键:每个被镜像 port 各订阅一次。仅这几个 port 写入触发回调,
            // 数据列(Times/Closes/Volumes 等)写入零开销——不挂全局 OnPortUpdated。
            foreach (var m in _mirrors) m.SubscribeOn(board, this);

            // 运行期加进来的 pane:别的板早就写过视口 / hit,镜像只在"下一次写"时分发,
            // 不补的话新板一直停在默认值(视口 [0,0]、图画不出来),直到用户动一下视口。
            foreach (var m in _mirrors) m.SeedTo(board);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  端口镜像基础设施(框架内部)
    // ──────────────────────────────────────────────────────────────────────

    internal interface IPortMirror
    {
        void SubscribeOn(DataBlackboard board, LinkedChartContext owner);

        /// <summary>把已经镜像过的最新值写进新挂上的板(还没有值就不写)。</summary>
        void SeedTo(DataBlackboard board);
    }

    /// <summary>
    /// 类型安全的 per-port 镜像器:某块板写入 _port 后,把值同步到其它已挂载的板。
    /// <para>
    /// 跨板写不在源板锁内做:订阅回调跑在源板写锁内,若在这里拿别的板的写锁,
    /// 两块板在两个线程上同时写(A→B 与 B→A)就互相等死。回调只记下最新值并向源板登记
    /// <see cref="DataBlackboard.PostAfterUnlock"/>,等本线程彻底退出源板的锁后再逐板写。
    /// </para>
    /// <para>
    /// 收敛:分发时持目标板写锁读"最新值"再写,所以无论几路并发写怎么交错,
    /// 最后一次真实写入的值总会被它自己那次分发铺到所有板。镜像写引发的回调按线程标记识别为回声,不再分发。
    /// </para>
    /// </summary>
    internal sealed class PortMirror<T> : IPortMirror, IAfterUnlockAction
    {
        private readonly DataPort<T> _port;
        private readonly object[] _ports;
        private readonly object _gate = new();
        private LinkedChartContext? _owner;
        private T _latest = default!;
        private bool _hasLatest;

        public PortMirror(DataPort<T> port)
        {
            _port = port;
            _ports = new object[] { port };
        }

        // 板跟联动同生共死,不单独退订(板 Dispose 时清空 Watch 表)
        public void SubscribeOn(DataBlackboard board, LinkedChartContext owner)
        {
            _owner = owner;
            board.AddWatch(_ports, OnOriginWritten);
        }

        public void SeedTo(DataBlackboard board)
        {
            T value;
            lock (_gate)
            {
                if (!_hasLatest) return;
                value = _latest;
            }
            if (board.IsDisposed) return;
#if DEBUG
            using var traceScope = DevTools.TopologyTracer.EnterScope(DevTools.TopologyTracer.MIRROR_ID);
            PortMirror.TraceDepth++;
            try
            {
#endif
            using (board.WriteScope())
            {
                // 补写不是新的真实写入,按回声处理,不再分发回其它板
                var prev = PortMirror.Applying;
                PortMirror.Applying = this;
                try { board.Write(_port, value); }
                finally { PortMirror.Applying = prev; }
            }
#if DEBUG
            }
            finally { PortMirror.TraceDepth--; }
#endif
        }

        // 调用栈:Commit ← ForceWrite(Watch 回调里的写入当下)/ EndWriteScope(WriteScope 里所有端口通知完之后),均持源板写锁。
        // 事务里比"逐端口通知时"晚一点,但这里只记最新值、跨板写在锁外,结果相同。
        private void OnOriginWritten(DataBlackboard origin)
        {
            if (ReferenceEquals(PortMirror.Applying, this)) return;

#if DEBUG
            // 回调跑在原写入方的追踪作用域里;不切换的话,原格子会多出一条"写入方读端口"的假边。
            // 分发途中触发的是回声(目标板 WriteScope 结束才通知,那时 Applying 已复位,靠下面的值相等短路):
            // 它不是数据流,不记追踪,免得副图多出 端口 → 跨图镜像 的假读取和假环路。
            var traceScope = DevTools.TopologyTracer.EnterScope(
                PortMirror.TraceDepth > 0 ? null : DevTools.TopologyTracer.MIRROR_ID);
            var value = origin.Read(_port);
            traceScope.Dispose();
#else
            var value = origin.Read(_port);
#endif
            lock (_gate)
            {
                if (_hasLatest && EqualityComparer<T>.Default.Equals(_latest, value)) return;
                _latest = value;
                _hasLatest = true;
            }
            origin.PostAfterUnlock(this);
        }

        // 锁外:本线程已不持有源板的任何锁
        void IAfterUnlockAction.Run(DataBlackboard origin)
        {
            var boards = _owner?.AttachedBoards;
            if (boards == null) return;
#if DEBUG
            // 跨图同步的写入方记成"跨图镜像",别的格子里不再冒出原写入方的 Feature
            using var traceScope = DevTools.TopologyTracer.EnterScope(DevTools.TopologyTracer.MIRROR_ID);
            PortMirror.TraceDepth++;
            try { Dispatch(origin, boards); }
            finally { PortMirror.TraceDepth--; }
        }

        private void Dispatch(DataBlackboard origin, DataBlackboard[] boards)
        {
#endif
            for (int i = 0; i < boards.Length; i++)
            {
                var b = boards[i];
                if (ReferenceEquals(b, origin) || b.IsDisposed) continue;
                try
                {
                    using (b.WriteScope())
                    {
                        T value;
                        lock (_gate) value = _latest;
                        var prev = PortMirror.Applying;
                        PortMirror.Applying = this;
                        try { b.Write(_port, value); }
                        finally { PortMirror.Applying = prev; }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 目标板在检查之后被释放:它已经退出联动,跳过
                }
            }
        }
    }

    internal static class PortMirror
    {
        // 当前线程正在执行哪个镜像器的分发写入 —— 那次写触发的回调是回声,不再分发
        [ThreadStatic] internal static object? Applying;

#if DEBUG
        // 拓扑追踪用:本线程正处在镜像分发里的层数(所有端口共用),分发途中触发的镜像回调是回声
        [ThreadStatic] internal static int TraceDepth;
#endif

        public static IPortMirror For<T>(DataPort<T> port) => new PortMirror<T>(port);
    }
}
