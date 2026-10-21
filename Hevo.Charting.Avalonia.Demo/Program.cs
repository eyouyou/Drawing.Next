using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.Linked;
using Hevo.Charting.Samples;
using Hevo.Charting.Samples.CodedKLine;

namespace Hevo.Charting.Avalonia.Demo
{
    internal static class Program
    {
        /// <summary>命令行 <c>--blueprint</c> / <c>--designer</c>:打开就停在"蓝图 JSON" / "蓝图编辑器"页。</summary>
        internal static int StartTab { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            StartTab = args.Contains("--designer") ? 2 : args.Contains("--blueprint") ? 1 : 0;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
    }

    internal sealed class App : Application
    {
        public override void Initialize()
        {
            // 示例数据源不引 UI 框架:推送定时器由宿主给,跑在 Avalonia UI 线程
            MockKLineDataSource.StartTimer = (interval, tick) => DispatcherTimer.Run(() => { tick(); return true; }, interval, DispatcherPriority.Background);
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>
    /// 三页:手写 schema 的联动看板;同样内容的蓝图 JSON(Hevo.Charting.Samples 的 kline_dashboard.json)经
    /// <see cref="AvaloniaDashboardLauncher"/> 起的看板;打开这份 JSON 的蓝图编辑器。schema / 数据源 / 蓝图跟 WPF 示例是同一份。
    /// </summary>
    internal sealed class MainWindow : Window
    {
        private readonly List<AvaloniaChartCell> _cells = new();

        public MainWindow()
        {
            Title = "Hevo.Charting · Avalonia";
            Width = 1100;
            Height = 700;
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C));

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "手写 schema", Content = BuildCodedDashboard() });
            tabs.Items.Add(new TabItem { Header = "蓝图 JSON", Content = BuildBlueprintDashboard() });
            tabs.Items.Add(new TabItem { Header = "蓝图编辑器", Content = BuildDesigner() });
            tabs.SelectedIndex = Program.StartTab;
            Content = tabs;

            Closed += (_, _) =>
            {
                foreach (var cell in _cells) cell.Shutdown();
            };
        }

        /// <summary>K 线主图(蜡烛 + SMA20)+ 成交量副图,共享视口和十字线,各自带顶部信息栏。数据在窗口打开后开始拉取。</summary>
        private Control BuildCodedDashboard()
        {
            var dataSource = new MockKLineDataSource();
            var dashboard = new AvaloniaLinkedChartDashboard(new LinkedChartContext { HorizontalRight = ChartLength.Pixel(60) })
                .AddMaster(new KLineMainSchema(dataSource), heightRatio: 3)
                .AddPane(new KLineVolumeSchema(dataSource), heightRatio: 1);
            dashboard.IsPaneResizeEnabled = true;
            _cells.AddRange(dashboard.Cells);
            Opened += (_, _) => dataSource.SwitchContext("demo");
            return dashboard;
        }

        /// <summary>蓝图 JSON 起看板:数据源由蓝图里的 DefaultContext 自动加载,失败时把错误显示在页面上。</summary>
        private Control BuildBlueprintDashboard()
        {
            SampleDashboards.RegisterComponents();
            var result = AvaloniaDashboardLauncher.LaunchFromJson(SampleDashboards.KLineDashboardJson());
            foreach (var d in result.Diagnostics) Console.WriteLine(d);
            if (result.Dashboard == null)
                return new TextBlock { Text = "蓝图启动失败:" + result.Error, Foreground = Brushes.OrangeRed, Margin = new Thickness(16) };
            _cells.AddRange(result.Dashboard.Cells);
            return result.Dashboard;
        }

        /// <summary>蓝图编辑器:打开示例 dashboard 的主图 cell,可拖节点、连线、点"运行"弹预览。</summary>
        private Control BuildDesigner()
        {
            var designer = new AvaloniaBlueprintDesigner();
            designer.LoadJson(SampleDashboards.KLineDashboardJson());
            _cells.Add(designer.Cell);
            return designer;
        }
    }
}
