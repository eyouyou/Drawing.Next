using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using SkiaSharp;

namespace Hevo.Charting.Skia
{
    /// <summary>光栅目标。</summary>
    public enum SkiaRenderTarget
    {
        /// <summary>CPU 光栅进 WriteableBitmap(默认;任何机器都可用,远程桌面也行)。</summary>
        Cpu,
        /// <summary>OpenGL(SKGLElement)。需要显卡 GL 3.3+ 与 NV_DX_interop;创建或绘制失败自动回退 CPU。</summary>
        Gpu,
    }

    /// <summary>
    /// WPF 专用的 Skia 上屏后端:<c>cell.RenderBackend = new SkiaRenderBackend();</c>,或全局
    /// <c>ChartCell.DefaultRenderBackendFactory = () => new SkiaRenderBackend();</c>。
    /// <para>
    /// 所有权:挂到 <see cref="ChartCell.RenderBackend"/> 之后由 cell 负责释放(换掉 / 置空 / Shutdown 时 Detach + Dispose),
    /// 调用方不要再自己 Dispose。一个实例只能挂在一个 cell 上。
    /// </para>
    /// <para>
    /// 保留模式,失效粒度 = 图层(与 WPF DrawingVisual 后端一致):每个图层一张 SKPicture,只在图层变脏时重录;
    /// 静态层 / 交互层各一块合成面(对应 WPF 路径的静态画布 / 交互画布),哪块面上有图层重录就整面重画那块 ——
    /// 十字光标移动只重画交互面,不碰 K 线那块。想再省就继续拆图层,后端不做图层以下的区域推算。
    /// picture 按设备像素录制(像素对齐在设备像素上做),DPI 变化时所有图层重录。
    /// </para>
    /// </summary>
    public sealed class SkiaRenderBackend : IChartRenderBackend
    {
        private static readonly Action<RenderContext> s_noop = _ => { };

        private readonly SkiaRenderTarget _target;
        private readonly SkiaChartRenderer _renderer = new();
        private readonly Dictionary<ChartLayer, SKPicture?> _pictures = new();
        private readonly Group _static = new();
        private readonly Group _overlay = new();
        private readonly List<SKPicture?> _gpuPictures = new();
        private ChartCell? _cell;
        private Grid? _host;
        private double _pixelsPerDip = 1.0;

        public SkiaRenderBackend(SkiaRenderTarget target = SkiaRenderTarget.Cpu) => _target = target;

        /// <summary>实际生效的光栅目标(Gpu 创建或绘制失败时为 Cpu)。</summary>
        public SkiaRenderTarget ActiveTarget { get; private set; }

        /// <summary>是否已释放。释放后不能再挂到 cell 上。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>最近一帧:重录 picture 的耗时(ticks)、CPU 光栅耗时(ticks,GPU 目标恒为 0)、重录图层数、重画的合成面数。</summary>
        public long LastRecordTicks { get; private set; }
        public long LastRasterTicks { get; private set; }
        public int LastRecordedLayers { get; private set; }
        public int LastRepaintedSurfaces { get; private set; }

        /// <summary>GPU 目标:合成面最近一次 WPF 渲染时的绘制耗时(ticks);CPU 目标为 0。</summary>
        public long LastGpuPaintTicks => (_static.Surface as SkiaGpuSurface)?.LastPaintTicks ?? 0;

        /// <summary>主题切换后调用:资源笔刷按新主题重新解析,所有图层在下一帧重录(这里会请求一帧)。</summary>
        public void InvalidateResources()
        {
            _renderer.ClearResourceCaches();
            ClearPictures();
            _static.Changed = _overlay.Changed = true;
            _cell?.RequestUpdate(s_noop);
        }

        private sealed class Group
        {
            public ISkiaLayerSurface? Surface;
            public readonly List<SKPicture?> Pictures = new();
            public int LastCount = -1;
            public bool Changed = true;

            public void Reset()
            {
                Pictures.Clear();
                LastCount = -1;
                Changed = true;
            }
        }

        public UIElement Attach(ChartCell cell)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(SkiaRenderBackend));
            if (_cell != null)
                throw new InvalidOperationException("这个 SkiaRenderBackend 已经挂在一个 ChartCell 上了;每个 cell 要用自己的后端实例。");
            _cell = cell;
            _host = new Grid { IsHitTestVisible = false };
            var first = CreateSurface();
            _static.Surface = first;
            if (first is SkiaGpuSurface)
            {
                // GPU:单块 GL 面,两组按 Z 序画在一起(两块 GL 面叠放时上层不透明会盖住下层)
                _overlay.Surface = first;
                _host.Children.Add(first.Element);
            }
            else
            {
                _overlay.Surface = new SkiaCpuSurface();
                _host.Children.Add(first.Element);
                _host.Children.Add(_overlay.Surface.Element);
            }
            _static.Reset();
            _overlay.Reset();
            return _host;
        }

        private ISkiaLayerSurface CreateSurface()
        {
            if (_target == SkiaRenderTarget.Gpu)
            {
                try
                {
                    if (SkiaGpuSurface.TryCreate() is { } gpu)
                    {
                        gpu.Failed = OnGpuFailed;
                        ActiveTarget = SkiaRenderTarget.Gpu;
                        return gpu;
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"[SkiaRenderBackend] GPU 面创建失败,回退 CPU:{ex.Message}");
                }
            }
            ActiveTarget = SkiaRenderTarget.Cpu;
            return new SkiaCpuSurface();
        }

        // GPU 面绘制失败:换成两块 CPU 面,用现有 picture 整面重画,并请求一帧
        private void OnGpuFailed(SkiaGpuSurface gpu)
        {
            if (_host == null || !ReferenceEquals(_static.Surface, gpu)) return;
            _host.Children.Remove(gpu);
            gpu.Release();
            _gpuPictures.Clear();

            ActiveTarget = SkiaRenderTarget.Cpu;
            var bottom = new SkiaCpuSurface();
            var top = new SkiaCpuSurface();
            _static.Surface = bottom;
            _overlay.Surface = top;
            _host.Children.Add(bottom);
            _host.Children.Add(top);
            // 还没布局时 Redraw 只记下 picture,尺寸到位后 OnRenderSizeChanged 会补画
            bottom.Redraw(_static.Pictures);
            top.Redraw(_overlay.Pictures);
            _cell?.RequestUpdate(s_noop);
        }

        public void Present(IReadOnlyList<ChartLayer> staticLayers, IReadOnlyList<ChartLayer> overlayLayers)
        {
            if (_cell == null || _static.Surface == null || _overlay.Surface == null) return;

            // picture 按设备像素录制:DPI 变了(跨屏拖动 / 系统缩放)所有图层都要按新 DPI 重录。
            // 取合成面自己的 DPI —— 位图按它分配像素,录制网格必须与之一致
            double ppd = VisualTreeHelper.GetDpi(_static.Surface.Element).PixelsPerDip;
            if (ppd != _pixelsPerDip)
            {
                _pixelsPerDip = ppd;
                _renderer.PixelsPerDip = ppd;
                ClearPictures();
                _static.Changed = _overlay.Changed = true;
            }

            long t0 = Stopwatch.GetTimestamp();
            int recorded = Record(_static, staticLayers) + Record(_overlay, overlayLayers);
            long t1 = Stopwatch.GetTimestamp();

            long raster = 0;
            int repainted = 0;
            if (ActiveTarget == SkiaRenderTarget.Gpu)
            {
                if (_static.Changed || _overlay.Changed)
                {
                    _gpuPictures.Clear();
                    _gpuPictures.AddRange(_static.Pictures);
                    _gpuPictures.AddRange(_overlay.Pictures);
                    _static.Surface.Redraw(_gpuPictures);
                    repainted = 1;
                }
            }
            else
            {
                if (_static.Changed) { raster += _static.Surface.Redraw(_static.Pictures); repainted++; }
                if (_overlay.Changed) { raster += _overlay.Surface.Redraw(_overlay.Pictures); repainted++; }
            }
            _static.Changed = _overlay.Changed = false;

            LastRecordTicks = t1 - t0;
            LastRasterTicks = raster;
            LastRecordedLayers = recorded;
            LastRepaintedSurfaces = repainted;
        }

        // 脏图层重录 picture;本组有图层重录(或图层增删)就标记整面重画
        private int Record(Group g, IReadOnlyList<ChartLayer> layers)
        {
            int recorded = 0;
            if (layers.Count != g.LastCount) g.Changed = true;
            g.LastCount = layers.Count;
            g.Pictures.Clear();

            var bounds = new SKRect(0, 0,
                (float)Math.Max(1, _cell!.ActualWidth * _pixelsPerDip),
                (float)Math.Max(1, _cell.ActualHeight * _pixelsPerDip));
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (layer.IsDirty || !_pictures.TryGetValue(layer, out var pic))
                {
                    if (_pictures.TryGetValue(layer, out var old)) old?.Dispose();
                    pic = layer.Buffer is LayerBuffer lb ? RecordPicture(lb, bounds) : null;
                    _pictures[layer] = pic;
                    recorded++;
                }
                g.Pictures.Add(pic);
            }
            if (recorded > 0) g.Changed = true;
            return recorded;
        }

        private SKPicture? RecordPicture(LayerBuffer buffer, SKRect bounds)
        {
            if (buffer.Drawing.Commands.Count == 0) return null;
            using var recorder = new SKPictureRecorder();
            _renderer.Render(buffer.Drawing, recorder.BeginRecording(bounds));
            return recorder.EndRecording();
        }

        public void OnLayerRemoved(ChartLayer layer)
        {
            if (!_pictures.TryGetValue(layer, out var pic)) return;
            _pictures.Remove(layer);
            // 合成面在下一次 Present 之前也可能重画(改尺寸):先把列表里的引用摘掉再释放
            if (pic != null)
            {
                Unreference(_static.Pictures, pic);
                Unreference(_overlay.Pictures, pic);
                Unreference(_gpuPictures, pic);
                pic.Dispose();
            }
            // 不知道它原来在哪块面上:两块都重画(随后的 Present 也会因图层数变化标记)
            _static.Changed = _overlay.Changed = true;
        }

        private static void Unreference(List<SKPicture?> list, SKPicture pic)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], pic)) list[i] = null;
        }

        // 释放全部 picture,并让合成面不再持有它们(否则改尺寸 / GPU 重画时会回放已释放的 picture)
        private void ClearPictures()
        {
            _static.Pictures.Clear();
            _overlay.Pictures.Clear();
            _gpuPictures.Clear();
            _static.Surface?.Forget();
            _overlay.Surface?.Forget();
            foreach (var p in _pictures.Values) p?.Dispose();
            _pictures.Clear();
        }

        public void Detach()
        {
            if (_cell == null && _host == null) return;
            ClearPictures();
            _static.Surface?.Release();
            if (!ReferenceEquals(_overlay.Surface, _static.Surface)) _overlay.Surface?.Release();
            _static.Surface = _overlay.Surface = null;
            _static.Reset();
            _overlay.Reset();
            _host?.Children.Clear();
            _host = null;
            _cell = null;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            Detach();
            _renderer.Dispose();
            IsDisposed = true;
        }
    }
}
