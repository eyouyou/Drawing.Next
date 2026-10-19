using System;
using BenchmarkDotNet.Attributes;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 黑板写入 + 提交分发的成本,跟 Watch 数量的关系。Watch 走正式的 <c>flow.Watch</c> 挂上去(跟 Feature 装配同一条路),
    /// 写入都在 <c>WriteScope</c> 里裸写一次(非事务写 = 写完立即提交)。
    /// <list type="bullet">
    /// <item>CommitOneDirty:50 个 Watch 各听一个端口,写其中一个 → 只该跑 1 个回调。</item>
    /// <item>CommitNoneDirty:50 个 Watch,写一个谁都不听的端口 → 一个回调都不该跑(纯提交开销)。</item>
    /// <item>RawWriteNoWatch:没有 Watch 的黑板上单次裸写(写入本身 + 版本号)。</item>
    /// <item>CrosshairMove:仿十字光标 —— 20 个 Watch,鼠标端口的 Watch 写命中端口,命中端口的 Watch 再读它;
    /// 其余 18 个听视口端口不动。每次迭代 = 一次鼠标移动。</item>
    /// </list>
    ///   dotnet run -c Release -- --filter "*BlackboardWatch*"
    /// </summary>
    [MemoryDiagnoser]
    public class BlackboardWatchBenchmarks
    {
        private const int WatchCount = 50;

        private DataBlackboard _watched = null!;
        private DataPort<double>[] _watchedPorts = Array.Empty<DataPort<double>>();
        private readonly DataPort<double> _unwatched = new("unwatched");
        private IDisposable[] _watchedSubs = Array.Empty<IDisposable>();

        private DataBlackboard _bare = null!;
        private readonly DataPort<double> _barePort = new("bare");

        private DataBlackboard _cross = null!;
        private readonly DataPort<double> _pointer = new("pointer");
        private readonly DataPort<double> _hit = new("hit");
        private IDisposable[] _crossSubs = Array.Empty<IDisposable>();

        private double _v;
        private int _calls;

        [GlobalSetup]
        public void Setup()
        {
            _watched = new DataBlackboard();
            _watchedPorts = new DataPort<double>[WatchCount];
            _watchedSubs = new IDisposable[WatchCount];
            for (int i = 0; i < WatchCount; i++)
            {
                _watchedPorts[i] = new DataPort<double>($"w{i}");
                _watchedSubs[i] = Source(_watched).Watch(new object[] { _watchedPorts[i] }, _ => _calls++).Subscribe(_ => { });
            }

            _bare = new DataBlackboard();

            _cross = new DataBlackboard();
            var subs = new System.Collections.Generic.List<IDisposable>
            {
                Source(_cross).Watch(new object[] { _pointer }, b => b.Write(_hit, b.Read(_pointer) * 2)).Subscribe(_ => { }),
                Source(_cross).Watch(new object[] { _hit }, b => _calls += (int)b.Read(_hit) & 1).Subscribe(_ => { }),
            };
            for (int i = 0; i < 18; i++)
            {
                var vp = new DataPort<double>($"vp{i % 3}");
                subs.Add(Source(_cross).Watch(new object[] { vp }, _ => _calls++).Subscribe(_ => { }));
            }
            _crossSubs = subs.ToArray();
        }

        // 订阅即推一次当前板:跟 schema 主管线推板给 Feature 的 Watch 一样
        private static IWorkflow<DataBlackboard> Source(DataBlackboard board)
            => new WorkflowEngine<DataBlackboard>((next, _) => { next(board); return DisposableAction.Empty; });

        [GlobalCleanup]
        public void Cleanup()
        {
            foreach (var s in _watchedSubs) s.Dispose();
            foreach (var s in _crossSubs) s.Dispose();
            _watched.Dispose();
            _bare.Dispose();
            _cross.Dispose();
        }

        [Benchmark]
        public int CommitOneDirty()
        {
            using (_watched.WriteScope()) _watched.Write(_watchedPorts[0], ++_v);
            return _calls;
        }

        [Benchmark]
        public int CommitNoneDirty()
        {
            using (_watched.WriteScope()) _watched.Write(_unwatched, ++_v);
            return _calls;
        }

        [Benchmark]
        public double RawWriteNoWatch()
        {
            using (_bare.WriteScope()) _bare.Write(_barePort, ++_v);
            return _v;
        }

        [Benchmark]
        public int CrosshairMove()
        {
            using (_cross.WriteScope()) _cross.Write(_pointer, ++_v);
            return _calls;
        }
    }
}
