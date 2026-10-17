using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Hevo.Charting.Skia;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Skia 上屏后端(Hevo.Charting.Skia):
    /// ① 增量更新(只重录脏图层、只重画有变化的合成面)与从头渲染逐像素一致;只动交互层时静态面不重画;
    /// ② 图层移除 / 隐藏后原区域被擦干净;
    /// ③ 后端切换:挂上即上屏,切回 WPF 时 DrawingVisual 全量重放(挂 Skia 期间的新内容也要带回来);
    /// ④ 后端归 cell 所有:置空 / 替换 / Shutdown 时被释放,一个实例不能挂两个 cell;InvalidateResources 后合成面不再引用已释放的 picture;
    /// ⑤ 画面口径:线端 / 虚线与位图后端覆盖一致,非 100% DPI 下 1px 线只占 1 个设备像素,缺字形时字体回退。
    /// 截图走 RenderTargetBitmap(CPU 合成面是 1:1 位图,结果确定)。每个测试创建的 cell(连同挂着的后端)都在结尾 Shutdown 释放。
    /// </summary>
    public sealed class SkiaBackendTests
    {
        private readonly ITestOutputHelper _out;
        public SkiaBackendTests(ITestOutputHelper output) => _out = output;

        private const int W = 240, H = 160;

        private sealed class ScriptLayer : ChartLayer
        {
            public Action<IDrawingSink> Draw;
            public ScriptLayer(Action<IDrawingSink> draw, ChartLayerType level = ChartLayerType.Main)
            {
                Draw = draw;
                Level = level;
                Name = "script";
            }
            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink) => Draw(drawSink);
        }

        private sealed class NoData : IVisualData
        {
            public static readonly NoData Instance = new();
            public T? Get<T>() where T : class => null;
            public void Publish<T>(T snapshot) where T : class { }
        }

        private sealed class Hidden : IVisualData
        {
            public static readonly Hidden Instance = new();
            private static readonly VisibilityTrait Off = new(false);
            public T? Get<T>() where T : class => typeof(T) == typeof(VisibilityTrait) ? (T)(object)Off : null;
            public void Publish<T>(T snapshot) where T : class { }
        }

        /// <summary>测试里建的 cell 统一登记,结尾 Shutdown(cell 负责 Detach + Dispose 挂着的后端)。</summary>
        private sealed class Cells : IDisposable
        {
            private readonly List<ChartCell> _cells = new();
            public ChartCell Add(ChartCell c) { _cells.Add(c); return c; }
            public void Dispose()
            {
                foreach (var c in _cells) c.Shutdown();
                _cells.Clear();
            }
        }

        private static ChartCell NewCell(Cells cells, IChartRenderBackend? backend, double dpi = 1.0)
        {
            var cell = new ChartCell
            {
                Template = new ChartTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)) },
            };
            cells.Add(cell);
            cell.RenderBackend = backend;
            cell.Measure(new Size(W, H));
            cell.Arrange(new Rect(0, 0, W, H));
            cell.UpdateLayout();
            // 不挂窗口时子元素默认取系统 DPI:可视树建好后再设根 DPI,往下传播,截图结果与机器缩放无关
            VisualTreeHelper.SetRootDpi(cell, new DpiScale(dpi, dpi));
            cell.UpdateLayout();
            return cell;
        }

        // 生产路径:Update → SwapBuffer → 后端 Present → PostRender
        private static void Record(ChartCell cell, ChartLayer layer, IVisualData? data = null)
        {
            layer.MarkDirty();
            layer.Update(data ?? NoData.Instance);
            cell.Invalidate();
        }

        private static uint[] Capture(UIElement v) => Capture(v, 1.0, out _, out _);

        private static uint[] Capture(UIElement v, double dpi, out int pw, out int ph)
        {
            // 合成面换位图时 InvalidateVisual,OnRender 要等一次布局才跑(生产里每帧合成前都会布局)
            v.UpdateLayout();
            pw = (int)Math.Round(W * dpi);
            ph = (int)Math.Round(H * dpi);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            rtb.Render(v);
            var px = new int[pw * ph];
            rtb.CopyPixels(px, pw * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        private static int CountDiff(uint[] a, uint[] b)
        {
            int d = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
            return d;
        }

        private static int Count(uint[] px) { int n = 0; foreach (var p in px) if (p != 0) n++; return n; }

        /// <summary>覆盖率 = Σ alpha / 255(抗锯齿边缘按比例计)。</summary>
        private static double Coverage(uint[] px) { double s = 0; foreach (var p in px) s += (p >> 24) / 255.0; return s; }

        private static readonly HevoSolidBrush Up = new(Colors.Red);
        private static readonly HevoSolidBrush Down = new(Color.FromRgb(0, 160, 0));
        private static readonly HevoPen Grid = new(new HevoSolidBrush(Color.FromRgb(90, 90, 90)), 1);
        private static readonly HevoPen Cross = new(new HevoSolidBrush(Colors.White), 1, new double[] { 3, 3 });
        private static readonly HevoPen Line = new(new HevoSolidBrush(Colors.Orange), 1.5);
        private static readonly IHevoTypeface Face = new HevoTypeface("Segoe UI");
        private static readonly HevoSolidBrush LabelBg = new(Colors.Black);

        // 静态层:网格 + 30 根 K 线 + 一条均线;lastClose 控制最后一根的实体高度(模拟 tick)
        private static Action<IDrawingSink> Candles(Func<float> lastBody) => d =>
        {
            for (int i = 1; i < 4; i++) d.DrawLine(Grid, new HevoPoint(0, 40 * i), new HevoPoint(W, 40 * i));
            var ups = new List<HevoRect>();
            var downs = new List<HevoRect>();
            var ma = new List<HevoPoint>();
            for (int i = 0; i < 30; i++)
            {
                float x = 5 + i * 7.5f;
                float top = 40 + (i * 37 % 50), h = i == 29 ? lastBody() : 6 + (i * 13 % 30);
                (i % 3 == 0 ? downs : ups).Add(new HevoRect(x, top, 5, h));
                ma.Add(new HevoPoint(x + 2.5f, top + h / 2));
            }
            d.DrawRectangles(Up, null, ups);
            d.DrawRectangles(Down, null, downs);
            d.DrawPolyline(Line, ma);
        };

        // 交互层:十字光标(两条虚线 + 价格标签)
        private static Action<IDrawingSink> Crosshair(Func<float> x) => d =>
        {
            float cx = x(), cy = 30 + cx * 0.4f;
            d.DrawLine(Cross, new HevoPoint(cx, 0), new HevoPoint(cx, H));
            d.DrawLine(Cross, new HevoPoint(0, cy), new HevoPoint(W, cy));
            d.DrawText(cx.ToString("F1"), Face, Up, 10, new HevoPoint(W - 2, cy),
                TextAlignX.Right, TextAlignY.Center, LabelBg, null, 2, 1);
        };

        // 一张图:静态 K 线层 + 交互十字光标层,两层都按当前 cross / body 录一次
        private static ChartCell Build(Cells cells, Func<float> cross, Func<float> body, out ScriptLayer candles, out ScriptLayer crosshair)
        {
            var cell = NewCell(cells, new SkiaRenderBackend());
            candles = new ScriptLayer(Candles(body));
            crosshair = new ScriptLayer(Crosshair(cross), ChartLayerType.Interaction);
            cell.AddUnmanagedLayer(candles);
            cell.AddUnmanagedLayer(crosshair);
            Record(cell, candles);
            Record(cell, crosshair);
            return cell;
        }

        /// <summary>单层画一遍并截图。backend = null 时走 WPF;bitmap = true 时 WPF 路径用位图后端(RenderMode.Bitmap)。</summary>
        private static uint[] RenderOnce(Cells cells, Action<IDrawingSink> draw, IChartRenderBackend? backend, bool bitmap = false)
        {
            var cell = NewCell(cells, backend);
            if (bitmap) cell.RenderModeOverride = RenderMode.Bitmap;
            var layer = new ScriptLayer(draw);
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            return Capture(cell);
        }

        [Fact]
        public void IncrementalUpdates_MatchFreshRender_PixelForPixel() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            float cross = 20, body = 10;
            var live = Build(cells, () => cross, () => body, out var candles, out var crosshair);
            Assert.True(Count(Capture(live)) > 3000, "场景没画出来");

            for (int i = 0; i < 40; i++)
            {
                // 十字光标横扫(每步 5px)+ 每 4 步最后一根跳一次;每步都跟同状态下新建的图从头画的结果比
                cross = 20 + (i * 5) % 200;
                Record(live, crosshair);
                if (i % 4 == 0)
                {
                    body = 5 + (i * 7) % 40;
                    Record(live, candles);
                }
                float c = cross, b = body;
                using var freshCells = new Cells();
                var fresh = Build(freshCells, () => c, () => b, out _, out _);
                int diff = CountDiff(Capture(live), Capture(fresh));
                Assert.True(diff == 0, $"第 {i} 步增量结果与从头渲染差 {diff} 像素");
            }
        });

        [Fact]
        public void OverlayChange_RepaintsOnlyOverlaySurface() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            float cross = 20;
            var cell = Build(cells, () => cross, () => 10, out _, out var crosshair);
            var backend = (SkiaRenderBackend)cell.RenderBackend!;

            cross = 60;
            Record(cell, crosshair);
            _out.WriteLine($"十字光标移动:重录 {backend.LastRecordedLayers} 层,重画 {backend.LastRepaintedSurfaces} 块合成面");
            Assert.Equal(1, backend.LastRecordedLayers);
            Assert.Equal(1, backend.LastRepaintedSurfaces);

            // 没有图层变脏的帧:不重录、不重画
            cell.Invalidate();
            Assert.Equal(0, backend.LastRecordedLayers);
            Assert.Equal(0, backend.LastRepaintedSurfaces);
        });

        [Fact]
        public void RemovedLayer_IsErased() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            var cell = NewCell(cells, new SkiaRenderBackend());
            var layer = new ScriptLayer(Candles(() => 10));
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            Assert.True(Count(Capture(cell)) > 1000);

            cell.RemoveUnmanagedLayer(layer);
            Assert.Equal(0, Count(Capture(cell)));
        });

        [Fact]
        public void HiddenLayer_IsErased() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            var cell = NewCell(cells, new SkiaRenderBackend());
            var layer = new ScriptLayer(Candles(() => 10));
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            Assert.True(Count(Capture(cell)) > 1000);

            Record(cell, layer, Hidden.Instance);
            Assert.Equal(0, Count(Capture(cell)));
        });

        [Fact]
        public void SwitchBackends_RepresentsWithoutReRecording() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            int recordings = 0;
            float body = 10;
            var cell = NewCell(cells, null);
            var layer = new ScriptLayer(Candles(() => 10));
            layer.Draw = d => { recordings++; Candles(() => body)(d); };
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            var before = Capture(cell);
            Assert.True(Count(before) > 1000);

            // 挂 Skia:不重录图层,下一帧直接用前台指令上屏
            cell.RenderBackend = new SkiaRenderBackend();
            cell.UpdateLayout();
            cell.Invalidate();
            Assert.True(Count(Capture(cell)) > 1000, "挂上 Skia 后没有上屏");
            Assert.Equal(1, recordings);

            // 挂着 Skia 期间图层内容变了(WPF 的 DrawingVisual 里还是旧内容)
            body = 45;
            Record(cell, layer);
            Assert.Equal(2, recordings);

            // 切回 WPF:要按前台指令全量重放,画面必须是新内容,不能是挂 Skia 之前留下的旧画面
            cell.RenderBackend = null;
            cell.UpdateLayout();
            cell.Invalidate();
            var after = Capture(cell);
            Assert.Equal(2, recordings);

            var expected = RenderOnce(cells, Candles(() => 45), null);
            Assert.True(CountDiff(before, expected) > 0, "新旧内容应该不同,否则测不出来");
            Assert.Equal(0, CountDiff(after, expected));
        });

        [Fact]
        public void InvalidateResources_ThenResize_DoesNotTouchReleasedPictures() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            var backend = new SkiaRenderBackend();
            var cell = NewCell(cells, backend);
            var layer = new ScriptLayer(Candles(() => 10));
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            Assert.True(Count(Capture(cell)) > 1000);

            // picture 全部释放后立刻改尺寸:合成面 OnRenderSizeChanged 会重画,不能回放已释放的 picture
            backend.InvalidateResources();
            cell.Measure(new Size(W + 40, H + 30));
            cell.Arrange(new Rect(0, 0, W + 40, H + 30));
            cell.UpdateLayout();
            Capture(cell);

            // 下一帧全部重录,画面恢复
            cell.Invalidate();
            Assert.True(backend.LastRecordedLayers >= 1);
            Assert.True(Count(Capture(cell)) > 1000, "InvalidateResources 之后没有重录上屏");
        });

        [Fact]
        public void Backend_IsOwnedAndDisposedByCell() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();

            // 置空:旧后端被 Detach + Dispose
            var b1 = new SkiaRenderBackend();
            var cell = NewCell(cells, b1);
            cell.RenderBackend = null;
            Assert.True(b1.IsDisposed);

            // 替换:旧的被释放,新的挂上
            var b2 = new SkiaRenderBackend();
            var b3 = new SkiaRenderBackend();
            cell.RenderBackend = b2;
            cell.RenderBackend = b3;
            Assert.True(b2.IsDisposed);
            Assert.False(b3.IsDisposed);

            // 同一个实例挂第二个 cell:抛异常,两边都不受影响
            var other = NewCell(cells, null);
            Assert.Throws<InvalidOperationException>(() => other.RenderBackend = b3);
            Assert.Null(other.RenderBackend);
            Assert.Same(b3, cell.RenderBackend);
            Assert.False(b3.IsDisposed);

            // Shutdown:后端被释放;重复释放无害
            cell.Shutdown();
            Assert.True(b3.IsDisposed);
            Assert.Null(cell.RenderBackend);
            b3.Dispose();

            // 已释放的实例不能再挂
            Assert.Throws<ObjectDisposedException>(() => other.RenderBackend = b1);
        });

        [Fact]
        public void LineCapsAndDashes_MatchBitmapBackendCoverage() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            var brush = new HevoSolidBrush(Colors.White);
            // 容差:偶数线宽时段端落在整像素,两边应基本一致;奇数线宽时 Square 段端外扩 1.5px 落在半像素上,
            // 位图后端按像素中心取整(每段少算约 1 列),Skia 按面积抗锯齿,覆盖率会差几个百分点
            var cases = new (string Name, HevoPen Pen, double Tolerance)[]
            {
                ("Flat", new HevoPen(brush, 6, null, 0), 0.02),
                ("Square", new HevoPen(brush, 6, null, 1), 0.02),
                ("Round", new HevoPen(brush, 6, null, 2), 0.02),
                ("Dash 2,2 @2px", new HevoPen(brush, 2, new double[] { 2, 2 }), 0.02),
                ("Dash 4,1 @2px", new HevoPen(brush, 2, new double[] { 4, 1 }), 0.02),
                ("Dash 3,1 @2px(段端盖满 = 实线)", new HevoPen(brush, 2, new double[] { 3, 1 }), 0.02),
                ("Dash 1,3 @3px(段端在半像素)", new HevoPen(brush, 3, new double[] { 1, 3 }), 0.08),
            };
            foreach (var (name, pen, tolerance) in cases)
            {
                Action<IDrawingSink> draw = d =>
                {
                    d.DrawLine(pen, new HevoPoint(30, 40), new HevoPoint(200, 40));   // 水平
                    d.DrawLine(pen, new HevoPoint(120, 60), new HevoPoint(120, 150)); // 垂直(与上一条同 pen:走合批路径)
                };
                var skiaPx = RenderOnce(cells, draw, new SkiaRenderBackend());
                var bmpPx = RenderOnce(cells, draw, null, bitmap: true);
                double skia = Coverage(skiaPx), bmp = Coverage(bmpPx);
                double err = Math.Abs(skia - bmp) / bmp;
                _out.WriteLine($"{name}: skia {skia:F1} / bitmap {bmp:F1} px,误差 {err:P2}");
                Action<IDrawingSink> hOnly = d => d.DrawLine(pen, new HevoPoint(30, 40), new HevoPoint(200, 40));
                Action<IDrawingSink> vOnly = d => d.DrawLine(pen, new HevoPoint(120, 60), new HevoPoint(120, 150));
                Assert.True(bmp > 100, $"{name}:位图后端没画出来");
                Assert.True(err < tolerance, $"{name}:Skia 覆盖 {skia:F1} 与位图后端 {bmp:F1} 差 {err:P2}");
                // 平头 / 方头的水平垂直线没有抗锯齿边缘,应逐像素相同(线端映射错了这里会直接失败)
                if (pen.LineCap is 0 or 1 && pen.DashArray == null) Assert.Equal(0, CountDiff(skiaPx, bmpPx));
            }

            // Triangle(3)Skia 没有,退化为平头
            Action<IDrawingSink> tri = d => d.DrawLine(new HevoPen(brush, 6, null, 3), new HevoPoint(30, 40), new HevoPoint(200, 40));
            Action<IDrawingSink> flat = d => d.DrawLine(new HevoPen(brush, 6, null, 0), new HevoPoint(30, 40), new HevoPoint(200, 40));
            Assert.Equal(0, CountDiff(RenderOnce(cells, tri, new SkiaRenderBackend()), RenderOnce(cells, flat, new SkiaRenderBackend())));
        });

        // 小数坐标的填充矩形(K 线实体)与裁剪框边缘全是实心像素,跟 WPF 后端一致(Skia 填充 / 裁剪不开抗锯齿,按像素中心取舍)
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void FractionalFillRects_HaveNoSemiTransparentEdges(double dpi) => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            var cell = NewCell(cells, new SkiaRenderBackend(), dpi);
            var layer = new ScriptLayer(d =>
            {
                using (d.PushClip(new HevoRect(3.3f, 2.6f, W - 7.1f, H - 5.4f)))
                {
                    var rects = new List<HevoRect>();
                    for (int i = 0; i < 30; i++) rects.Add(new HevoRect(1.3f + i * 7.1f, 20.4f + i % 7, 4.6f, 30.7f));
                    d.DrawRectangles(Up, null, rects);
                    d.DrawRectangle(Down, null, new HevoRect(0.4f, 100.2f, W, 20.5f)); // 两侧被裁剪框截断
                }
            });
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            var px = Capture(cell, dpi, out _, out _);
            int solid = 0, partial = 0;
            foreach (var p in px)
            {
                uint a = p >> 24;
                if (a == 255) solid++;
                else if (a != 0) partial++;
            }
            _out.WriteLine($"DPI {dpi}:实心 {solid},半透明 {partial}");
            Assert.True(solid > 2000, "场景没画出来");
            Assert.Equal(0, partial);
        });

        [Theory]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void OnePixelLines_CoverOneDevicePixel_AtFractionalDpi(double dpi) => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            var pen = new HevoPen(new HevoSolidBrush(Colors.White), 1);
            var cell = NewCell(cells, new SkiaRenderBackend(), dpi);
            Assert.Equal(dpi, VisualTreeHelper.GetDpi(cell).PixelsPerDip);
            var layer = new ScriptLayer(d =>
            {
                d.DrawLine(pen, new HevoPoint(10, 40.3f), new HevoPoint(230, 40.3f));   // 横线
                d.DrawLine(pen, new HevoPoint(100.6f, 60), new HevoPoint(100.6f, 150)); // 竖线
            });
            cell.AddUnmanagedLayer(layer);
            Record(cell, layer);
            var px = Capture(cell, dpi, out int pw, out int ph);

            // 横线:离竖线远的一列,只有 1 个设备像素有墨,而且是满覆盖
            int col = (int)(20 * dpi);
            var rows = new List<int>();
            for (int y = 0; y < ph; y++) if (px[y * pw + col] != 0) rows.Add(y);
            _out.WriteLine($"DPI {dpi}:横线占行 [{string.Join(",", rows)}]");
            Assert.Single(rows);
            Assert.Equal(255u, px[rows[0] * pw + col] >> 24);

            // 竖线:离横线远的一行
            int row = (int)(140 * dpi);
            var cols = new List<int>();
            for (int x = 0; x < pw; x++) if (px[row * pw + x] != 0) cols.Add(x);
            _out.WriteLine($"DPI {dpi}:竖线占列 [{string.Join(",", cols)}]");
            Assert.Single(cols);
            Assert.Equal(255u, px[row * pw + cols[0]] >> 24);
        });

        [Fact]
        public void ChineseText_FallsBackInsteadOfTofu() => IncrementalTestKit.RunOnSta(() =>
        {
            using var cells = new Cells();
            const string text = "中文字体回退测试";
            const float size = 20;
            var red = new HevoSolidBrush(Colors.Red);
            Action<IDrawingSink> Draw(IHevoTypeface face) => d =>
                d.DrawText(text, face, red, size, new HevoPoint(10, 40), TextAlignX.Left, TextAlignY.Top, null, null, 0, 0);

            // Arial 没有中文字形:要靠回退字体画出来
            int fallback = Count(RenderOnce(cells, Draw(new HevoTypeface("Arial")), new SkiaRenderBackend()));
            // 参照 1:直接用中文字体
            int cjk = Count(RenderOnce(cells, Draw(new HevoTypeface("Microsoft YaHei")), new SkiaRenderBackend()));
            // 参照 2:不做回退、直接用 Arial 排(全是 .notdef 豆腐块)
            int tofu;
            using (var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul))
            using (var canvas = new SKCanvas(bmp))
            using (var tf = SKTypeface.FromFamilyName("Arial"))
            using (var font = new SKFont(tf, size) { Edging = SKFontEdging.Antialias, Subpixel = true })
            using (var paint = new SKPaint { Color = SKColors.Red })
            using (var blob = SKTextBlob.Create(text, font))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawText(blob, 10, 40 - font.Metrics.Ascent, paint);
                canvas.Flush();
                tofu = 0;
                foreach (var c in bmp.Pixels) if (c.Alpha != 0) tofu++;
            }

            _out.WriteLine($"墨量:回退 {fallback} / 中文字体 {cjk} / 豆腐块 {tofu}");
            Assert.True(fallback > 0, "中文没有画出来");
            Assert.True(Math.Abs(fallback - cjk) < Math.Abs(fallback - tofu), "回退结果更像豆腐块而不是中文字形");
            Assert.True(Math.Abs(fallback - cjk) < cjk * 0.35, $"回退墨量 {fallback} 与中文字体 {cjk} 相差过大");
        });
    }
}
