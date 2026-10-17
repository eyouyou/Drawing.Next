using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using Hevo.Charting.Renderers;
using Xunit;
using T = Hevo.Charting.Tests.CandleColumnPixelTests;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 对比截图工具(不是断言型测试):设了环境变量 HEVO_SHOT_DIR 才运行,把 K 线 + 成交量 + 十字星三块面板
    /// 按 100% / 125% / 150%、每格 ≈3 / 8 / 20 DIP、影线 1 / 2px 离屏渲染成 PNG,并把从像素里量出来的
    /// 实体宽度、影线偏移、间隙、成交量柱中心偏移、十字星高度、半透明像素数写进 stats.json。
    /// 没设变量时直接返回,不影响常规测试。
    /// </summary>
    public sealed class CandleColumnShotTool
    {
        [Fact]
        public void WriteShots()
        {
            string? dir = Environment.GetEnvironmentVariable("HEVO_SHOT_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            var json = new StringBuilder("[\n");
            bool first = true;
            IncrementalTestKit.RunOnSta(() =>
            {
                foreach (double k in new[] { 1.0, 1.25, 1.5 })
                foreach (int cellTarget in new[] { 3, 8, 20 })
                foreach (double wick in new[] { 1.0, 2.0 })
                {
                    float w = T.PlotWidth(cellTarget);
                    var style = T.Style(wick);
                    var candle = T.RenderCandles(w, T.CandleH, k, style, doji: false, raster: false, out int pw, out int ph1);
                    var volume = T.RenderVolume(w, k, raster: false, out _, out int ph2);
                    var doji = T.RenderCandles(w, T.DojiH, k, style, doji: true, raster: false, out _, out int ph3);
                    string name = $"k{k:0.00}_c{cellTarget}_w{wick:0}";
                    SavePng(Path.Combine(dir, name + ".png"), pw, new[] { (candle, ph1), (volume, ph2), (doji, ph3) });

                    var m = T.Measure(candle, volume, doji, pw, ph3, k);
                    double cell = T.CellDip(w);
                    if (!first) json.Append(",\n");
                    first = false;
                    json.Append($"{{\"name\":\"{name}\",\"k\":{k},\"cellDip\":{cell:0.###},\"cellPx\":{cell * k:0.###},\"wick\":{wick},"
                        + $"\"expectBody\":{cell * k * (1 - style.BodyPadding):0.###},\"expectBar\":{cell * k * T.BarRatio:0.###},"
                        + $"\"body\":{Hist(m.Body.Select(v => (double)v))},\"wickOffset\":{Hist(m.WickOffset)},\"gap\":{Hist(m.Gap.Select(v => (double)v))},"
                        + $"\"bar\":{Hist(m.Bar.Select(v => (double)v))},\"barOffset\":{Hist(m.BarOffset)},\"doji\":{Hist(m.Doji.Select(v => (double)v))},"
                        + $"\"semi\":{m.Semi},\"candles\":{m.Body.Count}}}");
                }
            });
            json.Append("\n]\n");
            File.WriteAllText(Path.Combine(dir, "stats.json"), json.ToString());
        }

        private static string Hist(IEnumerable<double> values)
            => "{" + string.Join(",", values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => $"\"{g.Key:0.##}\":{g.Count()}")) + "}";

        private static void SavePng(string path, int pw, (uint[] Px, int H)[] panels)
        {
            const int sep = 4;
            int total = panels.Sum(p => p.H) + sep * (panels.Length - 1);
            var all = new uint[pw * total];
            for (int i = 0; i < all.Length; i++) all[i] = 0xFFFFFFFF;
            int y0 = 0;
            foreach (var (px, h) in panels)
            {
                for (int y = 0; y < h; y++)
                for (int x = 0; x < pw; x++)
                {
                    uint p = px[y * pw + x];
                    all[(y0 + y) * pw + x] = Over(p);
                }
                y0 += h;
                if (y0 < total) { for (int y = 0; y < sep; y++) for (int x = 0; x < pw; x++) all[(y0 + y) * pw + x] = 0xFFDDDDDD; y0 += sep; }
            }
            var bmp = BitmapSource.Create(pw, total, 96, 96, PixelFormats.Bgra32, null, all, pw * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        // 预乘 BGRA 叠到白底上
        private static uint Over(uint p)
        {
            uint a = p >> 24;
            uint inv = 255 - a;
            uint b = (p & 0xFF) + inv, g = ((p >> 8) & 0xFF) + inv, r = ((p >> 16) & 0xFF) + inv;
            return 0xFF000000 | (Math.Min(r, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u);
        }

    }
}
