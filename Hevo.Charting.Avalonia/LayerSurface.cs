using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 一张图层录好的 SKPicture,带引用计数。Avalonia 在渲染线程回放绘制操作,UI 线程重录时旧 picture
    /// 可能还在被回放:图层、合成面、每个绘制操作各持一份引用,最后一份放掉才真正释放。
    /// </summary>
    internal sealed class PictureRef
    {
        private int _refs = 1;

        public PictureRef(SKPicture picture) => Picture = picture;

        public SKPicture Picture { get; }

        public void AddRef() => Interlocked.Increment(ref _refs);

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) == 0) Picture.Dispose();
        }
    }

    /// <summary>图层在 Avalonia 下的上屏对象:最近一次录制的 picture(空图层为 null)。随图层 Dispose。</summary>
    internal sealed class AvaloniaLayerVisual : IDisposable
    {
        public PictureRef? Picture;

        /// <summary>需要重录(DPI 变了、资源换了):下次上屏不管图层脏不脏都按前台指令重录。</summary>
        public bool Stale;

        public void Replace(PictureRef? picture)
        {
            var old = Picture;
            Picture = picture;
            old?.Release();
        }

        public void Dispose() => Replace(null);
    }

    /// <summary>
    /// 一组图层(静态层或交互层)的合成面。两组各一块,对应 WPF 路径的静态画布 / 交互画布:
    /// 十字光标只让交互面重画,K 线那块的绘制数据不变。picture 按设备像素录制,回放时把画布变换换成只平移。
    /// </summary>
    internal sealed class LayerSurface : Control
    {
        private PictureRef[] _pictures = Array.Empty<PictureRef>();

        public LayerSurface()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        /// <summary>按 Z 序换一组 picture(null 跳过)并请求重画。</summary>
        public void SetPictures(List<PictureRef?> pictures)
        {
            int n = 0;
            for (int i = 0; i < pictures.Count; i++) if (pictures[i] != null) n++;
            var next = n == 0 ? Array.Empty<PictureRef>() : new PictureRef[n];
            for (int i = 0, j = 0; i < pictures.Count; i++)
            {
                if (pictures[i] is not { } p) continue;
                p.AddRef();
                next[j++] = p;
            }
            ReleaseAll();
            _pictures = next;
            InvalidateVisual();
        }

        public void Clear()
        {
            ReleaseAll();
            InvalidateVisual();
        }

        private void ReleaseAll()
        {
            var old = _pictures;
            _pictures = Array.Empty<PictureRef>();
            for (int i = 0; i < old.Length; i++) old[i].Release();
        }

        /// <summary>测试用:当前挂着的 picture 数。</summary>
        internal int PictureCount => _pictures.Length;

        public override void Render(DrawingContext context)
        {
            if (_pictures.Length == 0) return;
            context.Custom(new PictureDrawOperation(new Rect(Bounds.Size), _pictures));
        }

        private sealed class PictureDrawOperation : ICustomDrawOperation
        {
            private readonly PictureRef[] _pictures;
            private int _disposed;

            public PictureDrawOperation(Rect bounds, PictureRef[] pictures)
            {
                Bounds = bounds;
                _pictures = pictures;
                for (int i = 0; i < pictures.Length; i++) pictures[i].AddRef();
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature == null) return;
                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;

                int save = canvas.Save();
                // Avalonia 给的画布带 RenderScaling 缩放;picture 已按设备像素录好(像素对齐在设备像素上做),
                // 这里只保留平移并取整,原点落在设备像素上,1px 线不发虚
                var m = canvas.TotalMatrix;
                float ox = MathF.Round(m.TransX), oy = MathF.Round(m.TransY);
                canvas.SetMatrix(SKMatrix.CreateTranslation(ox, oy));
                canvas.ClipRect(new SKRect(0, 0, (float)(Bounds.Width * m.ScaleX), (float)(Bounds.Height * m.ScaleY)));
                for (int i = 0; i < _pictures.Length; i++) canvas.DrawPicture(_pictures[i].Picture);
                canvas.RestoreToCount(save);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                for (int i = 0; i < _pictures.Length; i++) _pictures[i].Release();
            }
        }
    }
}
