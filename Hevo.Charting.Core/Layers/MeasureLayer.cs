using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Layers
{
    /// <summary>测量框的一帧画面:起点到当前点的矩形 + 标签(根数、价差、涨跌幅)。</summary>
    /// <param name="Active">false = 不画。</param>
    /// <param name="From">起点(按下点,屏幕坐标)。</param>
    /// <param name="To">当前点(屏幕坐标)。</param>
    /// <param name="Label">标签文字。</param>
    /// <param name="Rising">终点价格不低于起点:决定配色。</param>
    public record MeasureTrait(bool Active, HevoPoint From, HevoPoint To, string? Label, bool Rising) : IVisualTrait
    {
        public static readonly MeasureTrait Inactive = new(false, default, default, null, true);

        internal static readonly IHevoBrush RiseFill = new HevoSolidBrush(HevoColor.FromArgb(40, 230, 70, 70));
        internal static readonly IHevoBrush FallFill = new HevoSolidBrush(HevoColor.FromArgb(40, 40, 180, 90));
        internal static readonly HevoPen RisePen = new(new HevoSolidBrush(HevoColor.FromArgb(200, 230, 70, 70)), 1.0);
        internal static readonly HevoPen FallPen = new(new HevoSolidBrush(HevoColor.FromArgb(200, 40, 180, 90)), 1.0);
        internal static readonly IHevoBrush LabelBackground = new HevoSolidBrush(HevoColor.FromArgb(220, 30, 30, 30));
        internal static readonly IHevoBrush LabelText = new HevoSolidBrush(HevoColors.White);
        internal static readonly IHevoTypeface LabelTypeface = new HevoTypeface("Arial");
    }

    /// <summary>测量工具的框与标签(交互层)。</summary>
    public partial class MeasureLayer : ChartLayer
    {
        public MeasureLayer()
        {
            Name = "Measure";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Interaction;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var m = data.Get<MeasureTrait>();
            var plot = data.Get<PlotAreaTrait>();
            if (m == null || !m.Active || plot == null || plot.Area.IsEmpty) return;

            float left = Math.Min(m.From.X, m.To.X), right = Math.Max(m.From.X, m.To.X);
            float top = Math.Min(m.From.Y, m.To.Y), bottom = Math.Max(m.From.Y, m.To.Y);
            var rect = new HevoRect(left, top, right - left, bottom - top);
            using (draw.PushClip(plot.Area))
            {
                draw.DrawRectangle(m.Rising ? MeasureTrait.RiseFill : MeasureTrait.FallFill, m.Rising ? MeasureTrait.RisePen : MeasureTrait.FallPen, rect);
                draw.DrawLine(m.Rising ? MeasureTrait.RisePen : MeasureTrait.FallPen, m.From, m.To);
            }

            if (m.Label != null)
                draw.DrawText(m.Label, MeasureTrait.LabelTypeface, MeasureTrait.LabelText, 11f, m.To,
                    TextAlignX.Left, m.To.Y <= m.From.Y ? TextAlignY.Bottom : TextAlignY.Top, MeasureTrait.LabelBackground, null, 4f, 2f);
        }
    }
}
