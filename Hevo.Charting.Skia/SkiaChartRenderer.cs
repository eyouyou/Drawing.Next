using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using SkiaSharp;

namespace Hevo.Charting.Skia
{
    /// <summary>
    /// DrawCmd → SKCanvas 回放器,不依赖任何 UI 框架(WPF 合成面在 Hevo.Charting.Skia.Wpf,Avalonia 等宿主直接拿 SKCanvas 用它)。
    /// 资源笔刷 / 资源字体 / 宿主图片由 <see cref="ISkiaHostResources"/> 按宿主解析。
    /// 几何口径与 WPF 的 WpfDrawingRenderer 逐分支对齐(同样的像素对齐规则、
    /// 同样的相邻 DrawLine 合批、DrawRectangles 逐个画、Pen 的线端 / 虚线端 / 斜接上限取 WPF 的值)。
    /// <para>
    /// 按设备像素录制:<see cref="PixelsPerDip"/> ≠ 1 时画布先整体缩放,像素对齐在设备像素上做
    /// (DIP 坐标先乘 DPI 再取整),整数线宽也换算成整数设备像素,125% / 150% 下 1px 线不发虚。
    /// PixelsPerDip = 1 时与按 DIP 录制逐像素相同。
    /// </para>
    /// 单线程使用:每个后端实例一份,只在 UI 线程调用。
    /// </summary>
    public sealed class SkiaChartRenderer : IDisposable
    {
        // 容量与 WpfDrawingRenderer 一致:brush 128 / pen 128 / 字体 64 / SVG 几何 256;文本 = 字符串 × 字号 × 字体
        private const int TextCacheCapacity = 4096;
        private const int FallbackCacheCapacity = 4096;
        private const float WpfMiterLimit = 10f; // WPF Pen.MiterLimit 默认值(Skia 默认 4)

        private readonly LruCache<IHevoBrush, PaintEntry?> _fill;
        private readonly LruCache<HevoPen, PaintEntry?> _stroke;
        private readonly Dictionary<IHevoTypeface, SKTypeface> _typefaces = new();
        private readonly LruCache<(IHevoTypeface, float), SKFont> _fonts;
        private readonly LruCache<string, SKPath?> _svg;
        private readonly Dictionary<FallbackKey, SKTypeface?> _fallback = new();
        private readonly Dictionary<TextKey, TextEntry> _text = new();
        private readonly ConditionalWeakTable<object, SKImage> _images = new();
        private readonly ISkiaHostResources? _resources;
        private readonly SKPaint?[] _opacityPaints = new SKPaint?[256];
        private readonly SKPath _path = new(); // 复用:Reset 后重填,避免每条命令 new 一个原生 path

        // last-used 短路:K 线 / 网格一帧上百条命令复用同一 brush/pen,ReferenceEquals 命中跳过 LRU
        private IHevoBrush? _lastFillDesc; private PaintEntry? _lastFill;
        private HevoPen? _lastStrokeDesc; private PaintEntry? _lastStroke;

        private double _pixelsPerDip = 1.0;
        private float _scale = 1f;

        private readonly record struct TextKey(IHevoString Text, IHevoTypeface Typeface, float Size);
        private readonly record struct FallbackKey(string Family, int Weight, int Slant, int CodePoint);

        private sealed class TextEntry
        {
            public SKTextBlob? Blob;
            public float Width;
        }

        /// <summary>
        /// 渐变参数。shader 依赖图元包围盒(RelativeToBoundingBox),所以不缓存在画笔里,每个图元现建。
        /// </summary>
        private sealed class Gradient
        {
            public SKColor[] Colors = Array.Empty<SKColor>();
            public float[]? Positions;
            public SKPoint Start, End;
            public bool Relative;
        }

        /// <summary>缓存的画笔。纯色直接用;渐变每次 <see cref="Use"/> 时按图元包围盒换一个 shader。</summary>
        private sealed class PaintEntry : IDisposable
        {
            public readonly SKPaint Paint;
            public readonly Gradient? Gradient;
            public SKShader? Shader;

            public PaintEntry(SKPaint paint, Gradient? gradient) { Paint = paint; Gradient = gradient; }

            public void Dispose()
            {
                Paint.Dispose();
                Shader?.Dispose();
                Shader = null;
            }
        }

        /// <param name="resources">宿主资源解析(主题笔刷 / 字体、宿主图片);null = 只认纯色 / 线性渐变 / 具名字体,图片不画。</param>
        public SkiaChartRenderer(ISkiaHostResources? resources = null)
        {
            _resources = resources;
            _fill = new LruCache<IHevoBrush, PaintEntry?>(128, OnFillEvicted);
            _stroke = new LruCache<HevoPen, PaintEntry?>(128, OnStrokeEvicted);
            _fonts = new LruCache<(IHevoTypeface, float), SKFont>(64, f => f.Dispose());
            _svg = new LruCache<string, SKPath?>(256, p => p?.Dispose());
        }

        /// <summary>
        /// 录制目标的 DPI 缩放。变了会清掉画笔缓存(线宽按设备像素换算),已录好的 picture 需要调用方重录。
        /// </summary>
        public double PixelsPerDip
        {
            get => _pixelsPerDip;
            set
            {
                double v = value > 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 1.0;
                if (v == _pixelsPerDip) return;
                _pixelsPerDip = v;
                _scale = (float)v;
                ClearResourceCaches();
            }
        }

        private SKCanvas _canvas = null!;

        /// <summary>把一个图层的绘制指令回放进 canvas(canvas 按设备像素:宽高 = DIP × <see cref="PixelsPerDip"/>)。</summary>
        public void Render(DrawingBuffer buffer, SKCanvas canvas)
        {
            _canvas = canvas;
            int baseSave = canvas.Save();
            if (_scale != 1f) canvas.Scale(_scale);

            var span = CollectionsMarshal.AsSpan(buffer.Commands);
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var cmd = ref span[i];

                // 相邻同 pen 的 DrawLine 合成一条多段路径(网格 / 刻度线几十条短线 → 1 次绘制)
                if (cmd.Op == DrawOp.DrawLine && cmd.Pen != null)
                {
                    int run = 1;
                    while (i + run < span.Length && span[i + run].Op == DrawOp.DrawLine && ReferenceEquals(span[i + run].Pen, cmd.Pen)) run++;
                    if (run >= 2)
                    {
                        if (Stroke(cmd.Pen) is { } batchPen)
                        {
                            float vh = PixelSnap.HalfPx(DeviceThickness(cmd.Pen.Thickness));
                            _path.Reset();
                            for (int j = 0; j < run; j++)
                            {
                                ref readonly var c = ref span[i + j];
                                SnapEndpoints(c.Payload.P1, c.Payload.P2, vh, out var a, out var b);
                                _path.MoveTo(a);
                                _path.LineTo(b);
                            }
                            canvas.DrawPath(_path, UsePath(batchPen, _path));
                        }
                        i += run - 1;
                        continue;
                    }
                }

                switch (cmd.Op)
                {
                    case DrawOp.DrawLine:
                    {
                        if (Stroke(cmd.Pen) is not { } pen) break;
                        SnapEndpoints(cmd.Payload.P1, cmd.Payload.P2, PixelSnap.HalfPx(DeviceThickness(cmd.Pen!.Thickness)), out var a, out var b);
                        canvas.DrawLine(a, b, pen.Gradient == null ? pen.Paint : Use(pen, Bounds(a, b)));
                        break;
                    }
                    case DrawOp.DrawRectangle:
                        Rect(cmd.Payload.RectArea, cmd.Brush, cmd.Pen);
                        break;
                    case DrawOp.DrawRoundedRectangle:
                    {
                        var r = cmd.Payload.RectArea;
                        if (Fill(cmd.Brush) is { } fill) canvas.DrawRoundRect(R(r), cmd.Payload.Val1, cmd.Payload.Val2, UseRect(fill, R(r)));
                        if (Stroke(cmd.Pen) is { } pen)
                        {
                            var sr = R(InsideStroke(r, DeviceThickness(cmd.Pen!.Thickness)));
                            canvas.DrawRoundRect(sr, cmd.Payload.Val1, cmd.Payload.Val2, UseRect(pen, sr));
                        }
                        break;
                    }
                    case DrawOp.DrawEllipse:
                    {
                        // 圆心 snap 到整数(设备)像素,与 WPF 后端一致
                        var c = cmd.Payload.P1;
                        float cx = MathF.Round(c.X * _scale) / _scale, cy = MathF.Round(c.Y * _scale) / _scale;
                        var oval = new SKRect(cx - cmd.Payload.Val1, cy - cmd.Payload.Val2, cx + cmd.Payload.Val1, cy + cmd.Payload.Val2);
                        if (Fill(cmd.Brush) is { } fill) canvas.DrawOval(oval, UseRect(fill, oval));
                        if (Stroke(cmd.Pen) is { } pen) canvas.DrawOval(oval, UseRect(pen, oval));
                        break;
                    }
                    case DrawOp.DrawPolyline:
                    {
                        if (Stroke(cmd.Pen) is not { } pen || cmd.RefData is not List<HevoPoint> pts || pts.Count < 2) break;
                        float vh = PixelSnap.HalfPx(DeviceThickness(cmd.Pen!.Thickness));
                        var s = CollectionsMarshal.AsSpan(pts);
                        _path.Reset();
                        _path.MoveTo(Vertex(s[0].X, vh), Vertex(s[0].Y, vh));
                        for (int k = 1; k < s.Length; k++) _path.LineTo(Vertex(s[k].X, vh), Vertex(s[k].Y, vh));
                        canvas.DrawPath(_path, UsePath(pen, _path));
                        break;
                    }
                    case DrawOp.DrawLineSegments:
                    {
                        if (Stroke(cmd.Pen) is not { } pen || cmd.RefData is not List<HevoPoint> pts || pts.Count < 2) break;
                        float vh = PixelSnap.HalfPx(DeviceThickness(cmd.Pen!.Thickness));
                        var s = CollectionsMarshal.AsSpan(pts);
                        _path.Reset();
                        for (int k = 0; k + 1 < s.Length; k += 2)
                        {
                            SnapEndpoints(s[k], s[k + 1], vh, out var a, out var b);
                            _path.MoveTo(a);
                            _path.LineTo(b);
                        }
                        canvas.DrawPath(_path, UsePath(pen, _path));
                        break;
                    }
                    case DrawOp.DrawCubicBezier:
                    {
                        if (Stroke(cmd.Pen) is not { } pen || cmd.RefData is not HevoPoint[] ctrls || ctrls.Length != 2) break;
                        float vh = PixelSnap.HalfPx(DeviceThickness(cmd.Pen!.Thickness));
                        _path.Reset();
                        _path.MoveTo(Vertex(cmd.Payload.P1.X, vh), Vertex(cmd.Payload.P1.Y, vh));
                        _path.CubicTo(ctrls[0].X, ctrls[0].Y, ctrls[1].X, ctrls[1].Y,
                            Vertex(cmd.Payload.P2.X, vh), Vertex(cmd.Payload.P2.Y, vh));
                        canvas.DrawPath(_path, UsePath(pen, _path));
                        break;
                    }
                    case DrawOp.DrawRectangles:
                    {
                        if (cmd.RefData is not IList<HevoRect> rects || rects.Count == 0) break;
                        // fill / stroke 分属两张缓存表,取 stroke 不会把刚取到的 fill 挤掉
                        var fill = Fill(cmd.Brush);
                        var pen = Stroke(cmd.Pen);
                        double thick = cmd.Pen != null ? DeviceThickness(cmd.Pen.Thickness) : 0;
                        for (int k = 0; k < rects.Count; k++)
                        {
                            var r = rects[k];
                            if (fill != null) canvas.DrawRect(R(r), UseRect(fill, R(r)));
                            if (pen != null)
                            {
                                var sr = R(InsideStroke(r, thick));
                                canvas.DrawRect(sr, UseRect(pen, sr));
                            }
                        }
                        break;
                    }
                    case DrawOp.DrawGeometry:
                    {
                        if (cmd.RefData is not string svg) break;
                        var p = Svg(svg);
                        if (p == null) break;
                        if (Fill(cmd.Brush) is { } fill) canvas.DrawPath(p, UsePath(fill, p));
                        if (Stroke(cmd.Pen) is { } pen) canvas.DrawPath(p, UsePath(pen, p));
                        break;
                    }
                    case DrawOp.DrawPolygon:
                    {
                        if (cmd.RefData is not List<HevoPoint> pts || pts.Count < 3) break;
                        var s = CollectionsMarshal.AsSpan(pts);
                        if (Fill(cmd.Brush) is { } fill)
                        {
                            var w = new PathWriter(_path, true, true, 0f, _scale);
                            if (FillGeometry.Polygon(s, ref w)) canvas.DrawPath(_path, UsePath(fill, _path));
                        }
                        if (Stroke(cmd.Pen) is { } pen)
                        {
                            var w = new PathWriter(_path, false, true, PixelSnap.HalfPx(DeviceThickness(cmd.Pen!.Thickness)), _scale);
                            if (FillGeometry.Polygon(s, ref w)) canvas.DrawPath(_path, UsePath(pen, _path));
                        }
                        break;
                    }
                    case DrawOp.DrawBand:
                    {
                        if (cmd.RefData is not HevoBand band || band.Count < 2) break;
                        var up = CollectionsMarshal.AsSpan(band.Upper);
                        var lo = CollectionsMarshal.AsSpan(band.Lower);
                        var crossed = band.CrossedBrush;
                        if (Fill(cmd.Brush) is { } fill)
                        {
                            var w = new PathWriter(_path, true, true, 0f, _scale);
                            FillGeometry.Band(up, lo, crossed != null ? 1 : 0, ref w);
                            if (w.Figures > 0) canvas.DrawPath(_path, UsePath(fill, _path));
                        }
                        if (crossed != null && Fill(crossed) is { } crossedFill)
                        {
                            var w = new PathWriter(_path, true, true, 0f, _scale);
                            FillGeometry.Band(up, lo, -1, ref w);
                            if (w.Figures > 0) canvas.DrawPath(_path, UsePath(crossedFill, _path));
                        }
                        if (Stroke(cmd.Pen) is { } pen)
                        {
                            var w = new PathWriter(_path, false, false, PixelSnap.HalfPx(DeviceThickness(cmd.Pen!.Thickness)), _scale);
                            FillGeometry.BandEdges(up, lo, ref w);
                            if (w.Figures > 0) canvas.DrawPath(_path, UsePath(pen, _path));
                        }
                        break;
                    }
                    case DrawOp.DrawText:
                        Text(cmd);
                        break;
                    case DrawOp.DrawImage:
                    {
                        if (cmd.RefData is not { } src || Image(src) is not { } img) break;
                        canvas.DrawImage(img, R(cmd.Payload.RectArea), new SKSamplingOptions(SKFilterMode.Linear));
                        break;
                    }
                    case DrawOp.PushClip:
                        canvas.Save();
                        canvas.ClipRect(R(cmd.Payload.RectArea));
                        break;
                    case DrawOp.PushOpacity:
                    {
                        int a = Math.Clamp((int)(cmd.Payload.Val1 * 255f + 0.5f), 0, 255);
                        canvas.SaveLayer(_opacityPaints[a] ??= new SKPaint { Color = new SKColor(0, 0, 0, (byte)a) });
                        break;
                    }
                    case DrawOp.PushTransform:
                    {
                        var m = cmd.Payload.Transform; // System.Numerics.Matrix3x2:(M11 M12 / M21 M22 / M31 M32)
                        canvas.Save();
                        var sk = new SKMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);
                        canvas.Concat(in sk);
                        break;
                    }
                    case DrawOp.Pop:
                        // 多余的 Pop 不能弹掉外面的 DPI 缩放
                        if (canvas.SaveCount > baseSave + 1) canvas.Restore();
                        break;
                    // DrawVideo:Skia 没有视频解码;该命令无生产者,跳过
                }
            }
            canvas.RestoreToCount(baseSave);
        }

        // ==========================================
        // 设备像素对齐:规则只有 PixelSnap 一份(WPF 后端同用),这里只做 SKPoint / 设备缩放的转接
        // ==========================================

        private double DeviceThickness(double t) => PixelSnap.DeviceThickness(t, _pixelsPerDip);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Vertex(float v, float halfPx) => PixelSnap.Vertex(v, halfPx, _scale);

        private void SnapEndpoints(HevoPoint p1, HevoPoint p2, float halfPx, out SKPoint a, out SKPoint b)
        {
            PixelSnap.SnapEndpoints(p1, p2, halfPx, _scale, out var ha, out var hb);
            a = new SKPoint(ha.X, ha.Y);
            b = new SKPoint(hb.X, hb.Y);
        }

        private HevoRect InsideStroke(HevoRect r, double deviceThickness) => PixelSnap.InsideStroke(r, deviceThickness, _scale);

        /// <summary>DrawPolygon / DrawBand 写进复用的 _path(Reset 后重填)。顶点对齐同 WPF 后端:填充取设备像素边界,描边同 polyline。</summary>
        private struct PathWriter : IFigureWriter
        {
            private readonly SKPath _p;
            private readonly bool _fill, _closed;
            private readonly float _half, _k;
            public int Figures;

            public PathWriter(SKPath path, bool fill, bool closed, float half, float k)
            {
                _p = path; _fill = fill; _closed = closed; _half = half; _k = k; Figures = 0;
                path.Reset(); // 默认非零环绕,跟 WPF / 位图后端一致
            }

            private SKPoint S(HevoPoint p) => _fill
                ? new SKPoint(PixelSnap.FillVertex(p.X, _k), PixelSnap.FillVertex(p.Y, _k))
                : new SKPoint(PixelSnap.Vertex(p.X, _half, _k), PixelSnap.Vertex(p.Y, _half, _k));

            public void Begin(HevoPoint p) { _p.MoveTo(S(p)); Figures++; }
            public void LineTo(HevoPoint p) => _p.LineTo(S(p));
            public void End() { if (_closed) _p.Close(); }
        }

        private void Rect(HevoRect r, IHevoBrush? brush, HevoPen? penDesc)
        {
            if (Fill(brush) is { } fill) _canvas.DrawRect(R(r), UseRect(fill, R(r)));
            if (penDesc != null && Stroke(penDesc) is { } pen)
            {
                var sr = R(InsideStroke(r, DeviceThickness(penDesc.Thickness)));
                _canvas.DrawRect(sr, UseRect(pen, sr));
            }
        }

        private void Text(in DrawCmd cmd)
        {
            if (cmd.RefData is not TextInfo info || Fill(cmd.Brush) == null) return;
            float size = cmd.Payload.Val1;
            var font = Font(info.Typeface, size);
            var key = new TextKey(info.Text, info.Typeface, size);
            if (!_text.TryGetValue(key, out var t))
            {
                if (_text.Count >= TextCacheCapacity) ClearText();
                // 已录进 SKPicture 的 blob 由 picture 持有引用,缓存淘汰时 Dispose 不影响已录制内容
                t = BuildText(_resources?.ResolveText(info.Text) ?? HevoStrings.Resolve(info.Text), info.Typeface, font, size);
                _text[key] = t;
            }
            if (t.Blob == null) return;

            var metrics = font.Metrics;
            float h = metrics.Descent - metrics.Ascent;
            var layout = TextLayoutHelper.Compute(cmd.Payload.P1.X, cmd.Payload.P1.Y, t.Width, h,
                info.PaddingX, info.PaddingY, info.AlignX, info.AlignY);
            if (info.BgBrush != null || info.BorderPen != null)
                Rect(new HevoRect(layout.BoxX, layout.BoxY, layout.BoxWidth, layout.BoxHeight), info.BgBrush, info.BorderPen);
            // 背景框可能往填充缓存里插了新项:文字画笔在这里重取,保证拿到的是活的
            if (Fill(cmd.Brush) is not { } fill) return;
            var textBounds = new SKRect(layout.TextX, layout.TextY, layout.TextX + t.Width, layout.TextY + h);
            // TextLayoutHelper 给的是顶左坐标,Skia 文本按基线画:叠加 -Ascent
            _canvas.DrawText(t.Blob, layout.TextX, layout.TextY - metrics.Ascent, UseRect(fill, textBounds));
        }

        /// <summary>
        /// 排版一段文字。主字体有全部字形时整段一个 run;缺字(如中文落在 Arial 上)时按字符找回退字体
        /// (<see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/>),
        /// 相邻同字体的字符并成一个 run,逐 run 排版、测宽,不出豆腐块。
        /// </summary>
        private TextEntry BuildText(string s, IHevoTypeface face, SKFont font, float size)
        {
            if (string.IsNullOrEmpty(s)) return new TextEntry();
            if (font.ContainsGlyphs(s))
                return new TextEntry { Blob = SKTextBlob.Create(s, font), Width = font.MeasureText(s) };

            var primary = Typeface(face);
            using var builder = new SKTextBlobBuilder();
            var run = new StringBuilder();
            SKTypeface? runFace = null;
            float x = 0;
            for (int i = 0; i < s.Length;)
            {
                int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                int cp = len == 2 ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];
                var use = primary.ContainsGlyph(cp) ? primary : Fallback(primary, cp) ?? primary;
                if (runFace != null && !ReferenceEquals(use, runFace)) Flush();
                runFace = use;
                run.Append(s, i, len);
                i += len;
            }
            Flush();
            return new TextEntry { Blob = builder.Build(), Width = x };

            void Flush()
            {
                if (run.Length == 0 || runFace == null) return;
                string text = run.ToString();
                run.Clear();
                if (ReferenceEquals(runFace, primary)) AddRun(font, text);
                else
                {
                    using var rf = new SKFont(runFace, size) { Edging = font.Edging, Subpixel = font.Subpixel };
                    AddRun(rf, text);
                }
            }

            void AddRun(SKFont f, string text)
            {
                var glyphs = f.GetGlyphs(text);
                builder.AddRun(glyphs, f, new SKPoint(x, 0));
                x += f.MeasureText(text);
            }
        }

        private static readonly string[]? s_bcp47 = string.IsNullOrEmpty(System.Globalization.CultureInfo.CurrentUICulture.Name)
            ? null : new[] { System.Globalization.CultureInfo.CurrentUICulture.Name };

        private SKTypeface? Fallback(SKTypeface primary, int codePoint)
        {
            var key = new FallbackKey(primary.FamilyName, primary.FontWeight, (int)primary.FontSlant, codePoint);
            if (_fallback.TryGetValue(key, out var tf) && (tf == null || tf.Handle != IntPtr.Zero)) return tf;
            if (_fallback.Count >= FallbackCacheCapacity) DisposeTypefaces(clearPrimary: false);
            using var style = new SKFontStyle(primary.FontWeight, primary.FontWidth, primary.FontSlant);
            tf = SKFontManager.Default.MatchCharacter(primary.FamilyName, style, s_bcp47, codePoint);
            _fallback[key] = tf;
            return tf;
        }

        // ==========================================
        // 资源解析(首次遇到时解析一次,之后命中缓存)
        // ==========================================

        private PaintEntry? Fill(IHevoBrush? desc)
        {
            if (desc == null) return null;
            if (ReferenceEquals(desc, _lastFillDesc)) return _lastFill;
            if (!_fill.TryGet(desc, out var p))
            {
                p = CreateFill(desc);
                _fill.Add(desc, p);
            }
            _lastFillDesc = desc; _lastFill = p;
            return p;
        }

        private PaintEntry? Stroke(HevoPen? desc)
        {
            if (desc == null) return null;
            if (ReferenceEquals(desc, _lastStrokeDesc)) return _lastStroke;
            if (!_stroke.TryGet(desc, out var p))
            {
                p = CreateStroke(desc);
                _stroke.Add(desc, p);
            }
            _lastStrokeDesc = desc; _lastStroke = p;
            return p;
        }

        // 被挤出 LRU 的画笔若正好是 last-used 短路项,短路也要一起失效,否则会拿到已释放的 SKPaint
        private void OnFillEvicted(PaintEntry? e)
        {
            if (e != null && ReferenceEquals(e, _lastFill)) { _lastFillDesc = null; _lastFill = null; }
            e?.Dispose();
        }

        private void OnStrokeEvicted(PaintEntry? e)
        {
            if (e != null && ReferenceEquals(e, _lastStroke)) { _lastStrokeDesc = null; _lastStroke = null; }
            e?.Dispose();
        }

        private PaintEntry? CreateFill(IHevoBrush desc)
        {
            // 填充不开 AA:与 WPF 后端画布的 EdgeMode.Aliased 一致,K 线实体边缘锐利,光栅也更快
            var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
            if (!ApplyBrush(paint, desc, out var gradient)) { paint.Dispose(); return null; }
            return new PaintEntry(paint, gradient);
        }

        private PaintEntry? CreateStroke(HevoPen desc)
        {
            bool dashed = desc.DashArray is { Length: > 0 };
            float width = (float)(DeviceThickness(desc.Thickness) / _scale);
            var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = width,
                IsAntialias = desc.IsAntialias,
                // 数值即 WPF PenLineCap:0 Flat / 1 Square / 2 Round / 3 Triangle(Skia 没有三角头,退化为 Butt)。
                // 虚线的段端由下面的间隔补偿模拟,线端统一 Butt,否则每段都会再长出一个线端
                StrokeCap = dashed ? SKStrokeCap.Butt : desc.LineCap switch { 1 => SKStrokeCap.Square, 2 => SKStrokeCap.Round, _ => SKStrokeCap.Butt },
                StrokeJoin = desc.LineJoin switch { 1 => SKStrokeJoin.Bevel, 2 => SKStrokeJoin.Round, _ => SKStrokeJoin.Miter },
                StrokeMiter = WpfMiterLimit,
            };
            if (!ApplyBrush(paint, desc.Brush, out var gradient)) { paint.Dispose(); return null; }
            if (dashed) paint.PathEffect = CreateWpfDash(desc.DashArray!, desc.Thickness, width);
            return new PaintEntry(paint, gradient);
        }

        /// <summary>
        /// WPF 虚线:DashStyle 长度以线宽为单位,且 Pen.DashCap 默认 Square —— 每段实线两端各外扩半个线宽。
        /// Skia 的 dash 段端是平的,这里把每段实线加长一个线宽、空白缩短一个线宽,相位后移半个线宽,
        /// 等效于段端外扩;空白不够扣时相邻两段连成一片(与 WPF 段端重叠时的观感一致)。
        /// 线首仍是平头(同位图后端 Stroker:首段起点用 StartLineCap)。
        /// </summary>
        private static SKPathEffect? CreateWpfDash(double[] dash, double thickness, float strokeWidth)
        {
            var arr = new float[dash.Length % 2 == 0 ? dash.Length : dash.Length * 2];
            for (int k = 0; k < arr.Length; k++) arr[k] = (float)(Math.Max(0, dash[k % dash.Length]) * thickness);
            bool anyGap = false;
            for (int k = 0; k < arr.Length; k += 2)
            {
                arr[k] += strokeWidth;
                arr[k + 1] -= strokeWidth;
                if (arr[k + 1] < 0) { arr[k] += arr[k + 1]; arr[k + 1] = 0; }
                if (arr[k + 1] > 0) anyGap = true;
            }
            if (!anyGap) return null; // 段端互相盖满 = 实线
            return SKPathEffect.CreateDash(arr, strokeWidth / 2f);
        }

        /// <summary>纯色直接取;渐变记下参数(shader 按图元建);资源笔刷交给宿主解析成真实画刷(主题)。</summary>
        private bool ApplyBrush(SKPaint paint, IHevoBrush desc, out Gradient? gradient)
        {
            gradient = null;
            switch (desc)
            {
                case HevoSolidBrush s:
                    paint.Color = C(s.Color);
                    return true;
                case HevoLinearGradientBrush g:
                    // 与 WpfRenderRegistry 一致:new LinearGradientBrush(c0, c1, start, end),MappingMode 默认 RelativeToBoundingBox
                    gradient = new Gradient
                    {
                        Colors = new[] { C(g.StartColor), C(g.EndColor) },
                        Start = P(g.StartPoint), End = P(g.EndPoint), Relative = true,
                    };
                    return true;
            }
            if (_resources == null || !_resources.TryResolveBrush(desc, out var r) || r.Colors.Length == 0) return false;
            if (!r.IsGradient)
            {
                paint.Color = r.Colors[0];
                return true;
            }
            gradient = new Gradient { Colors = r.Colors, Positions = r.Positions, Start = r.Start, End = r.End, Relative = r.Relative };
            return true;
        }

        /// <summary>取画笔:纯色原样返回;渐变按图元包围盒(画布坐标)换算起止点、现建 shader。</summary>
        private static SKPaint Use(PaintEntry e, SKRect bounds)
        {
            var g = e.Gradient;
            if (g == null) return e.Paint;
            SKPoint s = g.Start, t = g.End;
            if (g.Relative)
            {
                s = new SKPoint(bounds.Left + s.X * bounds.Width, bounds.Top + s.Y * bounds.Height);
                t = new SKPoint(bounds.Left + t.X * bounds.Width, bounds.Top + t.Y * bounds.Height);
            }
            var prev = e.Shader;
            e.Shader = SKShader.CreateLinearGradient(s, t, g.Colors, g.Positions, SKShaderTileMode.Clamp);
            e.Paint.Shader = e.Shader;
            prev?.Dispose(); // 录制时 SKPicture 已拷走 paint(含 shader 引用),这里只释放托管句柄
            return e.Paint;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPaint UseRect(PaintEntry e, SKRect bounds) => e.Gradient == null ? e.Paint : Use(e, bounds);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPaint UsePath(PaintEntry e, SKPath path) => e.Gradient == null ? e.Paint : Use(e, path.Bounds);

        private static SKRect Bounds(SKPoint a, SKPoint b)
            => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));

        /// <summary>
        /// 解析 WPF 路径标记。先剥掉 WPF 特有的填充规则前缀 F0 / F1(Skia 的 SVG 解析不认),
        /// F0 = EvenOdd、F1 = Nonzero;没有前缀时与 WPF Geometry.Parse 一样默认 EvenOdd。
        /// </summary>
        private SKPath? Svg(string svg)
        {
            if (_svg.TryGet(svg, out var p)) return p;
            var fillType = SKPathFillType.EvenOdd;
            string data = svg;
            int i = 0;
            while (i < data.Length && char.IsWhiteSpace(data[i])) i++;
            if (i < data.Length && (data[i] == 'F' || data[i] == 'f'))
            {
                int j = i + 1;
                while (j < data.Length && char.IsWhiteSpace(data[j])) j++;
                if (j < data.Length && (data[j] == '0' || data[j] == '1'))
                {
                    fillType = data[j] == '1' ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
                    data = data.Substring(j + 1);
                }
            }
            p = SKPath.ParseSvgPathData(data);
            if (p != null) p.FillType = fillType;
            _svg.Add(svg, p);
            return p;
        }

        private SKFont Font(IHevoTypeface face, float size)
        {
            if (_fonts.TryGet((face, size), out var f)) return f;
            f = new SKFont(Typeface(face), size) { Edging = SKFontEdging.Antialias, Subpixel = true };
            _fonts.Add((face, size), f);
            return f;
        }

        private SKTypeface Typeface(IHevoTypeface face)
        {
            // SkiaSharp 按原生句柄复用托管对象:另一个后端实例清缓存时可能已 Dispose 了同一个 SKTypeface,句柄为 0 就重建
            if (_typefaces.TryGetValue(face, out var tf) && tf.Handle != IntPtr.Zero) return tf;
            string family = "Arial"; // 与 WpfDrawingRenderer 的默认字体一致
            int weight = 400;
            bool italic = false;
            if (face is HevoTypeface ht) { family = ht.FontFamily; weight = ht.FontWeight; italic = ht.IsItalic; }
            else if (face is HevoResourceTypeface rt)
            {
                if (_resources?.ResolveFontFamily(rt.ResourceKey) is { } resolved) family = resolved;
                weight = rt.FontWeight; italic = rt.IsItalic;
            }
            tf = SKTypeface.FromFamilyName(family, (SKFontStyleWeight)weight, SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default;
            _typefaces[face] = tf;
            return tf;
        }

        private SKImage? Image(object src)
        {
            if (_images.TryGetValue(src, out var img)) return img;
            img = _resources?.CreateImage(src);
            if (img != null) _images.Add(src, img);
            return img;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKRect R(HevoRect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKColor C(HevoColor c) => new(c.R, c.G, c.B, c.A);

        private static SKPoint P(HevoPoint p) => new(p.X, p.Y);

        private void ClearText()
        {
            foreach (var e in _text.Values) e.Blob?.Dispose();
            _text.Clear();
        }

        // 主字体与回退字体可能是同一个托管 SKTypeface(SkiaSharp 按句柄复用),去重后各释放一次;
        // SKTypeface.Default 是共享单例,不释放
        private void DisposeTypefaces(bool clearPrimary)
        {
            var set = new HashSet<SKTypeface>(ReferenceEqualityComparer.Instance);
            foreach (var tf in _fallback.Values) if (tf != null) set.Add(tf);
            _fallback.Clear();
            if (clearPrimary)
            {
                foreach (var tf in _typefaces.Values) set.Add(tf);
                _typefaces.Clear();
            }
            else
            {
                foreach (var tf in _typefaces.Values) set.Remove(tf); // 主字体还在用,只放掉纯回退的
            }
            var def = SKTypeface.Default;
            foreach (var tf in set)
                if (!ReferenceEquals(tf, def)) tf.Dispose();
        }

        /// <summary>主题切换后调用:资源笔刷 / 资源字体按新主题重新解析(已录制的 picture 需要图层重录才会变色)。</summary>
        public void ClearResourceCaches()
        {
            _lastFillDesc = null; _lastFill = null; _lastStrokeDesc = null; _lastStroke = null;
            _fill.Clear();
            _stroke.Clear();
            _fonts.Clear();
            ClearText();
            DisposeTypefaces(clearPrimary: true);
        }

        public void Dispose()
        {
            ClearResourceCaches();
            _svg.Clear();
            foreach (var kv in _images) kv.Value.Dispose();
            _images.Clear();
            for (int i = 0; i < _opacityPaints.Length; i++) { _opacityPaints[i]?.Dispose(); _opacityPaints[i] = null; }
            _path.Dispose();
        }
    }
}
