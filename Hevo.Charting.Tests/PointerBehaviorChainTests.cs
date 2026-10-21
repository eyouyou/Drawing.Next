using System.IO;
using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 行为链:ChartInteractionFeature 只按 SupportedModes 装行为,输入由路由器统一分发(捕获者 → 区域 → 行为链 → 旁听)。
    /// 这里补拆成行为之后才有的约束:任意键停惯性、挂起吞输入、多个 ChartInteractionFeature、卸载时收拾干净、
    /// 进出信号走端口、旧的 OnMouse 订阅不参与路由。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PointerBehaviorChainTests
    {
        private static KeyEventArgs PreviewKey(ChartCell cell, Key key) =>
            new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cell)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        private static InertiaPanOptions WideWindow => new InertiaPanOptions { VelocityWindowMs = 2000 };

        // ================= 键盘 =================

        [Fact]
        public void AnyKey_StopsInertia_InKeyboardMode() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();   // All:含 Keyboard
            rig.FlingRight();
            bool runningAfterFling = rig.InertiaRunning;
            var a = PreviewKey(rig.Cell, Key.A);
            rig.Cell.RaiseEvent(a);
            bool runningAfterKey = rig.InertiaRunning;
            rig.Close();
            Assert.True(runningAfterFling);
            Assert.False(runningAfterKey);
            Assert.False(a.Handled);   // 字母键只停惯性,不吞
        });

        [Fact]
        public void AnyKey_KeyboardOff_InertiaContinues() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit,
                ChartInteractionMode.All & ~ChartInteractionMode.Keyboard, inertiaPanConfig: WideWindow));
            rig.FlingRight();
            bool runningAfterFling = rig.InertiaRunning;
            rig.Cell.RaiseEvent(PreviewKey(rig.Cell, Key.A));
            bool runningAfterKey = rig.InertiaRunning;
            rig.Close();
            Assert.True(runningAfterFling);
            Assert.True(runningAfterKey);
        });

        // ================= 挂起 =================

        [Fact]
        public void Suspended_SwallowsInput_ResumeRestores() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Schema.Suspend();
            rig.Drag(100, 250);
            var whileSuspended = rig.ActiveRange;
            rig.Schema.Resume();
            rig.Frame();
            rig.Drag(100, 250);
            var afterResume = rig.ActiveRange;
            rig.Close();
            Assert.Equal(rig.Initial.Min, whileSuspended.Min, 6);
            Assert.NotEqual(rig.Initial.Min, afterResume.Min);
        });

        // ================= 多个 / 零个 ChartInteractionFeature =================

        [Fact]
        public void TwoInteractionFeatures_BothTrackHover_FirstOneConsumesPan() => IncrementalTestKit.RunOnSta(() =>
        {
            var hit2 = new DataPort<PointerHitState?>("T_Hit2");
            var rig = new Rig();
            rig.Add(new ChartInteractionFeature { PointerHitPort = hit2, SupportedModes = ChartInteractionMode.Hover | ChartInteractionMode.Pan });
            Assert.Equal(2, rig.Cell.PointerRegions.Behaviors.Count);

            rig.Move(200, 150);
            var h1 = rig.Hit;
            var h2 = rig.Read(hit2);

            // 平移只发生一次:第一个拦下了按下,第二个收不到
            var single = new Rig();
            single.Drag(100, 200);
            var singleShift = single.ActiveRange.Min - single.Initial.Min;
            single.Close();
            rig.Drag(100, 200);
            var shift = rig.ActiveRange.Min - rig.Initial.Min;
            rig.Close();

            Assert.NotNull(h1);
            Assert.NotNull(h2);
            Assert.NotEqual(0, singleShift);
            Assert.Equal(singleShift, shift, 6);
        });

        [Fact]
        public void NoInteractionFeature_NoPan_NoHit() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Schema.Transact(c => c.Remove<ChartInteractionFeature>());
            rig.Frame();
            Assert.Equal(0, rig.Cell.PointerRegions.Behaviors.Count);
            rig.Write(rig.Schema.Hit, null);
            rig.Drag(100, 250);
            rig.Move(200, 150);
            var range = rig.ActiveRange;
            var hit = rig.Hit;
            rig.Close();
            Assert.Equal(rig.Initial.Min, range.Min, 6);
            Assert.Null(hit);
        });

        // ================= 卸载 =================

        [Fact]
        public void TransactRemove_MidPan_ReleasesCapture_StopsPanning() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Frame();
            for (int x = 125; x <= 175; x += 25) { rig.Mouse.Move(rig.Canvas, new Point(x, 150)); rig.Frame(); }
            bool capturedWhilePanning = Mouse.Captured == rig.Cell;
            var panned = rig.ActiveRange;

            rig.Schema.Transact(c => c.Remove<ChartInteractionFeature>());
            rig.Frame();
            bool capturedAfterRemove = Mouse.Captured == rig.Cell;
            var before = rig.ActiveRange;
            rig.Mouse.Move(rig.Canvas, new Point(250, 150));
            rig.Frame();
            var after = rig.ActiveRange;
            rig.Mouse.Up(rig.Canvas, new Point(250, 150));
            rig.Close();

            Assert.True(panned.Min != rig.Initial.Min, $"拔之前确实在平移: {rig.Initial} → {panned} → {before}");
            Assert.True(capturedWhilePanning);
            Assert.False(capturedAfterRemove);
            Assert.Equal(before.Min, after.Min, 6);
        });

        [Fact]
        public void TransactReAdd_PansAgain() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var cif = rig.Interaction;
            rig.Schema.Transact(c => c.Remove(cif));
            rig.Schema.Transact(c => c.Add(cif));
            rig.Frame();
            rig.Drag(100, 250);
            var range = rig.ActiveRange;
            rig.Close();
            Assert.NotEqual(rig.Initial.Min, range.Min);
        });

        // ================= 旁听:进出 =================

        [Fact]
        public void IsPointerOver_WrittenByRouter() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Move(200, 150);
            bool over = rig.Read(rig.Router.Ports.IsPointerOver);
            rig.Close();
            Assert.True(over);
        });

        // ================= 旧写法 =================

        /// <summary>
        /// 直接订 WPF 冒泡事件的旧写法不参与路由:它排在路由器之后,标了 Handled 也挡不住内置平移。
        /// 想抢在平移之前,发布指针区域(OnMouse 已标 Obsolete)。
        /// </summary>
        [Fact]
        public void LegacyOnMouseHandled_DoesNotBlockPan() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Add(new LegacyHandledProbe());
            rig.Drag(100, 250);
            var range = rig.ActiveRange;
            rig.Close();
            Assert.NotEqual(rig.Initial.Min, range.Min);
        });

        private sealed class LegacyHandledProbe : ChartFeature
        {
            public override FeaturePhase Phase => FeaturePhase.PreLayout;

            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
            {
                // 模拟业务自己挂的 WPF 冒泡处理器
                var cell = (ChartCell)chart;
                MouseButtonEventHandler handler = (_, e) => e.Handled = true;
                cell.AddHandler(UIElement.MouseDownEvent, handler);
                RegisterDisposable(new DisposableAction(() => cell.RemoveHandler(UIElement.MouseDownEvent, handler)));
            }

            protected override void OnProject(FeatureContext ctx) { }
        }

        // ================= 源码 =================

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Drawing.Next.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// <summary>框架里只有适配器订平台输入:Features / Interaction 下别处不出现 WPF 输入类型和订阅。</summary>
        [Fact]
        public void Source_OnlyAdapterSubscribesPlatformInput()
        {
            var files = new[] { "Hevo.Charting", "Hevo.Charting.Core" }
                .Select(p => Path.Combine(RepoRoot(), p))
                .SelectMany(root => new[] { Path.Combine(root, "Features"), Path.Combine(root, "Interaction") })
                .Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories));
            var offenders = files
                .Where(f => Path.GetFileName(f) != "WpfPointerAdapter.cs")
                .Select(f => (File: f, Text: File.ReadAllText(f)))
                .Where(x => x.Text.Contains("this.OnMouse(") || x.Text.Contains("this.OnKey(") || x.Text.Contains("AddHandler(")
                            || x.Text.Contains("System.Windows.Input"))
                .Select(x => Path.GetFileName(x.File))
                .ToArray();
            Assert.Empty(offenders);
        }
    }
}
