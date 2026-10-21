using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hevo.Charting.CodeAnalysis;
using Hevo.Charting.Core;
using Hevo.Charting.DocSamples.UseMemo;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// HEVO008:UseMemo 只能直接写在 Feature.OnProject 里。
    /// 用 Roslyn 现编一段引用真实 Charting 的源码跑 <see cref="UseMemoPlacementAnalyzer"/>,
    /// 断言三种情况各自的文案(哪个类型 / 方法、哪一行、改法);01 号文档引用的 DocSamples 改前 / 改后也在这里验证。
    /// </summary>
    public sealed class HookAnalyzerTests
    {
        private const string Header = @"
using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;

abstract class F : Feature
{
";

        private static async Task<Diagnostic[]> AnalyzeAsync(string source)
        {
            var tpa = ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var refs = tpa.Select(p => MetadataReference.CreateFromFile(p))
                .Append(MetadataReference.CreateFromFile(typeof(FeatureContext).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(WindowStats).Assembly.Location));
            var compilation = CSharpCompilation.Create(
                "HookAnalyzerProbe",
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9)) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            var diagnostics = await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new UseMemoPlacementAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            return diagnostics.Where(d => d.Id.StartsWith("HEVO")).ToArray();
        }

        private static async Task<(Diagnostic[] Diagnostics, int UseMemoLine)> RunAsync(string body)
        {
            var source = Header + body + "\n}\n";
            int line = source.Split('\n').ToList().FindIndex(t => t.Contains("UseMemo(")) + 1;
            return (await AnalyzeAsync(source), line);
        }

        private static Diagnostic Single008(Diagnostic[] diagnostics)
        {
            var d = Assert.Single(diagnostics);
            Assert.Equal("HEVO008", d.Id);
            Assert.Equal(UseMemoPlacementAnalyzer.HelpLink, d.Descriptor.HelpLinkUri);
            Assert.Contains("01.业务Feature设计.md「UseMemo 报错怎么改」", d.GetMessage());
            return d;
        }

        [Fact]
        public async Task UseMemo_DirectlyInOnProject_IncludingBranches_IsAllowed()
        {
            var (ids, _) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx)
    {
        ctx.UseMemo(1, static d => d);
        if (ctx != null)
        {
            ctx.UseMemo(2, static d => d);
        }
    }");
            Assert.Empty(ids);
        }

        [Fact]
        public async Task UseMemo_InHelperMethod_ReportsHelperCase()
        {
            var (ds, line) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx) => Calc(ctx);
    private int Calc(FeatureContext ctx) => ctx.UseMemo(1, static d => d).Value;");
            var message = Single008(ds).GetMessage();
            Assert.StartsWith("[UseMemo 写在 helper 里]", message);
            Assert.Contains($"F.Calc(第 {line} 行)", message);
            Assert.Contains("static d => Calc(d)", message);
        }

        [Fact]
        public async Task UseMemo_InStaticHelperTakingContext_ReportsHelperCase()
        {
            var (ds, _) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx) { }
    public static int Compute(FeatureContext ctx, int x) => ctx.UseMemo(x, static d => d * 2).Value;");
            Assert.StartsWith("[UseMemo 写在 helper 里] F.Compute", Single008(ds).GetMessage());
        }

        [Fact]
        public async Task UseMemo_InLoopInsideOnProject_ReportsRepeatedCase()
        {
            var (ds, line) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx)
    {
        for (int i = 0; i < 3; i++)
            ctx.UseMemo(i, static d => d);
    }");
            var message = Single008(ds).GetMessage();
            Assert.StartsWith($"[UseMemo 会被执行多次] F.OnProject 第 {line} 行的 UseMemo 写在循环里", message);
            Assert.Contains("_cache[i]", message);
        }

        [Fact]
        public async Task UseMemo_InLocalFunctionInsideOnProject_ReportsRepeatedCase()
        {
            var (ds, _) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx)
    {
        int Local() => ctx.UseMemo(1, static d => d).Value;
        Local();
        Local();
    }");
            Assert.Contains("写在本地函数 Local()里", Single008(ds).GetMessage());
        }

        [Fact]
        public async Task UseMemo_InLambdaInsideOnProject_ReportsRepeatedCase()
        {
            var (ds, _) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx)
    {
        Func<int, int> f = x => ctx.UseMemo(x, static d => d).Value;
        f(1);
    }");
            Assert.Contains("写在lambda里", Single008(ds).GetMessage());
        }

        [Fact]
        public async Task UseMemo_NestedInFactory_ReportsRepeatedCase()
        {
            var (ds, _) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx)
        => ctx.UseMemo(1, d => ctx.UseMemo(d, static e => e).Value);");
            Assert.Contains("写在lambda里", Single008(ds).GetMessage());
        }

        [Fact]
        public async Task UseMemo_OutsideFeature_ReportsNotInFeatureCase()
        {
            // 名字叫 OnProject 也不算:所在类型不是 Feature。
            var (ds, line) = await RunAsync(@"
    protected override void OnProject(FeatureContext ctx) { }
}
class LabelProvider
{
    public void OnProject(FeatureContext ctx) => ctx.UseMemo(1, static d => d);");
            var message = Single008(ds).GetMessage();
            Assert.StartsWith($"[UseMemo 不在 Feature 里] LabelProvider.OnProject(第 {line} 行)", message);
            Assert.Contains("_strategy ??=", message);
        }

        [Fact]
        public async Task UsePort_InHelperMethod_IsAllowed()
        {
            // UsePort 按端口对象存令牌,写在哪都不撞。
            var (ds, _) = await RunAsync(@"
    private readonly DataPort<double> _port = new DataPort<double>(""p"");
    protected override void OnProject(FeatureContext ctx) => Read(ctx);
    private double Read(FeatureContext ctx) => ctx.UsePort(_port).Value;");
            Assert.Empty(ds);
        }

        [Fact]
        public async Task UseMemo_WithPortVersionDependency_IsAllowed()
        {
            // 内容版本是只读令牌、不是 Hook:并进 UseMemo 依赖、放进分支都不报
            var (ds, _) = await RunAsync(@"
    private readonly DataPort<ReadOnlyMemory<double>> _port = new DataPort<ReadOnlyMemory<double>>(""p"");
    protected override void OnProject(FeatureContext ctx)
    {
        var (closes, _) = ctx.UsePort(_port);
        ctx.UseMemo((closes, ctx.PortVersion(_port)), static d => d.closes.Length);
        if (closes.Length > 0) { var v = ctx.PortVersion(_port); }
    }");
            Assert.Empty(ds);
        }

        // ---- 01 号文档「UseMemo 报错怎么改」引用的改前 / 改后 ----

        private static string DocSample(string file)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Drawing.Next.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!.FullName, "Hevo.Charting.Tests", "DocSamples", "UseMemo", file));
        }

        [Theory]
        [InlineData("HelperCase.Before.cs", "[UseMemo 写在 helper 里] DualStatsFeature.Stats")]
        [InlineData("LoopCase.Before.cs", "[UseMemo 会被执行多次] MultiStatsFeature.OnProject")]
        [InlineData("ProviderCase.Before.cs", "[UseMemo 不在 Feature 里] PriceLabelProvider.GetFormat")]
        public async Task DocSample_Before_ReportsExpectedCase(string file, string expectedPrefix)
        {
            Assert.StartsWith(expectedPrefix, Single008(await AnalyzeAsync(DocSample(file))).GetMessage());
        }

        [Theory]
        [InlineData("CloseStatsFeature.cs")]
        [InlineData("HelperCase.After.cs")]
        [InlineData("LoopCase.After.cs")]
        [InlineData("ProviderCase.After.cs")]
        public async Task DocSample_After_IsClean(string file)
        {
            Assert.Empty(await AnalyzeAsync(DocSample(file)));
        }
    }
}
