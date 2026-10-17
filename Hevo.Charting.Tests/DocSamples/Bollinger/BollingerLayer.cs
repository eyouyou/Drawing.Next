using System;
using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

// 开发规范 §5 布林带 SOP —— 步骤 C:无状态图层。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 只做三件事:取 Trait → 用比例尺投影 → 录制指令。
    /// <para>
    /// 必须是 <c>partial</c>:Roslyn 生成器扫描 <see cref="OnUpdate"/> 里读过的 Trait,
    /// 自动补上 <c>IConsumes&lt;T&gt;</c>,Feature 侧 <c>ctx.For(layer).PublishData(...)</c> 才能通过编译。不要手写 IConsumes。
    /// </para>
    /// </summary>
    [RequiredTrait(typeof(BollingerDataTrait))]
    public partial class BollingerLayer : ChartLayer
    {
        // 💥 0-GC:点缓冲跨帧复用,OnUpdate 里不 new。
        // 三条线各用一块 —— DrawPolyline 录的是 List 引用,本帧上屏前不能被下一条线覆盖。
        // 2048 ≈ 全屏可见 ~1500 根 + 余量,只是"避免首帧扩容"的下限。
        private readonly List<HevoPoint> _upperPts = new List<HevoPoint>(2048);
        private readonly List<HevoPoint> _middlePts = new List<HevoPoint>(2048);
        private readonly List<HevoPoint> _lowerPts = new List<HevoPoint>(2048);

        public BollingerLayer()
        {
            Name = "BollingerLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var bands = data.Get<BollingerDataTrait>();
            var style = data.Get<BollingerStyle>();
            var scale = data.Get<ScaleStrategyTrait>();
            var plotArea = data.Get<PlotAreaTrait>();
            var xAxis = data.Get<XAxisTrait>();
            var yAxis = data.Get<YAxisTrait>();

            // 💥 防空拦截:任一前提缺失或区域无效直接退帧
            if (bands == null || style == null || scale == null || plotArea == null || xAxis == null || yAxis == null) return;
            HevoRect area = plotArea.Area;
            RealRange rangeX = xAxis.Viewport;
            RealRange rangeY = yAxis.Viewport;
            if (area.Width <= 0 || area.Height <= 0 || rangeX.Span <= 0 || rangeY.Span <= 0) return;

            int count = Math.Min(bands.Upper.Length, Math.Min(bands.Middle.Length, bands.Lower.Length));
            if (count == 0) return;

            // 视口裁剪:屏幕左右边界反投影回逻辑索引,只画可见区间(各往外扩一根,让线段延伸到边缘)
            double left = scale.DomainScale.Denormalize(0.0, rangeX);
            double right = scale.DomainScale.Denormalize(1.0, rangeX);
            int start = Math.Clamp((int)Math.Floor(Math.Min(left, right)), 0, count - 1);
            int end = Math.Clamp((int)Math.Ceiling(Math.Max(left, right)), 0, count - 1);

            Project(bands.Upper.Span, start, end, area, rangeX, rangeY, scale, _upperPts);
            Project(bands.Middle.Span, start, end, area, rangeX, rangeY, scale, _middlePts);
            Project(bands.Lower.Span, start, end, area, rangeX, rangeY, scale, _lowerPts);

            if (_upperPts.Count > 1) draw.DrawPolyline(style.BandPen, _upperPts);
            if (_middlePts.Count > 1) draw.DrawPolyline(style.MiddlePen, _middlePts);
            if (_lowerPts.Count > 1) draw.DrawPolyline(style.BandPen, _lowerPts);
        }

        // 💥 坐标一律走 ProjectToScreen(内部是 IScale.Normalize),严禁手写 (x - min) / span * width
        private static void Project(ReadOnlySpan<double> values, int start, int end, HevoRect area,
            RealRange rangeX, RealRange rangeY, ScaleStrategyTrait scale, List<HevoPoint> output)
        {
            output.Clear();
            for (int i = start; i <= end; i++)
            {
                double v = values[i];
                if (double.IsNaN(v)) continue; // 暖机期 NaN 不画
                output.Add(CoordinateExtensions.ProjectToScreen(area, rangeX, rangeY, scale, i, v));
            }
        }
    }
}
