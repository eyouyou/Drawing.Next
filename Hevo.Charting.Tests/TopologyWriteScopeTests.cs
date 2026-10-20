#if DEBUG
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.Interaction;
using Hevo.Charting.Features;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 拓扑监控的写入归属:黑板写入发生时追踪作用域(当前调用方)必须是真正的写入方。
    /// 每条对应一个曾被记错的入口:管线处理、跨图镜像、环境纪元、惯性平移、蓝图编辑器交互。
    /// </summary>
    public sealed class TopologyWriteScopeTests
    {
        private static string FeatureId(object f) => $"F_{RuntimeHelpers.GetHashCode(f)}";

        private static string Writers(TopologyTracer t, string portId)
            => string.Join(", ", t.LinkHits.Keys.Where(k => k.Tgt == portId).Select(k => k.Src));

        private sealed class WriteIngestor : IDataIngestor<int>
        {
            private readonly DataPort<int> _port;
            public WriteIngestor(DataPort<int> port) => _port = port;
            public void Process(DataSnapshot<int> snapshot, DataBlackboard board) => board.Write(_port, snapshot.Count);
        }

        // ── 1. 管线处理:不管谁触发推送,摄入器的写入都算数据源 ─────────────────
        [Fact]
        public void PipeProcess_TriggeredInsideFeatureScope_IsAttributedToPipe()
        {
            var port = new DataPort<int>("Rows");
            using var pipe = new UniversalDataPipe<int>();
            pipe.AddIngestor(new WriteIngestor(port));
            var tracer = new TopologyTracer();
            TracerRegistry.Attach(pipe.Board, tracer);

            // 模拟 DataPagingFeature 在自己的 Watch 作用域里让数据源推送
            var paging = new object();
            using (TopologyTracer.EnterScope(paging))
                pipe.Process(new DataSnapshot<int>(new[] { 1, 2, 3 }, 3, default));

            Assert.True(tracer.LinkHits.ContainsKey((TopologyTracer.PIPE_ID, port.Id)),
                $"Rows 的写入方应为数据源,实际:[{Writers(tracer, port.Id)}](触发方={FeatureId(paging)})");
        }

        // ── 2. 跨图镜像:副图收到的写入记成"跨图镜像",不冒充主图的 Feature ──────────
        [Fact]
        public void MirrorWrite_IsAttributedToMirror_NotToForeignFeature()
        {
            var context = new LinkedChartContext();
            var master = new DataBlackboard();
            var pane = new DataBlackboard();
            var masterTracer = new TopologyTracer();
            var paneTracer = new TopologyTracer();
            TracerRegistry.Attach(master, masterTracer);
            TracerRegistry.Attach(pane, paneTracer);
            context.AttachBoard(master);
            context.AttachBoard(pane);

            var port = context.SharedViewport.UserRange;
            var interaction = new object(); // 主图的 ChartInteraction
            using (TopologyTracer.EnterScope(interaction))
            using (master.WriteScope())
                master.Write(port, new RealRange(10, 20));

            Assert.True(paneTracer.LinkHits.ContainsKey((TopologyTracer.MIRROR_ID, port.Id)),
                $"副图 UserRange 的写入方应为跨图镜像,实际:[{Writers(paneTracer, port.Id)}](主图写入方={FeatureId(interaction)})");
            Assert.False(masterTracer.LinkHits.ContainsKey((port.Id, FeatureId(interaction))),
                "镜像读取主图端口被记成了主图写入方在读");
            Assert.True(masterTracer.LinkHits.ContainsKey((port.Id, TopologyTracer.MIRROR_ID)), "主图应显示端口被镜像出去");
            Assert.True(masterTracer.NodeMetadata.ContainsKey(TopologyTracer.MIRROR_ID), "只读不写的镜像节点没登记名字");
            Assert.False(paneTracer.LinkHits.ContainsKey((port.Id, TopologyTracer.MIRROR_ID)),
                "副图收到同步后又被记成镜像读了一遍(回声),会形成假环路");
        }

        // ── 3. 环境纪元:尺寸 / 可见性 / Transact 触发的写入记成"环境" ──────────────
        [Fact]
        public void EnvironmentEpochWrite_IsAttributedToEnvironment()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var h = new SchemaHarness();
                var tracer = TracerRegistry.Get(h.Schema)!;
                var epoch = h.Schema.EnvironmentEpochPort;

                h.InvalidateEnvironment();
                var someFeature = new object(); // Transact 期间调用方可能是任意 Feature
                using (TopologyTracer.EnterScope(someFeature))
                    h.InvalidateEnvironment();

                var writers = tracer.LinkHits.Keys.Where(k => k.Tgt == epoch.Id).Select(k => k.Src).Distinct().ToList();
                Assert.True(writers.SequenceEqual(new[] { TopologyTracer.ENV_ID }),
                    $"环境纪元的写入方应只有环境,实际:[{string.Join(", ", writers)}]");
            });
        }

        // ── 4. 惯性平移:渲染帧回调里的写入记到 ChartInteraction ────────────────
        [Fact]
        public void InertiaTick_WriteIsAttributedToOwner()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var clock = new ManualClock();
                var runner = new InertiaPanRunner();
                for (int i = 0; i < 3; i++) { runner.RecordSample(40.0); Thread.Sleep(12); }

                var port = new DataPort<int>("Pan");
                var board = new DataBlackboard();
                var tracer = new TopologyTracer();
                TracerRegistry.Attach(board, tracer);
                int n = 0;
                Func<double, double> apply = dx => { using (board.WriteScope()) board.Write(port, ++n); return dx; };
                Func<RealRange> read = () => new RealRange(n, n + 10); // 每帧都在动,不触发撞墙自停

                var owner = new object(); // 发起惯性的 ChartInteraction
                runner.Start(new InertiaPanOptions(), clock, apply, read, traceOwner: owner);
                Assert.True(runner.IsRunning, "惯性没启动(前置条件)");

                // 之后每帧由宿主帧时钟回调,不在任何 Feature 作用域里
                Thread.Sleep(20);
                clock.Tick();
                runner.Stop();

                Assert.True(tracer.LinkHits.ContainsKey((FeatureId(owner), port.Id)),
                    $"惯性帧的写入方应为 ChartInteraction({FeatureId(owner)}),实际:[{Writers(tracer, port.Id)}]");
            });
        }
        // ── 5. 蓝图编辑器:鼠标操作改画布状态,写入方是 GraphInteraction ────────────
        [Fact]
        public void GraphEditorInteraction_StateWriteIsAttributedToInteraction()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new Hevo.Charting.LowCode.Designer.GraphViewer.GraphEditorSchema();
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                var tracer = TracerRegistry.Get(schema)!;

                var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 120)
                {
                    RoutedEvent = System.Windows.UIElement.MouseWheelEvent,
                };
                typeof(Hevo.Charting.LowCode.Designer.GraphViewer.GraphInteractionFeature)
                    .GetMethod("OnMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(schema.Interaction, new object[] { cell, wheel });

                var port = schema.StatePort;
                Assert.True(tracer.LinkHits.ContainsKey((FeatureId(schema.Interaction), port.Id)),
                    $"画布状态的写入方应为 GraphInteraction({FeatureId(schema.Interaction)}),实际:[{Writers(tracer, port.Id)}]");
            });
        }
        // ── 6. 触发捕获:从第一次写入开始按顺序录,窗口到点自动停 ──────────────────
        [Fact]
        public void Capture_RecordsOneWaveInOrder_FromFirstWrite()
        {
            var a = new DataPort<int>("A");
            var b = new DataPort<int>("B");
            var board = new DataBlackboard();
            var tracer = new TopologyTracer();
            TracerRegistry.Attach(board, tracer);
            var feature = new object();

            // 捕获前的读写不算
            using (TopologyTracer.EnterScope(feature))
            using (board.ReadScope()) board.Read(a);

            tracer.BeginCapture(windowMs: 80);
            Assert.True(tracer.IsCaptureWaiting);
            using (TopologyTracer.EnterScope(feature))
            using (board.ReadScope()) board.Read(a); // 还没等到写入,不录
            using (board.WriteScope()) board.Write(a, 1); // 数据源写 A:一波开始
            using (TopologyTracer.EnterScope(feature))
            {
                using (board.ReadScope()) board.Read(a);         // Feature 读 A
                using (board.WriteScope()) board.Write(b, 2); // Feature 写 B
            }
            Assert.False(tracer.TryTakeCapture(out _), "窗口还没结束,不应交出结果");

            Thread.Sleep(120);
            Assert.True(tracer.TryTakeCapture(out var steps));
            var fid = FeatureId(feature);
            Assert.Equal(new[]
            {
                (TopologyTracer.PIPE_ID, a.Id, true),
                (a.Id, fid, false),
                (fid, b.Id, true),
            }, steps.Select(x => (x.Src, x.Tgt, x.IsWrite)).ToArray());
            Assert.True(steps.Zip(steps.Skip(1), (x, y) => x.Ticks <= y.Ticks).All(ok => ok), "步骤应按时间顺序");
            Assert.False(tracer.IsCaptureArmed, "录完一波应自动停");
        }

        /// <summary>只提供帧回调的宿主;其余能力惯性用不到。</summary>
        private sealed class ManualClock : IPointerHost
        {
            private Action? _callback;
            public void Tick() => _callback?.Invoke();
            public void AddFrameCallback(Action callback) => _callback = callback;
            public void RemoveFrameCallback(Action callback) { if (_callback == callback) _callback = null; }

            public bool HasPendingFrame => false;
            public void RunPendingFrame() { }
            public void Focus() { }
            public void Capture() { }
            public void ReleaseCapture() { }
            public void HandOverCapture() { }
            public bool IsMouseCapturedByOther => false;
            public void SetCursor(PointerCursor cursor) { }
            public HevoRect PlotArea => HevoRect.Empty;
            public DataBlackboard? Board => null;
            public double DragThreshold => 4;
            public void CaptureForBackground() { }
            public void ReleaseBackgroundCapture() { }
            public int BackgroundCaptureCount => 0;
        }
    }
}
#endif
