using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;

namespace Hevo.Charting.Layers
{
    public static class TooltipPositionCalculator
    {
        /// <summary>
        /// 💥 智能避让算法：计算 Tooltip 的最终左上角坐标 (纯 float 极速运算)
        /// </summary>
        /// <param name="anchor">锚点 (通常是十字光标交叉点)</param>
        /// <param name="width">Tooltip 的真实物理宽度</param>
        /// <param name="height">Tooltip 的真实物理高度</param>
        /// <param name="plotArea">图表的安全绘图区域</param>
        /// <param name="mode">用户配置的停靠模式</param>
        /// <param name="offset">与光标的间距偏移量</param>
        public static HevoPoint Calc(HevoPoint anchor, float width, float height, HevoRect plotArea, TooltipPositionMode mode, HevoPoint offset)
        {
            float x = anchor.X;
            float y = anchor.Y;

            // 智能模式：动态计算剩余空间，决定最终模式
            if (mode == TooltipPositionMode.Auto)
            {
                bool fitRight = (x + offset.X + width) <= plotArea.Right;
                bool fitBottom = (y + offset.Y + height) <= plotArea.Bottom;

                // 优先停靠右下角，右边放不下就放左边，下面放不下就放上面
                mode = fitRight ?
                       (fitBottom ? TooltipPositionMode.BottomRight : TooltipPositionMode.TopRight) :
                       (fitBottom ? TooltipPositionMode.BottomLeft : TooltipPositionMode.TopLeft);
            }

            // 根据最终模式，计算出左上角绘制起点
            return mode switch
            {
                TooltipPositionMode.TopLeft => new HevoPoint(x - width - offset.X, y - height - offset.Y),
                TooltipPositionMode.TopRight => new HevoPoint(x + offset.X, y - height - offset.Y),
                TooltipPositionMode.BottomLeft => new HevoPoint(x - width - offset.X, y + offset.Y),
                TooltipPositionMode.BottomRight => new HevoPoint(x + offset.X, y + offset.Y),
                _ => new HevoPoint(x + offset.X, y + offset.Y)
            };
        }
    }

    /// <summary>悬浮窗渲染数据包(每帧由 <see cref="TooltipWidgetFeature{TX}"/> 生成下发)。</summary>
    /// <param name="AnchorPos">光标锚点(通常是十字光标交叉点的物理坐标)。</param>
    /// <param name="Rows">行数据内存切片(Name + Value + 颜色),0-GC 传递。</param>
    /// <param name="Background">背景画刷。</param>
    /// <param name="CornerRadius">圆角半径(像素)。</param>
    /// <param name="IsVisible">false 时 layer 直接退帧,不动 widget tree。</param>
    /// <param name="PositionMode">停靠模式(Auto / TopLeft / TopRight / BottomLeft / BottomRight)。</param>
    /// <param name="Offset">悬浮窗与锚点的像素偏移。</param>
    /// <param name="PlotArea">绘图区边界,用于 Auto 模式智能避让计算。</param>
    public record TooltipWidgetTrait(
                HevoPoint AnchorPos,
                ReadOnlyMemory<TooltipRow> Rows,
                IHevoBrush Background,
                double CornerRadius,
                bool IsVisible,
                TooltipPositionMode PositionMode,
                HevoPoint Offset,
                HevoRect PlotArea
            ) : IVisualTrait;
}
