using System.Windows;
using System.Windows.Input;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// graph editor 的交互层(WPF)。逻辑全在核心 <see cref="GraphInteractionFeatureBase"/>,
    /// 这里只把 ChartCell 上的鼠标 / 键盘 / 触屏 Manipulation 事件翻译过去。
    /// </summary>
    public sealed class GraphInteractionFeature : GraphInteractionFeatureBase
    {
        /// <summary>WPF MouseEventArgs 转画布像素坐标。</summary>
        private static HevoPoint Pt(MouseEventArgs e, IInputElement el)
        {
            var p = e.GetPosition(el);
            return new HevoPoint((float)p.X, (float)p.Y);
        }

        private ChartCell WpfChart => (ChartCell)Chart;

        protected override void OnCompose(IChartCell cell, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 图编辑器的 WPF 宿主:直接挂 WPF 路由事件
            var chart = (ChartCell)cell;
            chart.MouseLeftButtonDown  += OnMouseDown;
            chart.MouseMove            += OnMouseMove;
            chart.MouseLeftButtonUp    += OnMouseUp;
            chart.MouseRightButtonDown += OnMouseRightDown;
            chart.MouseRightButtonUp   += OnMouseRightUp;
            chart.MouseWheel           += OnMouseWheel;
            chart.MouseLeave           += OnMouseLeave;
            chart.LostMouseCapture     += OnLostCapture;
            chart.Focusable             = true;
            chart.KeyDown              += OnKeyDown;
            // 触屏 Manipulation:Mode 配置必须早于 Delta,所以用 Starting。Translate(平移)+ Scale(捏合)
            // 同时启用,跟鼠标 right-drag pan + wheel zoom 行为对齐。
            chart.ManipulationStarting += OnManipulationStarting;
            chart.ManipulationDelta    += OnManipulationDelta;
            chart.SizeChanged          += OnSizeChanged;
        }

        protected override void OnDecompose()
        {
            var chart = WpfChart;
            if (chart == null) return;
            chart.MouseLeftButtonDown  -= OnMouseDown;
            chart.MouseMove            -= OnMouseMove;
            chart.MouseLeftButtonUp    -= OnMouseUp;
            chart.MouseRightButtonDown -= OnMouseRightDown;
            chart.MouseRightButtonUp   -= OnMouseRightUp;
            chart.MouseWheel           -= OnMouseWheel;
            chart.MouseLeave           -= OnMouseLeave;
            chart.LostMouseCapture     -= OnLostCapture;
            chart.KeyDown              -= OnKeyDown;
            chart.ManipulationStarting -= OnManipulationStarting;
            chart.ManipulationDelta    -= OnManipulationDelta;
            chart.SizeChanged          -= OnSizeChanged;
        }

        protected override void CaptureInput() => WpfChart.CaptureMouse();
        protected override void ReleaseInput() => WpfChart.ReleaseMouseCapture();
        protected override void FocusHost() => WpfChart.Focus();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => HandleSizeChanged(e.NewSize.Width, e.NewSize.Height);

        // 这些是 chart 上直接挂的 WPF 事件,不在任何 Feature 作用域里;Handle* 内部自己进追踪作用域
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool ctrlOrShift = (Keyboard.Modifiers & ModifierKeys.Control) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (HandleLeftDown(Pt(e, WpfChart), e.ClickCount, ctrlOrShift)) e.Handled = true;
        }

        private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
        {
            if (HandleRightDown(Pt(e, WpfChart))) e.Handled = true;
        }

        private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
        {
            if (HandleRightUp()) e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e) => HandleMove(Pt(e, WpfChart));

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (HandleLeftUp(Pt(e, WpfChart))) e.Handled = true;
        }

        private void OnMouseLeave(object sender, MouseEventArgs e) => HandleLeave();

        private void OnLostCapture(object sender, MouseEventArgs e) => HandleLostCapture();

        private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
        {
            // Container 必须显式给到 ChartCell —— 默认 manipulation origin 跑到 visual root 偏移失真。
            e.ManipulationContainer = WpfChart;
            e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        }

        private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
        {
            var d = e.DeltaManipulation;
            var o = e.ManipulationOrigin;
            if (HandleManipulationDelta(d.Translation.X, d.Translation.Y, d.Scale.X, new HevoPoint((float)o.X, (float)o.Y))) e.Handled = true;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (HandleWheel(Pt(e, WpfChart), e.Delta, (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key switch
            {
                Key.Z => GraphKey.Z,
                Key.Y => GraphKey.Y,
                Key.Home => GraphKey.Home,
                Key.Delete => GraphKey.Delete,
                _ => GraphKey.Other,
            };
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (HandleKey(key, ctrl, shift)) e.Handled = true;
        }
    }
}
