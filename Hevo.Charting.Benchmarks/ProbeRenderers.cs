using System.Collections.Generic;
using System.Linq;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.Skia;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 测量用的绘制策略:WPF 矢量(默认)/ 全部图层位图 / 只有序列图层(K 线、折线、柱、散点)位图 /
    /// Skia 后端(<see cref="SkiaRenderBackend"/>)CPU 光栅 / GPU。
    /// </summary>
    internal enum ProbeRenderer { Wpf, BitmapAll, BitmapSeries, Skia, SkiaGpu }

    internal static class ProbeRenderers
    {
        /// <summary>--renderer=wpf|bitmap|series(latency-probe 用;render-probe 走 bmp / bmp-series 模式)。</summary>
        public static ProbeRenderer Parse(string? value) => value?.ToLowerInvariant() switch
        {
            null or "" or "wpf" or "software" => ProbeRenderer.Wpf,
            "bitmap" or "bmp" or "all" => ProbeRenderer.BitmapAll,
            "series" or "bmp-series" => ProbeRenderer.BitmapSeries,
            "skia" => ProbeRenderer.Skia,
            "skia-gpu" => ProbeRenderer.SkiaGpu,
            _ => throw new System.ArgumentException($"未知 --renderer={value}(可选 wpf / bitmap / series / skia / skia-gpu)"),
        };

        /// <summary>
        /// 只用公开 API 切策略:整张图用 <see cref="ChartCell.RenderModeOverride"/>,单个图层用 <see cref="ChartLayer.Mode"/>。
        /// 切换本身会排一帧重新上屏,调用方紧接着跑的归位帧会把它吃掉,不进测量。
        /// </summary>
        public static void Apply(ProbeRig rig, ProbeRenderer renderer)
        {
            var cells = rig.Charts.Select(c => c.Cell).Concat(rig.Panes.Select(p => p.Cell)).ToList();
            s_cells = cells;
            bool skia = renderer is ProbeRenderer.Skia or ProbeRenderer.SkiaGpu;
            s_gpu = renderer == ProbeRenderer.SkiaGpu;
            foreach (var cell in cells)
            {
                if (skia)
                {
                    var target = renderer == ProbeRenderer.SkiaGpu ? SkiaRenderTarget.Gpu : SkiaRenderTarget.Cpu;
                    if (cell.RenderBackend is not SkiaRenderBackend sb || sb.ActiveTarget != target)
                    {
                        cell.RenderBackend?.Dispose();
                        cell.RenderBackend = sb = new SkiaRenderBackend(target);
                        // 新挂的合成面要先布局拿到尺寸(探针不走 Dispatcher 泵,不会自动布局)
                        cell.UpdateLayout();
                    }
                }
                else if (cell.RenderBackend is { } old)
                {
                    cell.RenderBackend = null;
                    old.Dispose();
                }
                cell.RenderModeOverride = renderer == ProbeRenderer.BitmapAll ? RenderMode.Bitmap : null;
                foreach (var layer in cell.ActiveLayers.OfType<ChartLayer>())
                    layer.Mode = renderer == ProbeRenderer.BitmapSeries && IsSeries(layer) ? RenderMode.Bitmap : RenderMode.Software;
            }
        }

        private static List<ChartCell> s_cells = new();
        private static bool s_gpu;

        /// <summary>
        /// Skia 策略下刚跑完那一帧的光栅耗时(各图之和);非 Skia 策略返回 0。Stopwatch ticks。
        /// CPU 目标:光栅在 Present 里同步完成,已含在帧耗时里。
        /// GPU 目标:光栅发生在 WPF 渲染(GL 控件 OnRender → OnPaintSurface),这里主动布局一次触发它,
        /// 返回这次布局 + GL 绘制 + 提交的总耗时(不在帧耗时里,端到端要另加)。
        /// </summary>
        public static long LastRasterTicks()
        {
            if (s_gpu)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var cell in s_cells) cell.UpdateLayout();
                return System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
            long ticks = 0;
            foreach (var cell in s_cells)
                if (cell.RenderBackend is SkiaRenderBackend sb) ticks += sb.LastRasterTicks;
            return ticks;
        }

        /// <summary>GPU 目标下最近一帧 OnPaintSurface 的耗时(诊断用,确认 GL 真的在画)。</summary>
        public static long LastGpuPaintTicks()
        {
            long ticks = 0;
            foreach (var cell in s_cells)
                if (cell.RenderBackend is SkiaRenderBackend sb) ticks += sb.LastGpuPaintTicks;
            return ticks;
        }

        private static bool IsSeries(ChartLayer layer) => layer is CandleLayer or LineLayer or BarLayer or ScatterPlotLayer;
    }
}
