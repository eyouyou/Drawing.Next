using System.Reflection;
using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 一种 UI 宿主(WPF / Avalonia)给核心提供的平台实现:悬浮窗图层、外壳 Feature 的宿主子类、资源文本解析、调试菜单。
    /// <para>
    /// 每个宿主一份(<c>WpfChartHost.Services</c> / <c>AvaloniaChartHost.Services</c>),cell 经 <see cref="IChartCellHost"/>
    /// 带出自己宿主的那份,核心按 cell 取 —— 所以同一进程里 WPF 与 Avalonia 的 cell 可以共存,各用各的实现。
    /// 拿不到 cell 的地方(蓝图名解析、无上下文的文本解析)按宿主登记顺序找第一个能处理的。
    /// </para>
    /// </summary>
    public sealed class ChartHostServices
    {
        private static readonly List<ChartHostServices> _all = new();
        private static readonly object _gate = new();

        private readonly Dictionary<Type, Func<Feature>> _features = new();
        private readonly Dictionary<string, Type> _componentAliases = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, Func<IHevoString, string>> _stringResolvers = new();

        internal ChartHostServices(string name, Assembly hostAssembly)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            HostAssembly = hostAssembly ?? throw new ArgumentNullException(nameof(hostAssembly));
        }

        /// <summary>宿主名("Wpf" / "Avalonia"),诊断用。</summary>
        public string Name { get; }

        /// <summary>宿主程序集:内置组件登记(BuiltinRegistration.RegisterBuiltins)时跟核心程序集一起扫。</summary>
        public Assembly HostAssembly { get; }

        /// <summary>已登记的宿主(按登记顺序)。</summary>
        public static IReadOnlyList<ChartHostServices> All
        {
            get { lock (_gate) return _all.ToArray(); }
        }

        /// <summary>cell 所属宿主的那份。</summary>
        public static ChartHostServices Of(IChartCell chart)
        {
            if (chart is null) throw new ArgumentNullException(nameof(chart));
            return chart.Engine.HostServices;
        }

        /// <summary>宿主库加载时调一次(ModuleInitializer)。重复登记同一份无副作用。</summary>
        internal static void Register(ChartHostServices services)
        {
            lock (_gate)
            {
                if (!_all.Contains(services)) _all.Add(services);
            }
        }

        /// <summary>
        /// 装配期(schema 的 DefineFeatures 等)按 <paramref name="canvas"/> 找宿主:schema 已 attach 到 cell 就取该 cell 的宿主;
        /// 否则进程里只有一种宿主时取它,有多种就无法判断,报错。
        /// </summary>
        internal static ChartHostServices ResolveForCompose(IFeatureContext canvas)
        {
            if (canvas is ReactiveSchema { AttachedChart: { } chart }) return chart.Engine.HostServices;
            var all = All;
            if (all.Count == 1) return all[0];
            if (all.Count == 0)
                throw new InvalidOperationException("没有已加载的 UI 宿主(引用 Hevo.Charting 或 Hevo.Charting.Avalonia 并创建 cell 后再装配)。");
            throw new InvalidOperationException(
                $"进程里有多个 UI 宿主({string.Join(" / ", all.Select(h => h.Name))}),schema 还没挂到 cell 上,分不出该用哪个宿主的实现;" +
                "请在 DefineFeatures 里装配(那时 schema 已挂到 cell)。");
        }

        // ==========================================
        // 外壳 Feature:平台无关基类 → 本宿主子类
        // ==========================================

        /// <summary>
        /// 登记外壳 Feature 的本宿主实现:schema 里写 <c>canvas.AddHostFeature&lt;TBase&gt;()</c>,
        /// 挂到哪个宿主就建哪个宿主的子类。<typeparamref name="THost"/> 同时以 <paramref name="blueprintName"/>
        /// (默认 <typeparamref name="TBase"/> 的类名)登记成本宿主的蓝图组件名,蓝图 JSON 写基类名即可。
        /// </summary>
        public void RegisterFeature<TBase, THost>(string? blueprintName = null)
            where TBase : Feature
            where THost : TBase, new()
        {
            lock (_gate)
            {
                _features[typeof(TBase)] = () => new THost();
                _componentAliases[blueprintName ?? typeof(TBase).Name] = typeof(THost);
            }
        }

        /// <summary>本宿主有没有 <typeparamref name="TBase"/> 的实现;有则新建一个。</summary>
        public bool TryCreateFeature<TBase>(out TBase feature) where TBase : Feature
        {
            Func<Feature>? factory;
            lock (_gate) _features.TryGetValue(typeof(TBase), out factory);
            feature = factory?.Invoke() as TBase ?? null!;
            return factory != null;
        }

        /// <summary>只登记蓝图组件名(该名字在本宿主下解析成 <paramref name="type"/>)。</summary>
        public void RegisterComponentAlias(string blueprintName, Type type)
        {
            if (string.IsNullOrEmpty(blueprintName)) throw new ArgumentException("蓝图组件名不能为空。", nameof(blueprintName));
            if (type is null) throw new ArgumentNullException(nameof(type));
            lock (_gate) _componentAliases[blueprintName] = type;
        }

        internal bool TryResolveComponent(string blueprintName, out Type type)
        {
            lock (_gate) return _componentAliases.TryGetValue(blueprintName, out type!);
        }

        /// <summary>
        /// 没有 cell 上下文时按蓝图名找宿主实现(按宿主登记顺序,先登记的优先)。
        /// 只在全局表找不到该名字时才用到 —— 全局表里的类型(典型:WPF 的 UniversalHeaderFeature)不被宿主别名覆盖。
        /// </summary>
        internal static bool TryResolveComponentAnyHost(string blueprintName, out Type type)
        {
            foreach (var host in All)
            {
                if (host.TryResolveComponent(blueprintName, out type)) return true;
            }
            type = null!;
            return false;
        }

        /// <summary>
        /// 宿主实现类型 → 它登记的蓝图名(反查 <see cref="RegisterFeature{TBase, THost}"/> / <see cref="RegisterComponentAlias"/>)。
        /// 编辑器生成节点、序列化时用,存出来的是宿主无关的名字。
        /// </summary>
        public static bool TryGetBlueprintName(Type type, out string blueprintName)
        {
            foreach (var host in All)
            {
                lock (_gate)
                {
                    foreach (var kv in host._componentAliases)
                    {
                        if (kv.Value == type) { blueprintName = kv.Key; return true; }
                    }
                }
            }
            blueprintName = null!;
            return false;
        }

        // ==========================================
        // 资源文本
        // ==========================================

        /// <summary>
        /// 本宿主怎么解析某种文本(典型:<see cref="HevoResourceString"/> 查本宿主的资源字典)。
        /// 业务经 <see cref="HevoStrings.Register{TString}"/> 登记的全局解析优先于这里。
        /// </summary>
        public void RegisterString<TString>(Func<TString, string> resolver) where TString : IHevoString
        {
            if (resolver is null) throw new ArgumentNullException(nameof(resolver));
            lock (_gate) _stringResolvers[typeof(TString)] = s => resolver((TString)s);
        }

        internal bool TryResolveString(IHevoString text, out string result)
        {
            Func<IHevoString, string>? resolver;
            lock (_gate) _stringResolvers.TryGetValue(text.GetType(), out resolver);
            result = resolver?.Invoke(text) ?? string.Empty;
            return resolver != null;
        }

        // ==========================================
        // 核心内部用到的宿主实现
        // ==========================================

        /// <summary>悬浮窗图层(Interaction 级,吃 <see cref="Layers.TooltipWidgetTrait"/>)。没有时 TooltipWidgetFeature 装配报错。</summary>
        internal Func<ChartLayer>? CreateTooltipLayer { get; set; }

        // 万能联动头的工厂收在字段里经方法设置:类型里带 DataPort,写成可写属性会被当成端口引脚(HEVO007)
        private Func<LowCode.DataPort<Features.PointerHitState?>, Features.UniversalHeaderFeatureBase>? _createUniversalHeader;

        /// <summary>万能联动头(<see cref="Features.UniversalHeaderFeatureBase"/> 的宿主实现,参数是命中端口)。</summary>
        internal Func<LowCode.DataPort<Features.PointerHitState?>, Features.UniversalHeaderFeatureBase>? CreateUniversalHeader => _createUniversalHeader;

        internal void SetUniversalHeaderFactory(Func<LowCode.DataPort<Features.PointerHitState?>, Features.UniversalHeaderFeatureBase> factory)
            => _createUniversalHeader = factory;

#if DEBUG
        /// <summary>调试菜单(拓扑监控入口等),见 <see cref="ReactiveSchema.IDebugMenuHost"/>。</summary>
        internal ReactiveSchema.IDebugMenuHost? DebugMenuHost { get; set; }
#endif

        public override string ToString() => Name;
    }
}
