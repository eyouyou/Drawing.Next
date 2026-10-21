using System.Windows;

namespace Hevo.Charting.Features
{
    public static class TooltipPositionCalculator
    {
        /// <summary>
        /// 💥 智能避让算法：计算 Tooltip 的最终左上角坐标
        /// </summary>
        /// <param name="anchor">锚点 (通常是十字光标交叉点)</param>
        /// <param name="size">Tooltip 的真实物理尺寸 (由渲染器测量得出)</param>
        /// <param name="plotArea">图表的安全绘图区域</param>
        /// <param name="mode">用户配置的停靠模式</param>
        /// <param name="offset">与光标的间距偏移量</param>
        public static Point Calc(Point anchor, Size size, Rect plotArea, TooltipPositionMode mode, Point offset)
        {
            double x = anchor.X;
            double y = anchor.Y;

            // 智能模式：动态计算剩余空间，决定最终模式
            if (mode == TooltipPositionMode.Auto)
            {
                bool fitRight = (x + offset.X + size.Width) <= plotArea.Right;
                bool fitBottom = (y + offset.Y + size.Height) <= plotArea.Bottom;

                // 优先停靠右下角，右边放不下就放左边，下面放不下就放上面
                mode = fitRight ?
                       (fitBottom ? TooltipPositionMode.BottomRight : TooltipPositionMode.TopRight) :
                       (fitBottom ? TooltipPositionMode.BottomLeft : TooltipPositionMode.TopLeft);
            }

            // 根据最终模式，计算出左上角绘制起点
            return mode switch
            {
                TooltipPositionMode.TopLeft => new Point(x - size.Width - offset.X, y - size.Height - offset.Y),
                TooltipPositionMode.TopRight => new Point(x + offset.X, y - size.Height - offset.Y),
                TooltipPositionMode.BottomLeft => new Point(x - size.Width - offset.X, y + offset.Y),
                TooltipPositionMode.BottomRight => new Point(x + offset.X, y + offset.Y),
                _ => new Point(x + offset.X, y + offset.Y)
            };
        }
    }
}
