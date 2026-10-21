using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 按键分页(DataPagingFeature.OnRequireBefore):视口锚点之前不够"安全缓冲 + 左侧留白"根时问数据源一次,
    /// 够了不问;左墙来自 DataExtent.OlderExhausted;同一问题 Pacing 内不重复问。老回调的行为见 DataPagingFeatureTests。
    /// </summary>
    public sealed class DataPagingKeyedTests
    {
        private const int Span = 49;   // DefaultVisibleCount 50

        private sealed class PagingSchema : ChartReactiveSchema
        {
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public required ViewportManagerFeature Vpm { get; init; }
            public required DataPagingFeature Paging { get; init; }
            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Add(Vpm);
                canvas.Add(Paging);
            }
        }

        private sealed class Rig
        {
            public readonly ChartCell Cell = new();
            public readonly DataBlackboard Board = new();
            public readonly ViewportManagerFeature Vpm;
            public readonly DataPagingFeature Paging;
            public readonly ViewportPorts Vp;
            public readonly List<(long Key, int Count)> Calls = new();

            public Rig(OverscrollPolicy overscrollMin = OverscrollPolicy.Hard, TimeSpan? pacing = null)
            {
                Vpm = new ViewportManagerFeature
                {
                    Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
                    OverscrollMin = overscrollMin, OverscrollMax = OverscrollPolicy.Hard,
                };
                Paging = new DataPagingFeature
                {
                    OnRequireBefore = (key, count) => Calls.Add((key, count)),
                    FetchConfig = new DataFetchOptions { Pacing = pacing ?? TimeSpan.Zero },
                };
                var schema = new PagingSchema { Vpm = Vpm, Paging = Paging };
                Cell.Template = schema;
                using (var ctx = Cell.CreateContext()) schema.ComposeAll(Cell, ctx);
                schema.Trigger.Push(Board);
                Vp = ViewportPorts.RequireAttached(Cell);
            }

            public void SetKeys(long first, int count, object epoch, bool olderExhausted = false)
            {
                var keys = new long[count];
                for (int i = 0; i < count; i++) keys[i] = first + i;
                using (Board.WriteScope())
                {
                    Board.Write(Vp.Extent, new DataExtent(epoch, keys, olderExhausted));
                    Board.Write(Vp.LogicalLength, count);
                }
            }

            public void Clear()
            {
                using (Board.WriteScope())
                {
                    Board.Write(Vp.Extent, default);
                    Board.Write(Vp.LogicalLength, 0);
                }
            }

            public void Pan(double min) { using (Board.WriteScope()) Board.Write(Vp.UserRange, new RealRange(min, min + Span)); }
            public DataAvailability Availability { get { using (Board.ReadScope()) return Board.Read(Paging.AvailabilityPort); } }
        }

        [Fact]
        public void Startup_EnoughBeforeAnchor_DoesNotAsk() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(1000, 200, new object());     // 首屏 [150, 199],锚点前 150 根

            Assert.Empty(rig.Calls);
        });

        [Fact]
        public void PanIntoSafeBuffer_AsksBeforeAnchorKey() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(1000, 200, new object());
            rig.Pan(10);

            Assert.Equal((1010L, 30), Assert.Single(rig.Calls));
        });

        [Fact]
        public void LeftOverscroll_AddsBlankBarsToCount() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(OverscrollPolicy.Overscroll);
            rig.SetKeys(1000, 200, new object());
            rig.Pan(-20);

            Assert.Equal((1000L, 50), rig.Calls[^1]);
        });

        [Fact]
        public void ScrollToKey_NotLoaded_AsksForTarget() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(1000, 200, new object());
            rig.Vpm.ScrollToKey(500);

            Assert.Equal(500L, rig.Calls[^1].Key);
        });

        [Fact]
        public void OlderExhausted_LocksLeftWall_AndStopsAsking() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(1000, 200, new object(), olderExhausted: true);
            rig.Pan(0);

            Assert.Empty(rig.Calls);
            Assert.Equal(new DataAvailability(true, false), rig.Availability);
        });

        [Fact]
        public void ContextSwitch_ResetsLeftWall() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(1000, 200, new object(), olderExhausted: true);
            rig.Clear();

            Assert.Equal(new DataAvailability(false, false), rig.Availability);
        });

        [Fact]
        public void SameQuestionWithinPacing_AskedOnce() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(pacing: TimeSpan.FromMinutes(1));
            rig.SetKeys(1000, 200, new object());
            rig.Pan(10);
            rig.Pan(10.4);                            // 锚点那根没变、要的根数没变
            rig.Pan(5);                               // 换了锚点:照问

            Assert.Equal(new[] { (1010L, 30), (1005L, 30) }, rig.Calls);
        });

        [Fact]
        public void UntrackedSource_NeverAsks() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            using (rig.Board.WriteScope()) rig.Board.Write(rig.Vp.LogicalLength, 200);
            rig.Pan(0);

            Assert.Empty(rig.Calls);
            Assert.Equal(new DataAvailability(false, false), rig.Availability);
        });

        private sealed class StandardSchema : ChartReactiveSchema
        {
            public DataPort<ReadOnlyMemory<DateTime>> Time { get; } = new("T_Time");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public required Action<long, int> OnRequireBefore { get; init; }
            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                canvas
                    .Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge, defaultVisibleCount: 50))
                    .Interactions(i => i.EnableStandard(Time, FieldMeta.Literal("T", HevoColors.Gray),
                        new InteractionOptions<DateTime> { OnRequireBefore = OnRequireBefore }));
            }
        }

        [Fact]
        public void EnableStandard_WithOnRequireBefore_MountsPagingAndSharesAvailability() => IncrementalTestKit.RunOnSta(() =>
        {
            Action<long, int> cb = (_, _) => { };
            var schema = new StandardSchema { OnRequireBefore = cb };
            var cell = new ChartCell { Template = schema };
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

            var paging = Assert.Single(schema.ListFeatures().OfType<DataPagingFeature>());
            var interaction = Assert.Single(schema.ListFeatures().OfType<ChartInteractionFeature>());
            Assert.Same(cb, paging.OnRequireBefore);
            Assert.Null(paging.OnRequireDataAsync);
            Assert.Same(paging.AvailabilityPort, interaction.AvailabilityPort);
        });

        [Fact]
        public void BothCallbacks_ThrowOnCompose() => IncrementalTestKit.RunOnSta(() =>
        {
            var schema = new PagingSchema
            {
                Vpm = new ViewportManagerFeature(),
                Paging = new DataPagingFeature
                {
                    OnRequireBefore = (_, _) => { },
                    OnRequireDataAsync = (_, _) => Task.FromResult(true),
                },
            };
            var cell = new ChartCell { Template = schema };
            using var ctx = cell.CreateContext();
            Assert.Throws<InvalidOperationException>(() => schema.ComposeAll(cell, ctx));
        });
    }
}
