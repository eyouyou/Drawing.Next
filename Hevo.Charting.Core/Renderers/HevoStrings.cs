namespace Hevo.Charting
{
    /// <summary>
    /// <see cref="IHevoString"/> → 实际显示文本的解析。顺序:这里登记的全局解析(字面量 + 业务登记的)→
    /// cell 所属宿主的解析(<see cref="Core.ChartHostServices.RegisterString{TString}"/>,典型:资源文本查本宿主的资源字典)→
    /// 没给 cell 时按宿主登记顺序找第一个能解析的。都不认识的类型解析成空串。
    /// </summary>
    public static class HevoStrings
    {
        private static readonly Dictionary<Type, Func<IHevoString, string>> _resolvers = new();

        static HevoStrings()
        {
            Register<HevoLiteralString>(s => s.Text);
        }

        /// <summary>注册 / 覆盖某种文本的全局解析方式(所有宿主共用)。启动时调,不和 <see cref="Resolve(IHevoString)"/> 并发。</summary>
        public static void Register<TString>(Func<TString, string> resolver) where TString : IHevoString
            => _resolvers[typeof(TString)] = s => resolver((TString)s);

        /// <summary>没有 cell 上下文时解析(宿主相关的文本按宿主登记顺序找第一个能解析的)。</summary>
        public static string Resolve(IHevoString text) => Resolve(text, (Core.ChartHostServices?)null);

        /// <summary>按 <paramref name="host"/> 解析宿主相关的文本(资源文本等);host 为 null 同 <see cref="Resolve(IHevoString)"/>。</summary>
        public static string Resolve(IHevoString text, Core.ChartHostServices? host)
        {
            if (_resolvers.TryGetValue(text.GetType(), out var resolver)) return resolver(text);
            if (host != null) return host.TryResolveString(text, out var own) ? own : string.Empty;
            foreach (var h in Core.ChartHostServices.All)
            {
                if (h.TryResolveString(text, out var result)) return result;
            }
            return string.Empty;
        }

        /// <summary>按 cell 所属宿主解析。</summary>
        public static string Resolve(IHevoString text, Core.IChartCell chart)
            => Resolve(text, chart?.Engine.HostServices);
    }
}
