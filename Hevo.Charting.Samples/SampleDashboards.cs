using System;
using System.IO;
using Hevo.Charting.LowCode.Designer;

namespace Hevo.Charting.Samples
{
    /// <summary>
    /// 示例看板蓝图:K 线主图(蜡烛 + SMA20 + 头部 + 悬浮窗)+ 成交量副图,数据源是 <see cref="MockKLineDataSource"/>。
    /// 内容取自 LowCodeDemo 的 default_kline_dashboard.json,去掉了要 Python handler 的 strategy 图,
    /// 所以 WPF(DashboardLauncher)和 Avalonia(AvaloniaDashboardLauncher)都能不带 Python 直接起。
    /// </summary>
    public static class SampleDashboards
    {
        /// <summary>看板 JSON 原文。</summary>
        public static string KLineDashboardJson()
        {
            using var stream = typeof(SampleDashboards).Assembly.GetManifestResourceStream("Hevo.Charting.Samples.kline_dashboard.json")
                ?? throw new InvalidOperationException("缺少嵌入资源 kline_dashboard.json。");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>登记蓝图里用到的组件:框架内置 + 本程序集的数据源。起看板前调一次。</summary>
        public static void RegisterComponents()
        {
            LowCode.Designer.GraphViewer.GraphViewerBootstrap.Initialize();
            BuiltinRegistration.RegisterAssemblyOf<MockKLineDataSource>();
        }
    }
}
