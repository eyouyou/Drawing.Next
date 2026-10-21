using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting
{
    /// <summary>核心的平台无关类型 ↔ WPF 类型互转(只在 WPF 宿主 / 渲染器 / 业务 WPF 控件里用)。</summary>
    public static class WpfColorExtensions
    {
        public static Color ToWpf(this HevoColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
        public static HevoColor ToHevo(this Color c) => new HevoColor(c.A, c.R, c.G, c.B);

        public static Point ToWpf(this HevoPoint p) => new Point(p.X, p.Y);
        public static HevoPoint ToHevo(this Point p) => new HevoPoint((float)p.X, (float)p.Y);

        public static Rect ToWpf(this HevoRect r) => new Rect(r.X, r.Y, r.Width, r.Height);
        public static HevoRect ToHevo(this Rect r) => new HevoRect((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }
}

namespace Hevo.Charting.Core
{
    using System.Windows;

    public static class WpfWidgetBufferExtensions
    {
        /// <summary>WPF 写法:按 WPF <see cref="Rect"/> 摆控件(换算成核心的 <see cref="HevoRect"/>)。</summary>
        public static void UpdateLayout(this WidgetBuffer buffer, Rect bounds, object vm) => buffer.UpdateLayout(bounds.ToHevo(), vm);
    }
}
