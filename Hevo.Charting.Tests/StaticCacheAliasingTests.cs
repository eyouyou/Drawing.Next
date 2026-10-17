using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 全图抗锯齿 + 横竖内容按设备像素对齐:开缓存与关缓存截图逐像素一致,横竖线 / 矩形在任意 DPI 都是实心像素,
    /// 斜线是平滑的。祖先摆在小数位置时(Grid 星号行列、GridSplitter、125% DPI)同样成立,静态层与交互层都是。
    /// BitmapCache 只在元素真正进合成树时生效,所以图表要挂进窗口再截图;不挂窗口的截图照不出这个问题。
    /// 截图截的是窗口内容(cell 外面那层 Canvas),这样 cell 的原点偏移会算进来。
    /// 祖先小数偏移靠画布给图层设的 snapping guideline(矢量)和 ChartCell 维护的位图原点平移(位图模式)消化,只作用于渲染。
    /// </summary>
    public sealed class StaticCacheAliasingTests
    {
        private readonly ITestOutputHelper _out;
        public StaticCacheAliasingTests(ITestOutputHelper output) => _out = output;

        private const int W = 240, H = 160;
        // cell 外留一圈,够放下测试用的祖先偏移
        private const int Margin = 40;

        private sealed class ScriptLayer : ChartLayer
        {
            private readonly Action<IDrawingSink> _draw;
            public ScriptLayer(Action<IDrawingSink> draw) { _draw = draw; Level = ChartLayerType.Main; Name = "script"; }
            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink) => _draw(drawSink);
        }

        private sealed class NoData : IVisualData
        {
            public static readonly NoData Instance = new();
            public T? Get<T>() where T : class => null;
            public void Publish<T>(T snapshot) where T : class { }
        }

        private static readonly HevoSolidBrush RedBrush = new(Colors.Red);
        private static readonly HevoSolidBrush GreenBrush = new(Color.FromRgb(0, 160, 0));
        private static readonly HevoPen GridPen = new(new HevoSolidBrush(Color.FromRgb(90, 90, 90)), 1);
        private static readonly HevoPen WickPen = new(new HevoSolidBrush(Colors.Black), 1);

        // 小数坐标的网格 + K 线(实体宽度不是整数),不对齐时最容易糊
        private static void Scene(IDrawingSink d)
        {
            for (int i = 1; i < 4; i++)
            {
                d.DrawLine(GridPen, new HevoPoint(10, 30 * i + 0.3f), new HevoPoint(230, 30 * i + 0.3f));
                d.DrawLine(GridPen, new HevoPoint(55.6f * i, 5), new HevoPoint(55.6f * i, 150));
            }
            var ups = new List<HevoRect>();
            var downs = new List<HevoRect>();
            var wicks = new List<HevoPoint>();
            for (int i = 0; i < 30; i++)
            {
                float x = 12.3f + i * 7.1f;
                float top = 40 + (i * 37 % 50), h = 6 + (i * 13 % 30);
                (i % 3 == 0 ? downs : ups).Add(new HevoRect(x, top, 4.6f, h));
                wicks.Add(new HevoPoint(x + 2.3f, top - 8));
                wicks.Add(new HevoPoint(x + 2.3f, top + h + 8));
            }
            d.DrawLineSegments(WickPen, wicks);
            d.DrawRectangles(RedBrush, null, ups);
            d.DrawRectangles(GreenBrush, null, downs);
        }

        // 带斜线:缓存按抗锯齿栅格化,关缓存的画面要是走了锯齿模式(EdgeMode.Aliased),斜线的边缘就会不同
        private static void MixedScene(IDrawingSink d)
        {
            Scene(d);
            d.DrawPolyline(WickPen, new List<HevoPoint> { new(10, 140), new(60, 40), new(120, 120), new(200, 30) });
        }

        // 十字光标一类的交互层内容:小数坐标的横竖线
        private sealed class OverlayLayer : ChartLayer
        {
            public OverlayLayer() { Level = ChartLayerType.Interaction; Name = "overlay"; }
            protected override void OnUpdate(IVisualData data, IDrawingSink d, WidgetBuffer widgetSink)
            {
                d.DrawLine(WickPen, new HevoPoint(5, 77.7f), new HevoPoint(235, 77.7f));
                d.DrawLine(WickPen, new HevoPoint(100, 5), new HevoPoint(100, 155));
                d.DrawRectangle(RedBrush, null, new HevoRect(180.3f, 60.6f, 12.5f, 9.2f));
            }
        }

        private static ChartCell NewCell(StaticCachePolicy policy)
        {
            return new ChartCell
            {
                Template = new ChartTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)) },
                StaticCachePolicy = policy,
                Width = W,
                Height = H,
            };
        }

        // cell 包在 Border 里、Border 摆在 (x, y):Border 不开 UseLayoutRounding,小数偏移原样传给 cell
        private static Window Show(ChartCell cell, double x = 0, double y = 0)
        {
            var border = new System.Windows.Controls.Border { Child = cell };
            System.Windows.Controls.Canvas.SetLeft(border, x);
            System.Windows.Controls.Canvas.SetTop(border, y);
            var root = new System.Windows.Controls.Canvas { Width = W + Margin, Height = H + Margin };
            root.Children.Add(border);
            var win = new Window
            {
                Content = root,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -10000,
                Top = 0,
            };
            win.Show();
            win.UpdateLayout();
            return win;
        }

        private static void Record(ChartCell cell, ChartLayer layer)
        {
            layer.MarkDirty();
            layer.Update(NoData.Instance);
            cell.Invalidate();
        }

        private static uint[] Capture(Window win, double k = 1.0)
        {
            // 让渲染线程把缓存位图真正生成出来再截
            win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            int cw = Stride(k), ch = (int)Math.Ceiling((H + Margin) * k);
            var rtb = new RenderTargetBitmap(cw, ch, 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render((Visual)win.Content);
            var px = new int[cw * ch];
            rtb.CopyPixels(px, cw * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        private static int Stride(double k) => (int)Math.Ceiling((W + Margin) * k);

        private static int CountDiff(uint[] a, uint[] b, out int covered)
        {
            int diff = 0;
            covered = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != 0 || b[i] != 0) covered++;
                if (a[i] != b[i]) diff++;
            }
            return diff;
        }

        // 模拟 125% / 150% 屏幕:cell 按这个 DPI 录制(像素对齐、画笔取整都按它),截图也按它。
        // 只能配关缓存:RenderTargetBitmap 按 96 DPI 生成缓存位图再放大,真实屏幕上缓存按设备比例生成,没有这个问题
        private static void SimulateDpi(ChartCell cell, double k)
        {
            if (k == 1.0) return;
            typeof(ChartCell).GetMethod("OnDpiChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(cell, new object[] { new DpiScale(1, 1), new DpiScale(k, k) });
        }

        private static uint[] Render(StaticCachePolicy policy, Action<IDrawingSink> scene, double x = 0, double y = 0,
            bool overlay = false, RenderMode mode = RenderMode.Software, double k = 1.0)
        {
            var cell = NewCell(policy);
            var win = Show(cell, x, y);
            try
            {
                SimulateDpi(cell, k);
                var layer = new ScriptLayer(scene) { Mode = mode };
                cell.AddUnmanagedLayer(layer);
                if (overlay)
                {
                    var o = new OverlayLayer { Mode = mode };
                    cell.AddUnmanagedLayer(o);
                    o.MarkDirty();
                    o.Update(NoData.Instance);
                }
                Record(cell, layer);
                return Capture(win, k);
            }
            finally { win.Close(); }
        }

        // 默认挂 BitmapCache:多图同屏时十字光标只动交互层,静态层贴缓存位图(实测 4 图十字光标 33 → 17 ms)
        [Fact]
        public void DefaultPolicy_IsOn_WithBitmapCache() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = new ChartCell
            {
                Template = new ChartTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)) },
                Width = W,
                Height = H,
            };
            var win = Show(cell);
            try
            {
                Assert.Equal(StaticCachePolicy.On, cell.StaticCachePolicy);
                Assert.IsType<BitmapCache>(cell.DrawingCanvas.CacheMode);
                cell.StaticCachePolicy = StaticCachePolicy.Off;
                Assert.Null(cell.DrawingCanvas.CacheMode);
            }
            finally { win.Close(); }
        });

        [Fact]
        public void CacheOn_MatchesCacheOff() => IncrementalTestKit.RunOnSta(() =>
        {
            var off = Render(StaticCachePolicy.Off, MixedScene);
            var on = Render(StaticCachePolicy.On, MixedScene);
            int diff = CountDiff(off, on, out int covered);
            _out.WriteLine($"开缓存 vs 关缓存:覆盖 {covered} 像素,不同 {diff}");
            Assert.True(covered > 2000, "场景没画出来");
            Assert.True(Coverage(off).Partial > 50, "场景里的斜线应有抗锯齿边缘");
            Assert.Equal(0, diff);
        });

        // 祖先在小数位置:静态层(开 / 关缓存)和交互层的横竖内容仍然只有实心像素,矢量和位图模式都是
        [Theory]
        [InlineData(20.4, 20.4, StaticCachePolicy.Off, RenderMode.Software)]
        [InlineData(20.4, 20.4, StaticCachePolicy.On, RenderMode.Software)]
        [InlineData(7.5, 13.75, StaticCachePolicy.Off, RenderMode.Software)]
        [InlineData(7.5, 13.75, StaticCachePolicy.On, RenderMode.Software)]
        [InlineData(20.6, 20.6, StaticCachePolicy.Off, RenderMode.Software)]
        [InlineData(20.4, 20.4, StaticCachePolicy.Off, RenderMode.Bitmap)]
        [InlineData(20.4, 20.4, StaticCachePolicy.On, RenderMode.Bitmap)]
        [InlineData(7.5, 13.75, StaticCachePolicy.Off, RenderMode.Bitmap)]
        [InlineData(20.6, 20.6, StaticCachePolicy.Off, RenderMode.Bitmap)]
        public void FractionalAncestorOffset_AxisAlignedContent_IsCrisp(double x, double y, StaticCachePolicy policy, RenderMode mode) => IncrementalTestKit.RunOnSta(() =>
        {
            var (solid, partial) = Coverage(Render(policy, Scene, x, y, overlay: true, mode: mode));
            _out.WriteLine($"偏移 ({x}, {y}) {policy} {mode}:实心 {solid},半透明 {partial}");
            Assert.True(solid > 2000, "场景没画出来");
            Assert.Equal(0, partial);
        });

        // 125% / 150%:整数 DIP 偏移在设备像素上也是小数
        [Theory]
        [InlineData(1.25, 21.0, RenderMode.Software)]
        [InlineData(1.25, 20.4, RenderMode.Software)]
        [InlineData(1.5, 21.0, RenderMode.Software)]
        [InlineData(1.25, 21.0, RenderMode.Bitmap)]
        [InlineData(1.5, 20.4, RenderMode.Bitmap)]
        public void FractionalAncestorOffset_IsCrisp_AtHighDpi(double k, double offset, RenderMode mode) => IncrementalTestKit.RunOnSta(() =>
        {
            var (solid, partial) = Coverage(Render(StaticCachePolicy.Off, Scene, offset, offset, overlay: true, mode: mode, k: k));
            _out.WriteLine($"k={k} 偏移 {offset} {mode}:实心 {solid},半透明 {partial}");
            Assert.True(solid > 2000, "场景没画出来");
            Assert.Equal(0, partial);
        });

        [Fact]
        public void FractionalAncestorOffset_CacheOn_MatchesCacheOff() => IncrementalTestKit.RunOnSta(() =>
        {
            var off = Render(StaticCachePolicy.Off, MixedScene, 20.4, 20.4, overlay: true);
            var on = Render(StaticCachePolicy.On, MixedScene, 20.4, 20.4, overlay: true);
            int diff = CountDiff(off, on, out int covered);
            _out.WriteLine($"偏移 (20.4, 20.4) 开缓存 vs 关缓存:覆盖 {covered} 像素,不同 {diff}");
            Assert.True(covered > 2000, "场景没画出来");
            Assert.Equal(0, diff);
        });

        // 位图图层跟矢量图层贴到同一个像素原点上(半像素处取舍也一致),同一画面切模式不会挪一格
        [Theory]
        [InlineData(20.4)]
        [InlineData(20.6)]
        [InlineData(7.5)]
        public void FractionalAncestorOffset_BitmapLandsOnSamePixelsAsSoftware(double offset) => IncrementalTestKit.RunOnSta(() =>
        {
            var sw = Render(StaticCachePolicy.Off, Scene, offset, offset, overlay: true, mode: RenderMode.Software);
            var bmp = Render(StaticCachePolicy.Off, Scene, offset, offset, overlay: true, mode: RenderMode.Bitmap);
            int diff = CountDiff(sw, bmp, out int covered);
            _out.WriteLine($"偏移 {offset} 矢量 vs 位图:覆盖 {covered},不同 {diff}");
            Assert.True(covered > 2000, "场景没画出来");
            Assert.Equal(0, diff);
        });

        // 父布局变化(GridSplitter 拖动一类)后不重录图层,画面仍然锐利
        [Theory]
        [InlineData(RenderMode.Software)]
        [InlineData(RenderMode.Bitmap)]
        public void FractionalAncestorOffset_FollowsLayoutChange(RenderMode mode) => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell(StaticCachePolicy.Off);
            var win = Show(cell, 20.4, 20.4);
            try
            {
                var layer = new ScriptLayer(Scene) { Mode = mode };
                cell.AddUnmanagedLayer(layer);
                Record(cell, layer);
                Assert.Equal(0, Coverage(Capture(win)).Partial);

                var border = (System.Windows.Controls.Border)cell.Parent;
                System.Windows.Controls.Canvas.SetLeft(border, 33.7);
                System.Windows.Controls.Canvas.SetTop(border, 9.15);
                win.UpdateLayout();
                var (solid, partial) = Coverage(Capture(win));
                _out.WriteLine($"{mode} 挪到 (33.7, 9.15) 后:实心 {solid},半透明 {partial}");
                Assert.True(solid > 2000, "场景没画出来");
                Assert.Equal(0, partial);
            }
            finally { win.Close(); }
        });

        // 对齐只作用于渲染:画布没有附加变换,鼠标换算(GetPosition(DrawingCanvas) 与 TranslatePoint 同一套变换)不变,
        // 像素中心落在画出来的那一列上(画面挪动 ≤ 半个设备像素)
        [Theory]
        [InlineData(20.4, RenderMode.Software)]
        [InlineData(20.6, RenderMode.Software)]
        [InlineData(20.4, RenderMode.Bitmap)]
        [InlineData(20.6, RenderMode.Bitmap)]
        public void FractionalAncestorOffset_PointerMapsToDrawnPixel(double offset, RenderMode mode) => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell(StaticCachePolicy.On);
            var win = Show(cell, offset, offset);
            try
            {
                var o = new OverlayLayer { Mode = mode };
                cell.AddUnmanagedLayer(o);
                Record(cell, o);
                var px = Capture(win);

                // 竖线画在画布 x = 100(1px 线宽,占画布里 [100, 101) 这一列)
                var hit = cell.DrawingCanvas.TranslatePoint(new Point(100.5, 120), (UIElement)win.Content);
                Assert.Equal(offset + 100.5, hit.X, 6);
                int col = (int)Math.Floor(hit.X), row = (int)Math.Floor(hit.Y), stride = Stride(1.0);
                _out.WriteLine($"{mode} 画布 (100.5, 120) → 窗口 {hit},该像素 0x{px[row * stride + col]:X8}");
                Assert.Equal(0xFF000000u, px[row * stride + col]);
                Assert.Equal(0u, px[row * stride + col - 1] >> 24);
                Assert.Equal(0u, px[row * stride + col + 1] >> 24);
            }
            finally { win.Close(); }
        });

        private static uint[] RenderAt(Action<IDrawingSink> scene, double k)
        {
            var layer = new ScriptLayer(scene);
            layer.MarkDirty();
            layer.Update(NoData.Instance);
            layer.SwapBuffer();
            var provider = new WpfRenderProvider();
            provider.UpdateDpi(k);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) ((LayerBuffer)layer.Buffer!).Execute(provider, dc);
            int pw = (int)Math.Ceiling(W * k), ph = (int)Math.Ceiling(H * k);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new int[pw * ph];
            rtb.CopyPixels(px, pw * 4, 0);
            var u = new uint[px.Length];
            Buffer.BlockCopy(px, 0, u, 0, px.Length * 4);
            return u;
        }

        private static (int Solid, int Partial) Coverage(uint[] px)
        {
            int solid = 0, partial = 0;
            foreach (var p in px)
            {
                uint a = p >> 24;
                if (a == 255) solid++;
                else if (a != 0) partial++;
            }
            return (solid, partial);
        }

        // 横竖线(整数线宽)、小数坐标的实体、矩形描边、裁剪框:在 100% / 125% / 150% 下都只有实心像素,没有半透明边
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void AxisAlignedContent_IsCrisp_AtAnyDpi(double k) => IncrementalTestKit.RunOnSta(() =>
        {
            var (solid, partial) = Coverage(RenderAt(d =>
            {
                using (d.PushClip(new HevoRect(3.3f, 2.6f, W - 7.1f, H - 5.4f)))
                {
                    Scene(d);
                    d.DrawRectangle(RedBrush, new HevoPen(new HevoSolidBrush(Colors.Blue), 2), new HevoRect(30.2f, 100.7f, 40.3f, 20.6f));
                }
            }, k));
            _out.WriteLine($"k={k}:实心 {solid},半透明 {partial}");
            Assert.True(solid > 2000, "场景没画出来");
            Assert.Equal(0, partial);
        });

        [Fact]
        public void DiagonalLines_AreAntialiased() => IncrementalTestKit.RunOnSta(() =>
        {
            var (_, partial) = Coverage(RenderAt(d => d.DrawPolyline(WickPen, new List<HevoPoint>
            {
                new(10, 140), new(60, 40), new(120, 120), new(200, 30),
            }), 1.0));
            Assert.True(partial > 50, $"斜线应有抗锯齿边缘,实际半透明像素 {partial}");
        });

        // 祖先 RenderTransform 动画停在小数位置:动画不触发布局,位图图层在下一次重画时自己重新对齐(矢量靠 guideline,一直对齐)
        [Theory]
        [InlineData(RenderMode.Software)]
        [InlineData(RenderMode.Bitmap)]
        public void FractionalAncestorAnimation_CrispAfterNextFrame(RenderMode mode) => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell(StaticCachePolicy.Off);
            var win = Show(cell);
            try
            {
                var layer = new ScriptLayer(Scene) { Mode = mode };
                var overlay = new OverlayLayer { Mode = mode };
                cell.AddUnmanagedLayer(layer);
                cell.AddUnmanagedLayer(overlay);
                overlay.MarkDirty();
                overlay.Update(NoData.Instance);
                Record(cell, layer);
                Assert.Equal(0, Coverage(Capture(win)).Partial);

                var shift = new TranslateTransform();
                ((System.Windows.Controls.Border)cell.Parent).RenderTransform = shift;
                win.UpdateLayout();
                var duration = new Duration(TimeSpan.FromMilliseconds(60));
                shift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(3.4, duration));
                shift.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(5.6, duration));
                var until = DateTime.UtcNow.AddSeconds(5);
                while ((shift.X != 3.4 || shift.Y != 5.6) && DateTime.UtcNow < until)
                    win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(3.4, shift.X);
                Assert.Equal(5.6, shift.Y);
                overlay.MarkDirty();
                overlay.Update(NoData.Instance);
                Record(cell, layer);
                var (solid, partial) = Coverage(Capture(win));
                _out.WriteLine($"{mode} 祖先动画停在 (3.4, 5.6) 后下一帧:实心 {solid},半透明 {partial}");
                Assert.True(solid > 2000, "场景没画出来");
                Assert.Equal(0, partial);
            }
            finally { win.Close(); }
        });

        // 位图模式 + 小数偏移:开缓存后静态层的位图平移要归零(缓存自己贴整像素),关缓存再恢复,都不用重挂
        [Theory]
        [InlineData(0.0, RenderMode.Software)]
        [InlineData(20.4, RenderMode.Software)]
        [InlineData(20.4, RenderMode.Bitmap)]
        public void TurningCacheOn_ReplaysLayersWithoutReRecording(double offset, RenderMode mode) => IncrementalTestKit.RunOnSta(() =>
        {
            var expected = Render(StaticCachePolicy.Off, MixedScene, offset, offset, mode: mode);

            var cell = NewCell(StaticCachePolicy.Off);
            var win = Show(cell, offset, offset);
            try
            {
                var layer = new ScriptLayer(MixedScene) { Mode = mode };
                cell.AddUnmanagedLayer(layer);
                Record(cell, layer);

                // 图层不脏,只切缓存:不用重录,缓存位图跟原来的画面一致
                cell.StaticCachePolicy = StaticCachePolicy.On;
                cell.Invalidate();
                int diff = CountDiff(expected, Capture(win), out int covered);
                _out.WriteLine($"偏移 {offset} {mode} 运行中开缓存:覆盖 {covered} 像素,不同 {diff}");
                Assert.True(covered > 2000, "场景没画出来");
                Assert.Equal(0, diff);

                cell.StaticCachePolicy = StaticCachePolicy.Off;
                cell.Invalidate();
                diff = CountDiff(expected, Capture(win), out _);
                _out.WriteLine($"偏移 {offset} {mode} 再关缓存:不同 {diff}");
                Assert.Equal(0, diff);
            }
            finally { win.Close(); }
        });
    }
}
