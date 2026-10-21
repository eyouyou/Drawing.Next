using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers
{
    /// <summary>
    /// <see cref="TooltipWidgetFeature{TX}"/> 的 closed-over-double 子类,蓝图可见。
    /// </summary>
    public class TooltipDoubleWidgetFeature : TooltipWidgetFeature<double> { }

    /// <summary>
    /// <see cref="TooltipWidgetFeature{TX}"/> 的 closed-over-DateTime 子类,蓝图可见。
    /// 跟 <see cref="CrosshairDateTimeFeature"/> 配对,典型 K 线 / 分时 X 轴用。
    /// </summary>
    public class TooltipDateTimeWidgetFeature : TooltipWidgetFeature<DateTime> { }
}
