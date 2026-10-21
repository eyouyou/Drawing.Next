#if DEBUG
using Hevo.Charting.Core;
using System.Windows.Controls;

namespace Hevo.Charting.DevTools
{
    /// <summary>
    /// Debug 下给 ChartCell 的右键菜单加"拓扑水管监控器"入口及 schema 子类自定义的项;
    /// DecomposeAll 时成对摘除(chart.ContextMenu 跨 schema 复用,不摘的话 reload 蓝图后菜单项累加)。
    /// </summary>
    internal sealed class WpfDebugMenuHost : ReactiveSchema.IDebugMenuHost
    {
        public object? Inject(ReactiveSchema schema, IChartCell chart)
        {
            if (chart is not ChartCell cell) return null;
            var injected = new List<MenuItem>();
            if (cell.ContextMenu == null) cell.ContextMenu = new ContextMenu();
            var monitorItem = new MenuItem { Header = "🚀 开启拓扑水管监控器" };
            monitorItem.Click += (s, e) => ShowTopologyMonitor(cell);
            cell.ContextMenu.Items.Add(monitorItem);
            injected.Add(monitorItem);

            // 子类 override 可能直接往 chart.ContextMenu 加项(如 KLineSchema 的"物理内存迷你图")——
            // snapshot count 前后差,把增量也纳入清理范围,子类不必改签名。
            int before = cell.ContextMenu.Items.Count;
            schema.InjectDebugMenus(chart);
            for (int i = before; i < cell.ContextMenu.Items.Count; i++)
            {
                if (cell.ContextMenu.Items[i] is MenuItem mi) injected.Add(mi);
            }
            return injected;
        }

        public void Remove(object? injected, IChartCell chart)
        {
            if (injected is not List<MenuItem> items || chart is not ChartCell cell) return;
            var menu = cell.ContextMenu;
            if (menu != null)
            {
                foreach (var mi in items) menu.Items.Remove(mi);
            }
        }

        private static void ShowTopologyMonitor(ChartCell chart)
        {
            var win = new System.Windows.Window
            {
                Title = "🌊 Hevo 拓扑流向监控仪 + 时间线",
                Content = new TopologyInspectorControl(chart),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 10, 15)),
                Width = 1100,
                // 加了 TimelineHeight=180 这块,顺手把窗口 950 → 950 不够紧;给到 950 + 180 = 1130 差不多,
                // 用户真不够还能拉。
                Height = 930,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                Owner = System.Windows.Window.GetWindow(chart)
            };
            win.Show();
        }
    }
}
#endif
