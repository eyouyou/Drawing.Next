using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DocSamples.BareCell;
using Hevo.Charting.DocSamples.Bollinger;
using Hevo.Charting.DocSamples.QuickStart;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.Renderers;
using Hevo.Charting.Samples;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 文档示例(Hevo.Charting.Tests/DocSamples)的回归:开发规范 §5 布林带 SOP 与《15 分钟上手》L1 / L2 引用的代码和 JSON
    /// 必须真的能跑出文档说的结果。文档改了示例、示例改了文档,都靠这里兜住。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class DocSampleTests
    {
        // ── 公共工具 ────────────────────────────────────────────────────────

        private static double[] Prices(int n)
        {
            var r = new double[n];
            for (int i = 0; i < n; i++) r[i] = 100 + 10 * Math.Sin(i * 0.2) + i * 0.1;
            return r;
        }

        private static void WaitFor(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
        }

        private static double[] ReadPort(DataBlackboard board, DataPort<ReadOnlyMemory<double>> port)
        {
            using (board.ReadScope()) return board.Read(port).ToArray();
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Drawing.Next.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"找不到 {path}");
            return path;
        }

        private static void AssertSameSeries(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                if (double.IsNaN(expected[i])) Assert.True(double.IsNaN(actual[i]), $"[{i}] 应为 NaN,实际 {actual[i]}");
                else Assert.Equal(expected[i], actual[i], 9);
            }
        }

        // ── 布林带 SOP ─────────────────────────────────────────────────────

        [Fact]
        public void BollingerMath_MatchesNaiveDefinition()
        {
            var close = Prices(80);
            int n = close.Length;
            var u = new double[n]; var m = new double[n]; var l = new double[n];
            BollingerMath.Compute(close, 20, 2.0, u, m, l);

            for (int i = 0; i < n; i++)
            {
                if (i < 19) { Assert.True(double.IsNaN(m[i])); continue; }
                var window = close.Skip(i - 19).Take(20).ToArray();
                double mean = window.Average();
                double sd = Math.Sqrt(window.Sum(x => (x - mean) * (x - mean)) / 20);
                Assert.Equal(mean, m[i], 9);
                Assert.Equal(mean + 2 * sd, u[i], 6);   // Σx² 滑窗有浮点相消,6 位足够
                Assert.Equal(mean - 2 * sd, l[i], 6);
            }
        }

        [Theory]
        [InlineData(BollingerComputeStyle.Pipeline)]
        [InlineData(BollingerComputeStyle.ComputeFeature)]
        [InlineData(BollingerComputeStyle.ComputeNode)]
        public void BollingerSchema_AllThreeComputeStyles_ProduceSameBands(BollingerComputeStyle style)
        {
            var close = Prices(120);
            int n = close.Length;
            var eu = new double[n]; var em = new double[n]; var el = new double[n];
            BollingerMath.Compute(close, 20, 2.0, eu, em, el);

            double[]? u = null, m = null, l = null;
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new BollingerSampleSchema(style);
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                schema.Source.Load(close);

                WaitFor(() => schema.CurrentBoard is { } b && ReadPort(b, schema.Bands.Lower).Length == n);
                var board = schema.CurrentBoard;
                Assert.NotNull(board);
                u = ReadPort(board!, schema.Bands.Upper);
                m = ReadPort(board!, schema.Bands.Middle);
                l = ReadPort(board!, schema.Bands.Lower);
            });

            AssertSameSeries(eu, u!);
            AssertSameSeries(em, m!);
            AssertSameSeries(el, l!);
        }

        [Fact]
        public void BollingerSchema_TransactToggle_AddsAndRemovesRenderFeature()
        {
            IncrementalTestKit.RunOnSta(() =>
            {
                var schema = new BollingerSampleSchema();
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);
                Assert.Single(schema.ListFeatures().OfType<BollingerFeature>());

                schema.SetBollingerVisible(false);
                Assert.Empty(schema.ListFeatures().OfType<BollingerFeature>());

                schema.SetBollingerVisible(true);
                Assert.Single(schema.ListFeatures().OfType<BollingerFeature>());
            });
        }

        private sealed class TraitBag : IVisualData
        {
            private readonly Dictionary<Type, object> _traits = new();
            public TraitBag With<T>(T trait) where T : class { _traits[typeof(T)] = trait; return this; }
            public T? Get<T>() where T : class => _traits.TryGetValue(typeof(T), out var v) ? (T)v : null;
            public void Publish<T>(T snapshot) where T : class => _traits[typeof(T)] = snapshot;
        }

        [Fact]
        public void BollingerLayer_RecordsThreeSeparatePolylines()
        {
            var close = Prices(60);
            int n = close.Length;
            var u = new double[n]; var m = new double[n]; var l = new double[n];
            BollingerMath.Compute(close, 20, 2.0, u, m, l);

            IncrementalTestKit.RunOnSta(() =>
            {
                var layer = new BollingerLayer();
                var data = new TraitBag()
                    .With(new BollingerDataTrait(u, m, l))
                    .With(BollingerStyle.Create(HevoColors.Orange))
                    .With(ScaleStrategyTrait.LineMode)
                    .With(new PlotAreaTrait(new HevoRect(0, 0, 400, 200)))
                    .With(new XAxisTrait(new RealRange(0, n - 1)))
                    .With(new YAxisTrait(new RealRange(80, 130)));

                layer.MarkDirty();
                layer.Update(data);
                layer.SwapBuffer();

                var cmds = ((LayerBuffer)layer.Buffer).Drawing.Commands;
                Assert.Equal(3, cmds.Count);
                Assert.All(cmds, c => Assert.Equal(DrawOp.DrawPolyline, c.Op));
                var lists = cmds.Select(c => (List<HevoPoint>)c.RefData!).ToArray();
                // 暖机期 19 个 NaN 不画,剩下 n - 19 个点
                Assert.All(lists, pts => Assert.Equal(n - 19, pts.Count));
                // 三条线各用一块点缓冲,不能互相覆盖
                Assert.Equal(3, lists.Distinct(ReferenceEqualityComparer.Instance).Count());
            });
        }

        // ── 06 号文档「填充」:DrawBand 区间填色 ───────────────────────────

        private struct FigureCount : IFigureWriter
        {
            public int Figures;
            public void Begin(HevoPoint p) => Figures++;
            public void LineTo(HevoPoint p) { }
            public void End() { }
        }

        [Fact]
        public void RangeShadeLayer_RecordsOneClippedBand_WithGapAndTwoSides()
        {
            // 价格围绕阈值 100 上下穿越;前 10 根暖机期 NaN,第 30 根停牌缺口
            var close = Prices(60);
            int n = close.Length;
            var threshold = new double[n];
            for (int i = 0; i < n; i++) threshold[i] = 100;
            for (int i = 0; i < 10; i++) close[i] = double.NaN;
            close[30] = double.NaN;

            IncrementalTestKit.RunOnSta(() =>
            {
                var layer = new Hevo.Charting.DocSamples.Fill.RangeShadeLayer();
                var data = new TraitBag()
                    .With(new Hevo.Charting.DocSamples.Fill.RangeShadeTrait(close, threshold))
                    .With(Hevo.Charting.DocSamples.Fill.RangeShadeStyle.Create(System.Windows.Media.Colors.Green, System.Windows.Media.Colors.Red))
                    .With(ScaleStrategyTrait.LineMode)
                    .With(new PlotAreaTrait(new HevoRect(0, 0, 400, 200)))
                    .With(new XAxisTrait(new RealRange(0, n - 1)))
                    .With(new YAxisTrait(new RealRange(80, 130)));

                layer.MarkDirty();
                layer.Update(data);
                layer.SwapBuffer();

                var cmds = ((LayerBuffer)layer.Buffer).Drawing.Commands;
                Assert.Equal(new[] { DrawOp.PushClip, DrawOp.DrawBand, DrawOp.Pop }, cmds.Select(c => c.Op));
                var band = Assert.IsType<HevoBand>(cmds[1].RefData);
                Assert.Equal(n, band.Count); // NaN 原样留在带子里(断带),不跳过
                Assert.NotNull(band.CrossedBrush);

                var up = band.Upper.ToArray();
                var lo = band.Lower.ToArray();
                var whole = new FigureCount();
                FillGeometry.Band(up, lo, 0, ref whole);
                Assert.Equal(2, whole.Figures); // 缺口两侧各一段
                var above = new FigureCount();
                var below = new FigureCount();
                FillGeometry.Band(up, lo, +1, ref above);
                FillGeometry.Band(up, lo, -1, ref below);
                Assert.True(above.Figures > 0 && below.Figures > 0, "价格在阈值上下都有,两种颜色都该出图形");
            });
        }

        // ── 15 分钟上手 L2:C# 指标类 ──────────────────────────────────────

        [Fact]
        public void QuickStartIndicators_RegisterWithExpectedDelegateShapes()
        {
            var reg = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(QuickStartIndicators));
            Assert.IsType<ComputeInto>(reg.TryGet("qs_sma20"));
            Assert.IsType<Func<ReadOnlyMemory<double>, object>>(reg.TryGet("qs_channel"));
        }

        [Fact]
        public void QuickStartIndicators_ComputeExpectedValues()
        {
            var close = Prices(40);
            var sma = new double[close.Length];
            QuickStartIndicators.Sma20(close, sma);
            Assert.True(double.IsNaN(sma[18]));
            Assert.Equal(close.Skip(20).Take(20).Average(), sma[39], 9);

            var dict = (IDictionary<string, object?>)QuickStartIndicators.Channel20(close);
            var upper = (double[])dict["upper"]!;
            var lower = (double[])dict["lower"]!;
            Assert.Equal(close.Skip(20).Max(), upper[39]);
            Assert.Equal(close.Skip(20).Min(), lower[39]);
        }

        // ── 15 分钟上手 L1 / L2:蓝图 JSON 片段 ──────────────────────────────

        private static List<FeatureModel> LoadSnippet(string file)
        {
            var json = File.ReadAllText(RepoFile("Hevo.Charting.Tests", "DocSamples", "QuickStart", file));
            var nodes = JsonSerializer.Deserialize<List<FeatureModel>>(json, BlueprintJsonOptions.Default);
            Assert.NotNull(nodes);
            Assert.NotEmpty(nodes!);
            return nodes!;
        }

        /// <summary>Demo 默认 dashboard(数据源就是 Demo 用的 MockKLineDataSource),主图追加文档里的片段 —— 即新人粘贴后的样子。</summary>
        private static Dashboard DemoDashboardWith(List<FeatureModel> snippet)
        {
            ComponentRegistry.Register<MockKLineDataSource>();
            var json = File.ReadAllText(RepoFile("Hevo.Drawing.LowCodeDemo", "Assets", "default_kline_dashboard.json"));
            var dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default)!;
            dashboard.Cells.Single(c => c.Id == "main").Blueprint.Features.AddRange(snippet);
            return dashboard;
        }

        [Theory]
        [InlineData("l1_triple_ma.json")]
        [InlineData("l2_csharp_indicators.json")]
        public void QuickStartSnippet_PastedIntoDemoMainCell_DryRunHasNoErrors(string file)
        {
            var dashboard = DemoDashboardWith(LoadSnippet(file));
            var r = DashboardLauncher.DryRun(dashboard);
            Assert.True(r.Error == null, r.Error);
            var errors = r.Diagnostics.Where(d => d.Severity == BlueprintDiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        [Fact]
        public void QuickStartL1_PythonIndicatorExistsInDemo()
        {
            // L1 不写代码,引用的是 Demo 自带的 Python 指标;名字和 series 改了这里要跟着改文档
            var py = File.ReadAllText(RepoFile("Hevo.Drawing.LowCodeDemo", "PyIndicators", "pine_demo.py"));
            Assert.Contains("@register('pine_triple_ma'", py);
            Assert.Contains("@indicator('pine_triple_ma'", py);
        }

        [Fact]
        public void QuickStartL2_Blueprint_RunsAndWritesIndicatorPorts()
        {
            var close = Prices(100);
            int n = close.Length;
            var expectedSma = new double[n];
            QuickStartIndicators.Sma20(close, expectedSma);
            var expectedChannel = (IDictionary<string, object?>)QuickStartIndicators.Channel20(close);

            var bp = DemoDashboardWith(LoadSnippet("l2_csharp_indicators.json")).Cells.Single(c => c.Id == "main").Blueprint;
            var handlers = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(QuickStartIndicators));
            var ds = new MockKLineDataSource();

            double[]? sma = null, upper = null;
            IncrementalTestKit.RunOnSta(() =>
            {
                var deferred = BlueprintRunner.BuildSchemaDeferred(bp, new Dictionary<string, object> { ["primary"] = ds }, handlers);
                var schema = (ReactiveSchema)deferred.Schema;
                var cell = new ChartCell { Template = schema };
                using (var ctx = cell.CreateContext()) schema.ComposeAll(cell, ctx);

                var compute = schema.ListFeatures().OfType<ComputeFeature>().Single(f => f.Compute != null);
                var plot = schema.ListFeatures().OfType<PlotFeature>().Single(f => f.IndicatorName == "qs_channel");
                Assert.Equal(3, plot.Series.Length);   // Series 从 JSON Properties 读出
                var upperPort = plot.SeriesOutputs["upper"];

                ds.Load(close);
                WaitFor(() => schema.CurrentBoard is { } b
                              && ReadPort(b, compute.OutputPort).Length == n
                              && ReadPort(b, upperPort).Length == n);
                var board = schema.CurrentBoard!;
                sma = ReadPort(board, compute.OutputPort);
                upper = ReadPort(board, upperPort);
            });

            AssertSameSeries(expectedSma, sma!);
            AssertSameSeries((double[])expectedChannel["upper"]!, upper!);
        }

        // ── 01 号文档「UseMemo 是干什么的」 ─────────────────────────────────

        [Fact]
        public void UseMemo_CloseStats_RecomputesOnlyWhenDepsChange() => IncrementalTestKit.RunOnSta(() =>
        {
            var closes = new DataPort<ReadOnlyMemory<double>>("Close");
            var feature = new Hevo.Charting.DocSamples.UseMemo.CloseStatsFeature { Source = closes };
            var h = new SchemaHarness(feature);

            h.Board.WriteLocked(closes, new ReadOnlyMemory<double>(new[] { 1.0, 3.0, 2.0 }));
            h.InvalidateEnvironment();
            h.ProjectOnly();
            Assert.Equal(1, feature.ComputeCount);
            Assert.Equal(3.0, feature.Published.Max);

            // 全量重投影(比如窗口尺寸变了):OnProject 照跑,但依赖没变,factory 不跑
            h.InvalidateEnvironment();
            h.ProjectOnly();
            Assert.Equal(1, feature.ComputeCount);

            // 换了一块新数据:依赖变了才重算
            h.Board.WriteLocked(closes, new ReadOnlyMemory<double>(new[] { 5.0, 4.0 }));
            h.ProjectOnly();
            Assert.Equal(2, feature.ComputeCount);
            Assert.Equal(4.5, feature.Published.Mean);
        });

        // ── 01 号文档「Use 体系」 ─────────────────────────────────────────

        [Fact]
        public void Use_StatsBand_PublishesOnlyWhenDataOrPlotAreaChanges() => IncrementalTestKit.RunOnSta(() =>
        {
            var closes = new DataPort<ReadOnlyMemory<double>>("Close");
            var area = new PlotAreaTrait(new HevoRect(0, 0, 100, 50));
            // 代替 GridLayoutFeature:Layout 阶段发布当前的绘图区实例
            var layout = new ProbeFeature("layout", FeaturePhase.Layout)
            {
                OnProjectAction = (_, ctx) => ctx.Shared().PublishData(area),
            };
            var feature = new Hevo.Charting.DocSamples.Use.StatsBandFeature { Source = closes };
            var h = new SchemaHarness(layout, feature);

            h.Board.WriteLocked(closes, new ReadOnlyMemory<double>(new[] { 1.0, 3.0, 2.0 }));
            h.InvalidateEnvironment();
            h.Frame();
            Assert.Equal(1, feature.PublishCount);

            // 环境纪元变了、所有 Feature 都重跑,但数据和绘图区都没换:不发布
            h.InvalidateEnvironment();
            h.Frame();
            Assert.Equal(1, feature.PublishCount);

            // 绘图区换了
            area = new PlotAreaTrait(new HevoRect(0, 0, 200, 50));
            h.InvalidateEnvironment();
            h.Frame();
            Assert.Equal(2, feature.PublishCount);

            // 只有数据换了:端口唤醒本 Feature,Layout 不跑,绘图区还是上次那个
            h.Board.WriteLocked(closes, new ReadOnlyMemory<double>(new[] { 5.0, 4.0 }));
            h.Frame();
            Assert.Equal(3, layout.ProjectCount);
            Assert.Equal(3, feature.PublishCount);
        });

        // ── 裸 ChartCell(不用 Feature):喂了才画,再喂再画 ──────────────────

        [Fact]
        public void BareCell_FedLayerDraws_AndRedrawsOnNextFeed() => IncrementalTestKit.RunOnSta(() =>
        {
            var cell = new ChartCell();
            var layer = new LevelLineLayer();
            var feed = BareCellSample.AddLevelLine(cell, layer, y: 40);

            cell.RunFrameNow(PlotMode.Sync);
            var first = ((LayerBuffer)layer.Buffer).Drawing.Commands.Count;
            Assert.True(first > 0, "喂了数据的图层应在下一帧录出指令");
            Assert.True(layer.IsDiscovered);

            var style = feed.Read<LevelLineTrait>()!;
            feed.PublishData(style with { Y = 60 });
            cell.RunFrameNow(PlotMode.Sync);
            Assert.Equal(60, feed.Read<LevelLineTrait>()!.Y);
            Assert.Equal(1, cell.LastFrameDirtyLayers);
        });
    }
}
