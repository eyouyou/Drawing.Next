namespace Hevo.Charting
{
    /// <summary>
    /// 平台无关的 sRGB 颜色(8 位 ARGB)。样式记录、Trait、FieldMeta 都用它,各宿主在渲染器里换成自己的颜色类型;
    /// WPF 宿主互转用 <c>ToWpf()</c> / <c>ToHevo()</c>。
    /// </summary>
    public readonly record struct HevoColor(byte A, byte R, byte G, byte B)
    {
        public static HevoColor FromArgb(byte a, byte r, byte g, byte b) => new HevoColor(a, r, g, b);
        public static HevoColor FromRgb(byte r, byte g, byte b) => new HevoColor(0xFF, r, g, b);

        /// <summary>同 WPF Color.ToString 的 #AARRGGBB 形式:蓝图 JSON、日志里的颜色文本不因换类型而变。</summary>
        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }

    /// <summary>预定义颜色,名字和色值跟 WPF <c>System.Windows.Media.Colors</c> 一一对应。</summary>
    public static class HevoColors
    {
        public static HevoColor AliceBlue => new HevoColor(0xFF, 0xF0, 0xF8, 0xFF);
        public static HevoColor AntiqueWhite => new HevoColor(0xFF, 0xFA, 0xEB, 0xD7);
        public static HevoColor Aqua => new HevoColor(0xFF, 0x00, 0xFF, 0xFF);
        public static HevoColor Aquamarine => new HevoColor(0xFF, 0x7F, 0xFF, 0xD4);
        public static HevoColor Azure => new HevoColor(0xFF, 0xF0, 0xFF, 0xFF);
        public static HevoColor Beige => new HevoColor(0xFF, 0xF5, 0xF5, 0xDC);
        public static HevoColor Bisque => new HevoColor(0xFF, 0xFF, 0xE4, 0xC4);
        public static HevoColor Black => new HevoColor(0xFF, 0x00, 0x00, 0x00);
        public static HevoColor BlanchedAlmond => new HevoColor(0xFF, 0xFF, 0xEB, 0xCD);
        public static HevoColor Blue => new HevoColor(0xFF, 0x00, 0x00, 0xFF);
        public static HevoColor BlueViolet => new HevoColor(0xFF, 0x8A, 0x2B, 0xE2);
        public static HevoColor Brown => new HevoColor(0xFF, 0xA5, 0x2A, 0x2A);
        public static HevoColor BurlyWood => new HevoColor(0xFF, 0xDE, 0xB8, 0x87);
        public static HevoColor CadetBlue => new HevoColor(0xFF, 0x5F, 0x9E, 0xA0);
        public static HevoColor Chartreuse => new HevoColor(0xFF, 0x7F, 0xFF, 0x00);
        public static HevoColor Chocolate => new HevoColor(0xFF, 0xD2, 0x69, 0x1E);
        public static HevoColor Coral => new HevoColor(0xFF, 0xFF, 0x7F, 0x50);
        public static HevoColor CornflowerBlue => new HevoColor(0xFF, 0x64, 0x95, 0xED);
        public static HevoColor Cornsilk => new HevoColor(0xFF, 0xFF, 0xF8, 0xDC);
        public static HevoColor Crimson => new HevoColor(0xFF, 0xDC, 0x14, 0x3C);
        public static HevoColor Cyan => new HevoColor(0xFF, 0x00, 0xFF, 0xFF);
        public static HevoColor DarkBlue => new HevoColor(0xFF, 0x00, 0x00, 0x8B);
        public static HevoColor DarkCyan => new HevoColor(0xFF, 0x00, 0x8B, 0x8B);
        public static HevoColor DarkGoldenrod => new HevoColor(0xFF, 0xB8, 0x86, 0x0B);
        public static HevoColor DarkGray => new HevoColor(0xFF, 0xA9, 0xA9, 0xA9);
        public static HevoColor DarkGreen => new HevoColor(0xFF, 0x00, 0x64, 0x00);
        public static HevoColor DarkKhaki => new HevoColor(0xFF, 0xBD, 0xB7, 0x6B);
        public static HevoColor DarkMagenta => new HevoColor(0xFF, 0x8B, 0x00, 0x8B);
        public static HevoColor DarkOliveGreen => new HevoColor(0xFF, 0x55, 0x6B, 0x2F);
        public static HevoColor DarkOrange => new HevoColor(0xFF, 0xFF, 0x8C, 0x00);
        public static HevoColor DarkOrchid => new HevoColor(0xFF, 0x99, 0x32, 0xCC);
        public static HevoColor DarkRed => new HevoColor(0xFF, 0x8B, 0x00, 0x00);
        public static HevoColor DarkSalmon => new HevoColor(0xFF, 0xE9, 0x96, 0x7A);
        public static HevoColor DarkSeaGreen => new HevoColor(0xFF, 0x8F, 0xBC, 0x8F);
        public static HevoColor DarkSlateBlue => new HevoColor(0xFF, 0x48, 0x3D, 0x8B);
        public static HevoColor DarkSlateGray => new HevoColor(0xFF, 0x2F, 0x4F, 0x4F);
        public static HevoColor DarkTurquoise => new HevoColor(0xFF, 0x00, 0xCE, 0xD1);
        public static HevoColor DarkViolet => new HevoColor(0xFF, 0x94, 0x00, 0xD3);
        public static HevoColor DeepPink => new HevoColor(0xFF, 0xFF, 0x14, 0x93);
        public static HevoColor DeepSkyBlue => new HevoColor(0xFF, 0x00, 0xBF, 0xFF);
        public static HevoColor DimGray => new HevoColor(0xFF, 0x69, 0x69, 0x69);
        public static HevoColor DodgerBlue => new HevoColor(0xFF, 0x1E, 0x90, 0xFF);
        public static HevoColor Firebrick => new HevoColor(0xFF, 0xB2, 0x22, 0x22);
        public static HevoColor FloralWhite => new HevoColor(0xFF, 0xFF, 0xFA, 0xF0);
        public static HevoColor ForestGreen => new HevoColor(0xFF, 0x22, 0x8B, 0x22);
        public static HevoColor Fuchsia => new HevoColor(0xFF, 0xFF, 0x00, 0xFF);
        public static HevoColor Gainsboro => new HevoColor(0xFF, 0xDC, 0xDC, 0xDC);
        public static HevoColor GhostWhite => new HevoColor(0xFF, 0xF8, 0xF8, 0xFF);
        public static HevoColor Gold => new HevoColor(0xFF, 0xFF, 0xD7, 0x00);
        public static HevoColor Goldenrod => new HevoColor(0xFF, 0xDA, 0xA5, 0x20);
        public static HevoColor Gray => new HevoColor(0xFF, 0x80, 0x80, 0x80);
        public static HevoColor Green => new HevoColor(0xFF, 0x00, 0x80, 0x00);
        public static HevoColor GreenYellow => new HevoColor(0xFF, 0xAD, 0xFF, 0x2F);
        public static HevoColor Honeydew => new HevoColor(0xFF, 0xF0, 0xFF, 0xF0);
        public static HevoColor HotPink => new HevoColor(0xFF, 0xFF, 0x69, 0xB4);
        public static HevoColor IndianRed => new HevoColor(0xFF, 0xCD, 0x5C, 0x5C);
        public static HevoColor Indigo => new HevoColor(0xFF, 0x4B, 0x00, 0x82);
        public static HevoColor Ivory => new HevoColor(0xFF, 0xFF, 0xFF, 0xF0);
        public static HevoColor Khaki => new HevoColor(0xFF, 0xF0, 0xE6, 0x8C);
        public static HevoColor Lavender => new HevoColor(0xFF, 0xE6, 0xE6, 0xFA);
        public static HevoColor LavenderBlush => new HevoColor(0xFF, 0xFF, 0xF0, 0xF5);
        public static HevoColor LawnGreen => new HevoColor(0xFF, 0x7C, 0xFC, 0x00);
        public static HevoColor LemonChiffon => new HevoColor(0xFF, 0xFF, 0xFA, 0xCD);
        public static HevoColor LightBlue => new HevoColor(0xFF, 0xAD, 0xD8, 0xE6);
        public static HevoColor LightCoral => new HevoColor(0xFF, 0xF0, 0x80, 0x80);
        public static HevoColor LightCyan => new HevoColor(0xFF, 0xE0, 0xFF, 0xFF);
        public static HevoColor LightGoldenrodYellow => new HevoColor(0xFF, 0xFA, 0xFA, 0xD2);
        public static HevoColor LightGray => new HevoColor(0xFF, 0xD3, 0xD3, 0xD3);
        public static HevoColor LightGreen => new HevoColor(0xFF, 0x90, 0xEE, 0x90);
        public static HevoColor LightPink => new HevoColor(0xFF, 0xFF, 0xB6, 0xC1);
        public static HevoColor LightSalmon => new HevoColor(0xFF, 0xFF, 0xA0, 0x7A);
        public static HevoColor LightSeaGreen => new HevoColor(0xFF, 0x20, 0xB2, 0xAA);
        public static HevoColor LightSkyBlue => new HevoColor(0xFF, 0x87, 0xCE, 0xFA);
        public static HevoColor LightSlateGray => new HevoColor(0xFF, 0x77, 0x88, 0x99);
        public static HevoColor LightSteelBlue => new HevoColor(0xFF, 0xB0, 0xC4, 0xDE);
        public static HevoColor LightYellow => new HevoColor(0xFF, 0xFF, 0xFF, 0xE0);
        public static HevoColor Lime => new HevoColor(0xFF, 0x00, 0xFF, 0x00);
        public static HevoColor LimeGreen => new HevoColor(0xFF, 0x32, 0xCD, 0x32);
        public static HevoColor Linen => new HevoColor(0xFF, 0xFA, 0xF0, 0xE6);
        public static HevoColor Magenta => new HevoColor(0xFF, 0xFF, 0x00, 0xFF);
        public static HevoColor Maroon => new HevoColor(0xFF, 0x80, 0x00, 0x00);
        public static HevoColor MediumAquamarine => new HevoColor(0xFF, 0x66, 0xCD, 0xAA);
        public static HevoColor MediumBlue => new HevoColor(0xFF, 0x00, 0x00, 0xCD);
        public static HevoColor MediumOrchid => new HevoColor(0xFF, 0xBA, 0x55, 0xD3);
        public static HevoColor MediumPurple => new HevoColor(0xFF, 0x93, 0x70, 0xDB);
        public static HevoColor MediumSeaGreen => new HevoColor(0xFF, 0x3C, 0xB3, 0x71);
        public static HevoColor MediumSlateBlue => new HevoColor(0xFF, 0x7B, 0x68, 0xEE);
        public static HevoColor MediumSpringGreen => new HevoColor(0xFF, 0x00, 0xFA, 0x9A);
        public static HevoColor MediumTurquoise => new HevoColor(0xFF, 0x48, 0xD1, 0xCC);
        public static HevoColor MediumVioletRed => new HevoColor(0xFF, 0xC7, 0x15, 0x85);
        public static HevoColor MidnightBlue => new HevoColor(0xFF, 0x19, 0x19, 0x70);
        public static HevoColor MintCream => new HevoColor(0xFF, 0xF5, 0xFF, 0xFA);
        public static HevoColor MistyRose => new HevoColor(0xFF, 0xFF, 0xE4, 0xE1);
        public static HevoColor Moccasin => new HevoColor(0xFF, 0xFF, 0xE4, 0xB5);
        public static HevoColor NavajoWhite => new HevoColor(0xFF, 0xFF, 0xDE, 0xAD);
        public static HevoColor Navy => new HevoColor(0xFF, 0x00, 0x00, 0x80);
        public static HevoColor OldLace => new HevoColor(0xFF, 0xFD, 0xF5, 0xE6);
        public static HevoColor Olive => new HevoColor(0xFF, 0x80, 0x80, 0x00);
        public static HevoColor OliveDrab => new HevoColor(0xFF, 0x6B, 0x8E, 0x23);
        public static HevoColor Orange => new HevoColor(0xFF, 0xFF, 0xA5, 0x00);
        public static HevoColor OrangeRed => new HevoColor(0xFF, 0xFF, 0x45, 0x00);
        public static HevoColor Orchid => new HevoColor(0xFF, 0xDA, 0x70, 0xD6);
        public static HevoColor PaleGoldenrod => new HevoColor(0xFF, 0xEE, 0xE8, 0xAA);
        public static HevoColor PaleGreen => new HevoColor(0xFF, 0x98, 0xFB, 0x98);
        public static HevoColor PaleTurquoise => new HevoColor(0xFF, 0xAF, 0xEE, 0xEE);
        public static HevoColor PaleVioletRed => new HevoColor(0xFF, 0xDB, 0x70, 0x93);
        public static HevoColor PapayaWhip => new HevoColor(0xFF, 0xFF, 0xEF, 0xD5);
        public static HevoColor PeachPuff => new HevoColor(0xFF, 0xFF, 0xDA, 0xB9);
        public static HevoColor Peru => new HevoColor(0xFF, 0xCD, 0x85, 0x3F);
        public static HevoColor Pink => new HevoColor(0xFF, 0xFF, 0xC0, 0xCB);
        public static HevoColor Plum => new HevoColor(0xFF, 0xDD, 0xA0, 0xDD);
        public static HevoColor PowderBlue => new HevoColor(0xFF, 0xB0, 0xE0, 0xE6);
        public static HevoColor Purple => new HevoColor(0xFF, 0x80, 0x00, 0x80);
        public static HevoColor Red => new HevoColor(0xFF, 0xFF, 0x00, 0x00);
        public static HevoColor RosyBrown => new HevoColor(0xFF, 0xBC, 0x8F, 0x8F);
        public static HevoColor RoyalBlue => new HevoColor(0xFF, 0x41, 0x69, 0xE1);
        public static HevoColor SaddleBrown => new HevoColor(0xFF, 0x8B, 0x45, 0x13);
        public static HevoColor Salmon => new HevoColor(0xFF, 0xFA, 0x80, 0x72);
        public static HevoColor SandyBrown => new HevoColor(0xFF, 0xF4, 0xA4, 0x60);
        public static HevoColor SeaGreen => new HevoColor(0xFF, 0x2E, 0x8B, 0x57);
        public static HevoColor SeaShell => new HevoColor(0xFF, 0xFF, 0xF5, 0xEE);
        public static HevoColor Sienna => new HevoColor(0xFF, 0xA0, 0x52, 0x2D);
        public static HevoColor Silver => new HevoColor(0xFF, 0xC0, 0xC0, 0xC0);
        public static HevoColor SkyBlue => new HevoColor(0xFF, 0x87, 0xCE, 0xEB);
        public static HevoColor SlateBlue => new HevoColor(0xFF, 0x6A, 0x5A, 0xCD);
        public static HevoColor SlateGray => new HevoColor(0xFF, 0x70, 0x80, 0x90);
        public static HevoColor Snow => new HevoColor(0xFF, 0xFF, 0xFA, 0xFA);
        public static HevoColor SpringGreen => new HevoColor(0xFF, 0x00, 0xFF, 0x7F);
        public static HevoColor SteelBlue => new HevoColor(0xFF, 0x46, 0x82, 0xB4);
        public static HevoColor Tan => new HevoColor(0xFF, 0xD2, 0xB4, 0x8C);
        public static HevoColor Teal => new HevoColor(0xFF, 0x00, 0x80, 0x80);
        public static HevoColor Thistle => new HevoColor(0xFF, 0xD8, 0xBF, 0xD8);
        public static HevoColor Tomato => new HevoColor(0xFF, 0xFF, 0x63, 0x47);
        public static HevoColor Transparent => new HevoColor(0x00, 0xFF, 0xFF, 0xFF);
        public static HevoColor Turquoise => new HevoColor(0xFF, 0x40, 0xE0, 0xD0);
        public static HevoColor Violet => new HevoColor(0xFF, 0xEE, 0x82, 0xEE);
        public static HevoColor Wheat => new HevoColor(0xFF, 0xF5, 0xDE, 0xB3);
        public static HevoColor White => new HevoColor(0xFF, 0xFF, 0xFF, 0xFF);
        public static HevoColor WhiteSmoke => new HevoColor(0xFF, 0xF5, 0xF5, 0xF5);
        public static HevoColor Yellow => new HevoColor(0xFF, 0xFF, 0xFF, 0x00);
        public static HevoColor YellowGreen => new HevoColor(0xFF, 0x9A, 0xCD, 0x32);
    }
}
