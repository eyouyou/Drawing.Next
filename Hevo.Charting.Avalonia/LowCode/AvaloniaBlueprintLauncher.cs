using Avalonia.Controls;
using Avalonia.Media;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 单张蓝图在 Avalonia 下跑起来(对应 WPF 的 BlueprintLauncher):静态校验、render-leaf 数据源实例化 / 上下文注入
    /// 走核心 <see cref="BlueprintPreflight"/>,这里只建 <see cref="AvaloniaChartCell"/> 和预览窗口。失败不抛,返回错误串。
    /// </summary>
    public static class AvaloniaBlueprintLauncher
    {
        /// <summary>只做静态校验(同 WPF BlueprintLauncher.DryRun)。</summary>
        public static BlueprintLaunchResult DryRun(ChartBlueprint blueprint, BlueprintHandlerRegistry? handlers = null)
            => BlueprintPreflight.DryRun(blueprint, handlers);

        /// <summary>
        /// 把蓝图装到一个新建的 <see cref="AvaloniaChartCell"/> 上(不弹窗,业务自己决定挂哪)。
        /// <paramref name="dataSourceContext"/> / <paramref name="configureDataSource"/> 的语义同 WPF BlueprintLauncher.Launch。
        /// </summary>
        public static BlueprintLaunchResult CreateCell(
            ChartBlueprint blueprint,
            out AvaloniaChartCell? cell,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            cell = null;
            var diag = BlueprintPreflight.DryRun(blueprint, handlers);
            if (diag.Error != null) return diag;

            var prepError = BlueprintPreflight.PrepareLeafDataSource(blueprint, dataSourceContext, configureDataSource,
                out var leafId, out _, out var leafInstance);
            if (prepError != null) return new BlueprintLaunchResult { Error = prepError, Diagnostics = diag.Diagnostics };

            var created = new AvaloniaChartCell();
            try
            {
                var instances = new Dictionary<string, object>(StringComparer.Ordinal) { [leafId] = leafInstance };
                BlueprintRunner.Run(created, blueprint, instances, handlers);
            }
            catch (Exception ex)
            {
                return new BlueprintLaunchResult { Error = $"RunBlueprint 抛异常:{(ex.InnerException ?? ex).Message}", Diagnostics = diag.Diagnostics };
            }
            cell = created;
            return diag;
        }

        /// <summary>装配并弹预览窗口(关窗时 <see cref="AvaloniaChartCell.Shutdown"/>)。</summary>
        public static BlueprintLaunchResult LaunchEx(
            ChartBlueprint blueprint,
            Window? owner = null,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            var result = CreateCell(blueprint, out var cell, dataSourceContext, configureDataSource, handlers);
            if (result.Error != null || cell == null) return result;

            var win = new Window
            {
                Title = "蓝图预览",
                Width = 1024,
                Height = 600,
                Content = cell,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            win.Closed += (_, _) => cell.Shutdown();
            if (owner != null) win.Show(owner);
            else win.Show();
            return result;
        }

        /// <summary>极简版:返回错误串(成功 null)。</summary>
        public static string? Launch(ChartBlueprint blueprint, Window? owner = null, object? dataSourceContext = null,
            Action<object>? configureDataSource = null, BlueprintHandlerRegistry? handlers = null)
            => LaunchEx(blueprint, owner, dataSourceContext, configureDataSource, handlers).Error;
    }
}
