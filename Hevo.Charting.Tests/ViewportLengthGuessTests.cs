using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 视口管家对"逻辑长度变了"的处理,两条路径并排:
    /// <list type="bullet">
    ///   <item>旧路径(发布方不描述变化):只看到长度变长,靠"视图当时在哪"猜前插还是追加
    ///         (贴右或左侧 ≤ HistoryLoadTolerance 就整体平移,否则不动)。两个猜错的时机钉在 Currently* 用例里,行为保持不变。</item>
    ///   <item>新路径(发布方带 <see cref="DataChange"/>):前插总是平移、追加只在贴右时跟随、整段替换回首屏,不再猜。</item>
    /// </list>
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

            public Rig(ViewportManagerFeature? vpm = null)
            {
                // 同分页 K 线的常见配置(贴右开屏、两端硬钳)
                var schema = new VpmSchema
                {
                    Vpm = vpm ?? new ViewportManagerFeature
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

            /// <summary>旧发布方:只写长度。</summary>
            public void SetLength(int n) { using (Board.WriteScope()) Board.Write(Vp.LogicalLength, n); }

            /// <summary>新发布方:长度 + 变化描述(同 ProjectExtent 的写法)。</summary>
            public void Publish(int n, DataChange change) { using (Board.WriteScope()) Vp.WriteLength(Board, n, change); }

            public void Pan(double min) { using (Board.WriteScope()) Board.Write(Vp.UserRange, new RealRange(min, min + Span)); }
            public RealRange Active { get { using (Board.ReadScope()) return Board.Read(Vp.ActiveRange); } }
        }

        // ── 旧路径(不描述变化):行为保持不变 ──

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

            // 应为 400(原来那几根右移 300);旧路径不动,停在 100 —— 画面跳到早 300 根的位置
            Assert.Equal(100, rig.Active.Min, 6);
        });

        [Fact]
        public void Append_WhileViewingLeftBuffer_CurrentlyDriftsByOneBar() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(200);
            rig.Pan(10);                       // 停在最早的一段历史上看
            rig.SetLength(201);                // 盘中新来一根(追加在最右)

            // 应为 10(追加不影响左边);旧路径被当成前插,右移 1 根。每来一根 bar 就被推走一根
            Assert.Equal(11, rig.Active.Min, 6);
        });

        // ── 新路径(带变化描述) ──

        [Fact]
        public void Prepend_AfterUserPannedAway_WithChange_DoesNotJump() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);
            rig.Pan(20);
            rig.Pan(100);
            rig.Publish(500, DataChange.Prepend(300));

            Assert.Equal(400, rig.Active.Min, 6);
        });

        [Fact]
        public void Append_WhileViewingLeftBuffer_WithChange_DoesNotDrift() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);
            rig.Pan(10);
            rig.Publish(201, DataChange.Append(1));

            Assert.Equal(10, rig.Active.Min, 6);
        });

        [Fact]
        public void Append_WhileStuckRight_WithChange_Follows() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);   // 首屏贴右:[150, 199]
            rig.Publish(201, DataChange.Append(1));

            Assert.Equal(200, rig.Active.Max, 6);
            Assert.Equal(Span, rig.Active.Span, 6);
        });

        [Fact]
        public void TailUpdate_WithChange_KeepsUserPosition() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);
            rig.Pan(100);
            rig.Publish(200, DataChange.UpdateTail);

            Assert.Equal(100, rig.Active.Min, 6);
        });

        [Fact]
        public void Reset_SameLength_WithChange_ReturnsToFirstScreen() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(300, DataChange.Reset);
            rig.Pan(40);
            rig.Publish(300, DataChange.Reset);   // 换合约,新旧都是 300 根(数据先留着、到了再整段替换)

            Assert.Equal(299, rig.Active.Max, 6);
            Assert.Equal(Span, rig.Active.Span, 6);
        });

        [Fact]
        public void PrependAndAppend_InOneCommit_ShiftsByPrependWhenNotStuckRight() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);
            rig.Pan(100);
            // 两次发布在同一次提交里合并(前插 50 + 追加 1):只按前插平移
            using (rig.Board.WriteScope())
            {
                rig.Vp.WriteLength(rig.Board, 250, DataChange.Prepend(50));
                rig.Vp.WriteLength(rig.Board, 251, DataChange.Append(1));
            }

            Assert.Equal(150, rig.Active.Min, 6);
        });

        [Fact]
        public void LengthWrittenWithoutChange_FallsBackToLengthDiff() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);
            rig.Pan(10);
            rig.SetLength(201);                // 有人绕过变化描述只写了长度:按旧逻辑猜(当成前插,右移 1)

            Assert.Equal(11, rig.Active.Min, 6);

            rig.Publish(202, DataChange.Append(1));   // 下一笔先对齐记录(仍按长度差),之后回到新路径
            rig.Publish(203, DataChange.Append(1));
            Assert.Equal(12, rig.Active.Min, 6);
        });

        // 数据源带变化发布 → ProjectExtent 写长度和变化 → 视口按变化处理(整条链路)
        private sealed class PagedSource : BufferedDataSource<PagedSource, int>
        {
            public void Set(int length, DataChange change)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < length; i++) _buffer.Add(i);
                    Publish(change);
                }
            }
        }

        private sealed class PipeSchema : ChartReactiveSchema
        {
            public PagedSource Source { get; } = new();
            protected override void DefineDataFlow(IChartCell chart) =>
                Source.Pipe().ProjectExtent(ViewportPorts.RequireAttached(chart)).BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas) => canvas.Add(new ViewportManagerFeature
            {
                Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
            });
        }

        [Fact]
        public void DataSourceChange_FlowsThroughPipe_PrependDoesNotJump() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = new ChartCell();
            var schema = new PipeSchema();
            cell.Template = schema;
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
            var vp = ViewportPorts.RequireAttached(cell);


            schema.Source.Set(200, DataChange.Reset);

            // 拿到管线黑板:ViewportManager 的 Watch 挂在它上面
            var board = schema.CurrentBoard!;
            using (board.WriteScope()) board.Write(vp.UserRange, new RealRange(100, 100 + Span));
            schema.Source.Set(500, DataChange.Prepend(300));

            using (board.ReadScope()) Assert.Equal(400, board.Read(vp.ActiveRange).Min, 6);
        });

        [Fact]
        public void ChangeCountMismatchingLength_FallsBackToLengthDiff() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.Publish(200, DataChange.Reset);
            rig.Pan(100);
            rig.Publish(200, DataChange.Append(1));   // 说追加了 1 根,长度却没变(按自己的口径计数):不平移

            Assert.Equal(100, rig.Active.Min, 6);
        });

        [Fact]
        public void KeepVisibleCountOnReset_ReusesSpanAfterClear() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(new ViewportManagerFeature
            {
                Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
                KeepVisibleCountOnReset = true,
            });
            rig.Publish(500, DataChange.Reset);
            using (rig.Board.WriteScope()) rig.Board.Write(rig.Vp.UserRange, new RealRange(300, 399));   // 缩放到 100 根
            rig.Publish(0, DataChange.Reset);     // 切周期:先清空
            rig.Publish(400, DataChange.Reset);

            Assert.Equal(399, rig.Active.Max, 6);
            Assert.Equal(99, rig.Active.Span, 6);
        });

        [Fact]
        public void InitialRightPadding_LeavesBlankOnFirstScreen() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig(new ViewportManagerFeature
            {
                Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
                InitialRightPadding = 5, OverscrollMax = OverscrollPolicy.Overscroll,
            });
            rig.Publish(200, DataChange.Reset);

            Assert.Equal(204, rig.Active.Max, 6);
            Assert.Equal(Span, rig.Active.Span, 6);
        });
    }
}
