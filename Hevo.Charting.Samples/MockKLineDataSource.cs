using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Samples
{
    /// <summary>
    /// Mock OHLCV 数据点 —— 一个 K 线 bar(open/high/low/close + volume + 时间戳)。
    /// [PortGroup] + [Port] 由低代码层反射枚举字段,自动生成 ROM&lt;DateTime&gt; / ROM&lt;double&gt; 端口。
    /// </summary>
    [PortGroup("MockKLinePorts")]
    public readonly struct MockKLineBar
    {
        [Port] public DateTime Time   { get; init; }
        [Port] public double   Open   { get; init; }
        [Port] public double   High   { get; init; }
        [Port] public double   Low    { get; init; }
        [Port] public double   Close  { get; init; }
        [Port] public double   Volume { get; init; }
    }

    /// <summary>
    /// 流式 mock K 线数据源 —— TradingView 风味"边画边跑":
    /// <list type="number">
    ///   <item><b>seed</b>:OnFetchAsync 落 <see cref="SeedBars"/>(默认 30)根暖机数据(BB(20) 需要 20 暖机,30 给余量)</item>
    ///   <item><b>tick</b>:进程级定时器每 150ms 追加 1 根新 bar,所有活实例同步收到。定时器由宿主经 <see cref="StartTimer"/> 提供
    ///         (WPF / Avalonia 示例各用自己的 UI 线程定时器);没提供时不推送,只有 seed 那 30 根</item>
    ///   <item><b>cap</b>:总 bar 数 1000 封顶后 timer 自停(避免无限增长 + side-effect 风暴)</item>
    /// </list>
    ///
    /// <para>
    /// <b>多 cell 同源</b>:dashboard 3 个 cell 各 new 一份 MockKLineDataSource 实例。每个实例独立维护
    /// 自己的 ReactiveDataSource buffer(框架要求),但 bar 内容由静态 _sharedBars 提供 ——
    /// fetch 时拷贝快照,tick 时同步 append。保证三个 cell 的 X 轴帧帧对齐,不出现"主图到第 50 根、
    /// 副图还在第 49 根"的视觉错位。
    /// </para>
    ///
    /// <para>
    /// <b>Viewport trailing</b>:dashboard JSON 把 ViewportManagerFeature.Alignment 设 RightEdge —
    /// LogicalLength 涨 N→N+1 时,只要用户没主动往左拖,viewport ActiveRange 自动 +1 跟住右沿。
    /// 跟 TradingView "实时 K 线"一模一样。
    /// </para>
    ///
    /// <para>
    /// <b>Side-effect 速率</b>:bb_breakout 每 tick 重算全 buffer,看末根是否破轨。突破事件率
    /// = 5% 尖峰 ÷ 突破成功率,实际 demo 跑下来大约每秒 1-2 次买/卖触发,通知中心人眼可读不刷屏。
    /// </para>
    /// </summary>
    public class MockKLineDataSource : ReactiveDataSource<MockKLineDataSource, string, MockKLineBar>
    {
        // ── 进程级共享状态:K 线时间线、生成器、推送 timer、订阅者表 ─────────────

        private const int MaxBars  = 1000;

        /// <summary>
        /// 首次拉取时生成的历史根数(默认 30,BB(20) 暖机够用)。要大图的场景(基准测量)启动时改,早于第一次拉取。
        /// 超过 1000 根时推送不再追加(总数封顶取两者较大值)。
        /// </summary>
        public static int SeedBars { get; set; } = 30;
        private const int TickMs   = 150;

        private static readonly object _sharedLock = new();
        private static readonly List<MockKLineBar> _sharedBars = new(capacity: MaxBars);
        private static readonly List<WeakReference<MockKLineDataSource>> _liveInstances = new();
        private static IDisposable? _sharedTimer;
        private static Random _sharedRng = new(20260512);
        private static double _sharedLastClose = 100.0;
        private static readonly DateTime _t0 = DateTime.Today.AddDays(-1).AddHours(9).AddMinutes(30);

        /// <summary>
        /// 宿主提供的定时器:(间隔, 回调) → 停止句柄(暂时起不了返回 null,下次拉取再试)。回调要在 UI 线程跑(tick 广播给各实例,跟图表帧在同一线程,
        /// 不跟渲染抢 buffer 锁)。null = 不推送(单测等非 UI 宿主)。启动时设一次。
        /// </summary>
        public static Func<TimeSpan, Action, IDisposable?>? StartTimer { get; set; }

        // ── 实例状态 ──────────────────────────────────────────────────────────

        private int _localCount;
        public override int LogicalLength => _localCount;
        // LogicalLength 不依赖 _readSnapshot.Length,展示柜可以留余量(不再每追加一根就整块重分配)
        protected override bool ReserveSnapshotCapacity => true;

        /// <summary>
        /// §回测 把进程级共享 timeline 当前帧快照成数组,供 <c>BacktestView</c> 跑离线策略评估。
        /// 跟 <see cref="OnFetchAsync"/> 走的是同一个 <c>_sharedBars</c> 列表,只是这里不挂订阅、
        /// 不更新实例 buffer —— 纯只读。Caller 在 UI 线程调,锁 ≤ 1ms,不会卡帧。
        /// </summary>
        /// <returns>当前 timeline 的浅拷贝(N 根 bar);若 seed 尚未跑过返回空数组。</returns>
        public static MockKLineBar[] SnapshotBars()
        {
            lock (_sharedLock) return _sharedBars.ToArray();
        }

        /// <summary>
        /// 把进程级共享 timeline 重置回零状态 —— 下一次 OnFetchAsync 会重新 seed + 重启 timer。
        /// 业务侧典型用法:每次"运行 Dashboard"前调一次,实现 TradingView 那种"开窗就从头跑一遍"体验。
        /// 已开着的 cell 不会立刻看到效果(它们 buffer 里还是旧数据);效果在新 DS 实例首次 fetch 时显现。
        /// </summary>
        public static void Reset()
        {
            lock (_sharedLock)
            {
                _sharedTimer?.Dispose();
                _sharedTimer = null;
                _sharedBars.Clear();
                _sharedRng = new Random(20260512);
                _sharedLastClose = 100.0;
                // _liveInstances 留着 —— WeakReference 会自动清死的;留着活的(老 cell)避免误删
            }
        }

        public MockKLineDataSource()
        {
            lock (_sharedLock) _liveInstances.Add(new WeakReference<MockKLineDataSource>(this));
        }

        /// <summary>
        /// 用给定的 K 线替换本实例的数据并立即发布(不走拉取、不碰进程级时间线)。
        /// 要确定数据的场景用它:单测按已知收盘价验指标、回放一段固定行情。
        /// </summary>
        public void Load(IReadOnlyList<MockKLineBar> bars)
        {
            var snap = new MockKLineBar[bars.Count];
            for (int i = 0; i < snap.Length; i++) snap[i] = bars[i];
            _localCount = snap.Length;
            UpdateBuffer(buf => { buf.Clear(); buf.AddRange(snap); });
        }

        /// <summary>只有收盘价的简便写法:开 = 收,高 / 低 = 收 ± 1,成交量 1000,时间从 2026-01-01 起每分钟一根。</summary>
        public void Load(IReadOnlyList<double> closes)
        {
            var bars = new MockKLineBar[closes.Count];
            var t0 = new DateTime(2026, 1, 1);
            for (int i = 0; i < bars.Length; i++)
            {
                double c = closes[i];
                bars[i] = new MockKLineBar { Time = t0.AddMinutes(i), Open = c, High = c + 1, Low = c - 1, Close = c, Volume = 1000 };
            }
            Load(bars);
        }

        protected override Task<int> OnFetchAsync(string? context, CancellationToken token)
        {
            EnsureSharedFeedStarted();

            // 拿到当前已生成的 bar 快照,各实例独立 fill 自己的 buffer
            MockKLineBar[] snap;
            lock (_sharedLock) snap = _sharedBars.ToArray();

            // 必须在 UpdateBuffer 之前更新 _localCount —— UpdateBuffer 内部立即 Publish,
            // 下游 ViewportManager 读 LogicalLength 决定 ActiveRange,延后赋值会出现"buffer 已有 30 根
            // 但 LogicalLength 还是 0"的瞬间不一致(虽然过一帧自愈,初始 IsLoading flip 时可能误判 empty)。
            _localCount = snap.Length;
            UpdateBuffer(buf => { buf.Clear(); buf.AddRange(snap); });
            return Task.FromResult(_localCount);
        }

        /// <summary>幂等启动 —— 首个 fetch 时建立 seed + 启动 tick timer,后续 fetch 走快照即可。</summary>
        private static void EnsureSharedFeedStarted()
        {
            lock (_sharedLock)
            {
                if (_sharedTimer != null) return;

                // Seed:生成暖机数据(BB(20) 要 20 根,默认 30 留 10 余量让 BB 一开局就有值)。
                // 只在时间线为空时生成:没有定时器(单测 / 基准)时每次拉取都会走到这里,多个 cell 不能各叠一批
                if (_sharedBars.Count == 0)
                {
                    for (int i = 0; i < SeedBars; i++)
                    {
                        var bar = GenerateOneBarInLock();
                        _sharedBars.Add(bar);
                    }
                }

                var startTimer = StartTimer;
                if (startTimer == null) return;  // 没有 UI 宿主(单测等),不推送
                _sharedTimer = startTimer(TimeSpan.FromMilliseconds(TickMs), OnSharedTick);
            }
        }

        /// <summary>timer tick —— 在 UI 线程跑:① 生成 1 根新 bar ② 广播给所有活实例 append + Publish。</summary>
        private static void OnSharedTick()
        {
            MockKLineBar newBar;
            MockKLineDataSource[] subscribers;

            lock (_sharedLock)
            {
                if (_sharedBars.Count >= Math.Max(MaxBars, SeedBars))
                {
                    // 到顶,停止推送 —— 历史保留,用户仍可滚动查看
                    _sharedTimer?.Dispose();
                    _sharedTimer = null;
                    return;
                }

                newBar = GenerateOneBarInLock();
                _sharedBars.Add(newBar);

                // 快照活实例 + 顺手清死引用
                var alive = new List<MockKLineDataSource>(_liveInstances.Count);
                for (int i = _liveInstances.Count - 1; i >= 0; i--)
                {
                    if (_liveInstances[i].TryGetTarget(out var ds)) alive.Add(ds);
                    else _liveInstances.RemoveAt(i);
                }
                subscribers = alive.ToArray();
            }

            // UpdateBuffer 在 _sharedLock 外调 —— 内部自己有 ReactiveDataSource 的 buffer lock,
            // 嵌套两层锁顺序敏感(死锁风险)。
            foreach (var ds in subscribers)
            {
                try
                {
                    // _localCount 必须先增 —— 跟 OnFetchAsync 同理,UpdateBuffer 内立即 Publish,
                    // ViewportManager.OnLogicalLengthChanged 才能正确看到 N→N+1。
                    ds._localCount++;
                    ds.UpdateBuffer(buf => buf.Add(newBar));
                    // UpdateBuffer 内已自动 Publish() → 下游 LineSeries / Candle / bb_breakout 全部刷新
                }
                catch
                {
                    // 实例已 dispose / cell 关闭 → 静默,下一次 tick 会被 WeakReference 清掉
                }
            }
        }

        /// <summary>调用前请持有 _sharedLock。GBM 主体 + 5% 尖峰 + volume 跟 |return| 共振。</summary>
        private static MockKLineBar GenerateOneBarInLock()
        {
            const double dt = 1.0 / 240.0;
            const double sigma = 0.55;
            const double meanRev = 0.003;

            double drift = -meanRev * (Math.Log(_sharedLastClose) - Math.Log(100.0));
            double u1 = 1.0 - _sharedRng.NextDouble();
            double u2 = 1.0 - _sharedRng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double rawReturn = drift * dt + sigma * Math.Sqrt(dt) * z;

            if (_sharedRng.NextDouble() < 0.05)
            {
                double spike = (0.008 + _sharedRng.NextDouble() * 0.017) * (_sharedRng.NextDouble() < 0.5 ? -1 : 1);
                rawReturn += spike;
            }

            double close = _sharedLastClose * Math.Exp(rawReturn);
            double open = _sharedLastClose;
            double bodyHi = Math.Max(open, close);
            double bodyLo = Math.Min(open, close);
            double wick = Math.Abs(close - open) * (0.3 + _sharedRng.NextDouble() * 0.7) + _sharedLastClose * 0.0005;
            double high = bodyHi + wick * _sharedRng.NextDouble();
            double low  = bodyLo - wick * _sharedRng.NextDouble();
            double volume = 3000 + Math.Abs(rawReturn) * 1_500_000 + _sharedRng.NextDouble() * 2000;

            var bar = new MockKLineBar
            {
                Time   = _t0.AddMinutes(_sharedBars.Count),
                Open   = open,
                High   = high,
                Low    = low,
                Close  = close,
                Volume = volume,
            };
            _sharedLastClose = close;
            return bar;
        }
    }
}
