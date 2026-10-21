using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Tests
{
    /// <summary>测试读图层在 WPF 下的上屏对象(DrawingVisual 内容 / 位图宿主)。</summary>
    internal static class LayerVisualTestExtensions
    {
        public static LayerVisual Wpf(this ChartLayer layer)
            => layer.HostVisual as LayerVisual
               ?? throw new InvalidOperationException("图层还没挂到 ChartCell 上");
    }
}

namespace Hevo.Charting.Tests
{
    internal static class BareCellShell
    {
        /// <summary>
        /// 不挂 schema 的裸 cell 套一层只含 ContentPresenter 的模板,让 cell 的画布进可视树(截图 / 布局要用)。
        /// ChartCell.Template 只收 schema,这里直接写 WPF 的 Control.Template。
        /// </summary>
        public static T WithShell<T>(this T cell) where T : ChartCell
        {
            ((System.Windows.Controls.Control)cell).Template = new ChartTemplate
            {
                VisualTree = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter))
            };
            return cell;
        }
    }
}
