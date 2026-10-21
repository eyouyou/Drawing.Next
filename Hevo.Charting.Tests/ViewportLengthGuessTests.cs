using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 视口管家只看到"逻辑长度变长了",不知道是前插历史还是追加新 bar,靠"视图当时在哪"猜
    /// (ViewportManagerFeature.Clamp:贴右或左侧 ≤ HistoryLoadTolerance 就整体平移,否则不动)。
    /// 这里把猜错的两个时机钉住:断言写的是现状，测试名标明"现在会跳";框架能区分前插 / 追加后，把断言换成"不跳"。
    /// 对照组确认翻页的常规路径(在左侧缓冲区内回包)是对的。
    /// </summary>
    public sealed class ViewportLengthGuessTests
    {
        private const int Span = 49;   // DefaultVisibleCount 50

        private sealed class VpmSchema : ChartReactiveSchema
        {
            public WorkflowTrigger<DataBlackboard> Trigger { get; } = new();
            public required ViewportManagerFeature Vpm { get; init; }
            protected override void DefineDataFlow(IChartCell chart) => Trigger.BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas) => canvas.Add(Vpm);
        }

        private sealed class Rig
        {
            public readonly ChartCell Cell = new();
            public readonly DataBlackboard Board = new();
            public readonly ViewportPorts Vp;

            public Rig()
            {
                // 同分页 K 线的常见配置(贴右开屏、两端硬钳)
                var schema = new VpmSchema
                {
                    Vpm = new ViewportManagerFeature
                    {
                        Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
                        OverscrollMin = OverscrollPolicy.Hard, OverscrollMax = OverscrollPolicy.Hard,
                    },
                };
                Cell.Template = schema;
                using (var ctx = Cell.CreateContext()) schema.ComposeAll(Cell, ctx);
                schema.Trigger.Push(Board);
                Vp = ViewportPorts.RequireAttached(Cell);
            }

            public void SetLength(int n) { using (Board.WriteScope()) Board.Write(Vp.LogicalLength, n); }
            public void Pan(double min) { using (Board.WriteScope()) Board.Write(Vp.UserRange, new RealRange(min, min + Span)); }
            public RealRange Active { get { using (Board.ReadScope()) return Board.Read(Vp.ActiveRange); } }
        }

        [Fact]
        public void Prepend_WhileInsideLeftBuffer_KeepsSameBars() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(200);
            rig.Pan(20);                       // 进了左侧缓冲区(≤ 30),分页此时发请求
            rig.SetLength(500);                // 回包前插 300 根

            // 视图整体右移 300:看到的还是原来那几根
            Assert.Equal(320, rig.Active.Min, 6);
        });

        [Fact]
        public void Prepend_AfterUserPannedAway_CurrentlyJumpsByPageSize() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(200);
            rig.Pan(20);                       // 触发翻页
            rig.Pan(100);                      // 回包前用户往右拖出了缓冲区
            rig.SetLength(500);                // 回包前插 300 根

            // 应为 400(原来那几根右移 300);现状不动，停在 100 —— 画面跳到早 300 根的位置
            Assert.Equal(100, rig.Active.Min, 6);
        });

        [Fact]
        public void Append_WhileViewingLeftBuffer_CurrentlyDriftsByOneBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(200);
            rig.Pan(10);                       // 停在最早的一段历史上看
            rig.SetLength(201);                // 盘中新来一根(追加在最右)

            // 应为 10(追加不影响左边);现状被当成前插，右移 1 根。每来一根 bar 就被推走一根
            Assert.Equal(11, rig.Active.Min, 6);
        });
    }
}
