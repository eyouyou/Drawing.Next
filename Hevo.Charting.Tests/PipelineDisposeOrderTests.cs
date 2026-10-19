using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 管线销毁:订阅链从上游往下游释放,pipe(连同黑板)先于下游 Watch 的清理被释放。
    /// 下游清理在已释放的黑板上退订不能抛异常,否则后面的清理被截断;黑板释放时仍在跑的读写者不能炸出锁内部异常。
    /// </summary>
    public sealed class PipelineDisposeOrderTests
    {
        private static readonly DataPort<int> Port = new("PD_Port");

        private sealed class WriteIngestor : IDataIngestor<int>
        {
            public void Process(DataSnapshot<int> snapshot, DataBlackboard board)
            {
                if (snapshot.Count > 0) board.WriteIfChanged(Port, snapshot.AsSpan()[snapshot.Count - 1]);
            }
        }

        private static DataSnapshot<int> Snap(int v) => new(new[] { v }, 1, default);

        [Fact]
        public void DisposingChain_WithDownstreamWatch_RunsEveryCleanup()
        {
            var trigger = new WorkflowTrigger<DataSnapshot<int>>();
            var pipe = new UniversalDataPipe<int>();
            pipe.AddIngestor(new WriteIngestor());
            bool onDisposeRan = false;
            int watched = 0;

            var flow = PipelineWiring.WireToPipe(trigger, pipe, () => onDisposeRan = true)
                .Watch(new object[] { Port }, _ => watched++)
                .DoOnDispose(() => { });
            bool outerCleanupRan = false;
            var sub = flow.DoOnDispose(() => outerCleanupRan = true).Subscribe(_ => { });

            trigger.Push(Snap(1));
            Assert.True(watched > 0);

            var ex = Record.Exception(() => sub.Dispose());

            Assert.Null(ex);
            Assert.True(pipe.Board.IsDisposed);
            Assert.True(onDisposeRan);
            Assert.True(outerCleanupRan, "下游清理被截断");
        }

        [Fact]
        public void AddAndRemoveWatch_OnDisposedBoard_AreSilent()
        {
            var board = new DataBlackboard();
            Action<DataBlackboard> cb = _ => { };
            var watch = board.AddWatch(new object[] { Port }, cb);
            board.Dispose();

            Assert.Null(Record.Exception(() => watch.Dispose()));
            IDisposable? late = null;
            Assert.Null(Record.Exception(() => late = board.AddWatch(new object[] { Port }, cb)));
            Assert.Null(Record.Exception(() => late!.Dispose()));
        }

        [Fact]
        public void CompositeDisposable_OneThrows_OthersStillDisposed()
        {
            bool second = false;
            var cd = new CompositeDisposable(
                new DisposableAction(() => throw new InvalidOperationException("boom")),
                new DisposableAction(() => second = true));

            Assert.Throws<InvalidOperationException>(() => cd.Dispose());
            Assert.True(second);
        }

        [Fact]
        public void DisposeWhileWriterRuns_OnlyObjectDisposedOrSilent()
        {
            for (int round = 0; round < 50; round++)
            {
                var pipe = new UniversalDataPipe<int>();
                pipe.AddIngestor(new WriteIngestor());
                Exception? unexpected = null;
                using var started = new ManualResetEventSlim();
                var writer = Task.Factory.StartNew(() =>
                {
                    int i = 0;
                    while (!pipe.Board.IsDisposed)
                    {
                        try { pipe.Process(Snap(++i)); }
                        catch (ObjectDisposedException) { }
                        catch (Exception ex) { unexpected = ex; return; }
                        started.Set();
                    }
                }, TaskCreationOptions.LongRunning);

                started.Wait(TimeSpan.FromSeconds(5));
                var disposeError = Record.Exception(() => pipe.Dispose());
                Assert.True(writer.Wait(TimeSpan.FromSeconds(5)), "写线程没有退出");
                Assert.Null(disposeError);
                Assert.Null(unexpected);
            }
        }
    }
}
