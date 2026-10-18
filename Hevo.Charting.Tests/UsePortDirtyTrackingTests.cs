using System.Collections.Generic;
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
            _board.OnPortUpdated += p => _registry.NotifyPortUpdated(p);
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

        // 各自只有一个调用位置,从不同顺序调用时仍落在同一份状态上。
        private (int Value, bool IsChanged) MemoTwice(int dep) => _ctx.UseMemo(dep, static d => d * 2);
        private (int Value, bool IsChanged) MemoNeg(int dep) => _ctx.UseMemo(dep, static d => -d);

        [Fact]
        public void UseMemo_CallOrderAndSkippedCalls_DoNotAffectResult()
        {
            Assert.Equal((2, true), MemoTwice(1));
            Assert.Equal((-1, true), MemoNeg(1));

            // 换顺序、依赖不变:都命中缓存
            Assert.Equal((-1, false), MemoNeg(1));
            Assert.Equal((2, false), MemoTwice(1));

            // 某帧跳过 MemoNeg;再调时依赖没变仍命中,变了才重算
            Assert.Equal((2, false), MemoTwice(1));
            Assert.Equal((-1, false), MemoNeg(1));
            Assert.Equal((-5, true), MemoNeg(5));
            Assert.Equal((2, false), MemoTwice(1));
        }
    }
}
