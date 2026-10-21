using Hevo.Charting;
using SkiaSharp;

namespace Hevo.Charting.Skia
{
    /// <summary>
    /// <see cref="SkiaChartRenderer"/> 里要查宿主的三样东西:主题资源笔刷、资源字体、宿主自己的图片类型。
    /// 每个宿主(WPF / Avalonia)各实现一份;纯色 / 线性渐变 / 具名字体渲染器自己处理,不经过这里。
    /// </summary>
    public interface ISkiaHostResources
    {
        /// <summary>把渲染器不认识的笔刷(典型:<see cref="HevoResourceBrush"/>)解析成颜色;解析不了返回 false(该图元不画)。</summary>
        bool TryResolveBrush(IHevoBrush brush, out SkiaBrushInfo info);

        /// <summary>资源字体键 → 字体族名;null = 用默认字体。</summary>
        string? ResolveFontFamily(string resourceKey);

        /// <summary>DrawImage 指令里的宿主图片对象 → SKImage(渲染器按对象缓存);不认识返回 null。</summary>
        SKImage? CreateImage(object image);

        /// <summary>文本指令的多态文本 → 显示文本(资源文本按本宿主的资源字典查)。</summary>
        string ResolveText(IHevoString text) => HevoStrings.Resolve(text);
    }

    /// <summary>
    /// 宿主解析出的笔刷:纯色(<see cref="IsGradient"/> = false,取 Colors[0])或线性渐变。
    /// 颜色已乘过笔刷不透明度。Relative = 起止点按图元包围盒的 0..1 比例。
    /// </summary>
    public readonly struct SkiaBrushInfo
    {
        public SkiaBrushInfo(SKColor color)
        {
            Colors = new[] { color };
            Positions = null;
            Start = End = default;
            Relative = true;
            IsGradient = false;
        }

        public SkiaBrushInfo(SKColor[] colors, float[]? positions, SKPoint start, SKPoint end, bool relative)
        {
            Colors = colors;
            Positions = positions;
            Start = start;
            End = end;
            Relative = relative;
            IsGradient = true;
        }

        public SKColor[] Colors { get; }
        public float[]? Positions { get; }
        public SKPoint Start { get; }
        public SKPoint End { get; }
        public bool Relative { get; }
        public bool IsGradient { get; }
    }
}
