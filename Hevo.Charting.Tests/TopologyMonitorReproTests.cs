#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 拓扑流向监控(TopologyInspectorControl + TopologyTracer)的问题复现。
    /// 每条用例对应一个怀疑点:连线归属错位、监控窗关掉后仍在跑、连线方向画反。
    /// </summary>
    public sealed class TopologyMonitorReproTests
    {
        private static string FeatureId(object f) => $"F_{RuntimeHelpers.GetHashCode(f)}";

        /// <summary>Watch 监听 input,回调里读 input;给了 output 就把值 +1 写过去。</summary>
        private sealed class WatchRelay : Feature
        {
            private readonly DataPort<int> _input;
            private readonly DataPort<int>? _output;
            private readonly Func<int, int> _map;
            public int Fired;
            public WatchRelay(DataPort<int> input, DataPort<int>? output = null, Func<int, int>? map = null)
            { _input = input; _output = output; _map = map ?? (v => v + 1); }

            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
                => flow.Watch(new object[] { _input }, b =>
                {
                    Fired++;
                    int v;
                    using (b.ReadScope()) v = b.Read(_input);
                    if (_output != null)
                        using (b.WriteScope()) b.Write(_output, _map(v));
                });

            protected override void OnProject(FeatureContext ctx) { }
        }

        /// <summary>装配期把 child 挂进主管线(走 AddDuringCompose)。</summary>
        private sealed class ParentAddsChild : Feature
        {
            private readonly Feature _child;
            public ParentAddsChild(Feature child) => _child = child;

            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
                => AddChildFeatureDuringCompose(_child, chart, ctx, flow);

            protected override void OnProject(FeatureContext ctx) { }
        }

        private static TopologyTracer TracerOf(SchemaHarness h)
            => TracerRegistry.Get(h.Schema) ?? throw new InvalidOperationException("DEBUG 下 schema 应自带 tracer");

        // ── 1. 装配期子 Feature 的读写被记到父 Feature 名下 ─────────────────────────
        [Fact]
        public void ChildAddedDuringCompose_ReadIsAttributedToChild()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var input = new DataPort<int>("Input");
                // 跟 PlotFeature 的子(LineSeriesFeature)一样走 OnProject 读端口。
                var child = new ProbeFeature("Child") { OnProjectAction = (_, ctx) => ctx.UsePort(input) };
                var parent = new ParentAddsChild(child);
                var h = new SchemaHarness(parent);

                h.Board.WriteLocked(input, 1);
                h.InvalidateEnvironment();
                h.ProjectOnly();

                var tracer = TracerOf(h);
                var readers = tracer.LinkHits.Keys.Where(k => k.Src == input.Id).Select(k => k.Tgt).ToList();
                Assert.True(readers.Contains(FeatureId(child)),
                    $"期望 Input 的读者是 child({FeatureId(child)}),实际:[{string.Join(", ", readers)}](parent={FeatureId(parent)}, child Project 次数={child.ProjectCount}, links=[{string.Join("; ", tracer.LinkHits.Keys)}])");
            });
        }

        // ── 2. Transact 热插拔的 Feature 读写被记成 UnknownOwner ───────────────────
        [Fact]
        public void FeatureAddedByTransact_ReadIsAttributedToFeature()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var input = new DataPort<int>("Input");
                var h = new SchemaHarness();
                var late = new WatchRelay(input);
                h.Schema.Transact(c => c.Add(late));

                h.Board.WriteLocked(input, 1);

                var tracer = TracerOf(h);
                var readers = tracer.LinkHits.Keys.Where(k => k.Src == input.Id).Select(k => k.Tgt).ToList();
                Assert.True(readers.Contains(FeatureId(late)),
                    $"期望 Input 的读者是热插拔 feature({FeatureId(late)}),实际:[{string.Join(", ", readers)}]");
            });
        }

        // ── 3. Viewport.* 端口的归属 ────────────────────────────────────────────────
        private sealed class DummyChartFeature : ChartFeature
        {
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
            protected override void OnProject(FeatureContext ctx) { }
        }

        private sealed class ViewportSchema : ChartReactiveSchema
        {
            private readonly WorkflowTrigger<DataBlackboard> _trigger = new();
            public DummyChartFeature Dummy { get; } = new();
            protected override void DefineDataFlow(ChartCell chart) => _trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Add(Dummy);
                canvas.Environment(env => env.SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge));
            }
        }

        [Fact]
        public void ViewportPorts_OwnerIsViewportPortsFeature()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new ViewportSchema();
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                _ = new TopologyInspectorControl(cell);

                var tracer = TracerRegistry.Get(schema)!;
                var vp = ViewportPorts.RequireAttached(cell);
                var holder = schema.ListFeatures().OfType<ViewportPortsFeature>().Single();
                var order = string.Join(", ", schema.ListFeatures().Select(f => f.GetType().Name));
                Assert.True(tracer.PortOwner.TryGetValue(vp.ActiveRange.Id, out var owner), "ActiveRange 没登记 owner");
                Assert.True(owner == FeatureId(holder),
                    $"ActiveRange owner 应为 ViewportPortsFeature({FeatureId(holder)}),实际 {owner}" +
                    $"(Dummy={FeatureId(schema.Dummy)});features 顺序:{order}");
            });
        }

        // ── 4. 监控窗关闭后 CompositionTarget.Rendering 仍在跑 ─────────────────────
        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static Window ShowOffscreen(UIElement content)
        {
            var win = new Window
            {
                Content = content, Width = 1100, Height = 930,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            };
            win.Show();
            return win;
        }

        private static long LastSampleTicks(TopologyTracer t)
            => (long)typeof(TopologyTracer)
                .GetField("_lastActivitySampleTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(t)!;

        [Fact]
        public void ClosedInspector_StopsSampling()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var h = new SchemaHarness();
                var tracer = TracerOf(h);
                var win = ShowOffscreen(new TopologyInspectorControl(h.Cell));
                Pump(700);
                long whileOpen = LastSampleTicks(tracer);
                Assert.True(whileOpen != 0, "窗口开着时应该在采样(前置条件)");

                win.Close();
                Pump(100);
                long afterClose = LastSampleTicks(tracer);
                Pump(1200);
                long later = LastSampleTicks(tracer);
                Assert.True(later == afterClose,
                    $"监控窗关闭后仍每帧在跑:关闭时 {afterClose},1.2s 后 {later}");
            });
        }

        private static (Dictionary<string, TopoNode> ById, List<TopoLink> Links) LayoutOf(TopologyInspectorControl ctl)
        {
            var nodes = (List<TopoNode>)typeof(TopologyInspectorControl)
                .GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ctl)!;
            var links = (List<TopoLink>)typeof(TopologyInspectorControl)
                .GetField("_links", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ctl)!;
            return (nodes.ToDictionary(n => n.Id), links);
        }

        // ── 7. 折叠视图:有写有读的端口收进 Feature → Feature 的边 ──────────────────
        [Fact]
        public void FoldedView_CollapsesPortIntoFeatureEdge()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var input = new DataPort<int>("Input");
                var output = new DataPort<int>("Output");
                var relay = new WatchRelay(input, output);
                var reader = new ProbeFeature("Reader") { OnProjectAction = (_, ctx) => ctx.UsePort(output) };
                var h = new SchemaHarness(relay, reader);
                h.Board.WriteLocked(input, 1);
                h.InvalidateEnvironment();
                h.ProjectOnly();

                var ctl = new TopologyInspectorControl(h.Cell);
                typeof(TopologyInspectorControl).GetField("_foldPorts", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ctl, true);
                var win = ShowOffscreen(ctl);
                try
                {
                    Pump(400);
                    var (byId, links) = LayoutOf(ctl);
                    Assert.False(byId.ContainsKey(output.Id), "Output 有写有读,折叠后不该单独显示");
                    Assert.False(byId.ContainsKey(input.Id), "Input 有写有读,折叠后不该单独显示");
                    var edge = links.SingleOrDefault(l => l.Src == FeatureId(relay) && l.Tgt == FeatureId(reader));
                    Assert.True(edge != null, "折叠后应有 Relay → Reader 的边:" + string.Join("; ", links.Select(l => l.Src + "→" + l.Tgt)));
                    Assert.Contains(output.Id, edge!.Via);
                    Assert.Contains(links, l => l.Src == "PIPE" && l.Tgt == FeatureId(relay));
                }
                finally { win.Close(); }
            });
        }

        // ── 8. 图层终点:Feature 挂的图层出现在图上,并且是最右边的终点 ───────────────
        [Fact]
        public void LayerAttachedByFeature_ShowsAsTerminalNode()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var input = new DataPort<int>("Input");
                var layer = new ProbeLayer("PriceLine");
                var painter = new ProbeFeature("Painter")
                {
                    OnComposeAction = f => f.Attach(layer),
                    OnProjectAction = (_, ctx) => ctx.UsePort(input),
                };
                var h = new SchemaHarness(painter);
                h.Board.WriteLocked(input, 1);
                h.InvalidateEnvironment();
                h.ProjectOnly();

                var ctl = new TopologyInspectorControl(h.Cell);
                var win = ShowOffscreen(ctl);
                try
                {
                    Pump(400);
                    var (byId, links) = LayoutOf(ctl);
                    string lid = $"L_{RuntimeHelpers.GetHashCode(layer)}";
                    Assert.True(byId.ContainsKey(lid), "图层节点缺失");
                    Assert.Equal("PriceLine", byId[lid].Label);
                    var edge = links.SingleOrDefault(l => l.Src == FeatureId(painter) && l.Tgt == lid);
                    Assert.True(edge != null && edge.ToLayer, "缺少 Painter → 图层 的边");
                    Assert.True(byId[lid].Bounds.Left > byId[FeatureId(painter)].Bounds.Right, "图层应排在 Feature 右边");
                }
                finally { win.Close(); }
            });
        }

        // ── 9. 线宽不闪:流速在两档分界附近抖动时线宽档位保持不变 ──
        [Fact]
        public void LinkWidthStep_HasHysteresis()
        {
            var update = typeof(TopologyInspectorControl).GetMethod("UpdateWidthStep", BindingFlags.NonPublic | BindingFlags.Static)!;
            var link = new TopoLink { CurrentHeat = 0.5 }; // 目标 1.5 档
            update.Invoke(null, new object[] { link });
            int step = link.WidthStep;
            foreach (var h in new[] { 0.45, 0.55, 0.48, 0.58, 0.42, 0.52 }) // 在 1.5 档附近来回
            {
                link.CurrentHeat = h;
                update.Invoke(null, new object[] { link });
                Assert.Equal(step, link.WidthStep);
            }
            link.CurrentHeat = 1.0; // 真的变大了才换档
            update.Invoke(null, new object[] { link });
            Assert.Equal(3, link.WidthStep);
        }

        // ── 10. 没有流量的边粒子不动(静止实线 = 没数据),有流量才前进 ──
        [Fact]
        public void IdleLink_ParticlesDoNotAdvance_BusyLinkDoes()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var h = new SchemaHarness();
                var ctl = new TopologyInspectorControl(h.Cell);
                var merge = typeof(TopologyInspectorControl).GetMethod("MergeDynamicBlood", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var links = (List<TopoLink>)typeof(TopologyInspectorControl)
                    .GetField("_modelLinks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ctl)!;

                int busyHits = 0;
                void Tick() => merge.Invoke(ctl, new object[]
                {
                    new List<TopoNode>(),
                    new List<TopoLink>
                    {
                        new TopoLink { Src = "S", Tgt = "Idle", HitCount = 5 },        // 计数不再变:没流量
                        new TopoLink { Src = "S", Tgt = "Busy", HitCount = busyHits += 10 },
                    },
                });

                var deadline = DateTime.UtcNow.AddMilliseconds(1300); // 跨过两个 0.5s 统计窗口
                while (DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(16); }

                var idle = links.Single(l => l.Tgt == "Idle");
                var busy = links.Single(l => l.Tgt == "Busy");
                Assert.Equal(0, idle.CurrentHeat);
                Assert.Equal(0, idle.ParticleOffset);
                Assert.True(busy.CurrentHeat > 0, "有流量的边应有热度");
                Assert.True(busy.ParticleOffset > 0, "有流量的边粒子应前进");
            });
        }

        // ── 6. 环路:只切一条回边,环里的端口排在写它的 Feature 后面 ──────────────
        [Fact]
        public void Layout_CycleIsCutAtOneBackEdge()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var input = new DataPort<int>("Input");
                var mid = new DataPort<int>("Mid");
                var a = new WatchRelay(input, mid);
                var b = new WatchRelay(mid, input, _ => 0); // 写常量,WriteIfChanged 让环收敛
                var h = new SchemaHarness(a, b);

                h.Board.WriteLocked(input, 1);

                var ctl = new TopologyInspectorControl(h.Cell);
                var win = ShowOffscreen(ctl);
                try
                {
                    Pump(400);
                    var (byId, links) = LayoutOf(ctl);
                    string Name(string id) => id == FeatureId(a) ? "A" : id == FeatureId(b) ? "B"
                        : id == input.Id ? "Input" : id == mid.Id ? "Mid" : id;
                    double X(object key) => byId[key is string s ? s : FeatureId(key)].Bounds.Left;

                    var backward = links
                        .Where(l => byId.ContainsKey(l.Src) && byId.ContainsKey(l.Tgt))
                        .Where(l => byId[l.Src].Bounds.Right > byId[l.Tgt].Bounds.Left)
                        .Select(l => $"{Name(l.Src)} → {Name(l.Tgt)}")
                        .ToList();
                    var order = string.Join(" | ", byId.Values.OrderBy(n => n.Bounds.Left).Select(n => $"{Name(n.Id)}@{n.Bounds.Left:F0}"));
                    Assert.True(backward.SequenceEqual(new[] { "B → Input" }), $"回线应只有 B → Input,实际:[{string.Join(", ", backward)}];布局:{order}");
                    Assert.True(X(input.Id) < X(a) && X(a) < X(mid.Id) && X(mid.Id) < X(b), $"期望 Input < A < Mid < B,实际:{order}");
                }
                finally { win.Close(); }
            });
        }

        // ── 5. 连线方向:数据流 PIPE → Input → Relay → Output → Reader 应从左往右 ──
        [Fact]
        public void Layout_EveryLinkFlowsLeftToRight()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var input = new DataPort<int>("Input");
                var output = new DataPort<int>("Output");
                var relay = new WatchRelay(input, output);
                var reader = new ProbeFeature("Reader") { OnProjectAction = (_, ctx) => ctx.UsePort(output) };
                var h = new SchemaHarness(relay, reader);

                h.Board.WriteLocked(input, 1);
                h.InvalidateEnvironment();
                h.ProjectOnly();

                var ctl = new TopologyInspectorControl(h.Cell);
                var win = ShowOffscreen(ctl);
                try
                {
                    Pump(400);
                    var nodes = (List<TopoNode>)typeof(TopologyInspectorControl)
                        .GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ctl)!;
                    var links = (List<TopoLink>)typeof(TopologyInspectorControl)
                        .GetField("_links", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ctl)!;
                    var byId = nodes.ToDictionary(n => n.Id);

                    string Name(string id) => id == FeatureId(relay) ? "Relay" : id == FeatureId(reader) ? "Reader"
                        : id == input.Id ? "Input" : id == output.Id ? "Output" : id;

                    var backward = links
                        .Where(l => byId.ContainsKey(l.Src) && byId.ContainsKey(l.Tgt))
                        .Where(l => byId[l.Src].Bounds.Right > byId[l.Tgt].Bounds.Left)
                        .Select(l => $"{Name(l.Src)}(x={byId[l.Src].Bounds.Left:F0}) → {Name(l.Tgt)}(x={byId[l.Tgt].Bounds.Left:F0})")
                        .ToList();
                    Assert.True(links.Count >= 4, $"连线不足,实际 {links.Count} 条:{string.Join("; ", links.Select(l => Name(l.Src) + "→" + Name(l.Tgt)))},relay 回调={relay.Fired}, reader Project 次数={reader.ProjectCount}");
                    Assert.True(backward.Count == 0, "反向连线:\n" + string.Join("\n", backward));
                }
                finally { win.Close(); }
            });
        }
    }
}
#endif
