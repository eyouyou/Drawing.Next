using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 双源 TimeAxisCoordinator:两路源在各自线程上 Publish。
    /// Publish 持源锁同步跑 Merge,若 Merge 再去拿另一路源的锁(GetSnapshot),两个方向同时发生就互相等死。
    /// </summary>
    public sealed class TimeAxisCoordinatorConcurrencyTests
    {
        public readonly record struct TimedItem(DateTime Time, double Value) : ITimePoint;

        public sealed class TimedSource : BufferedDataSource<TimedSource, TimedItem>
        {
            public void PublishRange(int length, double value)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < length; i++) _buffer.Add(new TimedItem(new DateTime(2000, 1, 1).AddMinutes(i), value));
                    Publish();
                }
            }
        }

        private sealed class CoordSchema : ChartReactiveSchema
        {
            private readonly Action<ChartCell> _flow;
            public CoordSchema(Action<ChartCell> flow) { _flow = flow; }
            protected override void DefineDataFlow(ChartCell chart) => _flow(chart);
            protected override void DefineFeatures(IFeatureContext canvas) { }
        }

        [Fact]
        public void TwoSourcesPublishingConcurrently_DoNotDeadlock()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var a = new TimedSource();
                var b = new TimedSource();
                var timePort = new DataPort<ReadOnlyMemory<DateTime>>("TAC_Time");
                var portA = new DataPort<ReadOnlyMemory<double>>("TAC_A");
                var portB = new DataPort<ReadOnlyMemory<double>>("TAC_B");
                var schema = new CoordSchema(chart => new TimeAxisCoordinator(timePort)
                    .AddSource(a, (Func<TimedItem, double>)(x => x.Value), portA)
                    .AddSource(b, (Func<TimedItem, double>)(x => x.Value), portB)
                    .BindTo(chart));
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                using var start = new Barrier(2);
                Task Writer(TimedSource src) => Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < 2000; i++) src.PublishRange(50 + i % 7, i);
                }, TaskCreationOptions.LongRunning);

                var both = Task.WhenAll(Writer(a), Writer(b));
                Assert.True(both.Wait(TimeSpan.FromSeconds(15)), "两路源并发 Publish 发生死锁");
            });
        }

        [Fact]
        public void Merge_SeesLatestPublishOfEverySource()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var a = new TimedSource();
                var b = new TimedSource();
                var timePort = new DataPort<ReadOnlyMemory<DateTime>>("TAC2_Time");
                var portA = new DataPort<ReadOnlyMemory<double>>("TAC2_A");
                var portB = new DataPort<ReadOnlyMemory<double>>("TAC2_B");
                var coord = new TimeAxisCoordinator(timePort)
                    .AddSource(a, (Func<TimedItem, double>)(x => x.Value), portA)
                    .AddSource(b, (Func<TimedItem, double>)(x => x.Value), portB);
                DataBlackboard? board = null;
                var schema = new CoordSchema(chart => { coord.BindTo(chart); board = coord.Pipe()._pipe.Board; });
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                a.PublishRange(3, 1);
                b.PublishRange(5, 2);

                Assert.NotNull(board);
                using (board!.AcquireReadLock())
                {
                    Assert.Equal(5, board.Read(timePort).Length);
                    var colA = board.Read(portA).Span;
                    var colB = board.Read(portB).Span;
                    Assert.Equal(1, colA[2]);
                    Assert.True(double.IsNaN(colA[4]));
                    Assert.Equal(2, colB[4]);
                }
            });
        }
    }
}
