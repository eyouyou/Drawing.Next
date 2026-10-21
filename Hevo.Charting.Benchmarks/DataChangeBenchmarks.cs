using System;
using BenchmarkDotNet.Attributes;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 视口长度写入带 / 不带变化描述的开销(同一构建里对比,旧发布方走 Legacy)。
    /// 黑板上挂一个仿视口管家的 Watch(听长度 / 用户意图 / 变化记录,回调里读记录取合并变化)。
    /// <list type="bullet">
    /// <item>Tick:实时 tick 改末根,长度不变。带描述时 UpdateTail 不记账,应与 Legacy 持平(热路径不多写)。</item>
    /// <item>Grow:翻页前插(带描述 = Prepend)/ 旧发布方只写长度。带描述多写一个端口。</item>
    /// </list>
    ///   dotnet run -c Release -- --filter "*DataChange*"
    /// </summary>
    [MemoryDiagnoser]
    public class DataChangeBenchmarks
    {
        private readonly ViewportPorts _vp = new();
        private DataBlackboard _board = null!;
        private IDisposable _sub = null!;
        private DataChangeLog _cursor;
        private int _length;
        private long _sink;

        [GlobalSetup]
        public void Setup()
        {
            _board = new DataBlackboard();
            var source = new WorkflowEngine<DataBlackboard>((next, _) => { next(_board); return DisposableAction.Empty; });
            _sub = source.Watch(new object[] { _vp.LogicalLength, _vp.UserRange, _vp.LengthChange }, b =>
            {
                var log = b.Read(_vp.LengthChange);
                var change = log.Since(_cursor);
                _cursor = log;
                _sink += b.Read(_vp.LogicalLength) + (change.HasValue ? change.Value.Prepended : 0);
            }).Subscribe(_ => { });
            _length = 20000;
            using (_board.WriteScope()) _vp.WriteLength(_board, _length, DataChange.Reset);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _sub.Dispose();
            _board.Dispose();
        }

        [Benchmark(Baseline = true)]
        public void Tick_Legacy()
        {
            using (_board.WriteScope()) _vp.WriteLength(_board, _length, null);
        }

        [Benchmark]
        public void Tick_WithChange()
        {
            using (_board.WriteScope()) _vp.WriteLength(_board, _length, DataChange.UpdateTail);
        }

        [Benchmark]
        public void Grow_Legacy()
        {
            using (_board.WriteScope()) _vp.WriteLength(_board, ++_length, null);
        }

        [Benchmark]
        public void Grow_WithChange()
        {
            using (_board.WriteScope()) _vp.WriteLength(_board, ++_length, DataChange.Prepend(1));
        }
    }
}
