using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 往 WPF 输入管线里注入原始鼠标报告(跟 HwndMouseInputProvider 收到 WM_MOUSEMOVE / WM_LBUTTONDOWN 后做的一样)。
    /// 为什么不用 RaiseEvent:交互测试要的是真实的 DirectlyOver / 鼠标捕获 / Preview→冒泡提升,
    /// 以及 MouseEventArgs.GetPosition 读 MouseDevice 记下的位置,RaiseEvent 都模拟不了。
    /// RawMouseInputReport 等是 PresentationCore 的 internal 类型,只在测试里反射用。
    /// </summary>
    internal sealed class RawMouse
    {
        private static readonly Assembly Core = typeof(InputManager).Assembly;
        private static readonly Type ReportType = Core.GetType("System.Windows.Input.RawMouseInputReport", true)!;
        private static readonly Type ActionsType = Core.GetType("System.Windows.Input.RawMouseActions", true)!;
        private static readonly Type InputModeType = Core.GetType("System.Windows.Input.InputMode", true)!;
        private static readonly Type ReportBaseType = Core.GetType("System.Windows.Input.InputReport", true)!;
        private static readonly RoutedEvent PreviewInputReportEvent = (RoutedEvent)typeof(InputManager)
            .GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
        private static readonly ConstructorInfo ReportCtor = ReportType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 8);
        private static readonly Type ArgsType = Core.GetType("System.Windows.Input.InputReportEventArgs", true)!;
        private static readonly ConstructorInfo ArgsCtor = ArgsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 2 && c.GetParameters()[1].ParameterType == ReportBaseType);

        private readonly PresentationSource _source;
        private readonly UIElement _root;
        private int _timestamp = Environment.TickCount;

        public RawMouse(Window window)
        {
            _source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("窗口还没 Show");
            _root = window;
            // 隔离真鼠标:真光标在屏幕外窗口之外,SetCapture / ReleaseCapture 时系统会按真光标位置补发 WM_MOUSEMOVE,
            // 跟注入的报告交错,造成假的 MouseLeave / 移动。窗口只吃注入的鼠标移动与按键。
            // WM_CAPTURECHANGED 不拦:WPF 靠它得知捕获已释放
            ((HwndSource)_source).AddHook(SwallowRealMouse);
        }

        public void Move(UIElement relativeTo, Point p) => Send(relativeTo, p, 0, "AbsoluteMove");
        public void Down(UIElement relativeTo, Point p) => Send(relativeTo, p, 0, "AbsoluteMove", "Button1Press");
        public void Up(UIElement relativeTo, Point p) => Send(relativeTo, p, 0, "AbsoluteMove", "Button1Release");
        public void RightDown(UIElement relativeTo, Point p) => Send(relativeTo, p, 0, "AbsoluteMove", "Button2Press");
        public void RightUp(UIElement relativeTo, Point p) => Send(relativeTo, p, 0, "AbsoluteMove", "Button2Release");
        public void Wheel(UIElement relativeTo, Point p, int delta) => Send(relativeTo, p, delta, "AbsoluteMove", "VerticalWheelRotate");

        private static IntPtr SwallowRealMouse(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEMOVE = 0x0200, WM_MOUSELAST = 0x020E, WM_MOUSELEAVE = 0x02A3, WM_SETCURSOR = 0x0020;
            if ((msg >= WM_MOUSEMOVE && msg <= WM_MOUSELAST) || msg == WM_MOUSELEAVE || msg == WM_SETCURSOR)
                handled = true;
            return IntPtr.Zero;
        }

        private void Send(UIElement relativeTo, Point p, int wheel, params string[] actions)
        {
            // DIP(相对元素)→ 窗口客户区设备像素,跟 WM_MOUSEMOVE 的 lParam 同一口径
            var inRoot = relativeTo.TranslatePoint(p, _root);
            var device = _source.CompositionTarget!.TransformToDevice.Transform(inRoot);

            // 释放捕获后真窗口收到 WM_CAPTURECHANGED,鼠标设备按真光标位置被停用;停用后不带 Activate 的报告会被丢掉
            bool needActivate = Mouse.PrimaryDevice.ActiveSource != _source;
            var names = needActivate ? actions.Append("Activate").ToArray() : actions;
            long flags = 0;
            foreach (var n in names) flags |= Convert.ToInt64(Enum.Parse(ActionsType, n));
            object actionValue = Enum.ToObject(ActionsType, flags);
            object mode = Enum.Parse(InputModeType, "Foreground");

            _timestamp += 50;
            var report = ReportCtor.Invoke(new object[]
            {
                mode, _timestamp, _source, actionValue,
                (int)Math.Round(device.X), (int)Math.Round(device.Y), wheel, IntPtr.Zero,
            });
            var args = (InputEventArgs)ArgsCtor.Invoke(new object?[] { Mouse.PrimaryDevice, report });
            args.RoutedEvent = PreviewInputReportEvent;
            InputManager.Current.ProcessInput(args);
        }
    }

    /// <summary>真实鼠标输入注入的测试独占运行(xUnit 在并行批次之后单独跑这个集合):并行时实测约三成概率丢按下。</summary>
    [CollectionDefinition(nameof(RealMouseInputCollection), DisableParallelization = true)]
    public sealed class RealMouseInputCollection
    {
    }
}
