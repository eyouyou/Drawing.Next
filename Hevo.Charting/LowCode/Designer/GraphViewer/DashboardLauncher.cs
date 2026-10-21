using Hevo.Charting.Linked;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// <see cref="DashboardLauncher.LaunchEx"/> / <see cref="DashboardLauncher.DryRun"/> 的结构化结果。
    /// Diagnostics 来自所有 cell 蓝图的 DryRun 结果聚合(每条诊断里 FeatureTypeName 前缀有 "Cell:{id} / ")。
    /// </summary>
    public sealed class DashboardLaunchResult
    {
        public LinkedChartDashboard? Dashboard { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<BlueprintDiagnostic> Diagnostics { get; init; } = Array.Empty<BlueprintDiagnostic>();
        public bool Launched => Error == null && Dashboard != null;
    }

    /// <summary>
    /// §D1 dashboard 蓝图运行时入口(WPF)。把 <see cref="Dashboard"/> JSON 翻译成实际跑起来的
    /// <see cref="LinkedChartDashboard"/>:per-cell 走 BlueprintRunner.BuildSchema 拿 schema,然后按
    /// <see cref="DashboardCellRole"/> 分发 AddMaster / AddPane / AddRaw。
    ///
    /// <para>
    /// <b>跟 <see cref="BlueprintLauncher"/> 的区别</b>:Launcher 是单 cell + 弹窗预览;DashboardLauncher 装配
    /// 多 cell 联动,产出一个 <see cref="LinkedChartDashboard"/>(UserControl),业务侧自己决定挂哪。
    /// 装配逻辑平台无关,在核心 <see cref="DashboardAssembler"/>(Avalonia 的 AvaloniaDashboardLauncher 共用),这里只给 WPF 看板控件。
    /// </para>
    /// </summary>
    public static class DashboardLauncher
    {
        /// <summary>
        /// 静态校验:对 dashboard 内所有 cell 跑一遍 <see cref="BlueprintLauncher.DryRun"/>,
        /// 加上 dashboard 特有的诊断(角色顺序 / Master 唯一性 / cell.Id 重复 / 0 cells)。
        /// 不实例化任何数据源,不创建 LinkedChartContext。
        /// </summary>
        public static DashboardLaunchResult DryRun(Dashboard dashboard, DashboardLaunchOptions? options = null)
        {
            var r = DashboardAssembler.DryRun(dashboard, options);
            return new DashboardLaunchResult { Error = r.Error, Diagnostics = r.Diagnostics };
        }

        /// <summary>
        /// 装配并启动 dashboard,返回 <see cref="LinkedChartDashboard"/> 给业务侧挂载到 Window/UserControl。
        /// 失败时 Error 非空,Dashboard 为 null。Diagnostics 始终带回(成功也可能有警告)。
        /// </summary>
        public static DashboardLaunchResult LaunchEx(Dashboard dashboard, DashboardLaunchOptions? options = null)
        {
            var r = DashboardAssembler.Assemble(dashboard, options,
                (ctx, d) => new LinkedChartDashboard(ctx) { IsPaneResizeEnabled = d.EnablePaneResize },
                out var control);
            return new DashboardLaunchResult
            {
                Dashboard = r.Error == null ? (LinkedChartDashboard?)control : null,
                Error = r.Error,
                Diagnostics = r.Diagnostics,
            };
        }

        /// <summary>
        /// 极简版本:返回错误字符串(成功 null)+ 已装好的 dashboard 通过 out 参数。失败时 dashboard 为 null。
        /// </summary>
        public static string? Launch(Dashboard dashboard, out LinkedChartDashboard? linkedDashboard, DashboardLaunchOptions? options = null)
        {
            var result = LaunchEx(dashboard, options);
            linkedDashboard = result.Dashboard;
            return result.Error;
        }
    }
}
