using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

// 开发规范 §5 布林带 SOP —— 步骤 B 写法①:数据管线内 .Compute(state, static ...)。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 给 <c>Pipe().LinkStream(...).Compute(state, static (board, source, st) => st.Recompute(board))</c> 用的状态对象。
    /// <para>
    /// Compute 节点跟摄入器在<b>同一个黑板写事务</b>里同步执行(LinkStream 先把 Close 列写进去,这里紧接着读),
    /// 所以不用自己加锁,三根轨道跟收盘价在同一次提交里出现,不会有"价格新了、轨道还是旧的"的中间帧。
    /// 闭包必须 <c>static</c>,用到的东西全放进本 state。
    /// </para>
    /// <para>
    /// 发布列一律走 <see cref="ColumnPublisher{T}"/>:每次写一块新缓冲,不原地改已发布的数组
    /// (帧 / 指标可能正在锁外读它)。
    /// </para>
    /// </summary>
    public sealed class BollingerPipelineState
    {
        private readonly DataPort<ReadOnlyMemory<double>> _input;
        private readonly BollingerPorts _output;
        private readonly int _period;
        private readonly double _width;

        // 一个端口一个 publisher
        private readonly ColumnPublisher<double> _upper = new ColumnPublisher<double>();
        private readonly ColumnPublisher<double> _middle = new ColumnPublisher<double>();
        private readonly ColumnPublisher<double> _lower = new ColumnPublisher<double>();

        public BollingerPipelineState(DataPort<ReadOnlyMemory<double>> input, BollingerPorts output, int period = 20, double width = 2.0)
        {
            _input = input;
            _output = output;
            _period = period;
            _width = width;
        }

        /// <summary>在黑板写事务内调用(由管线保证),直接读写即可。</summary>
        public void Recompute(DataBlackboard board)
        {
            var close = board.Read(_input);
            int n = close.Length;
            if (n == 0) return;

            var upper = _upper.Rent(n);
            var middle = _middle.Rent(n);
            var lower = _lower.Rent(n);
            BollingerMath.Compute(close.Span, _period, _width, upper.AsSpan(0, n), middle.AsSpan(0, n), lower.AsSpan(0, n));

            _upper.Publish(board, _output.Upper, upper, n);
            _middle.Publish(board, _output.Middle, middle, n);
            _lower.Publish(board, _output.Lower, lower, n);
        }
    }
}
