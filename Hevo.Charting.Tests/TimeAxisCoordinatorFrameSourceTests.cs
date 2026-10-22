using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;
using static Hevo.Charting.Tests.TimeAxisCoordinatorConcurrencyTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 帧式数据源(快照里一个元素是一整块列)参与时间对齐:和逐点源混着登记,
    /// 合并结果要和把同样的数据全换成逐点源时一模一样。
    /// </summary>
    public sealed class TimeAxisCoordinatorFrameSourceTests
    {
        /// <summary>一帧 = 时间列 + 值列(模拟 K 线 / 分时的列式帧)。</summary>
        public sealed class Frame
        {
            public Frame(DateTime[] times, double[] values) { Times = times; Values = values; }
            public DateTime[] Times { get; }
            public double[] Values { get; }
        }

        public sealed class FrameSource : BufferedDataSource<FrameSource, Frame>
        {
            public void PublishFrame(Frame frame)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    _buffer.Add(frame);
                    Publish();
                }
            }
        }

        private sealed class PointSource : BufferedDataSource<PointSource, TimedItem>
        {
            public void PublishPoints(DateTime[] times, double[] values)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < times.Length; i++) _buffer.Add(new TimedItem(times[i], values[i]));
                    Publish();
                }
            }
        }

        private sealed class CoordSchema : ChartReactiveSchema
        {
            private readonly Action<IChartCell> _flow;
            public CoordSchema(Action<IChartCell> flow) { _flow = flow; }
            protected override void DefineDataFlow(IChartCell chart) => _flow(chart);
            protected override void DefineFeatures(IFeatureContext canvas) { }
        }

        private static readonly DateTime T0 = new DateTime(2026, 10, 22, 9, 30, 0);

        // 主源:9:30–9:39 缺 9:33;基准源:9:31–9:41(两头错开,中间缺 9:36)
        private static (DateTime[] t, double[] v) Main()
        {
            var t = new List<DateTime>();
            var v = new List<double>();
            for (int i = 0; i < 10; i++) if (i != 3) { t.Add(T0.AddMinutes(i)); v.Add(100 + i); }
            return (t.ToArray(), v.ToArray());
        }

        private static (DateTime[] t, double[] v) Baseline()
        {
            var t = new List<DateTime>();
            var v = new List<double>();
            for (int i = 1; i <= 11; i++) if (i != 6) { t.Add(T0.AddMinutes(i)); v.Add(i * 0.5); }
            return (t.ToArray(), v.ToArray());
        }

        private static (DateTime[] time, double[] a, double[] b) Run(bool mainAsFrame, bool baseAsFrame)
        {
            (DateTime[], double[], double[]) result = default;
            IncrementalTestKit.RunOnSta(() =>
            {
                var timePort = new DataPort<ReadOnlyMemory<DateTime>>("TACF_Time");
                var portA = new DataPort<ReadOnlyMemory<double>>("TACF_A");
                var portB = new DataPort<ReadOnlyMemory<double>>("TACF_B");
                var coord = new TimeAxisCoordinator(timePort);
                var mainFrame = new FrameSource();
                var mainPoints = new PointSource();
                var baseFrame = new FrameSource();
                var basePoints = new PointSource();

                if (mainAsFrame) coord.AddSource(mainFrame, f => f.Times.Length, (f, i) => f.Times[i], (f, i) => f.Values[i], portA);
                else coord.AddSource(mainPoints, (Func<TimedItem, double>)(x => x.Value), portA);
                if (baseAsFrame) coord.AddSource(baseFrame, f => f.Times.Length, (f, i) => f.Times[i], (f, i) => f.Values[i], portB);
                else coord.AddSource(basePoints, (Func<TimedItem, double>)(x => x.Value), portB);

                DataBlackboard? board = null;
                var schema = new CoordSchema(chart => { coord.BindTo(chart); board = coord.Pipe()._pipe.Board; });
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                var (mt, mv) = Main();
                var (bt, bv) = Baseline();
                if (mainAsFrame) mainFrame.PublishFrame(new Frame(mt, mv)); else mainPoints.PublishPoints(mt, mv);
                if (baseAsFrame) baseFrame.PublishFrame(new Frame(bt, bv)); else basePoints.PublishPoints(bt, bv);

                using (board!.ReadScope())
                    result = (board.Read(timePort).ToArray(), board.Read(portA).ToArray(), board.Read(portB).ToArray());
            });
            return result;
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void FrameSources_MergeExactlyLikePointSources(bool mainAsFrame, bool baseAsFrame)
        {
            var expected = Run(false, false);
            var actual = Run(mainAsFrame, baseAsFrame);

            Assert.Equal(12, expected.time.Length);                     // 9:30–9:41 并集
            Assert.Equal(expected.time, actual.time);
            Assert.Equal(expected.a, actual.a);                          // NaN 位置也一致(Assert.Equal 对 double 认 NaN == NaN)
            Assert.Equal(expected.b, actual.b);
            Assert.True(double.IsNaN(actual.a[3]));                      // 主源缺 9:33
            Assert.True(double.IsNaN(actual.b[0]));                      // 基准源 9:30 还没有
        }

        [Fact]
        public void EmptyFrameSnapshot_ContributesNoPoints()
        {
            var r = Run(true, false);
            Assert.Equal(12, r.time.Length);
            IncrementalTestKit.RunOnSta(() =>
            {
                var timePort = new DataPort<ReadOnlyMemory<DateTime>>("TACF2_Time");
                var port = new DataPort<ReadOnlyMemory<double>>("TACF2_A");
                var src = new FrameSource();
                var coord = new TimeAxisCoordinator(timePort).AddSource(src, f => f.Times.Length, (f, i) => f.Times[i], (f, i) => f.Values[i], port);
                DataBlackboard? board = null;
                var schema = new CoordSchema(chart => { coord.BindTo(chart); board = coord.Pipe()._pipe.Board; });
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                src.PublishFrame(new Frame(Array.Empty<DateTime>(), Array.Empty<double>()));
                using (board!.ReadScope()) Assert.Equal(0, board.Read(timePort).Length);
            });
        }
    }
}
