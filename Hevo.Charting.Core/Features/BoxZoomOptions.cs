using Hevo.Charting.Interaction;
using Hevo.Charting.Core;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 框选缩放配置 (Box-zoom / Rubber-band).
    /// 默认 Shift+Drag 触发,与普通 Pan 共存;业务可改 <see cref="PointerModifiers.None"/> 让框选完全替代 Pan。
    /// </summary>
    public record BoxZoomOptions
    {
        /// <summary>激活修饰键。默认 Shift —— Shift+Drag 进入 box-zoom,无修饰走 Pan。</summary>
        public PointerModifiers Activation { get; init; } = PointerModifiers.Shift;

        /// <summary>选框最小有效像素。低于此值视为误操作丢弃,不写视口。</summary>
        public double MinPixelSize { get; init; } = 8.0;

        /// <summary>选框边框样式。null = 用默认浅灰虚线。</summary>
        public LineStyle? BorderStyle { get; init; }

        /// <summary>选框填充。null = 用默认半透明蓝。</summary>
        public IHevoBrush? FillBrush { get; init; }
    }
}
