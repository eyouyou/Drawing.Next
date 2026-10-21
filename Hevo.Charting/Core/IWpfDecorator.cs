using System.Windows;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 给图表包 WPF 外壳 UI(图例条、边框、标题栏等)的切面实现它(范例:DynamicHeaderFeature)。
    /// ChartCell 应用模板时沿 Aspect 链从内到外调用:<c>a + b</c> 里 b 包在内、a 包在外。
    /// </summary>
    public interface IWpfDecorator : IHostDecorator
    {
        UIElement Decorate(UIElement inner);
    }
}
