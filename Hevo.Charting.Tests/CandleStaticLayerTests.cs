using System;
using System.Linq;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// CandleFeature 冷热分离:静态层(历史段 [0, len-1))只在历史段的底层引用变了时重录。
    /// 列的发布约定 = 历史段交出去后不原地改,只有最后一根可以原地改(实时 tick 原地改最后一根再 ForceWrite)。
    /// 用静态层的前台缓冲是否换过判断"重录了没有"(ChartLayer 只在重录时交换前后台缓冲)。
    /// </summary>
    public sealed class CandleStaticLayerTests
    {
        private sealed class CandleSchema : ChartReactiveSchema
        {
            public CandlePorts Ports { get; } = new();
            public DataPort<RealRange> YRange { get; } = new("T_YRange");
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            protected override void DefineDataFlow(ChartCell chart) => Trigger.BindTo(chart);

            protected override void DefineFeatures(IFeatureContext canvas)
            {
                canvas.Seed<ScaleStrategyTrait>(ScaleStrategyTrait.CandleMode);
                canvas
                    .Environment(env => env.SetupViewport(minVisibleCount: 5, alignment: ViewportAlignment.RightEdge,
                        defaultVisibleCount: 50, overscrollMin: OverscrollPolicy.Hard, overscrollMax: OverscrollPolicy.Hard))
                    .Series(s => s.AddCandle(YRange, Ports));
            }
        }

        private sealed class Rig
        {
            private const int Capacity = 64;
            public readonly ChartCell Cell = new();
            public readonly CandleSchema Schema = new();
            public readonly DataBlackboard Board = new();
            public double[] Open = new double[Capacity], High = new double[Capacity], Low = new double[Capacity], Close = new double[Capacity];
            public DateTime[] Time = new DateTime[Capacity];
            public int Length = 30;
            private ViewportPorts _viewport = null!;
            private readonly System.Windows.Window _window;
            public void CloseWindow() => _window.Close();

            public Rig()
            {
                for (int i = 0; i < Capacity; i++)
                {
                    Time[i] = new DateTime(2026, 1, 1).AddMinutes(i);
                    Open[i] = 100 + i; Close[i] = 101 + i; High[i] = 103 + i; Low[i] = 98 + i;
                }
                // 跟真实图表一样经 OnApplyTemplate 装配:挂进窗口(屏幕外)再布局
                Cell.Template = Schema;
                Cell.Width = 400; Cell.Height = 300;
                _window = new System.Windows.Window
                {
                    Content = Cell, SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                    WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                    Left = -10000, Top = 0,
                };
                _window.Show();
                _window.UpdateLayout();
                _viewport = ViewportPorts.RequireAttached(Cell);
                Publish();
                Schema.Trigger.Push(Board);
                Schema.InvalidateEnvironment();
                Frame();
            }

            private ReadOnlyMemory<T> Slice<T>(T[] a) => new(a, 0, Length);

            /// <summary>跟真实列发布一样 ForceWrite:即使底层数组 / 长度都没变也推一次(列版本每次都变)。</summary>
            public void Publish()
            {
                using (Board.BeginTransaction())
                {
                    Board.ForceWrite(Schema.Ports.Time, Slice(Time));
                    Board.ForceWrite(Schema.Ports.Open, Slice(Open));
                    Board.ForceWrite(Schema.Ports.High, Slice(High));
                    Board.ForceWrite(Schema.Ports.Low, Slice(Low));
                    Board.ForceWrite(Schema.Ports.Close, Slice(Close));
                    Board.WriteIfChanged(_viewport.LogicalLength, Length);
                    Board.WriteIfChanged(Schema.YRange, new RealRange(50, 200));
                }
            }

            public void Frame() => Cell.RunFrameNow(PlotMode.Sync);

            // 图层名 = LayerName + "_Static" / "_Dynamic" + 装配时追加的实例后缀
            private ChartLayer Layer(string suffix)
                => Cell.ActiveLayers.OfType<ChartLayer>().Single(l => l.Name.StartsWith("CandleLayer" + suffix + "_", StringComparison.Ordinal));

            /// <summary>跑一帧,返回静态 / 动态层这一帧有没有重录。</summary>
            public (bool StaticRecorded, bool DynamicRecorded) Step(Action change)
            {
                var s0 = Layer("_Static").Buffer;
                var d0 = Layer("_Dynamic").Buffer;
                change();
                Publish();
                Frame();
                return (!ReferenceEquals(s0, Layer("_Static").Buffer), !ReferenceEquals(d0, Layer("_Dynamic").Buffer));
            }

            /// <summary>写时复制:换一套新数组(内容相同)。</summary>
            public void CopyOnWrite()
            {
                Open = (double[])Open.Clone(); High = (double[])High.Clone();
                Low = (double[])Low.Clone(); Close = (double[])Close.Clone();
            }
        }

        [Fact]
        public void Tick_LastBarChangedInPlace_KeepsStaticLayer() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var (staticRecorded, dynamicRecorded) = rig.Step(() =>
            {
                int last = rig.Length - 1;
                rig.Close[last] += 1.5;
                rig.High[last] = Math.Max(rig.High[last], rig.Close[last]);
            });
            rig.CloseWindow();
            Assert.False(staticRecorded);
            Assert.True(dynamicRecorded);
        });

        [Fact]
        public void Append_RecordsStaticLayer() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            bool recorded = rig.Step(() => rig.Length++).StaticRecorded; // 原最后一根进入历史段
            rig.CloseWindow();
            Assert.True(recorded);
        });

        [Fact]
        public void CopyOnWrite_RecordsStaticLayer() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            bool recorded = rig.Step(rig.CopyOnWrite).StaticRecorded;
            rig.CloseWindow();
            Assert.True(recorded);
        });

        [Fact]
        public void CorrectingOlderBar_ThroughCopyOnWrite_RecordsStaticLayer() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            bool recorded = rig.Step(() =>
            {
                rig.CopyOnWrite();               // 发布约定:改历史段先换新数组
                rig.Close[rig.Length - 3] -= 2;
            }).StaticRecorded;
            rig.CloseWindow();
            Assert.True(recorded);
        });

        /// <summary>
        /// 红涨绿跌取色(DynamicColorTrait)装配时发布一次:跨帧是同一个实例(不每帧 new 闭包),
        /// 取色用调用方本帧传进来的列,数据变了颜色跟着变。
        /// </summary>
        [Fact]
        public void DynamicColor_PublishedOnce_UsesCallerData() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var dyn = rig.Cell.ActiveLayers.OfType<ChartLayer>().Single(l => l.Name.StartsWith("CandleLayer_Dynamic_", StringComparison.Ordinal));
            DynamicColorTrait? Trait() => rig.Cell.CreateContext().For(dyn).Read<DynamicColorTrait>();
            DoubleSeriesDataTrait? Data() => rig.Cell.CreateContext().For(dyn).Read<DoubleSeriesDataTrait>();

            var first = Trait();
            Assert.NotNull(first);
            int last = rig.Length - 1;
            var up = first!.Evaluator(Data()!, 3, last);

            rig.Step(() => rig.Close[last] = rig.Close[last - 1] - 5); // 最后一根收低于上一根
            var second = Trait();
            rig.CloseWindow();

            Assert.Same(first, second);
            Assert.Same(CandleStyle.Default.UpBrush, up);
            Assert.Same(CandleStyle.Default.DownBrush, second!.Evaluator(Data()!, 3, last));
        });

        /// <summary>
        /// 跨屏拖动 DPI 变了:列一根没动,静态层也要按新 DPI 重录(取整、LOD 都按设备像素算)。
        /// ChartCell.OnDpiChanged 走环境纪元 → FullPass,所有 UsePort 视为变脏。
        /// </summary>
        [Fact]
        public void DpiChange_RecordsStaticLayer_WithoutDataChange() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var onDpiChanged = typeof(ChartCell).GetMethod("OnDpiChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            bool recorded = rig.Step(() =>
                onDpiChanged.Invoke(rig.Cell, new object[] { new System.Windows.DpiScale(1, 1), new System.Windows.DpiScale(1.25, 1.25) })
            ).StaticRecorded;
            rig.CloseWindow();
            Assert.True(recorded);
        });

        /// <summary>
        /// 缓冲池复用:两帧之间先换成 B、再换回 A(A 被回收后改写了历史),A 的数组 / 起点 / 长度跟上次录制时一样。
        /// 按引用跨帧比较会漏掉;按引用版本比较每次换块都算变,照常重录。
        /// </summary>
        [Fact]
        public void PooledBufferSwappedBackWithinOneFrame_RecordsStaticLayer() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            bool recorded = rig.Step(() =>
            {
                double[] o = rig.Open, h = rig.High, l = rig.Low, c = rig.Close;
                rig.CopyOnWrite();
                rig.Publish();                   // 换成 B(这一帧还没跑)
                rig.Open = o; rig.High = h; rig.Low = l; rig.Close = c;
                rig.Close[rig.Length - 3] -= 2;  // A 被池子回收后重写:历史段变了
            }).StaticRecorded;                   // Step 再发布一次 A
            rig.CloseWindow();
            Assert.True(recorded);
        });

        /// <summary>
        /// 约定的反面:原地改历史段,底层引用没变,静态层不会重录(画面停在旧值)。
        /// 这条测试固定的是"只比引用、不比内容"这一行为本身,数据源必须遵守发布约定。
        /// </summary>
        [Fact]
        public void CorrectingOlderBar_InPlace_IsNotDetected() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            bool recorded = rig.Step(() => rig.Close[rig.Length - 3] -= 2).StaticRecorded;
            rig.CloseWindow();
            Assert.False(recorded);
        });
    }
}
