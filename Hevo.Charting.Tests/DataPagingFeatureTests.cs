using System.Diagnostics;
using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 分页加载:视口贴近数据边缘时要数据,单飞,撞墙锁方向,异常熔断,取完再判一次。
    /// 全程不走鼠标:直接写 LogicalLength / UserRange,视口管家和分页 Feature 都是同步 Watch,
    /// 写完即生效;回调返回测试手里的 TaskCompletionSource,完成时机由测试控制。
    /// SynchronizationContext 置空 + Pacing=0,让 await 之后的收尾在 SetResult 里同步跑完,结果确定。
    /// </summary>
    public sealed class DataPagingFeatureTests
    {
        private const int Span = 49;          // DefaultVisibleCount 50 → 跨度 49
        private const int Count = 147;        // ceil(49 × 3)

        private sealed record Call(int Anchor, int Count, TaskCompletionSource<bool> Tcs);

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
            public readonly DataPagingFeature Paging;
            public readonly ViewportPorts Vp;
            private readonly List<Call> _calls = new();
            private readonly Func<int, int, Task<bool>>? _responder;

            /// <param name="responder">给了就由它直接给结果(可同步完成);不给则每笔挂一个 TCS 由测试手动完成。</param>
            public Rig(DataFetchOptions? fetch = null, Func<int, int, Task<bool>>? responder = null)
            {
                _responder = responder;
                SynchronizationContext.SetSynchronizationContext(null);
                Paging = new DataPagingFeature
                {
                    OnRequireDataAsync = Handle,
                    FetchConfig = fetch ?? new DataFetchOptions { Pacing = TimeSpan.Zero },
                };
                var schema = new PagingSchema
                {
                    Vpm = new ViewportManagerFeature
                    {
                        Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
                        OverscrollMin = OverscrollPolicy.Hard, OverscrollMax = OverscrollPolicy.Hard,
                    },
                    Paging = Paging,
                };
                Cell.Template = schema;
                using (var ctx = Cell.CreateContext()) schema.ComposeAll(Cell, ctx);
                schema.Trigger.Push(Board);
                Vp = ViewportPorts.RequireAttached(Cell);
            }

            private Task<bool> Handle(int anchor, int count)
            {
                var tcs = new TaskCompletionSource<bool>();
                lock (_calls) _calls.Add(new Call(anchor, count, tcs));
                return _responder?.Invoke(anchor, count) ?? tcs.Task;
            }

            public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToArray(); } }
            public Call Last => Calls[^1];

            public void SetLength(int n) { using (Board.WriteScope()) Board.Write(Vp.LogicalLength, n); }
            public void Pan(double min) { using (Board.WriteScope()) Board.Write(Vp.UserRange, new RealRange(min, min + Span)); }
            public RealRange Active { get { using (Board.ReadScope()) return Board.Read(Vp.ActiveRange); } }
            public DataAvailability Availability { get { using (Board.ReadScope()) return Board.Read(Paging.AvailabilityPort); } }

            /// <summary>200 根贴右开屏,开屏那笔向右请求答"没有更多",右墙锁上,之后的平移只测左侧。</summary>
            public static Rig StartedAt200(DataFetchOptions? fetch = null)
            {
                var rig = new Rig(fetch);
                rig.SetLength(200);
                Assert.Single(rig.Calls);
                rig.Calls[0].Tcs.SetResult(false);
                Assert.Equal(new DataAvailability(false, true), rig.Availability);
                return rig;
            }
        }

        private static bool WaitUntil(Func<bool> cond, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                Thread.Sleep(10);
            }
            return cond();
        }

        // ---------------- 触发判定与回调参数 ----------------

        [Fact]
        public void DefaultOptions_MatchDocumentedValues()
        {
            var o = new DataFetchOptions();
            Assert.Equal(30, o.SafeBuffer);
            Assert.Equal(3.0, o.PrefetchMultiplier);
            Assert.Equal(TimeSpan.FromMilliseconds(200), o.Pacing);
            Assert.Equal(TimeSpan.FromSeconds(3), o.NetworkFaultThrottle);
        }

        [Fact]
        public void Startup_DockedRight_RequestsFutureWithLastIndexAnchor() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(200);
            Assert.Equal(new RealRange(150, 199), rig.Active);
            var call = Assert.Single(rig.Calls);
            Assert.Equal(199, call.Anchor);
            Assert.Equal(Count, call.Count);
            Assert.Equal(new DataAvailability(false, false), rig.Availability);
        });

        [Fact]
        public void PanIntoLeftBuffer_RequestsHistoryWithAnchorZero() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            Assert.Equal(2, rig.Calls.Count);
            Assert.Equal(0, rig.Last.Anchor);
            Assert.Equal(Count, rig.Last.Count);
        });

        [Fact]
        public void PrefetchMultiplier_ScalesRequestedCount() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.Zero, PrefetchMultiplier = 1.5 });
            Assert.Equal(74, rig.Calls[0].Count); // ceil(49 × 1.5)
            rig.Pan(10);
            Assert.Equal(74, rig.Last.Count);
        });

        [Fact]
        public void SafeBuffer_BoundaryIsInclusive() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(31);
            Assert.Single(rig.Calls);
            rig.Pan(30);
            Assert.Equal(2, rig.Calls.Count);
            Assert.Equal(0, rig.Last.Anchor);
        });

        [Fact]
        public void CustomSafeBuffer_MovesTriggerPoint() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.Zero, SafeBuffer = 5 });
            rig.Pan(6);
            Assert.Single(rig.Calls);
            rig.Pan(5);
            Assert.Equal(2, rig.Calls.Count);
        });

        [Fact]
        public void BothEdgesInBuffer_LeftCheckedFirst() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(40); // 视口 [0,39] 同时贴两边
            var call = Assert.Single(rig.Calls);
            Assert.Equal(0, call.Anchor);
        });

        [Fact]
        public void WhileFetching_FurtherTriggersAreIgnored() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.Pan(10);
            rig.Pan(0);
            rig.Pan(100);
            rig.Pan(5);
            Assert.Equal(2, rig.Calls.Count);
        });

        // ---------------- 取回数据后 ----------------

        [Fact]
        public void HistoryArrives_ViewportStaysOnSameBars_NoChainOutsideBuffer() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.SetLength(200 + Count);              // 左侧插入 147 根,视口跟着右移同样根数
            Assert.Equal(new RealRange(20 + Count, 69 + Count), rig.Active);
            rig.Last.Tcs.SetResult(true);
            Assert.Equal(2, rig.Calls.Count);         // 已出缓冲区,不再续拉
            Assert.Equal(new DataAvailability(false, false), rig.Availability); // 数据变长,右墙解锁
        });

        [Fact]
        public void HistoryArrives_StillInBuffer_ChainsAnotherRequest() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(0);
            rig.SetLength(210);
            Assert.Equal(new RealRange(10, 59), rig.Active);
            rig.Last.Tcs.SetResult(true);
            Assert.Equal(3, rig.Calls.Count);
            Assert.Equal(0, rig.Last.Anchor);
        });

        [Fact]
        public void Pacing_DelaysTheRecheckAfterSuccess() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.FromMilliseconds(150) });
            // 开屏那笔的 Pacing 先等完,否则还在飞
            Assert.True(WaitUntil(() => { rig.Pan(100); rig.Pan(0); return rig.Calls.Count == 2; }));
            rig.SetLength(210);
            var sw = Stopwatch.StartNew();
            rig.Last.Tcs.SetResult(true);
            Assert.Equal(2, rig.Calls.Count);
            Assert.True(WaitUntil(() => rig.Calls.Count == 3));
            Assert.True(sw.ElapsedMilliseconds >= 100, $"续拉过早:{sw.ElapsedMilliseconds}ms");
        });

        // ---------------- 撞墙 ----------------

        [Fact]
        public void NoMoreHistory_LocksLeftWall() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.Last.Tcs.SetResult(false);
            Assert.Equal(new DataAvailability(true, true), rig.Availability);
            rig.Pan(0);
            rig.Pan(100);
            rig.Pan(5);
            Assert.Equal(2, rig.Calls.Count);
        });

        [Fact]
        public void HasMoreButLengthUnchanged_TreatedAsWall() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.Last.Tcs.SetResult(true);             // 业务说还有,长度却没变
            Assert.True(rig.Availability.LeftExhausted);
            rig.Pan(0);
            Assert.Equal(2, rig.Calls.Count);
        });

        [Fact]
        public void LeftWall_SurvivesGrowth_RightWallUnlocks() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.Last.Tcs.SetResult(false);
            rig.SetLength(210);                       // 心跳长大:视口在历史容差内,随之右移到 [30,79]
            Assert.Equal(new DataAvailability(true, false), rig.Availability);
            Assert.Equal(2, rig.Calls.Count);         // Min=30 仍在缓冲内,但左墙不解
        });

        [Fact]
        public void Shrink_ResetsBothWalls() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.Last.Tcs.SetResult(false);
            rig.SetLength(60);                        // 视口被钳到 [10,59]
            Assert.Equal(new DataAvailability(false, false), rig.Availability);
            Assert.Equal(3, rig.Calls.Count);
            Assert.Equal(0, rig.Last.Anchor);
        });

        [Fact]
        public void RightWall_LockedUntilDataGrows() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(140);                             // Max=189 仍在右缓冲内
            Assert.Single(rig.Calls);
            rig.Pan(150);
            rig.SetLength(201);                       // 贴右视口跟着长大
            Assert.Equal(new RealRange(151, 200), rig.Active);
            Assert.Equal(new DataAvailability(false, false), rig.Availability);
            Assert.Equal(2, rig.Calls.Count);
            Assert.Equal(200, rig.Last.Anchor);
        });

        /// <summary>切股票时长度先归 0 再恢复:新数据不比旧的短,也要解开两侧墙(以前看不到归 0,新股票沿用旧墙锁)。</summary>
        [Fact]
        public void ContextReset_ThroughZero_ResetsWalls() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200();
            rig.Pan(20);
            rig.Last.Tcs.SetResult(false);
            Assert.Equal(new DataAvailability(true, true), rig.Availability);

            rig.SetLength(0);                         // 切股票:清空
            rig.SetLength(200);                       // 新股票同样 200 根,贴右开屏
            Assert.Equal(new DataAvailability(false, false), rig.Availability);
            Assert.Equal(3, rig.Calls.Count);
            Assert.Equal(199, rig.Last.Anchor);
        });

        // ---------------- 异常与超时 ----------------

        [Fact]
        public void Exception_ThrottlesWithoutLockingWall() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.Zero, NetworkFaultThrottle = TimeSpan.FromSeconds(10) });
            rig.Pan(20);
            rig.Last.Tcs.SetException(new System.IO.IOException("network down"));
            rig.Pan(0);
            rig.Pan(100);
            rig.Pan(10);
            Assert.Equal(2, rig.Calls.Count);         // 冷却期内仍算在飞,不重试
            Assert.False(rig.Availability.LeftExhausted);
        });

        [Fact]
        public void Exception_RetriesAfterThrottleElapses() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.Zero, NetworkFaultThrottle = TimeSpan.FromMilliseconds(150) });
            rig.Pan(20);
            var sw = Stopwatch.StartNew();
            rig.Last.Tcs.SetException(new InvalidOperationException("boom"));
            Assert.Equal(2, rig.Calls.Count);
            Assert.True(WaitUntil(() => rig.Calls.Count == 3), "冷却后没有自动重试");
            Assert.True(sw.ElapsedMilliseconds >= 100, $"重试过早:{sw.ElapsedMilliseconds}ms");
            Assert.Equal(0, rig.Last.Anchor);
        });

        [Fact]
        public void Timeout_SkipsFaultThrottle_RetriesRightAway() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.Zero, NetworkFaultThrottle = TimeSpan.FromSeconds(10) });
            rig.Pan(20);
            rig.Last.Tcs.SetCanceled();               // 超时以 OperationCanceledException 建模
            Assert.Equal(3, rig.Calls.Count);
            Assert.Equal(0, rig.Last.Anchor);
            Assert.False(rig.Availability.LeftExhausted);
        });

        /// <summary>超时只免额外的熔断冷却,常规 Pacing 照旧(以前连 Pacing 一起免了)。</summary>
        [Fact]
        public void Timeout_StillHonoursPacing() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = Rig.StartedAt200(new DataFetchOptions { Pacing = TimeSpan.FromMilliseconds(300), NetworkFaultThrottle = TimeSpan.FromSeconds(10) });
            Assert.True(WaitUntil(() => { rig.Pan(100); rig.Pan(20); return rig.Calls.Count == 2; }));
            rig.Last.Tcs.SetCanceled();
            Assert.Equal(2, rig.Calls.Count);
            Assert.True(WaitUntil(() => rig.Calls.Count == 3), "Pacing 过后没有重试");
        });

        /// <summary>
        /// 回调同步返回已取消的任务、Pacing 为 0:以前在 finally 的重判里同步再发、再失败,一路递归到栈溢出。
        /// 现在这一笔失败后不立即重试,下一次视口变化再试。
        /// </summary>
        [Fact]
        public void SynchronousTimeout_ZeroPacing_DoesNotRecurse() => IncrementalTestKit.RunOnSta(() =>
        {
            bool cancel = false;
            var rig = new Rig(new DataFetchOptions { Pacing = TimeSpan.Zero, NetworkFaultThrottle = TimeSpan.FromSeconds(10) },
                (_, _) => cancel ? Task.FromCanceled<bool>(new CancellationToken(true)) : Task.FromResult(false));
            rig.SetLength(200);
            Assert.Single(rig.Calls);

            cancel = true;
            rig.Pan(20);
            Assert.Equal(2, rig.Calls.Count);
            rig.Pan(10);
            Assert.Equal(3, rig.Calls.Count);
            Assert.False(rig.Availability.LeftExhausted);
        });

        // ---------------- 装配 ----------------

        private sealed class PointerSchema : ChartReactiveSchema
        {
            public DataPort<PointerHitState?> Hit { get; } = new("T_Hit");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public Func<int, int, Task<bool>>? OnRequire { get; init; }
            public Action<long, int>? OnRequireBefore { get; init; }
            public DataFetchOptions? Fetch { get; init; }
            public IZoomStrategy? Zoom { get; init; }
            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                canvas
                    .Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge, defaultVisibleCount: 50))
                    .Interactions(i => i.EnableStandardPointer(Hit, ChartInteractionMode.All,
                        onRequireDataAsync: OnRequire, fetchConfig: Fetch, zoomStrategy: Zoom, onRequireBefore: OnRequireBefore));
            }
        }

        [Fact]
        public void EnableStandardPointer_WithCallback_SharesAvailabilityPort() => IncrementalTestKit.RunOnSta(() =>
        {
            var fetch = new DataFetchOptions { SafeBuffer = 7 };
            Func<int, int, Task<bool>> cb = (_, _) => Task.FromResult(false);
            var schema = new PointerSchema { OnRequire = cb, Fetch = fetch };
            var cell = new ChartCell { Template = schema };
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

            var paging = Assert.Single(schema.ListFeatures().OfType<DataPagingFeature>());
            var interaction = Assert.Single(schema.ListFeatures().OfType<ChartInteractionFeature>());
            Assert.Same(cb, paging.OnRequireDataAsync);
            Assert.Same(fetch, paging.FetchConfig);
            Assert.NotNull(interaction.AvailabilityPort);
            Assert.Same(paging.AvailabilityPort, interaction.AvailabilityPort);
        });

        [Fact]
        public void EnableStandardPointer_WithRequireBefore_SharesAvailabilityPort() => IncrementalTestKit.RunOnSta(() =>
        {
            Action<long, int> cb = (_, _) => { };
            var schema = new PointerSchema { OnRequireBefore = cb };
            var cell = new ChartCell { Template = schema };
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

            var paging = Assert.Single(schema.ListFeatures().OfType<DataPagingFeature>());
            Assert.Same(cb, paging.OnRequireBefore);
            Assert.Null(paging.OnRequireDataAsync);
            Assert.Same(paging.AvailabilityPort, Assert.Single(schema.ListFeatures().OfType<ChartInteractionFeature>()).AvailabilityPort);
        });

        [Fact]
        public void EnableStandardPointer_WithoutCallback_NoPaging() => IncrementalTestKit.RunOnSta(() =>
        {
            var schema = new PointerSchema();
            var cell = new ChartCell { Template = schema };
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

            Assert.Empty(schema.ListFeatures().OfType<DataPagingFeature>());
            Assert.Null(Assert.Single(schema.ListFeatures().OfType<ChartInteractionFeature>()).AvailabilityPort);
        });

        // ---------------- 历史感知缩放 ----------------

        private static ZoomContext Ctx(bool leftExhausted, double rawSpan = 98) => new(
            BaseRange: new RealRange(100, 149), RawTargetSpan: rawSpan, MouseRelativeX: 0.5, HitState: null,
            LogicalLength: 300, Limits: new SpanLimits(4, 448), DomainScale: null,
            LeftDataExhausted: leftExhausted, RightDataExhausted: false);

        [Fact]
        public void HistoryAware_WhilePaging_AnchorsRightEdge()
        {
            var r = new HistoryAwareZoomStrategy().Calculate(Ctx(false));
            Assert.Equal(new RealRange(51, 149), r);
        }

        [Fact]
        public void HistoryAware_LeftExhausted_AnchorsLeftEdgeAndClamps()
        {
            var s = new HistoryAwareZoomStrategy();
            Assert.Equal(new RealRange(100, 198), s.Calculate(Ctx(true)));
            Assert.Equal(new RealRange(100, 548), s.Calculate(Ctx(true, rawSpan: 10_000))); // 夹到 MaxSpan
        }

        [Fact]
        public void HistoryAware_DuringPaging_IsPluggable()
        {
            var s = new HistoryAwareZoomStrategy { DuringPaging = MouseAnchorZoomStrategy.Instance };
            Assert.Equal(new RealRange(75.5, 173.5), s.Calculate(Ctx(false)));
        }

        [Fact]
        public void HistoryAware_EndToEnd_SwitchesAnchorAfterLeftWall() => IncrementalTestKit.RunOnSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var calls = new List<TaskCompletionSource<bool>>();
            var schema = new PointerSchema
            {
                OnRequire = (_, _) => { var t = new TaskCompletionSource<bool>(); calls.Add(t); return t.Task; },
                Fetch = new DataFetchOptions { Pacing = TimeSpan.Zero },
                Zoom = new HistoryAwareZoomStrategy(),
            };
            var cell = new ChartCell { Template = schema, Width = 400, Height = 300 };
            var window = new Window
            {
                Content = cell, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = 0,
            };
            window.Show();
            window.UpdateLayout();
            try
            {
                var vp = ViewportPorts.RequireAttached(cell);
                var interaction = schema.ListFeatures().OfType<ChartInteractionFeature>().Single();
                var board = new DataBlackboard();
                schema.Trigger.Push(board);
                schema.InvalidateEnvironment();
                using (board.WriteScope()) board.Write(vp.LogicalLength, 300);
                cell.RunFrameNow(PlotMode.Sync);
                cell.RunFrameNow(PlotMode.Sync);
                Assert.Single(calls);
                calls[0].SetResult(false);            // 右墙

                var state = cell.PointerRegions.Behaviors.StateOf<ChartInteractionState>(interaction)!;
                RealRange Active() { using (board.ReadScope()) return board.Read(vp.ActiveRange); }
                void Pan(double min) { using (board.WriteScope()) board.Write(vp.UserRange, new RealRange(min, min + Span)); }

                Pan(100);
                state.ApplyZoom(board, 2.0, 0);
                Assert.Equal(new RealRange(51, 149), Active());

                Pan(10);                              // 进左缓冲,拉历史,答"没有更多"
                Assert.Equal(2, calls.Count);
                calls[1].SetResult(false);
                Assert.True(board.ReadLocked(interaction.AvailabilityPort!).LeftExhausted);

                Pan(100);
                state.ApplyZoom(board, 2.0, 0);
                Assert.Equal(new RealRange(100, 198), Active());
            }
            finally { window.Close(); }
        });
    }

    internal static class DataPagingTestBoardExtensions
    {
        public static T ReadLocked<T>(this DataBlackboard board, DataPort<T> port)
        {
            using (board.ReadScope()) return board.Read(port);
        }
    }
}
