using System.Text.Json;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Avalonia
{
    /// <summary><see cref="AvaloniaDashboardLauncher.LaunchEx"/> 的结果(对应 WPF 的 DashboardLaunchResult)。</summary>
    public sealed class AvaloniaDashboardLaunchResult
    {
        public AvaloniaLinkedChartDashboard? Dashboard { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<BlueprintDiagnostic> Diagnostics { get; init; } = Array.Empty<BlueprintDiagnostic>();
        public bool Launched => Error == null && Dashboard != null;
    }

    /// <summary>
    /// dashboard 蓝图 JSON 在 Avalonia 下起联动看板(对应 WPF 的 DashboardLauncher)。
    /// 校验、逐 cell 装配、loading 同步都在核心 <see cref="DashboardAssembler"/>,跟 WPF 同一份;这里只给 Avalonia 看板控件。
    /// 蓝图里的 <c>"UniversalHeaderFeature"</c> 在 Avalonia cell 上解析成 <see cref="AvaloniaUniversalHeaderFeature"/>。
    /// </summary>
    public static class AvaloniaDashboardLauncher
    {
        public static AvaloniaDashboardLaunchResult DryRun(Dashboard dashboard, DashboardLaunchOptions? options = null)
        {
            var r = DashboardAssembler.DryRun(dashboard, options);
            return new AvaloniaDashboardLaunchResult { Error = r.Error, Diagnostics = r.Diagnostics };
        }

        /// <summary>装配并启动;返回的看板控件由业务挂进窗口。失败时 Error 非空、Dashboard 为 null。</summary>
        public static AvaloniaDashboardLaunchResult LaunchEx(Dashboard dashboard, DashboardLaunchOptions? options = null)
        {
            var r = DashboardAssembler.Assemble(dashboard, options,
                (ctx, d) => new AvaloniaLinkedChartDashboard(ctx) { IsPaneResizeEnabled = d.EnablePaneResize },
                out var control);
            return new AvaloniaDashboardLaunchResult
            {
                Dashboard = r.Error == null ? (AvaloniaLinkedChartDashboard?)control : null,
                Error = r.Error,
                Diagnostics = r.Diagnostics,
            };
        }

        /// <summary>从 dashboard JSON(跟 WPF Designer 导出的同一格式)起看板。JSON 解析失败也走 Error 返回。</summary>
        public static AvaloniaDashboardLaunchResult LaunchFromJson(string json, DashboardLaunchOptions? options = null)
        {
            Dashboard? dashboard;
            try { dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default); }
            catch (JsonException ex) { return new AvaloniaDashboardLaunchResult { Error = $"dashboard JSON 解析失败:{ex.Message}" }; }
            if (dashboard == null) return new AvaloniaDashboardLaunchResult { Error = "dashboard JSON 为空。" };
            return LaunchEx(dashboard, options);
        }
    }
}
