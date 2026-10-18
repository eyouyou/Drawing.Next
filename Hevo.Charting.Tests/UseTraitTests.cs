using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// <c>ctx.For(layer).Use&lt;T&gt;()</c> / <c>ctx.Shared().Use&lt;T&gt;()</c>:读 Trait,并告诉你和本 Feature 上一帧读到时比变没变。
    /// 直接驱动 FeatureContext,同一个 RenderContext 跨帧复用(共享区 / 图层的 live 袋子跟生产里一样是长寿的)。
    /// </summary>
    public sealed class UseTraitTests : System.IDisposable
    {
        private readonly DataBlackboard _board = new();
        private readonly ProbeFeature _feature = new("f");
        private readonly FeatureContext _ctx = new();
        private readonly RenderContext _render = NewRenderContext();

        public UseTraitTests()
        {
            _ctx._registry = new SubscriptionRegistry();
            _ctx._currentFeature = _feature;
        }

        public void Dispose() => _board.Dispose();

        private void Publish(ProbeTraitA trait) => _render.Shared().PublishData(trait);

        private (ProbeTraitA? Value, bool IsChanged) UseShared()
        {
            _ctx.BeginProject(_render, _board);
            try { return _ctx.Shared().Use<ProbeTraitA>(); }
            finally { _ctx.EndProject(); }
        }

        [Fact]
        public void FirstRead_IsChanged_ThenStableWhileNotRepublished()
        {
            var t = new ProbeTraitA(1);
            Publish(t);

            var (value, changed) = UseShared();
            Assert.Same(t, value);
            Assert.True(changed);

            Assert.False(UseShared().IsChanged);
            Assert.False(UseShared().IsChanged);
        }

        [Fact]
        public void Republish_IsChanged_ComparedByReference()
        {
            var t = new ProbeTraitA(1);
            Publish(t);
            UseShared();

            // 同一个引用再发一次:没变
            Publish(t);
            Assert.False(UseShared().IsChanged);

            // 值相等的新 record 也算变:比的是引用,发布方换了实例就是换了
            var t2 = new ProbeTraitA(1);
            Publish(t2);
            var (value, changed) = UseShared();
            Assert.Same(t2, value);
            Assert.True(changed);
            Assert.False(UseShared().IsChanged);
        }

        [Fact]
        public void NeverPublished_FirstReadIsChanged_ThenStable_UntilPublished()
        {
            var (value, changed) = UseShared();
            Assert.Null(value);
            Assert.True(changed);
            Assert.False(UseShared().IsChanged);

            Publish(new ProbeTraitA(3));
            Assert.True(UseShared().IsChanged);
        }

        [Fact]
        public void Reset_ReportsChangedAgain()
        {
            Publish(new ProbeTraitA(1));
            UseShared();
            Assert.False(UseShared().IsChanged);

            // Decompose 走 FeatureContext.Reset:Transact 重装后首帧要重新报变化
            _ctx.Reset();
            _ctx._currentFeature = _feature;
            Assert.True(UseShared().IsChanged);
        }

        [Fact]
        public void SameTraitTwiceInOneFrame_OnlyFirstSeesChange()
        {
            Publish(new ProbeTraitA(1));
            _ctx.BeginProject(_render, _board);
            Assert.True(_ctx.Shared().Use<ProbeTraitA>().IsChanged);
            Assert.False(_ctx.Shared().Use<ProbeTraitA>().IsChanged);
            _ctx.EndProject();
        }

        [Fact]
        public void Scopes_AndTraitTypes_AreTrackedSeparately() => RunOnSta(() =>
        {
            var layer1 = new ProbeLayer("l1");
            var layer2 = new ProbeLayer("l2");
            var shared = new ProbeTraitA(0);
            _render.Shared().PublishData(shared);
            _render.For(layer1).PublishData(new ProbeTraitA(1));
            _render.For(layer2).PublishData(new ProbeTraitA(2));

            void Frame(System.Action body)
            {
                _ctx.BeginProject(_render, _board);
                try { body(); } finally { _ctx.EndProject(); }
            }

            Frame(() =>
            {
                Assert.True(_ctx.Shared().Use<ProbeTraitA>().IsChanged);
                Assert.True(_ctx.For(layer1).Use<ProbeTraitA>().IsChanged);
                Assert.True(_ctx.For<ProbeLayer>(layer2).Use<ProbeTraitA>().IsChanged);
                Assert.True(_ctx.For(layer1).Use<ProbeTraitB>().IsChanged);
            });

            // 只换 layer1 的 Trait:另外几处不报
            _render.For(layer1).PublishData(new ProbeTraitA(10));
            Frame(() =>
            {
                Assert.False(_ctx.Shared().Use<ProbeTraitA>().IsChanged);
                var (v, changed) = _ctx.For(layer1).Use<ProbeTraitA>();
                Assert.True(changed);
                Assert.Equal(10, v!.Value);
                Assert.False(_ctx.For(layer2).Use<ProbeTraitA>().IsChanged);
                Assert.False(_ctx.For(layer1).Use<ProbeTraitB>().IsChanged);
            });

            // 不同 Feature 各记各的:另一个 ctx 第一次读到照样报变化
            var other = new FeatureContext { _currentFeature = new ProbeFeature("g") };
            other.BeginProject(_render, _board);
            Assert.True(other.For(layer1).Use<ProbeTraitA>().IsChanged);
            other.EndProject();
        });

        [Fact]
        public void ProxyFromRenderContext_HasNoFeature_UseThrows()
        {
            // RenderContext 直接给的代理(OnCompose / Layer 侧)不知道是谁在读,只能 Read
            Assert.Throws<System.InvalidOperationException>(() => _render.Shared().Use<ProbeTraitA>());
        }

#if DEBUG
        [Fact]
        public void Use_OutsideProject_ThrowsInDebug()
        {
            Publish(new ProbeTraitA(1));
            UseShared();
            // 帧结束之后(事件回调里存下 ctx 再用)
            var ex = Assert.Throws<System.InvalidOperationException>(() => _ctx.Shared().Use<ProbeTraitA>());
            Assert.Contains("Use<T>", ex.Message);
            // Read 是纯读取,帧外照样能用
            Assert.NotNull(_ctx.Shared().Read<ProbeTraitA>());
        }
#endif

        [Fact]
        public void TraitChange_DoesNotWakeFeature_OnlyReportsWhenItRuns() => RunOnSta(() =>
        {
            // Use<T> 只报告不唤醒:writer 被端口叫起、发了新 Trait,reader 没订阅任何端口,这一帧不跑;
            // 等它下次被叫起(这里是环境纪元变化 → FullPass),Use 才报出"跟上次读到的比变了"。
            var port = new DataPort<double>("P");
            var seen = new List<(int Frame, bool Changed)>();
            var writer = new ProbeFeature("writer", FeaturePhase.Scale)
            {
                OnProjectAction = (_, ctx) =>
                {
                    var (v, _) = ctx.UsePort(port);
                    ctx.Shared().PublishData(new ProbeAnchorTrait((int)v));
                },
            };
            var reader = new ProbeFeature("reader", FeaturePhase.Interaction)
            {
                OnProjectAction = (_, ctx) =>
                {
                    var (t, changed) = ctx.Shared().Use<ProbeAnchorTrait>();
                    seen.Add((t?.Frame ?? -1, changed));
                },
            };
            var h = new SchemaHarness(writer, reader);

            h.InvalidateEnvironment();
            h.Frame();
            Assert.Equal(new[] { (0, true) }, seen);

            h.Board.Write(port, 1.0);
            h.Frame();
            Assert.Equal(2, writer.ProjectCount);
            Assert.Single(seen); // reader 没被叫起

            h.InvalidateEnvironment();
            h.Frame();
            // FullPass 里 writer 又发了一个新实例,reader 读到的是本帧的,跟上次(0)比变了
            Assert.Equal((1, true), seen[1]);
        });
    }
}
