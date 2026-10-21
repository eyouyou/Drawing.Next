using System.IO;
using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Probe = Hevo.Charting.Tests.PointerRouterTests.Probe;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 路由器成为唯一输入入口之后:ChartInteractionFeature 往行为链上装内置交互、ElementPlotFeatureBase 改发布区域。
    /// 现有交互测试原样通过(行为不变)之外,这里补:源码不再碰 WPF 输入、旧配置照常生效、新开关默认关、
    /// 元素悬停 / 双击的默认行为跟原来一致。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class PointerBackgroundTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Hevo.Drawing.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // ================= 源码检查 =================

        [Theory]
        [InlineData("Features/ChartInteractionFeature.cs")]
        [InlineData("Features/ElementPlotFeatureBase.cs")]
        public void Source_NoWpfInput(string file)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "Hevo.Charting.Core", file));
            Assert.DoesNotContain("System.Windows.Input", text);
            Assert.DoesNotContain("CompositionTarget.Rendering +=", text);
            Assert.DoesNotContain("this.OnMouse(", text);
            Assert.DoesNotContain("OnUIEvent<", text);
        }

        /// <summary>WPF 输入类型只在适配器一个文件里。</summary>
        [Fact]
        public void Source_WpfInputOnlyInAdapter()
        {
            var offenders = new[] { "Hevo.Charting", "Hevo.Charting.Core" }
                .Select(p => Path.Combine(RepoRoot(), p, "Interaction"))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => Path.GetFileName(f) != "WpfPointerAdapter.cs" && File.ReadAllText(f).Contains("System.Windows.Input"))
                .ToArray();
            Assert.Empty(offenders);
        }

        /// <summary>
        /// 核心程序集不碰任何 UI 框架:源码里不出现 System.Windows(工程本身不引 WPF,
        /// 这条兜住 BCL 里同名命名空间 System.Windows.Input / Markup 这类能编过的漏网写法)。
        /// </summary>
        [Fact]
        public void Source_CoreHasNoWpf()
        {
            var dir = Path.Combine(RepoRoot(), "Hevo.Charting.Core");
            var offenders = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllLines(f).Any(l => !l.TrimStart().StartsWith("//") && l.Contains("System.Windows")))
                .Select(f => Path.GetRelativePath(dir, f))
                .ToArray();
            Assert.Empty(offenders);
        }

        // ================= 旧配置 / 新开关 =================

        [Fact]
        public void BoxZoom_DefaultShift_ModifierComesFromPointerInput() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                WpfPointerAdapter.ModifiersOverride = () => PointerModifiers.Shift;
                rig.Drag(100, 250);
            }
            finally { WpfPointerAdapter.ModifiersOverride = null; }
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(after.Span < rig.Initial.Span, $"Shift + 拖 = 框选: {rig.Initial} → {after}");
        });

        private static KeyEventArgs Key(ChartCell cell, Key key) =>
            new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cell)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        [Fact]
        public void UpDown_Default_NotHandled_NoZoom() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();   // ChartInteractionMode.All:不含 KeyboardZoom
            var up = Key(rig.Cell, System.Windows.Input.Key.Up);
            rig.Cell.RaiseEvent(up);
            rig.Frame();
            var after = rig.ActiveRange;
            rig.Close();
            Assert.False(up.Handled);
            Assert.Equal(rig.Initial.Span, after.Span, 6);
        });

        [Fact]
        public void UpDown_KeyboardZoomOn_ZoomsInAndOut() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit,
                ChartInteractionMode.All | ChartInteractionMode.KeyboardZoom));
            rig.Move(200, 150);
            var up = Key(rig.Cell, System.Windows.Input.Key.Up);
            rig.Cell.RaiseEvent(up);
            rig.Frame();
            var zoomedIn = rig.ActiveRange;
            rig.Cell.RaiseEvent(Key(rig.Cell, System.Windows.Input.Key.Down));
            rig.Frame();
            var zoomedOut = rig.ActiveRange;
            rig.Close();
            Assert.True(up.Handled);
            Assert.True(zoomedIn.Span < rig.Initial.Span, $"{rig.Initial} → {zoomedIn}");
            Assert.True(zoomedOut.Span > zoomedIn.Span);
        });

        [Fact]
        public void DragThreshold_FromInteractionOptions() => IncrementalTestKit.RunOnSta(() =>
        {
            // 唯一来源:InteractionOptions.DragThreshold
            var rig = new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandard(s.Ports.Time,
                FieldMeta.Literal("T", HevoColors.Gray), new InteractionOptions<DateTime> { DragThreshold = 30, HitPort = s.Hit }));
            var p = rig.Add(new Probe());
            p.Set(rig, new PointerRegion { Shape = PointerShape.HLine(150, 0, 1000), Accepts = PointerAccepts.Click | PointerAccepts.Drag });
            rig.Drag(100, 150, 120, 150, steps: 2);   // 20 DIP < 30:点击
            rig.Close();
            Assert.Contains(p.Log, g => g.Phase == PointerPhase.Click);
            Assert.DoesNotContain(p.Log, g => g.Phase == PointerPhase.Begin);
        });

        // ================= Q9 回调里取消 =================

        [Fact]
        public void CancelGesture_InCallback_CancelsAfterDispatch_OutsideThrows() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var p = rig.Add(new Probe());
            p.Set(rig, new PointerRegion { Shape = PointerShape.HLine(150, 0, 1000) });
            PointerGesture kept = default;
            p.OnGesture = (b, g, later) =>
            {
                if (g.Phase != PointerPhase.Begin) return;
                kept = g;
                g.CancelGesture();
                Assert.True(rig.Router.IsGestureActive);   // 这次分发结束后才生效
            };
            rig.Mouse.Move(rig.Canvas, new Point(100, 150));
            rig.Mouse.Down(rig.Canvas, new Point(100, 150));
            rig.Move(150, 150);
            bool activeAfter = rig.Router.IsGestureActive;
            bool captured = System.Windows.Input.Mouse.Captured != null;
            rig.Move(180, 150);
            rig.Mouse.Up(rig.Canvas, new Point(180, 150));
            rig.Frame();
            var outside = Record.Exception(() => kept.CancelGesture());
            rig.Close();

            var phases = p.Phases.Where(x => x is not (PointerPhase.HoverEnter or PointerPhase.HoverMove or PointerPhase.HoverLeave)).ToArray();
            Assert.Equal(new[] { PointerPhase.Press, PointerPhase.Begin, PointerPhase.Cancel }, phases);
            Assert.Equal(PointerCancelReason.Requested, p.Log.Single(g => g.Phase == PointerPhase.Cancel).Reason);
            Assert.False(activeAfter);
            Assert.False(captured);
            Assert.IsType<InvalidOperationException>(outside);
        });

        // ================= ElementPlot =================

        private static (Rig Rig, ScatterPlotFeature Scatter, DataPort<PointerHitState?> Hit) ScatterRig(bool claims = false)
        {
            var spec = new DataPort<ReadOnlyMemory<ScatterPoint>>("T_Scatter");
            var hit = new DataPort<PointerHitState?>("T_ScatterHit");
            ScatterPlotFeature? scatter = null;
            var rig = new Rig(extra: (i, s) => i.Canvas.Add(scatter = new ScatterPlotFeature
            {
                Spec = spec, HitStatePort = hit, XDomain = new RealRange(0, 100), YDomain = new RealRange(0, 100), DoubleClickClaimsPress = claims,
            }));
            rig.Write(spec, new ReadOnlyMemory<ScatterPoint>(new[] { new ScatterPoint(50, 50, 4, "#FF0000"), new ScatterPoint(80, 50, 4, "#FF0000") }));
            rig.Frame();
            rig.Frame();
            return (rig, scatter!, hit);
        }

        // 散点 (50,50) 在绘图区 (50,10,330,270) 的中心
        private const double Cx = 50 + 165, Cy = 10 + 135, Cx2 = 50 + 330 * 0.8;

        [Fact]
        public void ElementPlot_Hover_RaisesEvent_WritesHit_LeaveWritesOutOfBounds() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, scatter, hit) = ScatterRig();
            var hovered = new List<int>();
            scatter.ElementHovered += (_, e) => hovered.Add(e.Index);
            rig.Move(Cx + 3, Cy);
            var onFirst = rig.Read(hit);
            rig.Move(Cx + 4, Cy);
            rig.Move(Cx2, Cy);                    // 直接移到另一个元素:中间不插"已离开"
            var onSecond = rig.Read(hit);
            rig.Move(Cx, Cy + 80);
            var away = rig.Read(hit);
            rig.Close();
            Assert.Equal(new[] { 0, 0, 1 }, hovered);
            Assert.Equal(0, onFirst!.Value.LocalIndex);
            Assert.False(onFirst.Value.IsOutOfBounds);
            Assert.Equal((float)Cx, onFirst.Value.MousePos.X, 0);
            Assert.Equal(1, onSecond!.Value.LocalIndex);
            Assert.True(away!.Value.IsOutOfBounds);
        });

        /// <summary>原来元素悬停是普通 MouseMove 订阅,平移中指针扫过元素也报;保持。</summary>
        [Fact]
        public void ElementPlot_HoverStillReported_WhileChartPans() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, scatter, _) = ScatterRig();
            var hovered = new List<int>();
            scatter.ElementHovered += (_, e) => hovered.Add(e.Index);
            rig.Drag(Cx - 90, Cy, Cx, Cy, steps: 6);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(after.Min < rig.Initial.Min, "这是一次平移");
            Assert.Contains(0, hovered);
        });

        [Fact]
        public void ElementPlot_DoubleClick_Default_BothElementAndViewportReset() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, scatter, _) = ScatterRig();
            rig.Drag(100, 220, y: 60);
            var panned = rig.ActiveRange;
            var clicked = new List<int>();
            scatter.ElementDoubleClicked += (_, e) => clicked.Add(e.Index);
            rig.Click(Cx, Cy);
            rig.Click(Cx, Cy);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal(new[] { 0 }, clicked);
            Assert.NotEqual(rig.Initial.Min, panned.Min);
            Assert.Equal(rig.Initial.Min, after.Min, 6);   // 默认跟原来一样:双击复原也触发
        });

        [Fact]
        public void ElementPlot_DoubleClick_ClaimsPressOption_NoViewportReset() => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, scatter, _) = ScatterRig(claims: true);
            rig.Drag(100, 220, y: 60);
            var panned = rig.ActiveRange;
            var clicked = new List<int>();
            scatter.ElementDoubleClicked += (_, e) => clicked.Add(e.Index);
            rig.Click(Cx, Cy);
            rig.Click(Cx, Cy);
            var after = rig.ActiveRange;
            rig.Close();
            Assert.Equal(new[] { 0 }, clicked);
            Assert.Equal(panned.Min, after.Min, 6);
        });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ElementPlot_PressDragOnElement_Pans(bool claims) => IncrementalTestKit.RunOnSta(() =>
        {
            var (rig, _, _) = ScatterRig(claims);
            rig.Drag(Cx, Cy, Cx + 100, Cy);   // Q12:第一次按下不认领
            var after = rig.ActiveRange;
            rig.Close();
            Assert.True(after.Min < rig.Initial.Min);
        });
    }
}
