using System.Windows;
using System.Windows.Controls;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Transact 热插拔的生命周期:
    /// 删掉的装配期 Feature 不能再收到 Watch 回调;新增 Feature 装配期发布的 trait 要进到图层;
    /// 新增 / 删除带外壳 UI(Decorate)的 Feature 要重建外壳。
    /// </summary>
    public sealed class SchemaTransactLifecycleTests
    {
        private static readonly DataPort<int> Port = new("TX_Port");

        private sealed class WatchFeature : Feature
        {
            public int Calls;
            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
                => flow.Watch(new object[] { Port }, _ => Calls++);
            protected override void OnProject(FeatureContext ctx) { }
        }

        private sealed class ComposeTraitFeature : Feature
        {
            public ProbeLayer Layer { get; } = new("tx-layer") { Reads = d => Seen = d.Get<ProbeTraitA>()?.Value };
            public static int? Seen;
            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
            {
                AttachLayer(Layer);
                ctx.For(Layer).PublishData(new ProbeTraitA(7));
            }
            protected override void OnProject(FeatureContext ctx) { }
        }

        private sealed class DecoratingFeature : Feature, IWpfDecorator
        {
            public const string Marker = "tx-decorated";
            protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
            protected override void OnProject(FeatureContext ctx) { }
            public UIElement Decorate(UIElement inner) => new Border { Tag = Marker, Child = inner };
        }

        [Fact]
        public void RemovedComposeTimeFeature_NoLongerReceivesWatch()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var watch = new WatchFeature();
                var h = new SchemaHarness(watch);
                int start = watch.Calls;
                h.Board.WriteLocked(Port, 1);
                Assert.Equal(start + 1, watch.Calls);

                h.Schema.Transact(c => c.Remove(watch));
                h.Board.WriteLocked(Port, 2);

                Assert.Equal(start + 1, watch.Calls);
            });
        }

        [Fact]
        public void RemovedThenReaddedFeature_ReceivesWatchOnce()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var watch = new WatchFeature();
                var h = new SchemaHarness(watch);
                h.Schema.Transact(c => c.Remove(watch));
                h.Schema.Transact(c => c.Add(watch));
                int before = watch.Calls;

                h.Board.WriteLocked(Port, 3);

                Assert.Equal(before + 1, watch.Calls);
            });
        }

        [Fact]
        public void TransactAddedFeature_ComposeTimeTrait_ReachesLayer()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                ComposeTraitFeature.Seen = null;
                var h = new SchemaHarness();
                var added = new ComposeTraitFeature();

                h.Schema.Transact(c => c.Add(added));
                h.Frame();

                Assert.Equal(7, ComposeTraitFeature.Seen);
            });
        }

        [Fact]
        public void TransactAddOrRemoveDecoratingFeature_RebuildsShell()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new ProbeSchema();
                var cell = new ChartCell { Template = schema, Width = 300, Height = 200 };
                var window = new Window
                {
                    Content = cell, SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                    Left = -10000, Top = 0,
                };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    Assert.False(HasMarker(cell));

                    var deco = new DecoratingFeature();
                    schema.Transact(c => c.Add(deco));
                    Assert.True(HasMarker(cell), "Transact 新增带 Decorate 的 Feature 后外壳没有重建");

                    schema.Transact(c => c.Remove(deco));
                    Assert.False(HasMarker(cell), "Transact 删除带 Decorate 的 Feature 后外壳还在");
                }
                finally { window.Close(); }
            });
        }

        private static bool HasMarker(DependencyObject root)
        {
            if (root is FrameworkElement { Tag: DecoratingFeature.Marker }) return true;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (HasMarker(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) return true;
            if (root is Decorator { Child: { } c } && System.Windows.Media.VisualTreeHelper.GetChildrenCount(root) == 0)
                return HasMarker(c);
            return false;
        }
    }
}
