using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.Renderers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// ChartCell 一帧(RunSwappedFrame → ExecutePipeline)里对图层录制的三条保证:
    /// 整帧登记为列读者(录制期间摄入器不会回收它可能拿到的缓冲);录制前把 cell 的 DPI 写进脏图层;
    /// PlotMode.Parallel 下线程亲和的图层留在 UI 线程录制。
    /// 读写全局读者登记,不跟其它测试并行。
    /// </summary>
    [Collection(nameof(FramePipelineGuardCollection))]
    public sealed class FramePipelineGuardTests
    {
        private sealed class ProbeLayer : ChartLayer
        {
            private readonly bool _uiOnly;
            private readonly int _uiThread = Environment.CurrentManagedThreadId; // 在 UI(STA)线程上构造
            public ProbeLayer(string name, bool uiOnly = false) { _uiOnly = uiOnly; Level = ChartLayerType.Main; Name = name; }
            protected internal override bool RequiresUiThread => _uiOnly;

            public int ThreadId = -1;
            public bool EverRanOffUiThread;
            public double SeenPixelsPerDip = -1;
            public bool? ReclaimableDuringUpdate;

            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
            {
                ThreadId = Environment.CurrentManagedThreadId;
                if (ThreadId != _uiThread) EverRanOffUiThread = true;
                SeenPixelsPerDip = PixelsPerDip;
                // 刚退役的缓冲:本帧的读者登记在场时不能被回收
                long retiredAt = ColumnReadScope.Retire();
                ReclaimableDuringUpdate = ColumnReadScope.IsReclaimable(retiredAt);
                // 每个图层占一点时间,Parallel.ForEach 才会真的把任务摊到多个线程上;
                // 只自旋几微秒的话,2 核机器上调用线程常常一口气全做完,下面的对照断言就测不到 Parallel 路径
                Thread.Sleep(2);
            }
        }

        private static ChartCell NewCell() => new ChartCell()
        {
            Width = 200,
            Height = 100,
        }.WithShell();

        private static List<ProbeLayer> Add(ChartCell cell, int count, bool uiOnly)
        {
            var layers = new List<ProbeLayer>();
            for (int i = 0; i < count; i++)
            {
                var l = new ProbeLayer($"{(uiOnly ? "ui" : "any")}{i}", uiOnly);
                cell.AddUnmanagedLayer(l);
                layers.Add(l);
            }
            return layers;
        }

        private static void Frame(ChartCell cell, IEnumerable<ProbeLayer> layers, PlotMode mode)
        {
            foreach (var l in layers) l.MarkDirty();
            cell.RunFrameNow(mode);
        }

        [Fact]
        public void Frame_RegistersColumnReader_WhileLayersRecord() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell();
            var layers = Add(cell, 1, uiOnly: false);
            Frame(cell, layers, PlotMode.Sync);
            Assert.False(layers[0].ReclaimableDuringUpdate, "录制期间本帧应登记为列读者,刚退役的缓冲不能被回收");
        });

        [Fact]
        public void Frame_WritesCellDpiIntoDirtyLayers() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell();
            var layers = Add(cell, 1, uiOnly: false);
            // 跨屏拖动:ChartCell.OnDpiChanged 更新 cell 的 DPI
            typeof(ChartCell).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cell, new object[] { new DpiScale(1, 1), new DpiScale(1.5, 1.5) });
            Frame(cell, layers, PlotMode.Sync);
            Assert.Equal(1.5, layers[0].SeenPixelsPerDip);
        });

        [Fact]
        public void ParallelMode_KeepsUiThreadLayersOnUiThread() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = NewCell();
            var any = Add(cell, 8, uiOnly: false);
            var ui = Add(cell, 8, uiOnly: true);
            for (int round = 0; round < 5; round++)
            {
                Frame(cell, any.Concat(ui), PlotMode.Parallel);
                foreach (var l in ui) Assert.False(l.EverRanOffUiThread, $"{l.Name} 进了线程池");
            }
            // 对照:普通图层确实被摊到了线程池(否则这条测试测不到 Parallel 路径)。看 5 轮里有没有进过线程池,不只看最后一轮
            Assert.Contains(any, l => l.EverRanOffUiThread);
        });
    }

    [CollectionDefinition(nameof(FramePipelineGuardCollection), DisableParallelization = true)]
    public sealed class FramePipelineGuardCollection { }
}
