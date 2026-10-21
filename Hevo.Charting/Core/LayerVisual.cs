using Hevo.Charting.Renderers.Raster;
using System.Windows.Media;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 一个图层在 WPF 下的上屏对象:矢量指令回放进这个 DrawingVisual;位图模式下另挂一块位图宿主。
    /// 由 ChartCell 在图层挂上时创建,存在 <see cref="Abstractions.ChartLayer.HostVisual"/> 上,随图层 Dispose。
    /// </summary>
    internal sealed class LayerVisual : DrawingVisual, IDisposable
    {
        /// <summary>位图模式下的像素宿主(切回矢量 / 移除图层时释放)。</summary>
        internal BitmapLayerSurface? BitmapSurface;

        internal void ReleaseBitmap()
        {
            BitmapSurface?.Dispose();
            BitmapSurface = null;
        }

        public void Dispose() => ReleaseBitmap();

        // 【性能优化】彻底屏蔽 WPF HitTest
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        protected override GeometryHitTestResult? HitTestCore(GeometryHitTestParameters hitTestParameters) => null;
    }
}
