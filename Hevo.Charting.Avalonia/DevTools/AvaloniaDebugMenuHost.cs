#if DEBUG
using Avalonia.Controls;
using Avalonia.Media;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// Debug 下给 <see cref="AvaloniaChartCell"/> 的右键菜单加"拓扑水管监控器"入口及 schema 子类自定义的项(同 WPF 的 WpfDebugMenuHost);
    /// DecomposeAll 时成对摘除(ContextMenu 跨 schema 复用,不摘的话 reload 蓝图后菜单项累加)。
    /// </summary>
    internal sealed class AvaloniaDebugMenuHost : ReactiveSchema.IDebugMenuHost
    {
        public object? Inject(ReactiveSchema schema, IChartCell chart)
        {
            if (chart is not AvaloniaChartCell cell) return null;
            var injected = new List<MenuItem>();
            cell.ContextMenu ??= new ContextMenu();
            var monitorItem = new MenuItem { Header = "🚀 开启拓扑水管监控器" };
            monitorItem.Click += (_, _) => ShowTopologyMonitor(cell);
            cell.ContextMenu.Items.Add(monitorItem);
            injected.Add(monitorItem);

            // 子类 override 可能直接往 cell.ContextMenu 加项 —— 记下前后差,增量也纳入清理范围
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
            if (injected is not List<MenuItem> items || chart is not AvaloniaChartCell cell) return;
            var menu = cell.ContextMenu;
            if (menu == null) return;
            foreach (var mi in items) menu.Items.Remove(mi);
        }

        internal static Window ShowTopologyMonitor(AvaloniaChartCell cell)
        {
            var win = new Window
            {
                Title = "🌊 拓扑流向监控仪",
                Content = new AvaloniaTopologyMonitor(cell),
                Background = new SolidColorBrush(Color.FromRgb(10, 10, 15)),
                Width = 1100,
                Height = 760,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            if (TopLevel.GetTopLevel(cell) is Window owner) win.Show(owner);
            else win.Show();
            return win;
        }
    }
}
#endif
