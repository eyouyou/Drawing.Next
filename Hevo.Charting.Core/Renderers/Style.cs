using System.Runtime.CompilerServices;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 极致轻量的 float 结构体，替代 WPF 的 Point
    /// </summary>
    /// <param name="X"></param>
    /// <param name="Y"></param>
    public readonly record struct HevoPoint(float X, float Y);

    /// <summary>
    /// 💥 极致轻量的跨平台矩形 (基于 float)。
    /// 自带所有常用的空间数学算法，彻底干掉 System.Windows.Rect。
    /// </summary>
    public readonly record struct HevoRect(float X, float Y, float Width, float Height)
    {
        public static readonly HevoRect Empty = new HevoRect(0, 0, 0, 0);

        public float Left => X;
        public float Top => Y;
        public float Right => X + Width;
        public float Bottom => Y + Height;

        public HevoPoint TopLeft => new HevoPoint(Left, Top);
        public HevoPoint TopRight => new HevoPoint(Right, Top);
        public HevoPoint BottomLeft => new HevoPoint(Left, Bottom);
        public HevoPoint BottomRight => new HevoPoint(Right, Bottom);

        public HevoPoint Center => new HevoPoint(X + Width / 2f, Y + Height / 2f);
        public bool IsEmpty => Width <= 0 || Height <= 0;

        // ==========================================
        // 💥 核心空间数学算法
        // ==========================================

        /// <summary> 判断点是否在矩形内 </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(HevoPoint point)
            => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

        /// <summary> 判断另一个矩形是否完全被包含 </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(HevoRect rect)
            => rect.Left >= Left && rect.Right <= Right && rect.Top >= Top && rect.Bottom <= Bottom;

        /// <summary> 矩形偏移 (平移) </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HevoRect Offset(float dx, float dy)
            => new HevoRect(X + dx, Y + dy, Width, Height);

        /// <summary> 矩形向外/向内膨胀 (如计算边框的外扩) </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HevoRect Inflate(float dw, float dh)
            => new HevoRect(X - dw, Y - dh, Width + dw * 2f, Height + dh * 2f);

        /// <summary>
        /// 💥 求两个矩形的重叠交集 (极度高频使用的算法)
        /// 如果没有交集，返回 HevoRect.Empty
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HevoRect Intersect(HevoRect rect)
        {
            float newLeft = Math.Max(Left, rect.Left);
            float newTop = Math.Max(Top, rect.Top);
            float newRight = Math.Min(Right, rect.Right);
            float newBottom = Math.Min(Bottom, rect.Bottom);

            if (newRight >= newLeft && newBottom >= newTop)
                return new HevoRect(newLeft, newTop, newRight - newLeft, newBottom - newTop);

            return Empty;
        }
    }

    public enum TextAlignX : byte { Left, Center, Right }
    public enum TextAlignY : byte { Top, Center, Bottom }
    // ==========================================
    // 💥 跨平台文本抽象契约
    // ==========================================
    public interface IHevoString { }

    /// <summary>
    /// 静态字面量文本
    /// </summary>
    public record HevoLiteralString(string Text) : IHevoString;

    /// <summary>
    /// 动态多语言资源文本 (后期绑定)
    /// </summary>
    public record HevoResourceString(string ResourceKey) : IHevoString;

    /// <summary>
    /// 笔刷基类接口
    /// </summary>
    public interface IHevoBrush { }

    /// <summary>
    /// 纯色笔刷
    /// </summary>
    /// <param name="Color"></param>
    public record HevoSolidBrush(HevoColor Color) : IHevoBrush;

    /// <summary>
    /// 线性渐变笔刷
    /// </summary>
    /// <param name="StartColor"></param>
    /// <param name="EndColor"></param>
    /// <param name="StartPoint"></param>
    /// <param name="EndPoint"></param>
    public record HevoLinearGradientBrush(
        HevoColor StartColor,
        HevoColor EndColor,
        HevoPoint StartPoint,
        HevoPoint EndPoint) : IHevoBrush;

    /// <summary>
    /// 💥 主题资源笔刷 (后期绑定 / Late Binding)
    /// 它的本质是一个令牌(Token)，具体的渲染后端负责将其翻译为真正的动态画刷
    /// </summary>
    public record HevoResourceBrush(string ResourceKey) : IHevoBrush;

    /// <summary>
    /// 画笔描述符 (支持虚线、端点样式、抗锯齿)
    /// 像素对齐：renderer 内部对所有 stroke vertex 做 snap（中心落在像素 .5 / .0），
    /// 配合 IsAntialias=true 让 H/V 线由 AA scanline 算法渲染为精确 1 像素，斜线段呈现平滑边缘。
    /// thickness 写多少 = 物理多少像素（1 → 1px、2 → 2px、1.5 sub-pixel 行为由 Skia 决定）。
    /// </summary>
    /// <param name="Brush"></param>
    /// <param name="Thickness"></param>
    /// <param name="DashArray"></param>
    /// <param name="LineCap"></param>
    /// <param name="LineJoin"></param>
    /// <param name="IsAntialias">true(默认) = AA on，斜线段平滑、H/V 由 snap 保证锐利；false = AA off，全部按 scanline 整数边界</param>
    public record HevoPen(
        IHevoBrush Brush,
        double Thickness,
        double[]? DashArray = null, // 传 null 就是实线
        int LineCap = 0,           // 0 Flat / 1 Square / 2 Round / 3 Triangle（对应 WPF PenLineCap）
        int LineJoin = 0,          // 对应 Miter/Bevel/Round
        bool IsAntialias = true    // 默认开 AA：1px series 斜线段开箱即得平滑
    );

    /// <summary>
    /// 跨平台字体排版描述符
    /// </summary>
    public interface IHevoTypeface { }

    public record HevoTypeface(string FontFamily, int FontWeight = 400, bool IsItalic = false) : IHevoTypeface;

    // 💥 新增：资源字体令牌 (将从平台的资源池中解析真正的 FontFamily)
    public record HevoResourceTypeface(string ResourceKey, int FontWeight = 400, bool IsItalic = false) : IHevoTypeface;
}
