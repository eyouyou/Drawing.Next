using Hevo.Charting.Abstractions;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hevo.Charting
{
    public class ChartDrawingCanvas : Canvas
    {
        /// <summary>
        /// 和所有visual的交互
        /// </summary>
        public ChartDrawingCanvas()
        {
            _visuals = new VisualCollection(this);
        }

        private VisualCollection _visuals;

        // 原点对齐归宿主:渲染器在图层局部坐标里按设备像素取整,前提是图层原点落在设备像素上。
        // 祖先摆在小数位置(Grid 星号行列、GridSplitter、125% DPI 下的整数 DIP)时,挂上画布的每个 visual 都带这条 guideline,
        // 由合成器把原点贴到最近的设备像素。只作用于渲染(不改布局、命中测试和鼠标坐标),父布局变化 / 动画中都跟着。
        // guideline 只挪几何不挪 DrawImage,位图模式图层另由 ChartCell.SnapBitmapOrigin 平移(见 06 号文档 §14.9)。
        private static readonly DoubleCollection s_originGuideline = CreateOriginGuideline();

        private static DoubleCollection CreateOriginGuideline()
        {
            var g = new DoubleCollection { 0 };
            g.Freeze();
            return g;
        }

        private static void SnapOrigin(DrawingVisual v)
        {
            v.XSnappingGuidelines = s_originGuideline;
            v.YSnappingGuidelines = s_originGuideline;
        }

        public void InsertVisual(int physicalIndex, ChartLayer visual)
        {
            SnapOrigin(visual);
            if (physicalIndex >= _visuals.Count)
                _visuals.Add(visual);
            else
                _visuals.Insert(physicalIndex, visual);
        }

        internal void RemoveVisual(ChartLayer visual) => _visuals.Remove(visual);
        public DrawingVisual CreateVisual()
        {
            var v = new DrawingVisual();
            SnapOrigin(v);
            _visuals.Add(v);
            return v;
        }
        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index)
        {
            return _visuals[index];
        }

    }
}
