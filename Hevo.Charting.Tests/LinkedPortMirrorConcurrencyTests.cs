using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 联动镜像桥的并发:两块板在不同线程上同时写同一个镜像端口。
    /// 镜像回调跑在源板写锁内,若在锁内去拿其它板的写锁,A→B 与 B→A 两个方向同时发生就互相等死。
    /// </summary>
    public sealed class LinkedPortMirrorConcurrencyTests
    {
        private const int Iterations = 2000;

        [Fact]
        public void ConcurrentWritesOnTwoBoards_DoNotDeadlock_AndConverge()
        {
            var context = new LinkedChartContext();
            var a = new DataBlackboard();
            var b = new DataBlackboard();
            context.AttachBoard(a);
            context.AttachBoard(b);
            var port = context.SharedViewport.LogicalLength;

            using var start = new Barrier(2);
            Task Writer(DataBlackboard board, int offset) => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                for (int i = 1; i <= Iterations; i++)
                {
                    using (board.WriteScope()) board.Write(port, i * 2 + offset);
                }
            }, TaskCreationOptions.LongRunning);

            var both = Task.WhenAll(Writer(a, 0), Writer(b, 1));
            Assert.True(both.Wait(TimeSpan.FromSeconds(15)), "两块板交叉写镜像端口发生死锁");

            int va, vb;
            using (a.ReadScope()) va = a.Read(port);
            using (b.ReadScope()) vb = b.Read(port);
            Assert.Equal(va, vb);
        }

        [Fact]
        public void WriteInsideTransaction_MirrorsAfterCommit()
        {
            var context = new LinkedChartContext();
            var a = new DataBlackboard();
            var b = new DataBlackboard();
            context.AttachBoard(a);
            context.AttachBoard(b);
            var port = context.SharedViewport.LogicalLength;

            using (a.WriteScope()) a.Write(port, 42);

            using (b.ReadScope()) Assert.Equal(42, b.Read(port));
        }

        [Fact]
        public void Extent_IsMirrored_AndArrivesBeforeLength()
        {
            var context = new LinkedChartContext();
            var a = new DataBlackboard();
            var b = new DataBlackboard();
            context.AttachBoard(a);
            context.AttachBoard(b);
            var vp = context.SharedViewport;
            var extent = new Hevo.Charting.Core.DataExtent(new object(), new long[] { 1, 2, 3 }, false);

            // 对端长度变化时,键必须已经对得上(否则那次 Clamp 退回按长度差猜)
            int keysWhenLengthArrived = -1;
            b.AddWatch(new object[] { vp.LogicalLength }, board => keysWhenLengthArrived = board.Read(vp.Extent).Keys.Length);

            using (a.WriteScope())
            {
                a.Write(vp.LogicalLength, 3);
                a.Write(vp.Extent, extent);
            }

            Assert.Equal(3, keysWhenLengthArrived);
            using (b.ReadScope()) Assert.True(b.Read(vp.Extent).Equals(extent));
        }

        [Fact]
        public void MirroredWrite_IsVisibleToAllOtherBoards()
        {
            var context = new LinkedChartContext();
            var boards = new[] { new DataBlackboard(), new DataBlackboard(), new DataBlackboard() };
            foreach (var board in boards) context.AttachBoard(board);
            var port = context.SharedViewport.LogicalLength;

            using (boards[1].WriteScope()) boards[1].Write(port, 7);

            foreach (var board in boards)
                using (board.ReadScope()) Assert.Equal(7, board.Read(port));
        }

        [Fact]
        public void DisposedBoard_IsSkipped()
        {
            var context = new LinkedChartContext();
            var a = new DataBlackboard();
            var b = new DataBlackboard();
            context.AttachBoard(a);
            context.AttachBoard(b);
            b.Dispose();
            var port = context.SharedViewport.LogicalLength;

            using (a.WriteScope()) a.Write(port, 5);

            using (a.ReadScope()) Assert.Equal(5, a.Read(port));
        }
    }
}
