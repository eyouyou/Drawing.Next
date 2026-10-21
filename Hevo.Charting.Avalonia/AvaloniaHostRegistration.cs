using System.Runtime.CompilerServices;
using Hevo.Charting.Core;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// Avalonia 宿主给核心的平台实现(悬浮窗图层、外壳 Feature、资源文本、调试菜单)。
    /// 自写的双宿主外壳 Feature 在这里登记 Avalonia 实现:<c>AvaloniaChartHost.Services.RegisterFeature&lt;MyShellBase, MyAvaloniaShell&gt;()</c>。
    /// </summary>
    public static class AvaloniaChartHost
    {
        public static ChartHostServices Services => AvaloniaHostRegistration.Services;
    }

    /// <summary>
    /// Avalonia 宿主向核心登记平台实现。程序集一加载就跑(早于任何 cell / Feature 装配)。
    /// 登记的是本宿主自己的一份 <see cref="ChartHostServices"/>,跟 WPF 宿主互不覆盖,可同进程共存。
    /// </summary>
    internal static class AvaloniaHostRegistration
    {
        internal static readonly ChartHostServices Services = new("Avalonia", typeof(AvaloniaHostRegistration).Assembly);

#pragma warning disable CA2255 // 宿主库在加载时向核心注册平台实现,正是 ModuleInitializer 的用途(同 WPF 宿主)
        [ModuleInitializer]
        internal static void Register()
        {
            // 资源文本查 Application 资源(当前主题);找不到显示键名,跟 WPF 宿主一致
            Services.RegisterString<HevoResourceString>(s => AvaloniaBrushes.FindResource(s.ResourceKey) as string ?? s.ResourceKey);

            Services.CreateTooltipLayer = () => new AvaloniaTooltipLayer();
            Services.SetUniversalHeaderFactory(hitPort => new AvaloniaUniversalHeaderFeature { HitStatePort = hitPort });
            // 蓝图 JSON 里写的是 "UniversalHeaderFeature"(WPF 的类名):挂到 Avalonia cell 时解析到本宿主的实现
            Services.RegisterComponentAlias("UniversalHeaderFeature", typeof(AvaloniaUniversalHeaderFeature));
#if DEBUG
            Services.DebugMenuHost = new AvaloniaDebugMenuHost();
#endif
            ChartHostServices.Register(Services);
        }
#pragma warning restore CA2255

        /// <summary>显式触发模块初始化(<see cref="AvaloniaChartCell"/> 静态构造调,保证先于第一次装配)。</summary>
        internal static void EnsureRegistered() { }
    }
}
