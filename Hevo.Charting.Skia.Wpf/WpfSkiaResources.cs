using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Hevo.Charting.Skia
{
    /// <summary>WPF 宿主的 Skia 资源解析:资源笔刷 / 字体查 Application 资源字典(主题),图片取 BitmapSource 像素。</summary>
    public sealed class WpfSkiaResources : ISkiaHostResources
    {
        public static readonly WpfSkiaResources Instance = new();

        public string ResolveText(IHevoString text) => HevoStrings.Resolve(text, WpfChartHost.Services);

        public bool TryResolveBrush(IHevoBrush brush, out SkiaBrushInfo info)
        {
            switch (WpfRenderRegistry.CreateBrush(brush))
            {
                case SolidColorBrush sc:
                    info = new SkiaBrushInfo(C(sc.Color).WithAlpha((byte)(sc.Color.A * Math.Clamp(sc.Opacity, 0, 1))));
                    return true;
                case LinearGradientBrush lg when lg.GradientStops.Count > 0:
                {
                    var stops = lg.GradientStops.OrderBy(s => s.Offset).ToArray();
                    double op = Math.Clamp(lg.Opacity, 0, 1);
                    info = new SkiaBrushInfo(
                        stops.Select(s => C(s.Color).WithAlpha((byte)(s.Color.A * op))).ToArray(),
                        stops.Select(s => (float)Math.Clamp(s.Offset, 0, 1)).ToArray(),
                        P(lg.StartPoint), P(lg.EndPoint),
                        relative: lg.MappingMode == BrushMappingMode.RelativeToBoundingBox);
                    return true;
                }
                case GradientBrush gb when gb.GradientStops.Count > 0:
                {
                    // 径向等其它渐变:取首个色标当纯色(同位图后端)
                    var c = gb.GradientStops[0].Color;
                    info = new SkiaBrushInfo(C(c).WithAlpha((byte)(c.A * Math.Clamp(gb.Opacity, 0, 1))));
                    return true;
                }
                default:
                    info = default;
                    return false;
            }
        }

        public string? ResolveFontFamily(string resourceKey)
        {
            var res = Application.Current?.TryFindResource(resourceKey);
            if (res is FontFamily ff) return ff.Source;
            return res as string;
        }

        public SKImage? CreateImage(object image)
        {
            if (image is not BitmapSource bmp) return null;
            var conv = bmp.Format == PixelFormats.Pbgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            conv.CopyPixels(pixels, stride, 0);
            return SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, stride);
        }

        private static SKColor C(Color c) => new(c.R, c.G, c.B, c.A);
        private static SKPoint P(Point p) => new((float)p.X, (float)p.Y);
    }
}
