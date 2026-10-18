using System;
using System.Reflection;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Hevo.Charting.Core;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    // 并发与生命周期修复涉及的热路径:联动镜像、TimeAxisCoordinator 合并、RequestBus 往返。
    // 黑板事务 commit 路径见 BlackboardTransactionBenchmarks。只用修复前后都有的 API,两边可以跑同一份代码对比。
    //
    //   dotnet run -c Release -- --filter "*PortMirror*" "*TimeAxisMerge*" "*RequestBusRoundTrip*" "*BlackboardTransaction*"

    /// <summary>
    /// 联动 dashboard 里一块板写镜像端口(典型:平移改 ActiveRange / 十字光标改 linkedHit),值同步到其它 N-1 块板。
    /// UnmirroredWrite 写同一块板上不镜像的端口,看锁作用域本身有没有变贵。
    /// </summary>
    [MemoryDiagnoser]
    public class PortMirrorBenchmarks
    {
        [Params(2, 4)] public int Boards;

        private readonly DataPort<double> _plain = new("PM_Plain");
        private DataBlackboard[] _boards = Array.Empty<DataBlackboard>();
        private DataPort<int> _mirrored = null!;
        private int _v;

        [GlobalSetup]
        public void Setup()
        {
            var context = new LinkedChartContext();
            _mirrored = context.SharedViewport.LogicalLength;
            _boards = new DataBlackboard[Boards];
            for (int i = 0; i < Boards; i++)
            {
                _boards[i] = new DataBlackboard();
                context.AttachBoard(_boards[i]);
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            foreach (var b in _boards) b.Dispose();
        }

        [Benchmark(Baseline = true)]
        public void MirroredWrite()
        {
            var b = _boards[0];
            using (b.AcquireWriteLock()) b.WriteIfChanged(_mirrored, ++_v);
        }

        [Benchmark]
        public void MirroredWriteInTransaction()
        {
            var b = _boards[0];
            using (b.BeginTransaction()) b.WriteIfChanged(_mirrored, ++_v);
        }

        [Benchmark]
        public void UnmirroredWrite()
        {
            var b = _boards[0];
            using (b.AcquireWriteLock()) b.WriteIfChanged(_plain, ++_v);
        }
    }

    /// <summary>
    /// 两路源 × Bars 根的时间轴并集合并。每次调用:源 A 改最后一根再 Publish → 脉冲 → Merge 一次(跟实时 tick 一样)。
    /// </summary>
    [MemoryDiagnoser]
    public class TimeAxisMergeBenchmarks
    {
        [Params(2000, 20000)] public int Bars;

        public readonly record struct Item(DateTime Time, double Value) : ITimePoint;

        public sealed class Source : BufferedDataSource<Source, Item>
        {
            public void Fill(int n, int step)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    var t0 = new DateTime(2020, 1, 1);
                    for (int i = 0; i < n; i++) _buffer.Add(new Item(t0.AddMinutes(i * step), i));
                    Publish();
                }
            }

            public void Tick(double v)
            {
                lock (_lock)
                {
                    var last = _buffer[_buffer.Count - 1];
                    _buffer[_buffer.Count - 1] = last with { Value = v };
                    Publish();
                }
            }
        }

        private readonly Source _a = new();
        private readonly Source _b = new();
        private TimeAxisCoordinator _coord = null!;
        private IDisposable? _sub;
        private double _v;

        [GlobalSetup]
        public void Setup()
        {
            _a.Fill(Bars, 1);
            _b.Fill(Bars, 2);
            _coord = new TimeAxisCoordinator(new DataPort<ReadOnlyMemory<DateTime>>("TM_Time"))
                .AddSource(_a, (Func<Item, double>)(x => x.Value), new DataPort<ReadOnlyMemory<double>>("TM_A"))
                .AddSource(_b, (Func<Item, double>)(x => x.Value), new DataPort<ReadOnlyMemory<double>>("TM_B"));
            // BindTo(ChartCell) 之外接管线:只为脉冲放行,置位 _bound 后直接订阅封口后的流
            typeof(TimeAxisCoordinator).GetField("_bound", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_coord, true);
            _sub = _coord.Pipe().Seal().Subscribe(_ => { });
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _sub?.Dispose();
            _coord.Dispose();
        }

        [Benchmark]
        public void TickAndMerge() => _a.Tick(++_v);
    }

    /// <summary>
    /// RequestBus 一次 await 往返(handler 同步完成),量 bus 自身的开销:TCS、令牌联动、在途登记。
    /// </summary>
    [MemoryDiagnoser]
    public class RequestBusRoundTripBenchmarks
    {
        private RequestBus<int, int> _bus = null!;

        [GlobalSetup]
        public void Setup() => _bus = new RequestBus<int, int>((x, _) => Task.FromResult(x + 1));

        [GlobalCleanup]
        public void Cleanup() => _bus.Dispose();

        [Benchmark]
        public Task<int> RequestAsync() => _bus.RequestAsync(1);
    }
}
