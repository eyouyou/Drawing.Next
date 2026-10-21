using Avalonia.Controls;
using Hevo.Charting.Core;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 给图表包 Avalonia 外壳 UI(图例条、标题栏等)的切面实现它(WPF 下对应 IWpfDecorator)。
    /// <see cref="AvaloniaChartCell"/> 装配时沿 Aspect 链从内到外调用:<c>a + b</c> 里 b 包在内、a 包在外。
    /// </summary>
    public interface IAvaloniaDecorator : IHostDecorator
    {
        Control Decorate(Control inner);
    }
}
