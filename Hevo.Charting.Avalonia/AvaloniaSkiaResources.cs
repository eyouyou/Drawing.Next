using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hevo.Charting.Skia;
using SkiaSharp;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// Avalonia 宿主的 Skia 资源解析:资源笔刷 / 字体查 Application 资源(跟随当前主题),图片支持 Avalonia <see cref="Bitmap"/>。
    /// </summary>
    public sealed class AvaloniaSkiaResources : ISkiaHostResources
    {
        public static readonly AvaloniaSkiaResources Instance = new();

        public bool TryResolveBrush(IHevoBrush brush, out SkiaBrushInfo info)
        {
            switch (AvaloniaBrushes.Create(brush))
            {
                case ISolidColorBrush sc:
                    info = new SkiaBrushInfo(WithOpacity(sc.Color, sc.Opacity));
                    return true;
                case ILinearGradientBrush lg when lg.GradientStops.Count > 0:
                {
                    var stops = lg.GradientStops.OrderBy(s => s.Offset).ToArray();
                    // RelativePoint 的单位决定起止点按包围盒比例(Relative)还是按绝对坐标
                    bool relative = lg.StartPoint.Unit == RelativeUnit.Relative;
                    info = new SkiaBrushInfo(
                        stops.Select(s => WithOpacity(s.Color, lg.Opacity)).ToArray(),
                        stops.Select(s => (float)Math.Clamp(s.Offset, 0, 1)).ToArray(),
                        new SKPoint((float)lg.StartPoint.Point.X, (float)lg.StartPoint.Point.Y),
                        new SKPoint((float)lg.EndPoint.Point.X, (float)lg.EndPoint.Point.Y),
                        relative);
                    return true;
                }
                case IGradientBrush gb when gb.GradientStops.Count > 0:
                    // 径向等其它渐变:取首个色标当纯色(同 WPF / 位图后端)
                    info = new SkiaBrushInfo(WithOpacity(gb.GradientStops[0].Color, gb.Opacity));
                    return true;
                default:
                    info = default;
                    return false;
            }
        }

        public string ResolveText(IHevoString text) => HevoStrings.Resolve(text, AvaloniaHostRegistration.Services);

        public string? ResolveFontFamily(string resourceKey)
        {
            var res = AvaloniaBrushes.FindResource(resourceKey);
            if (res is FontFamily ff) return ff.Name;
            return res as string;
        }

        public SKImage? CreateImage(object image)
        {
            if (image is not Bitmap bmp) return null;
            // 走一次编码:Avalonia 位图的像素格式因平台而异,PNG 是两边都认的格式;渲染器按对象缓存,只付一次
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            using var data = SKData.Create(ms);
            return SKImage.FromEncodedData(data);
        }

        private static SKColor WithOpacity(Color c, double opacity)
            => new(c.R, c.G, c.B, (byte)(c.A * Math.Clamp(opacity, 0, 1)));
    }

    /// <summary>核心笔刷描述 → Avalonia 笔刷(控件类图层用,如悬浮窗)。</summary>
    public static class AvaloniaBrushes
    {
        public static Color ToAvalonia(this HevoColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        /// <summary>解析不了(资源找不到)返回 null。</summary>
        public static IBrush? Create(IHevoBrush? brush) => brush switch
        {
            HevoSolidBrush s => new SolidColorBrush(s.Color.ToAvalonia()),
            HevoLinearGradientBrush g => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(g.StartPoint.X, g.StartPoint.Y, RelativeUnit.Relative),
                EndPoint = new RelativePoint(g.EndPoint.X, g.EndPoint.Y, RelativeUnit.Relative),
                GradientStops = { new GradientStop(g.StartColor.ToAvalonia(), 0), new GradientStop(g.EndColor.ToAvalonia(), 1) },
            },
            HevoResourceBrush r => FindResource(r.ResourceKey) switch
            {
                IBrush b => b,
                Color c => new SolidColorBrush(c),
                _ => null,
            },
            _ => null,
        };

        /// <summary>
        /// 把笔刷设到控件属性上。资源笔刷做成动态资源绑定(换主题跟着变,同 WPF 的 SetResourceReference),
        /// 其余直接设值;后一次设置替换前一次(本地值 / 绑定同一优先级)。
        /// </summary>
        public static void Bind(Control target, AvaloniaProperty<IBrush?> property, IHevoBrush brush)
        {
            if (brush is HevoResourceBrush r)
                target.Bind(property, target.GetResourceObservable(r.ResourceKey, v => v is Color c ? new SolidColorBrush(c) : v as IBrush));
            else
                target.SetValue(property, Create(brush));
        }

        /// <summary>多态文本设到 TextBlock 上:资源文本做动态资源绑定(找不到显示键名),其余解析成字符串。</summary>
        public static void BindText(TextBlock target, IHevoString text)
        {
            if (text is HevoResourceString r)
                target.Bind(TextBlock.TextProperty, target.GetResourceObservable(r.ResourceKey, v => v as string ?? r.ResourceKey));
            else
                target.Text = HevoStrings.Resolve(text, AvaloniaHostRegistration.Services);
        }

        /// <summary>按当前主题查 Application 资源;没有 Application 或找不到返回 null。</summary>
        public static object? FindResource(string key)
        {
            var app = Application.Current;
            if (app == null) return null;
            return app.TryFindResource(key, app.ActualThemeVariant, out var value) ? value : null;
        }
    }
}
