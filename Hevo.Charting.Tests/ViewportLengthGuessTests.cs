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
    /// <para>
    /// 下半部分是新路径:数据源发了 DataExtent(每根的键)时视口按键找回位置,同样两个时机不跳、不漂。
    /// 上面三条旧断言原样保留,证明不发 Extent 的数据源行为没变。
    /// </para>
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
                Vpm = schema.Vpm;
            }

            public readonly ViewportManagerFeature Vpm;

            public void SetLength(int n) { using (Board.WriteScope()) Board.Write(Vp.LogicalLength, n); }

            /// <summary>同 ProjectExtent:长度和 Extent 同一事务写。</summary>
            public void SetKeys(long[] keys, object epoch, bool olderExhausted = false)
            {
                using (Board.WriteScope())
                {
                    Board.Write(Vp.Extent, new DataExtent(epoch, keys, olderExhausted));
                    Board.Write(Vp.LogicalLength, keys.Length);
                }
            }

            public long? AnchorKey { get { using (Board.ReadScope()) return Board.Read(Vp.AnchorKey); } }
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

        // ================= 新路径:按键锚定 =================

        private static long[] Keys(long first, int count)
        {
            var k = new long[count];
            for (int i = 0; i < count; i++) k[i] = first + i;
            return k;
        }

        [Fact]
        public void Keyed_Prepend_AfterUserPannedAway_KeepsSameBars() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);
            rig.Pan(20);
            rig.Pan(100);                              // 回包前拖出缓冲区(旧路径这里会跳)
            rig.SetKeys(Keys(700, 500), epoch);        // 前插 300 根

            Assert.Equal(400, rig.Active.Min, 6);
            Assert.Equal(1100, rig.AnchorKey);
        });

        [Fact]
        public void Keyed_Append_WhileViewingLeftBuffer_DoesNotDrift() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);
            rig.Pan(10);
            rig.SetKeys(Keys(1000, 201), epoch);       // 追加一根(旧路径这里会被推走一根)

            Assert.Equal(10, rig.Active.Min, 6);
        });

        [Fact]
        public void Keyed_Append_AtRightEdge_FollowsLatest() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);       // 首屏贴右 [150, 199]
            rig.SetKeys(Keys(1000, 201), epoch);

            Assert.Equal(200, rig.Active.Max, 6);
            Assert.Equal(Span, rig.Active.Span, 6);
        });

        [Fact]
        public void Keyed_PrependAndAppendTogether_AtRightEdge_FollowsLatest() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);
            rig.SetKeys(Keys(700, 501), epoch);        // 前插 300 + 追加 1,一次到达

            Assert.Equal(500, rig.Active.Max, 6);
        });

        [Fact]
        public void Keyed_FillGapBeforeView_ViewStaysOnSameBars() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            var even = new long[200];
            for (int i = 0; i < even.Length; i++) even[i] = i * 2;      // 0, 2, 4, …, 398
            rig.SetKeys(even, epoch);
            rig.Pan(100);                                                // 左边那根键 = 200

            var filled = new long[201];
            for (int i = 0, j = 0; i < filled.Length; i++) filled[i] = i == 26 ? 51 : even[j++];   // 视口左侧补进一根
            rig.SetKeys(filled, epoch);

            Assert.Equal(101, rig.Active.Min, 6);
            Assert.Equal(200, rig.AnchorKey);
        });

        [Fact]
        public void Keyed_NewEpochSameKeys_KeepsPosition() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(Keys(1000, 200), new object());
            rig.Pan(60);
            rig.SetKeys(Keys(1000, 200), new object());   // 复权:键没变、整段换

            Assert.Equal(60, rig.Active.Min, 6);
        });

        [Fact]
        public void Keyed_KeysLengthMismatch_FallsBackToLengthGuess() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetLength(200);
            rig.Pan(20);
            rig.Pan(100);
            using (rig.Board.WriteScope())
            {
                rig.Board.Write(rig.Vp.Extent, new DataExtent(new object(), Keys(700, 499), false));   // 少一根:对不上
                rig.Board.Write(rig.Vp.LogicalLength, 500);
            }

            Assert.Equal(100, rig.Active.Min, 6);   // 同旧路径
            Assert.Null(rig.AnchorKey);
        });

        [Fact]
        public void ScrollToKey_Loaded_CentersTarget() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            rig.SetKeys(Keys(1000, 200), new object());
            rig.Vpm.ScrollToKey(1060);

            Assert.Equal(60, (rig.Active.Min + rig.Active.Max) / 2, 6);
            Assert.Equal(Span, rig.Active.Span, 6);
        });

        [Fact]
        public void ScrollToKey_NotLoaded_StaysLeftThenLandsWhenOlderArrives() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);
            rig.Vpm.ScrollToKey(500);

            Assert.Equal(0, rig.Active.Min, 6);        // 先停最左
            Assert.Equal(500, rig.AnchorKey);          // 分页模块据此补到 500

            rig.SetKeys(Keys(400, 800), epoch);        // 补到了 400
            Assert.Equal(100, (rig.Active.Min + rig.Active.Max) / 2, 6);

            rig.SetKeys(Keys(0, 1200), epoch);         // 又前插一页:仍盯着目标
            Assert.Equal(500, (rig.Active.Min + rig.Active.Max) / 2, 6);
        });

        [Fact]
        public void ScrollToKey_BeforeEarliest_StopsAtFirstWhenExhausted() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);
            rig.Vpm.ScrollToKey(10);
            rig.SetKeys(Keys(900, 300), epoch, olderExhausted: true);   // 最早只到 900

            Assert.Equal(0, rig.Active.Min, 6);
            Assert.Equal(900, rig.AnchorKey);
        });

        [Fact]
        public void ScrollToKey_UserDragsBeforeArrival_DragWins() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            var epoch = new object();
            rig.SetKeys(Keys(1000, 200), epoch);
            rig.Vpm.ScrollToKey(500);
            rig.Pan(80);                               // 补拉途中用户拖走
            rig.SetKeys(Keys(800, 400), epoch);        // 回包前插 200

            Assert.Equal(280, rig.Active.Min, 6);      // 跟着拖动的那几根,不再去目标
            Assert.Equal(1080, rig.AnchorKey);
        });

        // ---- 整条链路:数据源 Publish(DataExtent) → ProjectExtent → 视口 ----

        private sealed class KeyedSource : BufferedDataSource<KeyedSource, long>
        {
            public void Set(long[] keys, object epoch)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    _buffer.AddRange(keys);
                    Publish(new DataExtent(epoch, keys, false));
                }
            }

            /// <summary>切合约:空帧紧接新数据,背靠背两次发布。</summary>
            public void SwitchBackToBack(long[] keys, object epoch)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    Publish();
                    _buffer.AddRange(keys);
                    Publish(new DataExtent(epoch, keys, false));
                }
            }
        }

        private sealed class PipeSchema : ChartReactiveSchema
        {
            public KeyedSource Source { get; } = new();
            protected override void DefineDataFlow(IChartCell chart) =>
                Source.Pipe().ProjectExtent(ViewportPorts.RequireAttached(chart)).BindTo(chart);
            protected override void DefineFeatures(IFeatureContext canvas) => canvas.Add(new ViewportManagerFeature
            {
                Alignment = ViewportAlignment.RightEdge, DefaultVisibleCount = 50, MinVisibleCount = 5,
            });
        }

        private static (PipeSchema Schema, ViewportPorts Vp) BuildPipe()
        {
            var cell = new ChartCell();
            var schema = new PipeSchema();
            cell.Template = schema;
            using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
            return (schema, ViewportPorts.RequireAttached(cell));
        }

        [Fact]
        public void Pipe_PublishExtent_PrependDoesNotJump() => IncrementalTestKit.RunOnSta(() =>
        {
            var (schema, vp) = BuildPipe();
            var epoch = new object();
            schema.Source.Set(Keys(1000, 200), epoch);
            var board = schema.CurrentBoard!;
            using (board.WriteScope()) board.Write(vp.UserRange, new RealRange(100, 100 + Span));
            schema.Source.Set(Keys(700, 500), epoch);

            using (board.ReadScope()) Assert.Equal(400, board.Read(vp.ActiveRange).Min, 6);
        });

        [Fact]
        public void Pipe_SwitchContextBackToBack_ViewportSeesZeroLength_ThenInitialRange() => IncrementalTestKit.RunOnSta(() =>
        {
            var (schema, vp) = BuildPipe();
            schema.Source.Set(Keys(1000, 200), new object());
            var board = schema.CurrentBoard!;
            using (board.WriteScope()) board.Write(vp.UserRange, new RealRange(60, 60 + Span));

            var lengths = new List<int>();
            using var watch = board.AddWatch(new object[] { vp.LogicalLength }, b => lengths.Add(b.Read(vp.LogicalLength)));

            // 新合约的键和旧合约重叠:视口要是没看到归零,会按锚点停在 60,而不是回首屏
            schema.Source.SwitchBackToBack(Keys(1000, 300), new object());

            Assert.Equal(new[] { 0, 300 }, lengths);
            using (board.ReadScope()) Assert.Equal(new RealRange(250, 299), board.Read(vp.ActiveRange));
        });
    }
}
