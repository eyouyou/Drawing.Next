using System.Linq;
using System.Windows;
using Hevo.Charting.Avalonia;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.Samples;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// WPF 与 Avalonia 宿主同进程共存:两边各登记自己的一份 ChartHostServices,核心按 cell 取,互不覆盖。
    /// 这里不起 Avalonia 平台(那要 Avalonia 应用 + Headless 运行器,见 Hevo.Charting.Avalonia.Tests),
    /// 只把 Avalonia 宿主程序集加载进来,看 WPF 侧的行为一点不变、Avalonia 的实现也按宿主拿得到。
    /// </summary>
    public sealed class HostCoexistenceTests
    {
        private sealed class HostOnlyString : IHevoString
        {
            public string Key { get; init; } = "";
        }

        static HostCoexistenceTests()
        {
            // 触发 Avalonia 宿主程序集的模块初始化(登记它那份 ChartHostServices)
            _ = AvaloniaChartHost.Services;
        }

        [Fact]
        public void BothHostsRegistered_EachWithOwnServices()
        {
            var all = ChartHostServices.All;
            Assert.Contains(WpfChartHost.Services, all);
            Assert.Contains(AvaloniaChartHost.Services, all);
            Assert.NotSame(WpfChartHost.Services, AvaloniaChartHost.Services);
            Assert.Same(typeof(ChartCell).Assembly, WpfChartHost.Services.HostAssembly);
            Assert.Same(typeof(AvaloniaChartCell).Assembly, AvaloniaChartHost.Services.HostAssembly);
        }

        [Fact]
        public void WpfCell_GetsWpfImplementations() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = new ChartCell();
            var services = ChartHostServices.Of(cell);
            Assert.Same(WpfChartHost.Services, services);
            Assert.IsType<TooltipWidgetLayer>(services.CreateTooltipLayer!());
            Assert.IsType<UniversalHeaderFeature>(services.CreateUniversalHeader!(new LowCode.DataPort<PointerHitState?>("t")));
            Assert.NotNull(AvaloniaChartHost.Services.CreateTooltipLayer);
        });

        [Fact]
        public void BlueprintName_ResolvesPerHost_GlobalStaysWpf()
        {
            BuiltinRegistration.RegisterBuiltins();
            // 全局表(没给宿主)保持旧行为:WPF 扫进来的 UniversalHeaderFeature
            Assert.Equal(typeof(UniversalHeaderFeature), ComponentRegistry.Resolve("UniversalHeaderFeature"));
            Assert.Equal(typeof(UniversalHeaderFeature), ComponentRegistry.Resolve("UniversalHeaderFeature", WpfChartHost.Services));
            Assert.Equal(typeof(AvaloniaUniversalHeaderFeature), ComponentRegistry.Resolve("UniversalHeaderFeature", AvaloniaChartHost.Services));
        }

        [Fact]
        public void HostStrings_ResolvePerHost_GlobalRegistrationWins()
        {
            WpfChartHost.Services.RegisterString<HostOnlyString>(s => "wpf:" + s.Key);
            AvaloniaChartHost.Services.RegisterString<HostOnlyString>(s => "ava:" + s.Key);
            var text = new HostOnlyString { Key = "k" };
            Assert.Equal("wpf:k", HevoStrings.Resolve(text, WpfChartHost.Services));
            Assert.Equal("ava:k", HevoStrings.Resolve(text, AvaloniaChartHost.Services));
            // 没给宿主:按登记顺序取第一个能解析的
            Assert.StartsWith(ChartHostServices.All[0].Name == "Wpf" ? "wpf:" : "ava:", HevoStrings.Resolve(text));
            Assert.Equal("字面量", HevoStrings.Resolve(new HevoLiteralString("字面量"), AvaloniaChartHost.Services));
        }

        [Fact]
        public void WpfDashboardFromJson_UsesWpfHeader_EvenWithAvaloniaLoaded() => IncrementalTestKit.RunOnSta(() =>
        {
            SampleDashboards.RegisterComponents();
            var result = DashboardLauncher.LaunchEx(System.Text.Json.JsonSerializer.Deserialize<Dashboard>(
                SampleDashboards.KLineDashboardJson(), LowCode.Designer.Converters.BlueprintJsonOptions.Default)!);
            Assert.Null(result.Error);
            var dash = Assert.IsType<LinkedChartDashboard>(result.Dashboard);
            Assert.Equal(2, dash.Cells.Count);

            var window = new Window { Content = dash, Width = 600, Height = 500 };
            window.Show();
            window.UpdateLayout();
            try
            {
                var schema = Assert.IsAssignableFrom<ReactiveSchema>(dash.Cells[0].Template);
                Assert.Contains(schema.ListFeatures(), f => f.GetType() == typeof(UniversalHeaderFeature));
                Assert.DoesNotContain(schema.ListFeatures(), f => f is AvaloniaUniversalHeaderFeature);
            }
            finally
            {
                window.Close();
                foreach (var c in dash.Cells) c.Shutdown();
            }
        });

        [Fact]
        public void SharedPortConfig_ToleratesTypeMovedToCore()
        {
            // 旧 JSON 写的程序集是 Hevo.Charting;PointerHitState 现在在 Hevo.Charting.Core
            var ctx = new LinkedChartContext();
            ctx.RegisterFromConfig(new[]
            {
                new SharedPortModel("legacyHit", "System.Nullable`1[[Hevo.Charting.Features.PointerHitState, Hevo.Charting]]"),
            });
            Assert.True(ctx.SharedPorts.ContainsKey("legacyHit"));
            Assert.IsType<LowCode.DataPort<PointerHitState?>>(ctx.SharedPorts["legacyHit"]);
        }
    }
}
