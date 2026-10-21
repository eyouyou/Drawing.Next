using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// IncrementalComputeFeature.ExtentPort:已算过的那段被换掉(换纪元 / 前插 / 补洞)清 state 从头算;
    /// 追加、改末根续算;不配时 state 只增不清(原行为)。
    /// handler 把"自上次清空以来调了几次"记在 state 里,清没清一看便知。
    /// </summary>
    public sealed class IncrementalComputeExtentTests
    {
        private sealed class Schema : ChartReactiveSchema
        {
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public required IncrementalComputeFeature Feature { get; init; }
            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas) => canvas.Add(Feature);
        }

        private sealed class Rig
        {
            public readonly DataBlackboard Board = new();
            public readonly DataPort<ReadOnlyMemory<double>> Input = new("T_In");
            public readonly DataPort<ReadOnlyMemory<double>> Output = new("T_Out");
            public readonly DataPort<DataExtent> Extent = new("T_Extent");

            public Rig(bool withExtent)
            {
                var feature = new IncrementalComputeFeature
                {
                    InputPort = Input,
                    OutputPort = Output,
                    ExtentPort = withExtent ? Extent : null,
                    StatefulCompute = (input, prev) =>
                    {
                        var next = new[] { prev.Length == 0 ? 1.0 : prev.Span[0] + 1 };
                        return (next, next);
                    },
                };
                var schema = new Schema { Feature = feature };
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                schema.Trigger.Push(Board);
            }

            /// <summary>同 ProjectExtent + LinkStream:输入和 Extent 同一事务写。</summary>
            public void Publish(long[] keys, object epoch)
            {
                using (Board.WriteScope())
                {
                    Board.Write(Extent, new DataExtent(epoch, keys, false));
                    Board.Write(Input, new double[keys.Length]);
                }
            }

            /// <summary>改末根:同一键数组、新的输入块(ForceWrite)。</summary>
            public void Tick(long[] keys, object epoch)
            {
                using (Board.WriteScope())
                {
                    Board.Write(Extent, new DataExtent(epoch, keys, false));
                    Board.ForceWrite(Input, new double[keys.Length]);
                }
            }

            public double Calls { get { using (Board.ReadScope()) return Board.Read(Output).Span[0]; } }
        }

        private static long[] Keys(long first, int count)
        {
            var k = new long[count];
            for (int i = 0; i < count; i++) k[i] = first + i;
            return k;
        }

        [Fact]
        public void AppendAndTick_ContinueState() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(withExtent: true);
            var epoch = new object();
            var keys = Keys(100, 10);
            rig.Publish(keys, epoch);
            rig.Tick(keys, epoch);
            rig.Publish(Keys(100, 11), epoch);

            Assert.Equal(3, rig.Calls);
        });

        [Fact]
        public void Prepend_RestartsState() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(withExtent: true);
            var epoch = new object();
            rig.Publish(Keys(100, 10), epoch);
            rig.Publish(Keys(100, 11), epoch);
            rig.Publish(Keys(50, 61), epoch);      // 前插 50 根:末根不在原下标

            Assert.Equal(1, rig.Calls);
        });

        [Fact]
        public void FillGap_RestartsState() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(withExtent: true);
            var epoch = new object();
            rig.Publish(new long[] { 1, 3, 5 }, epoch);
            rig.Publish(new long[] { 1, 2, 3, 5 }, epoch);

            Assert.Equal(1, rig.Calls);
        });

        [Fact]
        public void NewEpochSameKeys_RestartsState() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(withExtent: true);
            rig.Publish(Keys(100, 10), new object());
            rig.Publish(Keys(100, 10), new object());   // 复权:键没变、值全变

            Assert.Equal(1, rig.Calls);
        });

        [Fact]
        public void WithoutExtentPort_StateNeverResets() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(withExtent: false);
            rig.Publish(Keys(100, 10), new object());
            rig.Publish(Keys(50, 60), new object());

            Assert.Equal(2, rig.Calls);
        });
    }
}
