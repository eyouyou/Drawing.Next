﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Hevo.Charting.CodeAnalysis
{
    /// <summary>
    /// HEVO008:UseMemo 只能直接写在 Feature 的 OnProject override 里(不在 helper / 循环 / lambda / 本地函数里)。
    /// UseMemo 的缓存按调用这一行(文件 + 行号)存:同一行被多处调用或在一帧里执行多次,就共用一份缓存、互相覆盖。
    /// 按出错位置分三种情况给不同的改法。
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class UseMemoPlacementAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "HEVO008";

        public const string HelpLink =
            "https://github.com/eyouyou/Drawing.Next/blob/main/Hevo.Charting/01.%E4%B8%9A%E5%8A%A1Feature%E8%AE%BE%E8%AE%A1.md#usememo-%E6%8A%A5%E9%94%99%E6%80%8E%E4%B9%88%E6%94%B9";

        private const string Why = "UseMemo 的缓存按代码行存,不按调用方存:这一行被几处调用、或一帧执行几次,就共用一份缓存,互相覆盖,依赖一交替缓存就每次失效。";
        private const string DocHint = "详见 Hevo.Charting/01.业务Feature设计.md「UseMemo 报错怎么改」。";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            RuleId, "UseMemo 只能直接写在 Feature.OnProject 里", "{0}",
            "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
            description: "UseMemo 按调用位置(文件 + 行号)存缓存,只能直接写在 Feature 的 OnProject override 里,不能写进 helper 方法、循环、lambda 或本地函数。",
            helpLinkUri: HelpLink);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (!IsUseMemo(context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol)) return;

            // 由内往外:先记下最近的"会执行多次"的结构,直到碰到所在成员。
            string? repeated = null;
            MemberDeclarationSyntax? member = null;
            foreach (var node in invocation.Ancestors())
            {
                if (repeated == null)
                {
                    switch (node)
                    {
                        case ForStatementSyntax _:
                        case ForEachStatementSyntax _:
                        case WhileStatementSyntax _:
                        case DoStatementSyntax _:
                            repeated = "循环";
                            break;
                        case AnonymousFunctionExpressionSyntax _:
                            repeated = "lambda";
                            break;
                        case LocalFunctionStatementSyntax local:
                            repeated = $"本地函数 {local.Identifier.Text}()";
                            break;
                    }
                }
                if (node is MemberDeclarationSyntax m && !(m is BaseTypeDeclarationSyntax))
                {
                    member = m;
                    break;
                }
            }

            var typeSyntax = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var type = typeSyntax == null ? null : context.SemanticModel.GetDeclaredSymbol(typeSyntax);
            string typeName = type?.Name ?? "<type>";
            string memberName = MemberName(member, typeName);
            int line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            string? message;
            if (type == null || !DerivesFromFeature(type))
            {
                message =
                    $"[UseMemo 不在 Feature 里] {typeName}.{memberName}(第 {line} 行)调了 UseMemo。{Why}" +
                    $"同一个 {typeName} 类型的多个实例会撞成一份缓存。" +
                    $"改法:缓存改成 {typeName} 自己的字段,例如 `_strategy ??= new XxxStrategy(...)`,依赖变了再重建(参照 TickProvider)。{DocHint}";
            }
            else if (!(member is MethodDeclarationSyntax method && IsFeatureOnProject(context.SemanticModel.GetDeclaredSymbol(method))))
            {
                message =
                    $"[UseMemo 写在 helper 里] {typeName}.{memberName}(第 {line} 行)调了 UseMemo,它不是 OnProject。{Why}" +
                    $"改法:把 UseMemo 移到 OnProject 里,{memberName} 改成纯函数:`ctx.UseMemo(deps, static d => {memberName}(d))`。{DocHint}";
            }
            else if (repeated != null)
            {
                message =
                    $"[UseMemo 会被执行多次] {typeName}.OnProject 第 {line} 行的 UseMemo 写在{repeated}里。{Why}" +
                    $"改法:对整个集合调一次 UseMemo、factory 里一次算完返回数组;或者每项各自缓存在字段数组里(_cache[i])。{DocHint}";
            }
            else
            {
                message = null;
            }

            if (message != null)
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), message));
        }

        private static bool IsUseMemo(IMethodSymbol? methodSymbol)
        {
            if (methodSymbol == null || methodSymbol.Name != "UseMemo") return false;
            // ctx.UseMemo(...) 绑定到的是"约简"后的扩展方法,还原成原始声明再看所在类型。
            var original = methodSymbol.ReducedFrom ?? methodSymbol;
            return original.ContainingType?.Name == "FeatureContextExtensions";
        }

        private static string MemberName(MemberDeclarationSyntax? member, string typeName)
        {
            switch (member)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case ConstructorDeclarationSyntax _: return typeName;
                case PropertyDeclarationSyntax p: return p.Identifier.Text;
                case BaseFieldDeclarationSyntax f: return f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "字段";
                default: return "成员";
            }
        }

        private static bool DerivesFromFeature(INamedTypeSymbol type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (IsFeatureBase(t)) return true;
            }
            return false;
        }

        private static bool IsFeatureBase(INamedTypeSymbol t) =>
            t.Name == "Feature" && t.ContainingNamespace?.ToDisplayString() == "Hevo.Charting.Core";

        private static bool IsFeatureOnProject(IMethodSymbol? methodSymbol)
        {
            if (methodSymbol == null || methodSymbol.Name != "OnProject" || !methodSymbol.IsOverride) return false;
            for (var m = methodSymbol.OverriddenMethod; m != null; m = m.OverriddenMethod)
            {
                if (IsFeatureBase(m.ContainingType)) return true;
            }
            return false;
        }
    }
}
