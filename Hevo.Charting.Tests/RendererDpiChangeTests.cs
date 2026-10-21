using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 运行时 DPI 变化(跨屏拖动):同一个渲染器换 DPI 后,画笔线宽按新 DPI 重新取整(画笔缓存要清),
    /// 1px 线在新 DPI 下仍是 1 个实心设备像素。
    /// </summary>
    public sealed class RendererDpiChangeTests
    {
        private const int W = 200, H = 100;

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

        private static readonly HevoPen Pen = new(new HevoSolidBrush(HevoColors.Black), 1);

        private static int PartialPixels(WpfRenderProvider provider, double k)
        {
            var layer = new ScriptLayer(d =>
            {
                d.DrawLine(Pen, new HevoPoint(10, 20.3f), new HevoPoint(190, 20.3f));
                d.DrawLine(Pen, new HevoPoint(50.6f, 30), new HevoPoint(50.6f, 90));
            });
            layer.MarkDirty();
            layer.Update(NoData.Instance);
            layer.SwapBuffer();
            provider.UpdateDpi(k);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) ((LayerBuffer)layer.Buffer!).Execute(provider, dc);
            int pw = (int)Math.Ceiling(W * k), ph = (int)Math.Ceiling(H * k);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var px = new int[pw * ph];
            rtb.CopyPixels(px, pw * 4, 0);
            int partial = 0;
            foreach (var p in px)
            {
                uint a = (uint)p >> 24;
                if (a != 0 && a != 255) partial++;
            }
            return partial;
        }

        [Theory]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void SameRenderer_AfterDpiChange_LinesStayCrisp(double k) => IncrementalTestKit.RunOnSta(() =>
        {
            var provider = new WpfRenderProvider();
            Assert.Equal(0, PartialPixels(provider, 1.0)); // 先在 100% 下建好画笔缓存
            Assert.Equal(0, PartialPixels(provider, k));   // 换 DPI 后同一支画笔要按新 DPI 重建
        });
    }
}
