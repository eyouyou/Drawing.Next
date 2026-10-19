using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using System.Windows.Media;

namespace Hevo.Charting.Layers
{
    /// <summary>
    /// 可拖水平线的一帧画面:屏幕坐标已由 Feature 投影好,图层只画。
    /// </summary>
    /// <param name="Visible">false = 不画(价格无效或不在可见量程内)。</param>
    /// <param name="Left">线左端(绘图区左缘)。</param>
    /// <param name="Right">线右端(绘图区右缘)。</param>
    /// <param name="Y">线的屏幕 Y。</param>
    /// <param name="Hot">悬停或拖动中:用加粗样式。</param>
    /// <param name="Style">常态样式。</param>
    /// <param name="HotStyle">加粗样式。</param>
    /// <param name="Label">右侧价格标签;null = 不画。</param>
    public record DraggableLineTrait(bool Visible, float Left, float Right, float Y, bool Hot, LineStyle Style, LineStyle HotStyle, string? Label) : IVisualTrait
    {
        public static readonly LineStyle DefaultStyle = LineStyle.Create(new HevoSolidBrush(Color.FromRgb(0xF0, 0xA0, 0x30)), 1.0);
        public static readonly LineStyle DefaultHotStyle = LineStyle.Create(new HevoSolidBrush(Color.FromRgb(0xF0, 0xA0, 0x30)), 2.0);
        public static readonly DraggableLineTrait Hidden = new(false, 0, 0, 0, false, DefaultStyle, DefaultHotStyle, null);

        internal static readonly IHevoTypeface LabelTypeface = new HevoTypeface("Arial");
        internal static readonly IHevoBrush LabelText = new HevoSolidBrush(Colors.White);
    }

    /// <summary>可拖水平线(委托价、止损、预警线)。在交互层(独立画布),拖动时只重画它自己。</summary>
    public partial class DraggableLineLayer : ChartLayer
    {
        public DraggableLineLayer()
        {
            Name = "DraggableLine";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var line = data.Get<DraggableLineTrait>();
            var plot = data.Get<PlotAreaTrait>();
            if (line == null || !line.Visible || plot == null || plot.Area.IsEmpty) return;

            var style = line.Hot ? line.HotStyle : line.Style;
            using (draw.PushClip(plot.Area))
                draw.DrawLine(style.LinePen, new HevoPoint(line.Left, line.Y), new HevoPoint(line.Right, line.Y));

            if (line.Label != null)
                draw.DrawText(line.Label, DraggableLineTrait.LabelTypeface, DraggableLineTrait.LabelText, 11f,
                    new HevoPoint(line.Right, line.Y), TextAlignX.Right, TextAlignY.Center, style.LinePen.Brush, null, 4f, 1f);
        }
    }
}
