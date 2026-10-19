using System.Collections.Generic;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Feature 侧"读取即依赖":<see cref="FeatureContext.UsePort{T}"/> 首次读取时隐式订阅,之后按版本令牌判脏。
    /// 直接驱动 FeatureContext(不经过 schema),把 UsePort 的语义钉死:
    /// 只在端口版本变化时返回 IsChanged,订阅只登记一次,FullPass 强制全部视为变脏。
    /// </summary>
    public sealed class UsePortDirtyTrackingTests : System.IDisposable
    {
        private readonly DataPort<double> _a = new("A");
        private readonly DataPort<double> _b = new("B");
        private readonly DataBlackboard _board = new();
        private readonly SubscriptionRegistry _registry = new();
        private readonly ProbeFeature _feature = new("f");
        private readonly FeatureContext _ctx = new();

        public UsePortDirtyTrackingTests()
        {
            _ctx._registry = _registry;
            _ctx._currentFeature = _feature;
        }

        public void Dispose() => _board.Dispose();

        // 模拟一次 Feature.Project:只刷新帧级字段,_portTokens 跨帧保留(跟 Feature.Project 一致)。
        private (double Value, bool IsChanged) Use(DataPort<double> port, bool fullPass = false)
        {
            _ctx._isFullPass = fullPass;
            _ctx.BeginProject(IncrementalTestKit.NewRenderContext(), _board);
            using (_board.AcquireReadLock()) return _ctx.UsePort(port);
        }

        private bool PopDirty()
        {
            var set = new HashSet<Feature>();
            return _registry.PopDirtyFeatures(set) && set.Contains(_feature);
        }

        [Fact]
        public void FirstRead_IsChanged_AndSubscribesFeature()
        {
            _board.Write(_a, 1.0);

            var (value, changed) = Use(_a);

            Assert.Equal(1.0, value);
            Assert.True(changed);
            Assert.True(_registry.NotifyPortUpdated(_a)); // 已登记为 _a 的订阅者
            Assert.True(PopDirty());
        }

        [Fact]
        public void RepeatedRead_WithoutWrite_IsNotChanged()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            Assert.False(Use(_a).IsChanged);
            Assert.False(Use(_a).IsChanged);
        }

        [Fact]
        public void WriteSameValue_IsNotChanged_NewValue_IsChangedExactlyOnce()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            _board.Write(_a, 1.0); // WriteIfChanged 值防抖,令牌不动
            Assert.False(Use(_a).IsChanged);

            _board.Write(_a, 2.0);
            var (value, changed) = Use(_a);
            Assert.Equal(2.0, value);
            Assert.True(changed);
            Assert.False(Use(_a).IsChanged);
        }

        [Fact]
        public void ForceWriteSameValue_IsChanged()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            using (_board.BeginTransaction()) _board.ForceWrite(_a, 1.0);

            Assert.True(Use(_a).IsChanged);
        }

        [Fact]
        public void UnrelatedPortWrite_DoesNotAffectIsChanged()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            _board.Write(_b, 5.0);

            Assert.False(Use(_a).IsChanged);
        }

        [Fact]
        public void FullPass_ForcesIsChanged_WithoutVersionChange()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            Assert.True(Use(_a, fullPass: true).IsChanged);
            Assert.False(Use(_a).IsChanged); // FullPass 不污染令牌,下一帧恢复增量
        }

        [Fact]
        public void PortNeverWritten_FirstReadIsChanged_ThenStable()
        {
            var (value, changed) = Use(_a);
            Assert.Equal(default, value);
            Assert.True(changed);
            Assert.False(Use(_a).IsChanged);

            // 首次写入后照常判脏
            _board.Write(_a, 3.0);
            Assert.True(Use(_a).IsChanged);
        }

        [Fact]
        public void Subscription_IsRegisteredOnlyOnFirstSight_ResetReEnablesIt()
        {
            _board.Write(_a, 1.0);
            Use(_a);

            // 订阅被外部摘除(Transact 移除 Feature 时 Registry.UnsubscribeAll)后,
            // 只要 _portTokens 还认识这个端口,UsePort 不会重新订阅 —— 订阅只在"首次见到端口"时登记。
            _registry.UnsubscribeAll(_feature);
            Use(_a);
            Assert.False(_registry.NotifyPortUpdated(_a));

            // Decompose 走 FeatureContext.Reset 清空令牌,重装后首帧重新订阅
            _ctx.Reset();
            _ctx._registry = _registry;
            _ctx._currentFeature = _feature;
            Assert.True(Use(_a).IsChanged);
            Assert.True(_registry.NotifyPortUpdated(_a));
        }

        // 一帧:总是读 A,readB 为 true 时才读 B;返回 B 的读取结果(没读则 null)。
        private (double Value, bool IsChanged)? ProjectFrame(bool readB)
        {
            _ctx._isFullPass = false;
            _ctx.BeginProject(IncrementalTestKit.NewRenderContext(), _board);
            using (_board.AcquireReadLock())
            {
                _ctx.UsePort(_a);
                if (readB) return _ctx.UsePort(_b);
                return null;
            }
        }

        [Fact]
        public void ConditionalUsePort_PortWritesDoNotWakeFeature_UntilFirstRead()
        {
            // 订阅在"首次读取"时登记:分支没走到过,这个 Feature 还用不到 B,B 的写入不唤醒它是对的。
            // 分支一旦走到(被别的端口唤醒后),B 首次读到 IsChanged = true,之后照常订阅。
            _board.ObservePorts(p => _registry.NotifyPortUpdated(p));
            ProjectFrame(readB: false);
            PopDirty();

            _board.Write(_b, 1.0);
            Assert.False(PopDirty());

            _board.Write(_a, 1.0);
            Assert.True(PopDirty());
            var b = ProjectFrame(readB: true);
            Assert.Equal(1.0, b!.Value.Value);
            Assert.True(b.Value.IsChanged);

            _board.Write(_b, 2.0);
            Assert.True(PopDirty());
        }

        [Fact]
        public void ConditionalUsePort_SkippedFrames_IsChangedComparesWithLastRead()
        {
            _board.Write(_b, 1.0);
            Assert.True(ProjectFrame(readB: true)!.Value.IsChanged);

            // 跳过几帧、期间 B 没变:再读到时不算变
            ProjectFrame(readB: false);
            ProjectFrame(readB: false);
            Assert.False(ProjectFrame(readB: true)!.Value.IsChanged);

            // 跳过的帧里 B 变了两次:再读到时只报一次变化,拿到最新值
            ProjectFrame(readB: false);
            _board.Write(_b, 2.0);
            ProjectFrame(readB: false);
            _board.Write(_b, 3.0);
            ProjectFrame(readB: false);
            var b = ProjectFrame(readB: true)!.Value;
            Assert.Equal(3.0, b.Value);
            Assert.True(b.IsChanged);
            Assert.False(ProjectFrame(readB: true)!.Value.IsChanged);

            // 写回跳过前读到的同一个值也算变:比的是版本令牌,不是值
            ProjectFrame(readB: false);
            _board.Write(_b, 4.0);
            _board.Write(_b, 3.0);
            Assert.True(ProjectFrame(readB: true)!.Value.IsChanged);
        }

        [Fact]
        public void UsePort_CallOrderAcrossFrames_DoesNotAffectResult()
        {
            _board.Write(_a, 1.0);
            _board.Write(_b, 1.0);
            Use(_a);
            Use(_b);

            _board.Write(_b, 2.0);

            // 换个顺序读:令牌按端口对象存,B 变了 A 没变
            var b = Use(_b);
            var a = Use(_a);
            Assert.True(b.IsChanged);
            Assert.Equal(2.0, b.Value);
            Assert.False(a.IsChanged);
        }

        [Fact]
        public void UsePort_SamePortTwiceInOneFrame_OnlyFirstSeesChange()
        {
            _board.Write(_a, 1.0);
            Use(_a);
            _board.Write(_a, 2.0);

            Assert.True(Use(_a).IsChanged);
            Assert.False(Use(_a).IsChanged);
        }

        // 模拟 Feature.OnProject 里的 UseMemo 调用位置。方法名必须是 OnProject:Debug 下 UseMemo 按 [CallerMemberName] 查调用方。
        private static class MemoSites
        {
            // 两个固定调用位置,dep 为 null 表示这一帧走了另一条分支、没调到它。
            public static ((int, bool)? Twice, (int, bool)? Neg) OnProject(FeatureContext ctx, int? twiceDep, int? negDep)
            {
                (int, bool)? twice = null, neg = null;
                if (twiceDep.HasValue) twice = ctx.UseMemo(twiceDep.Value, static d => d * 2);
                if (negDep.HasValue) neg = ctx.UseMemo(negDep.Value, static d => -d);
                return (twice, neg);
            }

            // 循环里一个调用位置跑 n 轮:第二轮落在同一 key 上。
            public static void OnProject(FeatureContext ctx, int rounds)
            {
                for (int i = 0; i < rounds; i++)
                    ctx.UseMemo(i, static d => d);
            }
        }

        // Feature 里的 helper:调用方类型是 Feature,但方法不是 OnProject。
        private abstract class HelperFeature : Feature
        {
            public static int Calc(FeatureContext ctx) => ctx.UseMemo(1, static d => d).Value;
        }

        // 不在 Feature 里的对象借 ctx 存缓存。
        private sealed class LabelProvider
        {
            public string GetFormat(FeatureContext ctx) => ctx.UseMemo(2, static d => "F" + d).Value;
        }

        private void NextFrame() => _ctx.BeginProject(IncrementalTestKit.NewRenderContext(), _board);

        [Fact]
        public void UseMemo_SkippedInSomeFrames_KeepsItsOwnState()
        {
            NextFrame();
            var r0 = MemoSites.OnProject(_ctx, 1, 1);
            Assert.Equal((2, true), r0.Twice);
            Assert.Equal((-1, true), r0.Neg);

            // 依赖不变:都命中缓存
            NextFrame();
            var r1 = MemoSites.OnProject(_ctx, 1, 1);
            Assert.Equal((2, false), r1.Twice);
            Assert.Equal((-1, false), r1.Neg);

            // 某帧跳过 Neg;再调时依赖没变仍命中,变了才重算
            NextFrame();
            var r2 = MemoSites.OnProject(_ctx, 1, null);
            Assert.Equal((2, false), r2.Twice);
            Assert.Null(r2.Neg);
            NextFrame();
            var r3 = MemoSites.OnProject(_ctx, 1, 1);
            Assert.Equal((2, false), r3.Twice);
            Assert.Equal((-1, false), r3.Neg);
            NextFrame();
            var r4 = MemoSites.OnProject(_ctx, 1, 5);
            Assert.Equal((2, false), r4.Twice);
            Assert.Equal((-5, true), r4.Neg);
        }

        // 同一 Feature 的 ctx 驱动两个同类 tick provider(例如一个 Feature 同时管左右两根轴):
        // provider 的缓存若放在 ctx.UseMemo 里,key 是 provider 源文件行号,两个实例会撞成一份,
        // 依赖每次交替 → 每帧重建策略。缓存在 provider 自己身上才各管各的。
        [Fact]
        public void TwoTickProvidersOfSameType_OnOneContext_DoNotShareState()
        {
            _ctx.BeginProject(IncrementalTestKit.NewRenderContext(), _board);
            var left = new NumericTickProvider("F2");
            var right = new NumericTickProvider("F0");
            var schedLeft = new ScheduleTickProvider(() => 10, i => System.DateTime.MinValue.AddMinutes(i));
            var schedRight = new ScheduleTickProvider(() => 20, i => System.DateTime.MinValue.AddHours(i));

            var leftStrategy = left.GetStrategy(_ctx);
            var leftStyle = left.GetStyle(_ctx);
            var schedStrategy = schedLeft.GetStrategy(_ctx);
            for (int frame = 0; frame < 3; frame++)
            {
                Assert.NotSame(leftStrategy, right.GetStrategy(_ctx));
                Assert.Same(leftStrategy, left.GetStrategy(_ctx));
                Assert.Equal("2", right.GetStyle(_ctx).FormatLabel(1.5));
                Assert.Same(leftStyle, left.GetStyle(_ctx));
                Assert.NotSame(schedStrategy, schedRight.GetStrategy(_ctx));
                Assert.Same(schedStrategy, schedLeft.GetStrategy(_ctx));
            }
            Assert.Equal("1.50", leftStyle.FormatLabel(1.5));
        }

#if DEBUG
        [Fact]
        public void UseMemo_InFeatureHelper_ThrowsHelperCaseInDebug()
        {
            NextFrame();
            var ex = Assert.Throws<System.InvalidOperationException>(() => HelperFeature.Calc(_ctx));
            Assert.StartsWith("[UseMemo 写在 helper 里] ProbeFeature.Calc", ex.Message);
            Assert.Contains($"UsePortDirtyTrackingTests.cs 第 {LineOf("Calc(FeatureContext ctx)")} 行", ex.Message);
            Assert.Contains("static d => Calc(d)", ex.Message);
            Assert.Contains("「UseMemo 报错怎么改」", ex.Message);
        }

        [Fact]
        public void UseMemo_OutsideFeature_ThrowsNotInFeatureCaseInDebug()
        {
            NextFrame();
            var ex = Assert.Throws<System.InvalidOperationException>(() => new LabelProvider().GetFormat(_ctx));
            Assert.StartsWith("[UseMemo 不在 Feature 里] LabelProvider.GetFormat", ex.Message);
            Assert.Contains($"第 {LineOf("GetFormat(FeatureContext ctx)")} 行", ex.Message);
            Assert.Contains("当前帧属于 ProbeFeature", ex.Message);
            Assert.Contains("_strategy ??=", ex.Message);
        }

        [Fact]
        public void UseMemo_SameCallSiteTwiceInOneFrame_ThrowsWithBothDepsInDebug()
        {
            NextFrame();
            MemoSites.OnProject(_ctx, rounds: 1);
            NextFrame();
            var ex = Assert.Throws<System.InvalidOperationException>(() => MemoSites.OnProject(_ctx, rounds: 2));
            Assert.StartsWith("[UseMemo 同一帧执行了两次] ProbeFeature", ex.Message);
            Assert.Contains($"第 {LineOf("ctx.UseMemo(i, static d => d);")} 行", ex.Message);
            Assert.Contains("第一次依赖 = 0,第二次依赖 = 1", ex.Message);
            Assert.Contains("_cache[i]", ex.Message);
        }

        // 本文件里第一处包含 marker 的代码行行号(跳过注释与断言自身)。
        private static int LineOf(string marker, [System.Runtime.CompilerServices.CallerFilePath] string file = "")
        {
            var lines = System.IO.File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//") || t.Contains("LineOf(")) continue;
                if (lines[i].Contains(marker) && lines[i].Contains("UseMemo")) return i + 1;
            }
            throw new System.InvalidOperationException("找不到 " + marker);
        }

        [Fact]
        public void Hooks_OutsideProject_ThrowInDebug()
        {
            _board.Write(_a, 1.0);
            // 首帧之前(OnCompose / Watch 回调里)
            Assert.Throws<System.InvalidOperationException>(() => _ctx.UsePort(_a));
            Assert.Throws<System.InvalidOperationException>(() => _ctx.UseMemo(1, static d => d));

            Use(_a);
            _ctx.EndProject();
            // 帧结束之后(事件回调里)
            Assert.Throws<System.InvalidOperationException>(() => _ctx.UsePort(_a));
            Assert.Throws<System.InvalidOperationException>(() => _ctx.UsePorts(new[] { _a }, new double[1]));
        }

        [Fact]
        public void FeatureProject_ClearsProjectingFlag_AfterFrame() => IncrementalTestKit.RunOnSta(() =>
        {
            // Feature 把 ctx 存下来、在帧外(事件回调里)再用:Project 结束后入口守卫要拦住。
            FeatureContext? seen = null;
            var feature = new ProbeFeature("f") { OnProjectAction = (_, c) => { seen = c; c.UsePort(_a); } };
            var h = new SchemaHarness(feature);
            h.Board.Write(_a, 1.0);
            h.InvalidateEnvironment();
            h.ProjectOnly();

            Assert.NotNull(seen);
            Assert.False(seen!._isProjecting);
            Assert.Throws<System.InvalidOperationException>(() => seen.UsePort(_a));
        });
#endif
    }
}
