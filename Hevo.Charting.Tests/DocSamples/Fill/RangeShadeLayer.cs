using System;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

// 06 号文档「填充:DrawPolygon / DrawBand」引用的范例:用 DrawBand 做区间填色。
// 写法按业务侧红线(LangVersion 9):块级 namespace、显式 using、不用 record struct / required。
namespace Hevo.Charting.DocSamples.Fill
{
    /// <summary>
    /// 数据 Trait:上下两条序列,按下标对齐。区间外 / 暖机期写 NaN,带子在那里断开。
    /// 阈值填色(高于阈值绿、低于红)把阈值铺成 <see cref="Lower"/> 即可。列只在本帧有效,不存进字段。
    /// </summary>
    public sealed record RangeShadeTrait(ReadOnlyMemory<double> Upper, ReadOnlyMemory<double> Lower) : IVisualTrait;

    /// <summary>样式 Trait:上边在上的部分用 <see cref="Fill"/>,穿越后的部分用 <see cref="CrossedFill"/>(null = 不分段)。</summary>
    public sealed record RangeShadeStyle(IHevoBrush Fill, IHevoBrush? CrossedFill) : IVisualTrait
    {
        // 半透明:填色压在 K 线 / 折线下面也不挡
        public static RangeShadeStyle Create(Color fill, Color? crossed = null, byte alpha = 0x40)
            => new RangeShadeStyle(
                new HevoSolidBrush(HevoColor.FromArgb(alpha, fill.R, fill.G, fill.B)),
                crossed is Color c ? new HevoSolidBrush(HevoColor.FromArgb(alpha, c.R, c.G, c.B)) : null);
    }

    /// <summary>
    /// 区间填色图层:取 Trait → 投影 → 一条 DrawBand。
    /// <para>
    /// 不要拼 SVG 字符串走 DrawGeometry(数据一变就重新解析,填色跟着 K 线滚动时每帧都在解析),
    /// 也不要自己找 NaN 断点、算穿越点 —— DrawBand 在渲染器里做,三个后端同一份拆分。
    /// </para>
    /// </summary>
    [RequiredTrait(typeof(RangeShadeTrait))]
    public partial class RangeShadeLayer : ChartLayer
    {
        // 💥 0-GC:带子数据跨帧复用,OnUpdate 里只 Clear + Add。DrawBand 录的是引用,一帧只录一次。
        private readonly HevoBand _band = new HevoBand(2048);

        public RangeShadeLayer()
        {
            Name = "RangeShadeLayer";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var shade = data.Get<RangeShadeTrait>();
            var style = data.Get<RangeShadeStyle>();
            var scale = data.Get<ScaleStrategyTrait>();
            var plotArea = data.Get<PlotAreaTrait>();
            var xAxis = data.Get<XAxisTrait>();
            var yAxis = data.Get<YAxisTrait>();
            if (shade == null || style == null || scale == null || plotArea == null || xAxis == null || yAxis == null) return;
            HevoRect area = plotArea.Area;
            RealRange rangeX = xAxis.Viewport;
            RealRange rangeY = yAxis.Viewport;
            if (area.Width <= 0 || area.Height <= 0 || rangeX.Span <= 0 || rangeY.Span <= 0) return;

            int count = Math.Min(shade.Upper.Length, shade.Lower.Length);
            if (count == 0) return;
            double left = scale.DomainScale.Denormalize(0.0, rangeX);
            double right = scale.DomainScale.Denormalize(1.0, rangeX);
            int start = Math.Clamp((int)Math.Floor(Math.Min(left, right)), 0, count - 1);
            int end = Math.Clamp((int)Math.Ceiling(Math.Max(left, right)), 0, count - 1);

            var upper = shade.Upper.Span;
            var lower = shade.Lower.Span;
            _band.Clear();
            for (int i = start; i <= end; i++)
            {
                double u = upper[i], l = lower[i];
                // NaN 原样留下(不像折线那样跳过):跳过会把缺口两侧连成一片
                if (double.IsNaN(u) || double.IsNaN(l)) { _band.AddGap(); continue; }
                _band.Add(
                    CoordinateExtensions.ProjectToScreen(area, rangeX, rangeY, scale, i, u),
                    CoordinateExtensions.ProjectToScreen(area, rangeX, rangeY, scale, i, l));
            }

            using (draw.PushClip(area))
                draw.DrawBand(style.Fill, null, _band, style.CrossedFill);
        }
    }
}
