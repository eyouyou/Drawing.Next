using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;

[assembly: AvaloniaTestApplication(typeof(Hevo.Charting.Avalonia.Tests.TestApp))]

namespace Hevo.Charting.Avalonia.Tests
{
    public sealed class TestApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    /// <summary>
    /// Avalonia 宿主:装配、录制 SKPicture、真 Skia 出图,以及指针适配器把 Avalonia 输入转成正确的手势。
    /// 夹具跟 Hevo.Charting.Tests 的 PointerRouterTests.Rig 同口径:100 根 K 线、看最后 30 根、Y 量程 80–120、400×300。
    /// </summary>
    public sealed class AvaloniaChartCellTests
    {
        internal sealed class Schema : ChartReactiveSchema
        {
            public CandlePorts Ports { get; } = new();
            public DataPort<RealRange> YRange { get; } = new("T_YRange");
            public DataPort<PointerHitState?> Hit { get; } = new("T_Hit");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public ChartInteractionMode Mode { get; init; } = ChartInteractionMode.All;
            public CandleStyle? Style { get; init; }

            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                canvas
                    .Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge,
                        defaultVisibleCount: 30, overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard))
                    .Series(s => s.AddCandle(YRange, Ports, style: Style))
                    .Interactions(i => i.EnableStandardPointer(Hit, Mode, dismissOnEscape: true));
            }
        }

        internal sealed class Rig
        {
            public const int Length = 100;
            public readonly AvaloniaChartCell Cell = new() { Width = 400, Height = 300 };
            public readonly Schema Schema;
            public readonly DataBlackboard Board = new();
            public readonly Window Window;
            public readonly ViewportPorts Viewport;

            public Rig(ChartInteractionMode mode = ChartInteractionMode.All, CandleStyle? style = null)
            {
                Schema = new Schema { Mode = mode, Style = style };
                Cell.Template = Schema;
                // 窗口直接给定尺寸并先把布局跑完,鼠标坐标 = 图层画布坐标
                Window = new Window { Content = Cell, Width = 400, Height = 300 };
                Window.Show();
                Dispatcher.UIThread.RunJobs();
                Viewport = ViewportPorts.RequireAttached(Cell);

                var time = new DateTime[Length];
                double[] open = new double[Length], high = new double[Length], low = new double[Length], close = new double[Length];
                for (int i = 0; i < Length; i++)
                {
                    time[i] = new DateTime(2026, 1, 1).AddMinutes(i);
                    open[i] = 100 + i % 7; close[i] = 101 + i % 5; high[i] = 110; low[i] = 95;
                }
                using (Board.WriteScope())
                {
                    Board.ForceWrite(Schema.Ports.Time, new ReadOnlyMemory<DateTime>(time));
                    Board.ForceWrite(Schema.Ports.Open, new ReadOnlyMemory<double>(open));
                    Board.ForceWrite(Schema.Ports.High, new ReadOnlyMemory<double>(high));
                    Board.ForceWrite(Schema.Ports.Low, new ReadOnlyMemory<double>(low));
                    Board.ForceWrite(Schema.Ports.Close, new ReadOnlyMemory<double>(close));
                    Board.Write(Viewport.LogicalLength, Length);
                    Board.Write(Schema.YRange, new RealRange(80, 120));
                }
                Schema.Trigger.Push(Board);
                Schema.InvalidateEnvironment();
                Frame();
                Frame();
            }

            public void Frame() => Cell.RunFrameNow();

            public T Read<T>(DataPort<T> port) { using (Board.ReadScope()) return Board.Read(port); }
            public RealRange ActiveRange => Read(Viewport.ActiveRange);
            public PointerHitState? Hit => Read(Schema.Hit);

            public void Move(double x, double y) { Window.MouseMove(new Point(x, y)); Frame(); }

            public void Drag(double fromX, double toX, double y = 150, int steps = 6)
            {
                Window.MouseMove(new Point(fromX, y));
                Window.MouseDown(new Point(fromX, y), MouseButton.Left);
                Frame();
                for (int i = 1; i <= steps; i++)
                {
                    Window.MouseMove(new Point(fromX + (toX - fromX) * i / steps, y));
                    Frame();
                }
                Window.MouseUp(new Point(toX, y), MouseButton.Left);
                Frame();
            }
        }

        [AvaloniaFact]
        public void Compose_RecordsLayerPictures_AndSkiaRendersThem()
        {
            var rig = new Rig();

            Assert.True(rig.Cell.ActiveLayers.Count > 0);
            // 静态层(K 线)每层一张 picture,挂在 HostVisual 上;静态面拿到了它们
            var candle = rig.Cell.Engine.StaticLayers.Single(l => l is CandleLayer);
            var visual = Assert.IsType<AvaloniaLayerVisual>(candle.HostVisual);
            Assert.NotNull(visual.Picture);
            Assert.True(visual.Picture!.Picture.CullRect.Width > 0);
            Assert.True(rig.Cell.StaticSurface.PictureCount > 0);

            // 真 Skia 出图:K 线区域有非背景像素
            using var frame = rig.Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(CountInkPixels(frame!) > 500);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void EnvironmentInvalidated_ResolvesResourceBrushesAgain()
        {
            // 换主题 = 资源换值;数据没变时图层不脏,靠环境失效让宿主丢掉解析过的画刷、全部图层重画
            var app = Application.Current!;
            app.Resources["Test.Candle"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(255, 0, 0));
            try
            {
                var brush = new HevoResourceBrush("Test.Candle");
                var rig = new Rig(style: new CandleStyle(brush, brush, new HevoPen(brush, 1)));
                int red0 = CountColor(rig.Window, 255, 0, 0);
                Assert.True(red0 > 200, $"red={red0}");

                app.Resources["Test.Candle"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0, 255, 0));
                rig.Schema.InvalidateEnvironment();
                rig.Frame();
                int green = CountColor(rig.Window, 0, 255, 0), red = CountColor(rig.Window, 255, 0, 0);
                Assert.True(green > 200 && red == 0, $"green={green} red={red}");
                rig.Window.Close();
            }
            finally
            {
                app.Resources.Remove("Test.Candle");
            }
        }

        private static int CountColor(Window window, byte r, byte g, byte b)
        {
            using var frame = window.CaptureRenderedFrame()!;
            var size = frame.PixelSize;
            int stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            unsafe
            {
                fixed (byte* p = buffer) frame.CopyPixels(new PixelRect(size), (nint)p, buffer.Length, stride);
            }
            int n = 0;
            for (int i = 0; i < buffer.Length; i += 4)
                // 像素格式随平台(BGRA / RGBA),两种排列都认
                if (Math.Abs(buffer[i] - b) + Math.Abs(buffer[i + 1] - g) + Math.Abs(buffer[i + 2] - r) < 40
                    || Math.Abs(buffer[i] - r) + Math.Abs(buffer[i + 1] - g) + Math.Abs(buffer[i + 2] - b) < 40) n++;
            return n;
        }

        [AvaloniaFact]
        public void Idle_DisarmsFrameCallback()
        {
            var rig = new Rig();
            Assert.False(rig.Cell.HasPendingFrame);
            rig.Cell.RequestUpdate(_ => { });
            Assert.True(rig.Cell.HasPendingFrame);
            rig.Frame();
            Assert.False(rig.Cell.HasPendingFrame);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void Hover_PublishesHit_AndCrosshairGoesToOverlaySurface()
        {
            var rig = new Rig();
            int staticBefore = rig.Cell.StaticSurface.PictureCount;

            rig.Move(200, 150);
            var hit = rig.Hit;
            Assert.NotNull(hit);
            int first = hit!.Value.Hit.LogicalIndex;
            Assert.InRange(first, 70, 99);
            Assert.True(rig.Cell.OverlaySurface.PictureCount > 0);

            // 往右移,命中跟着往右
            rig.Move(300, 150);
            Assert.True(rig.Hit!.Value.Hit.LogicalIndex > first);
            Assert.Equal(staticBefore, rig.Cell.StaticSurface.PictureCount);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void Drag_PansViewport()
        {
            var rig = new Rig();
            var before = rig.ActiveRange;
            rig.Drag(300, 100);
            var after = rig.ActiveRange;
            // 往左拖 = 看更新的数据,但视口已贴右沿(Hard),所以往右拖再验证往左看历史
            rig.Drag(100, 300);
            var back = rig.ActiveRange;
            Assert.True(back.Min < after.Min, $"往右拖应看更早的 K 线: {after} → {back}");
            Assert.Equal(before.Span, back.Span, 3);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void Wheel_ZoomsViewport()
        {
            var rig = new Rig();
            var before = rig.ActiveRange;
            rig.Window.MouseMove(new Point(200, 150));
            rig.Window.MouseWheel(new Point(200, 150), new Vector(0, 1));
            rig.Frame();
            var zoomedIn = rig.ActiveRange;
            Assert.True(zoomedIn.Span < before.Span, $"滚轮上滚应放大(可见根数变少): {before} → {zoomedIn}");

            rig.Window.MouseWheel(new Point(200, 150), new Vector(0, -1));
            rig.Window.MouseWheel(new Point(200, 150), new Vector(0, -1));
            rig.Frame();
            Assert.True(rig.ActiveRange.Span > zoomedIn.Span);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void TemplateReplaced_RemovesLayers_AndReleasesPictures()
        {
            var rig = new Rig();
            var candle = rig.Cell.Engine.StaticLayers.Single(l => l is CandleLayer);
            Assert.NotNull(candle.HostVisual);

            rig.Cell.Template = null;
            Assert.Empty(rig.Cell.ActiveLayers);
            Assert.Null(candle.HostVisual);
            Assert.Equal(0, rig.Cell.StaticSurface.PictureCount);
            rig.Window.Close();
        }

        [AvaloniaFact]
        public void Tooltip_RegisteredAsAvaloniaLayer()
        {
            var services = ChartHostServices.Of(new AvaloniaChartCell());
            Assert.Same(AvaloniaChartHost.Services, services);
            Assert.IsType<AvaloniaTooltipLayer>(services.CreateTooltipLayer!());
            Assert.Contains(services, ChartHostServices.All);
            Assert.Same(typeof(AvaloniaChartCell).Assembly, services.HostAssembly);
        }

        // 背景透明 / 窗口底色之外的像素数(K 线实体、影线)
        private static int CountInkPixels(Bitmap frame)
        {
            var size = frame.PixelSize;
            int stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            unsafe
            {
                fixed (byte* p = buffer) frame.CopyPixels(new PixelRect(size), (nint)p, buffer.Length, stride);
            }
            byte b0 = buffer[0], g0 = buffer[1], r0 = buffer[2];
            int ink = 0;
            for (int i = 0; i < buffer.Length; i += 4)
                if (Math.Abs(buffer[i] - b0) + Math.Abs(buffer[i + 1] - g0) + Math.Abs(buffer[i + 2] - r0) > 60) ink++;
            return ink;
        }
    }
}
