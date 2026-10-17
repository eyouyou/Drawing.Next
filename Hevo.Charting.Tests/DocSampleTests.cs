using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.DocSamples.Bollinger;
using Hevo.Charting.DocSamples.QuickStart;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.Renderers;
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
            using (board.AcquireReadLock()) return board.Read(port).ToArray();
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Hevo.Drawing.slnx"))) dir = dir.Parent;
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
                    .With(BollingerStyle.Create(System.Windows.Media.Colors.Orange))
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

        /// <summary>跟 Demo 的 MockKLineDataSource 同样字段的测试数据源(Demo 工程拖不进测试)。</summary>
        public sealed class QsBar
        {
            [Port] public DateTime Time { get; init; }
            [Port] public double Open { get; init; }
            [Port] public double High { get; init; }
            [Port] public double Low { get; init; }
            [Port] public double Close { get; init; }
            [Port] public double Volume { get; init; }
        }

        public sealed class QsKLineSource : BufferedDataSource<QsKLineSource, QsBar>
        {
            public void Load(double[] closes)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    var t0 = new DateTime(2026, 1, 1);
                    for (int i = 0; i < closes.Length; i++)
                    {
                        double c = closes[i];
                        _buffer.Add(new QsBar { Time = t0.AddMinutes(i), Open = c, High = c + 1, Low = c - 1, Close = c, Volume = 1000 });
                    }
                    Publish();
                }
            }
        }

        private static List<FeatureModel> LoadSnippet(string file)
        {
            var json = File.ReadAllText(RepoFile("Hevo.Charting.Tests", "DocSamples", "QuickStart", file));
            var nodes = JsonSerializer.Deserialize<List<FeatureModel>>(json, BlueprintJsonOptions.Default);
            Assert.NotNull(nodes);
            Assert.NotEmpty(nodes!);
            return nodes!;
        }

        /// <summary>Demo 默认 dashboard,数据源换成同字段的测试数据源,主图追加文档里的片段 —— 即新人粘贴后的样子。</summary>
        private static Dashboard DemoDashboardWith(List<FeatureModel> snippet)
        {
            ComponentRegistry.Register<QsKLineSource>();
            var json = File.ReadAllText(RepoFile("Hevo.Drawing.LowCodeDemo", "Assets", "default_kline_dashboard.json"));
            var dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default)!;
            foreach (var cell in dashboard.Cells)
                foreach (var ds in cell.Blueprint.DataSources)
                    if (ds.TypeName == "MockKLineDataSource") ds.TypeName = nameof(QsKLineSource);
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
            var ds = new QsKLineSource();

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
    }
}
