using System;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

// 开发规范 §5 布林带 SOP —— 步骤 B 进阶:现成的不够用时自己写计算 Feature(继承 ComputeFeatureBase)。
// 能用 ComputeFeature 就别写这个:线程 / 锁 / 缓冲池 / Abandon 处理都要自己写,容易写错。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 布林带计算节点:收盘价列 → 上 / 中 / 下三根列。纯 board → board,不挂图层。
    /// <para>
    /// 跟 <see cref="ComputeFeature"/> 同一个基类、同一套线程模型,区别是线程 / 锁 / 缓冲池都自己写:
    /// <c>WatchAsync</c> 后台线程 + Compute-then-Commit(读锁内只取列引用 → 锁外算 → 写锁内提交),
    /// 输出缓冲从 <see cref="ResultBufferPool"/> 租,每次发布一块新缓冲,零分配。
    /// </para>
    /// </summary>
    public sealed class BollingerComputeNode : ComputeFeatureBase
    {
        private ResultBufferPool _buffers = new ResultBufferPool();

        public DataPort<ReadOnlyMemory<double>> InputPort { get; init; } = null!;

        [PortDirection(PortDirection.Output)]
        public DataPort<ReadOnlyMemory<double>> UpperPort { get; init; } = null!;

        [PortDirection(PortDirection.Output)]
        public DataPort<ReadOnlyMemory<double>> MiddlePort { get; init; } = null!;

        [PortDirection(PortDirection.Output)]
        public DataPort<ReadOnlyMemory<double>> LowerPort { get; init; } = null!;

        /// <summary>均线周期(根数)。</summary>
        public int Period { get; init; } = 20;

        /// <summary>上下轨距中轨几个标准差。</summary>
        public double Width { get; init; } = 2.0;

        protected override void OnComputeCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            flow.WatchAsync(new object[] { InputPort }, board =>
            {
                var buffers = _buffers;
                using (var call = buffers.BeginCall())
                {
                    try { Recompute(board, call); }
                    // 宿主判定本次调用超时会 Abandon 缓冲池,下一次换新的,旧池留给还在跑的线程
                    finally { if (buffers.IsAbandoned) _buffers = new ResultBufferPool(); }
                }
            });
        }

        // 数据流由 OnComputeCompose 里的 WatchAsync 驱动,帧路径无事可做。
        protected override void OnComputeProject(FeatureContext ctx) { }

        private void Recompute(DataBlackboard board, ResultBufferPool.CallScope call)
        {
            // ① 读锁内只拿列引用(WatchAsync 回调本身是受保护的列读者,出锁后本回调内可继续读)
            ReadOnlyMemory<double> close;
            using (board.AcquireReadLock())
            {
                close = board.Read(InputPort);
            }
            int n = close.Length;
            if (n == 0) return;

            // ② 锁外算,结果写进租来的缓冲
            var upper = call.Rent(n);
            var middle = call.Rent(n);
            var lower = call.Rent(n);
            BollingerMath.Compute(close.Span, Period, Width, upper.AsSpan(0, n), middle.AsSpan(0, n), lower.AsSpan(0, n));

            // ③ 写锁内提交,并告诉缓冲池哪块已发布(发布中的缓冲不会被再次租出)
            var u = new ReadOnlyMemory<double>(upper, 0, n);
            var m = new ReadOnlyMemory<double>(middle, 0, n);
            var l = new ReadOnlyMemory<double>(lower, 0, n);
            using (board.AcquireWriteLock())
            {
                board.WriteIfChanged(UpperPort, u); call.Written(UpperPort, u);
                board.WriteIfChanged(MiddlePort, m); call.Written(MiddlePort, m);
                board.WriteIfChanged(LowerPort, l); call.Written(LowerPort, l);
            }
        }
    }

    public static class BollingerComputeNodeExtensions
    {
        /// <summary>进阶写法的装配入口:挂一个 <see cref="BollingerComputeNode"/>。</summary>
        public static SeriesBuilder AddBollingerNode(this SeriesBuilder builder,
            DataPort<ReadOnlyMemory<double>> source, BollingerPorts output, int period = 20, double width = 2.0)
        {
            builder.Canvas.Add(new BollingerComputeNode
            {
                InputPort = source,
                UpperPort = output.Upper,
                MiddlePort = output.Middle,
                LowerPort = output.Lower,
                Period = period,
                Width = width,
            });
            return builder;
        }
    }
}
