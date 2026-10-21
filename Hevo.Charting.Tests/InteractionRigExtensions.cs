using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 交互行为测试共用的小工具:键盘 / 鼠标离开的注入、按 K 线下标算屏幕 X、取框选状态端口。
    /// 都挂在 <see cref="PointerRouterTests.Rig"/> 上,不改它本身。
    /// </summary>
    internal static class InteractionRigExtensions
    {
        /// <summary>
        /// 拦掉 WPF 在 CaptureMouse 时按真光标位置补发的那次移动:窗口在屏幕外,补发点离窗口上万像素,
        /// 平移会被它拖到墙上再拖回来,视口落点就跟真光标在哪有关。只认窗口附近的移动。
        /// </summary>
        public static Rig Isolated(this Rig rig)
        {
            var window = Window.GetWindow(rig.Cell)!;
            window.PreviewMouseMove += (_, e) =>
            {
                var p = e.GetPosition(window);
                if (p.X < -100 || p.Y < -100 || p.X > window.ActualWidth + 100 || p.Y > window.ActualHeight + 100) e.Handled = true;
            };
            return rig;
        }

        public static ChartInteractionState State(this Rig rig)
            => rig.Cell.PointerRegions.Behaviors.StateOf<ChartInteractionState>(rig.Interaction)!;

        public static HevoRect Plot(this Rig rig) => rig.Cell.GetPlotAreaForTest();

        /// <summary>第 index 根 K 线中心的屏幕 X(按当前视口和 CandleMode 比例尺)。</summary>
        public static double BarX(this Rig rig, int index, ScaleStrategyTrait? scale = null)
            => CoordinateExtensions.ProjectDomainToScreen(rig.Plot(), rig.ActiveRange, scale ?? ScaleStrategyTrait.CandleMode, index);

        /// <summary>按键:跟真实键盘一样先 Preview,没人处理再冒泡。返回是否被处理。</summary>
        public static bool Key(this Rig rig, Key key)
        {
            var src = PresentationSource.FromVisual(rig.Cell)!;
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            rig.Cell.RaiseEvent(e);
            bool handled = e.Handled;
            if (!handled)
            {
                var bubble = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                rig.Cell.RaiseEvent(bubble);
                handled = bubble.Handled;
            }
            rig.Frame();
            return handled;
        }

        /// <summary>直接发 MouseLeave:注入的鼠标在屏幕外窗口上很难可靠地"移出"宿主。</summary>
        public static void Leave(this Rig rig)
        {
            rig.Cell.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        }

        /// <summary>框选状态端口是 Feature 私有字段,测试里反射取出来读。</summary>
        public static BoxZoomTrait? BoxTrait(this Rig rig)
        {
            var f = typeof(ChartInteractionFeature).GetField("_boxStatePort", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var port = (DataPort<BoxZoomTrait>)f.GetValue(rig.Interaction)!;
            return rig.Read(port);
        }

        public static void AssertRange(double min, double max, RealRange actual)
        {
            Assert.True(Math.Abs(actual.Min - min) < 1e-6 && Math.Abs(actual.Max - max) < 1e-6, $"期望 [{min}, {max}],实际 {actual}");
        }
    }
}
