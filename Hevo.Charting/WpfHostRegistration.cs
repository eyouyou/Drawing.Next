using Hevo.Charting.Core;
using Hevo.Charting.Layers;
using System.Runtime.CompilerServices;

namespace Hevo.Charting
{
    /// <summary>
    /// WPF 宿主给核心的平台实现(悬浮窗图层、外壳 Feature、资源文本、调试菜单)。
    /// 自写的双宿主外壳 Feature 在这里登记 WPF 实现:<c>WpfChartHost.Services.RegisterFeature&lt;MyShellBase, MyWpfShell&gt;()</c>。
    /// </summary>
    public static class WpfChartHost
    {
        public static ChartHostServices Services => WpfHostRegistration.Services;
    }

    /// <summary>
    /// WPF 宿主向核心登记平台实现。程序集一加载就跑(早于任何 ChartCell / Feature 装配)。
    /// 登记的是本宿主自己的一份 <see cref="ChartHostServices"/>,跟别的宿主(Avalonia)互不覆盖,可同进程共存。
    /// </summary>
    internal static class WpfHostRegistration
    {
        internal static readonly ChartHostServices Services = new("Wpf", typeof(WpfHostRegistration).Assembly);

#pragma warning disable CA2255 // 宿主库在加载时向核心登记平台实现,正是 ModuleInitializer 的用途
        [ModuleInitializer]
        internal static void Register()
        {
            // 资源文本(HevoResourceString)查 Application 资源字典:登记在 WpfRenderRegistry 的静态构造里
            RuntimeHelpers.RunClassConstructor(typeof(WpfRenderRegistry).TypeHandle);

            Services.CreateTooltipLayer = () => new TooltipWidgetLayer();
            Services.SetUniversalHeaderFactory(hitPort => new Features.UniversalHeaderFeature { HitStatePort = hitPort });
#if DEBUG
            Services.DebugMenuHost = new DevTools.WpfDebugMenuHost();
#endif
            ChartHostServices.Register(Services);
        }
#pragma warning restore CA2255
    }
}
