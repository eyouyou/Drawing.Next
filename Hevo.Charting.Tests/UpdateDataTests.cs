using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;
using Xunit;
using static Hevo.Charting.Tests.IncrementalTestKit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 公开的 <c>UpdateData</c> 扩展(图层 / 共享两条通道)必须发布 modifier 的返回值,而不是委托本身。
    /// 跨程序集调用时 internal 的同名实例方法看不见,只能绑到这两个扩展上 —— 测试工程照样走扩展(两个类型实参)。
    /// </summary>
    public sealed class UpdateDataTests
    {
        public sealed record CounterTrait(int Value) : IVisualTrait;

        [Fact]
        public void LayerUpdateData_PublishesModifierResult() => RunOnSta(() =>
        {
            var layer = new CounterLayer();
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx =>
            {
                var proxy = ctx.For(layer);
                LayerProxyExtensions.UpdateData<CounterLayer, CounterTrait>(proxy, old => new CounterTrait((old?.Value ?? 0) + 1));
                // 同一帧再改一次:读到的是本帧草稿里的旧值
                LayerProxyExtensions.UpdateData<CounterLayer, CounterTrait>(proxy, old => new CounterTrait((old?.Value ?? 0) + 1));

                Assert.Null(proxy.Read<Func<CounterTrait?, CounterTrait>>());
            });

            Assert.Equal(2, layer.LastSeen?.Value);

            // 跨帧:基于上一帧提交的值继续改
            driver.Frame(ctx => LayerProxyExtensions.UpdateData<CounterLayer, CounterTrait>(
                ctx.For(layer), old => new CounterTrait(old!.Value * 10)));
            Assert.Equal(20, layer.LastSeen?.Value);
        });

        [Fact]
        public void SharedUpdateData_PublishesModifierResult() => RunOnSta(() =>
        {
            var layer = new CounterLayer();
            var driver = new LayerFrameDriver(layer);

            driver.Frame(ctx =>
            {
                SharedProxyExtensions.UpdateData<IVisualData, CounterTrait>(
                    ctx.Shared(), old => new CounterTrait((old?.Value ?? 0) + 5));
                // 深度睡眠:图层要等第一份本层 Trait 才会被唤醒,这里随手发一个
                ctx.For(layer).PublishData(new ProbeTraitA(0));
            });
            Assert.Equal(5, layer.LastSeen?.Value);

            driver.Frame(ctx =>
            {
                var shared = ctx.Shared();
                SharedProxyExtensions.UpdateData<IVisualData, CounterTrait>(shared, old => new CounterTrait(old!.Value + 1));
                Assert.Equal(6, shared.Read<CounterTrait>()?.Value);
                Assert.Null(shared.Read<Func<CounterTrait?, CounterTrait>>());
            });
            Assert.Equal(6, layer.LastSeen?.Value);
        });

        // 测试工程不挂 TraitConsumerGenerator,IConsumes 只能手写(业务代码里交给生成器)。
        private sealed class CounterLayer : ChartLayer, IConsumes<CounterTrait>
        {
            public CounterTrait? LastSeen { get; private set; }

            protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
                => LastSeen = data.Get<CounterTrait>();
        }
    }
}
