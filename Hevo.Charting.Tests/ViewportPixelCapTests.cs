using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// ViewportManagerFeature.MinBarSpacingPx:可见根数卡在绘图区物理宽度以内,任何图层拿到的视口都不会过密。
    /// 默认关(0),不设就跟原来一样;开了覆盖首次布局、窗口缩放后重算、贴右边、缩放交互读到的上限。
    /// </summary>
    public sealed class ViewportPixelCapTests
    {
        private sealed class VpSchema : ChartReactiveSchema
        {
            private readonly double? _spacing;
            private readonly WorkflowTrigger<DataBlackboard> _trigger = new();
            public ViewportPorts Ports { get; private set; } = null!;

            public VpSchema(double? spacing) => _spacing = spacing;

            protected override void DefineDataFlow(ChartCell chart) => _trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                // null = 不传参数,走默认值
                canvas.Environment(env =>
                {
                    if (_spacing is { } spacing)
                        env.SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge, minBarSpacingPx: spacing);
                    else
                        env.SetupViewport(minVisibleCount: 10, alignment: ViewportAlignment.RightEdge);
                });
                Ports = ViewportPorts.RequireAttached(Chart);
            }

            public void Push(DataBlackboard board) => _trigger.Push(board);
        }

        private sealed class Harness
        {
            public ChartCell Cell { get; } = new();
            public VpSchema Schema { get; }
            public DataBlackboard Board { get; } = new();

            public Harness(double? spacing, int length)
            {
                Schema = new VpSchema(spacing);
                Cell.Template = Schema;
                using (var ctx = Cell.CreateContext()) Schema.ComposeAll(Cell, ctx);
                Schema.Push(Board);
                Write(Schema.Ports.LogicalLength, length);
            }

            public void Write<T>(DataPort<T> port, T value)
            {
                using (Board.WriteScope()) Board.Write(port, value);
            }

            public RealRange Active
            {
                get { using (Board.ReadScope()) return Board.Read(Schema.Ports.ActiveRange); }
            }

            /// <summary>模拟 SizeChanged,再跑两帧:第一帧全量投影发现尺寸变化,第二帧帧首按新绘图区重算。</summary>
            public void Resize(double width)
            {
                Cell.RequestUpdate(ctx =>
                {
                    ctx.Shared().PublishData(new ViewportSizeTrait(width, 400));
                    Schema.InvalidateEnvironment();
                });
                Cell.RunFrameNow(PlotMode.Sync);
                Cell.RunFrameNow(PlotMode.Sync);
            }

            /// <summary>当前绘图区宽(DIP,测试里 DPI = 1 即物理像素)。Y 轴等边距由 schema 默认布局决定。</summary>
            public double PlotWidth
            {
                get { using var ctx = Cell.CreateContext(); return ctx.GetPlotArea().Width; }
            }

            public ViewportSpanLimitsTrait? Limits
            {
                get { using var ctx = Cell.CreateContext(); return ctx.Shared().Read<ViewportSpanLimitsTrait>(); }
            }
        }

        [Fact]
        public void ShowAll_IsCappedToPlotPixelWidth_AndSticksToRightEdge() => RunOnSta(() =>
        {
            var h = new Harness(spacing: 1.0, length: 100_000);
            Assert.Equal(99_999, h.Active.Span, 6); // 布局前不知道宽度,不限制

            h.Resize(1000);
            double cap = h.PlotWidth - 1;           // 可见根数 = 绘图区物理像素数
            Assert.InRange(cap, 500, 999);
            Assert.Equal(cap, h.Active.Span, 6);
            Assert.Equal(99_999, h.Active.Max, 6);  // 盯盘的最新一根还在右边
            Assert.Equal(cap, h.Limits!.MaxSpanByPixels, 6);
        });

        [Fact]
        public void Shrinking_Window_Reclamps_WithoutUserInput() => RunOnSta(() =>
        {
            var h = new Harness(spacing: 1.0, length: 100_000);
            h.Resize(1000);
            h.Resize(500);
            double narrow = h.PlotWidth - 1;
            Assert.Equal(narrow, h.Active.Span, 6);
            Assert.Equal(99_999, h.Active.Max, 6);

            // 放大窗口只放宽上限,不替用户改视口
            h.Resize(2000);
            Assert.Equal(narrow, h.Active.Span, 6);
            Assert.Equal(h.PlotWidth - 1, h.Limits!.MaxSpanByPixels, 6);
        });

        [Fact]
        public void UserIntent_BeyondCap_IsClamped() => RunOnSta(() =>
        {
            var h = new Harness(spacing: 1.0, length: 100_000);
            h.Resize(1000);
            h.Write(h.Schema.Ports.UserRange, new RealRange(10_000, 60_000));
            Assert.Equal(h.PlotWidth - 1, h.Active.Span, 6);
        });

        [Fact]
        public void Default_IsOff_ShowAllStaysUncapped() => RunOnSta(() =>
        {
            var h = new Harness(spacing: null, length: 100_000);
            h.Resize(1000);
            Assert.Equal(99_999, h.Active.Span, 6);
            Assert.True(double.IsPositiveInfinity(h.Limits?.MaxSpanByPixels ?? double.PositiveInfinity));
        });

        [Fact]
        public void Spacing_Zero_DisablesCap() => RunOnSta(() =>
        {
            var h = new Harness(spacing: 0, length: 100_000);
            h.Resize(1000);
            Assert.Equal(99_999, h.Active.Span, 6);
            Assert.True(double.IsPositiveInfinity(h.Limits?.MaxSpanByPixels ?? double.PositiveInfinity));
        });

        [Fact]
        public void SmallData_IsUnaffected() => RunOnSta(() =>
        {
            var h = new Harness(spacing: 1.0, length: 500);
            h.Resize(1000);
            Assert.Equal(499, h.Active.Span, 6);
        });

        [Theory]
        [InlineData(1000, 1.0, 1.0, 999)]
        [InlineData(1000, 1.5, 1.0, 1499)]   // 150% DPI:物理像素多一半,能放更多根
        [InlineData(1000, 1.0, 2.0, 499)]    // 每根 2px
        public void PixelMaxSpan_UsesPhysicalPixels(float width, double ppd, double spacing, double expected)
            => Assert.Equal(expected, ViewportManagerFeature.ComputePixelMaxSpan(new HevoRect(0, 0, width, 100), ppd, spacing), 6);

        [Fact]
        public void PixelMaxSpan_UnknownLayout_IsUnlimited()
        {
            Assert.True(double.IsPositiveInfinity(ViewportManagerFeature.ComputePixelMaxSpan(HevoRect.Empty, 1.0, 1.0)));
            Assert.True(double.IsPositiveInfinity(ViewportManagerFeature.ComputePixelMaxSpan(new HevoRect(0, 0, 1000, 100), 1.0, 0)));
        }
    }
}
