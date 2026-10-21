using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTK.Wpf;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Hevo.Charting.Skia
{
    /// <summary>
    /// 一块图层合成面(保留模式):持有本组图层当前的 SKPicture,收到重画请求时整面清空并按 Z 序回放。
    /// picture 由后端按设备像素录制,合成面 1:1 回放,不再缩放。
    /// </summary>
    internal interface ISkiaLayerSurface
    {
        FrameworkElement Element { get; }

        /// <summary>按 Z 序的本组 picture(null = 空图层),整面重画。返回 CPU 光栅耗时 ticks(GPU 面为 0)。</summary>
        long Redraw(IReadOnlyList<SKPicture?> pictures);

        /// <summary>丢掉对 picture 的引用但不重画(picture 马上要被释放;下一次 Present 会给新的)。</summary>
        void Forget();

        /// <summary>从 cell 摘下时释放位图 / GL 资源。</summary>
        void Release();
    }

    /// <summary>
    /// CPU 光栅面:SKSurface 直接包住 WriteableBitmap 的后台缓冲(零拷贝),重画后整面 AddDirtyRect 交给 WPF。
    /// </summary>
    internal sealed class SkiaCpuSurface : FrameworkElement, ISkiaLayerSurface
    {
        private WriteableBitmap? _bitmap;
        private SKSurface? _surface;
        private int _pw, _ph;
        private double _sx = 1, _sy = 1;
        private IReadOnlyList<SKPicture?> _pictures = Array.Empty<SKPicture?>();

        public FrameworkElement Element => this;

        public SkiaCpuSurface()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        public long Redraw(IReadOnlyList<SKPicture?> pictures)
        {
            long t0 = Stopwatch.GetTimestamp();
            _pictures = pictures;
            EnsureSurface();
            if (_surface == null || _bitmap == null) return 0; // 还没布局:尺寸到位时 OnRenderSizeChanged 会补画

            _bitmap.Lock();
            try
            {
                var canvas = _surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                var pics = _pictures;
                for (int i = 0; i < pics.Count; i++)
                    if (pics[i] is { } p) canvas.DrawPicture(p);
                canvas.Flush();
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _pw, _ph));
            }
            finally
            {
                _bitmap.Unlock();
            }
            return Stopwatch.GetTimestamp() - t0;
        }

        public void Forget() => _pictures = Array.Empty<SKPicture?>();

        /// <summary>按当前尺寸与 DPI 准备位图(尺寸 / DPI 变了才换)。</summary>
        private void EnsureSurface()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int pw = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
            int ph = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
            if (pw <= 0 || ph <= 0) return;
            if (_bitmap != null && pw == _pw && ph == _ph && dpi.DpiScaleX == _sx && dpi.DpiScaleY == _sy) return;

            _surface?.Dispose();
            _pw = pw; _ph = ph; _sx = dpi.DpiScaleX; _sy = dpi.DpiScaleY;
            _bitmap = new WriteableBitmap(pw, ph, 96 * _sx, 96 * _sy, PixelFormats.Pbgra32, null);
            _surface = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul),
                _bitmap.BackBuffer, _bitmap.BackBufferStride);
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_pictures.Count > 0) Redraw(_pictures);
        }

        // DPI 变化不在这里重画:picture 是按旧 DPI 的设备像素录的,后端在下一次 Present 发现 DPI 变了会全部重录再重画

        protected override void OnRender(DrawingContext dc)
        {
            // 位图内容更新走 Lock / AddDirtyRect,不需要重跑 OnRender;这里只在换位图时挂一次
            if (_bitmap != null) dc.DrawImage(_bitmap, new Rect(0, 0, _pw / _sx, _ph / _sy));
        }

        public void Release()
        {
            _surface?.Dispose();
            _surface = null;
            _bitmap = null;
            _pictures = Array.Empty<SKPicture?>();
        }
    }

    /// <summary>
    /// GPU 面:SkiaSharp.Views.WPF 的 SKGLElement(OpenGL → WPF D3DImage 互操作)。
    /// GL 帧缓冲每帧整面重画(GLWpfControl 不支持局部呈现),但合成的是已录好的 picture,GPU 上整面也很便宜。
    /// 后端只建一块 GPU 面,静态层与交互层按 Z 序画在同一块上(两块 GL 面叠放时上层不透明会盖住下层)。
    /// 绘制失败(GRContext 建不出来 / GL 调用抛异常)时通过 <see cref="Failed"/> 通知后端切回 CPU 面。
    /// </summary>
    internal sealed class SkiaGpuSurface : SKGLElement, ISkiaLayerSurface
    {
        private static readonly FieldInfo? s_settingsField =
            typeof(GLWpfControl).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);

        private IReadOnlyList<SKPicture?> _pictures = Array.Empty<SKPicture?>();
        private bool _failed;

        public FrameworkElement Element => this;

        /// <summary>最近一次 OnPaintSurface(GPU 合成 + 提交)的耗时 ticks。</summary>
        public long LastPaintTicks { get; private set; }

        /// <summary>绘制失败回调(已经派发回 Dispatcher,不在 WPF 渲染过程中)。只触发一次。</summary>
        public Action<SkiaGpuSurface>? Failed;

        private SkiaGpuSurface()
        {
            IsHitTestVisible = false;
        }

        /// <summary>
        /// 建 GPU 面并打开透明背景;做不到返回 null(调用方回退 CPU)。
        /// <para>
        /// SKGLElement 用默认设置 Start,GL 帧缓冲不透明(图表背景会变黑),要改 GLWpfControl 内部的设置。
        /// 在 GLWpfControl 4.2.3 里,构造时 Start 就已执行:_settings 是那时拷的副本,DxGlContext(GL 上下文)也随之创建;
        /// 但 TransparentBackground 要到 OnRender → SetupRenderSize 建帧缓冲时才读,所以构造之后改这份副本仍然生效。
        /// GRContext 则要到首次 paint 才创建,它建不出来只能在 paint 时发现(见 <see cref="OnPaintSurface"/>)。
        /// </para>
        /// </summary>
        public static SkiaGpuSurface? TryCreate()
        {
            if (s_settingsField == null)
            {
                Trace.TraceWarning("[SkiaRenderBackend] 找不到 GLWpfControl._settings(OpenTK.GLWpfControl 版本变了?),无法打开透明背景,回退 CPU。");
                return null;
            }
            var gpu = new SkiaGpuSurface();
            if (s_settingsField.GetValue(gpu) is not GLWpfControlSettings settings)
            {
                Trace.TraceWarning("[SkiaRenderBackend] GLWpfControl._settings 为空或类型不符,无法打开透明背景,回退 CPU。");
                gpu.Dispose();
                return null;
            }
            settings.TransparentBackground = true;
            return gpu;
        }

        public long Redraw(IReadOnlyList<SKPicture?> pictures)
        {
            _pictures = pictures;
            InvalidateVisual(); // 实际光栅在 WPF 渲染时的 OnPaintSurface 里,耗时不在这里
            return 0;
        }

        public void Forget() => _pictures = Array.Empty<SKPicture?>();

        public void Release()
        {
            Failed = null;
            _pictures = Array.Empty<SKPicture?>();
            Dispose();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_failed) return;
            // GRContext 建不出来时 SKGLElement 在调 OnPaintSurface 之前就会抛(拿空 GRContext 建渲染目标),只能在这一层接住
            try { base.OnRender(dc); }
            catch (Exception ex) { Fail(ex); }
        }

        protected override void OnPaintSurface(SKPaintGLSurfaceEventArgs e)
        {
            if (_failed) return;
            if (GRContext == null) { Fail(null); return; }
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                base.OnPaintSurface(e);
                var canvas = e.Surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                var pics = _pictures;
                for (int i = 0; i < pics.Count; i++)
                    if (pics[i] is { } p) canvas.DrawPicture(p);
                canvas.Flush();
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }
            LastPaintTicks = Stopwatch.GetTimestamp() - t0;
        }

        private void Fail(Exception? ex)
        {
            if (_failed) return;
            _failed = true;
            Trace.TraceWarning($"[SkiaRenderBackend] GPU 合成面绘制失败,切回 CPU:{ex?.Message ?? "GRContext 创建失败"}");
            // 正在 WPF 渲染过程里,不能改可视树:派发回 Dispatcher 再换面
            var cb = Failed;
            if (cb != null) Dispatcher.BeginInvoke(new Action(() => cb(this)));
        }
    }
}
