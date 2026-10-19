using System;
using System.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Feature 装配期(OnCompose)里用 CombineLatest / Join / Merge:
    /// 这几个算子登记的变换在 ComposeAll 的 Build 里执行,变换不能再往正在遍历的变换列表里登记,
    /// 否则装配直接抛 "Collection was modified"。用例走真实 schema 装配路径,顺带验算子语义。
    /// </summary>
    public sealed class FeatureComposeOperatorTests
    {
        private static readonly DataPort<double> A = new("FC_A");
        private static readonly DataPort<double> B = new("FC_B");
        private static readonly DataPort<double> C = new("FC_C");

        private sealed class ComposeFeature : Feature
        {
            private readonly Action<IRenderFlow<DataBlackboard>> _compose;
            public ComposeFeature(Action<IRenderFlow<DataBlackboard>> compose) => _compose = compose;
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) => _compose(flow);
            protected override void OnProject(FeatureContext ctx) { }
        }

        [Fact]
        public void CombineLatest_InCompose_ComposesAndPassesOnceAllPortsWritten()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                using var fired = new ManualResetEventSlim(false);
                var feature = new ComposeFeature(flow =>
                    flow.CombineLatest(new object[] { A, B }, _ => fired.Set()));
                var h = new SchemaHarness(feature);

                h.Board.WriteLocked(A, 1.0);
                Assert.False(fired.Wait(200), "只写了一个端口不该放行");

                h.Board.WriteLocked(B, 1.0);
                Assert.True(fired.Wait(5000), "两个端口都写过后应放行");
            });
        }

        [Fact]
        public void Join_InCompose_ComposesAndBlocksUntilAllPortsWritten()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                int calls = 0;
                var feature = new ComposeFeature(flow =>
                    flow.Join(A, B).Watch(new object[] { C }, _ => calls++));
                var h = new SchemaHarness(feature); // 装配后推一次黑板:端口都没写,挡住

                h.Board.WriteLocked(A, 1.0);
                h.Schema.Trigger.Push(h.Board); // 只写了一个:挡住
                Assert.Equal(0, calls);

                h.Board.WriteLocked(B, 1.0);
                h.Schema.Trigger.Push(h.Board); // 写全:放行,Watch 首次执行
                Assert.Equal(1, calls);
            });
        }

        [Fact]
        public void Merge_InCompose_Composes()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var other = new WorkflowTrigger<DataBlackboard>();
                int calls = 0;
                var feature = new ComposeFeature(flow =>
                    flow.Merge(flow.Wrap(other)).Watch(new object[] { C }, _ => calls++));
                var h = new SchemaHarness(feature);

                Assert.Equal(1, calls);
            });
        }
    }
}
