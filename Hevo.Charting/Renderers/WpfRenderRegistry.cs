using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 终极 WPF 渲染策略注册表 (OCP 开闭原则的极致体现)
    /// 职责：
    /// 1. 为底层 Renderer 提供无 switch 的 O(1) 资源实例化与解析。
    /// 2. 为 UI 控件提供大一统的 DependencyProperty 绑定网关。
    /// </summary>
    public static class WpfRenderRegistry
    {
        // 💥 策略 1：供底层 Renderer 调用的工厂 (产出 Brush 实例)
        private static readonly Dictionary<Type, Func<IHevoBrush, Brush?>> _brushFactories = new();

        // 💥 策略 3：供 UI 逻辑使用的全能绑定网关 (抹平所有抽象接口差异)
        private static readonly Dictionary<Type, Action<FrameworkElement, DependencyProperty, object>> _universalBinders = new();

        static WpfRenderRegistry()
        {
            // ==========================================
            // 1. 画刷策略注册
            // ==========================================
            RegisterBrush<HevoSolidBrush>(
                factory: b => new SolidColorBrush(b.Color.ToWpf()),
                binder: (el, dp, b) => {
                    var brush = new SolidColorBrush(b.Color.ToWpf());
                    brush.Freeze(); // 性能优化：静态资源强制冻结
                    el.SetValue(dp, brush);
                });

            RegisterBrush<HevoResourceBrush>(
                factory: b => Application.Current.TryFindResource(b.ResourceKey) as Brush ?? new SolidColorBrush(Colors.Magenta),
                binder: (el, dp, b) => el.SetResourceReference(dp, b.ResourceKey));

            RegisterBrush<HevoLinearGradientBrush>(
                factory: b => new LinearGradientBrush(b.StartColor.ToWpf(), b.EndColor.ToWpf(), b.StartPoint.ToWpf(), b.EndPoint.ToWpf()),
                binder: (el, dp, b) => {
                    var brush = new LinearGradientBrush(b.StartColor.ToWpf(), b.EndColor.ToWpf(), b.StartPoint.ToWpf(), b.EndPoint.ToWpf());
                    brush.Freeze();
                    el.SetValue(dp, brush);
                });

            // ==========================================
            // 2. 文本策略注册
            // ==========================================
            RegisterString<HevoLiteralString>(
                resolver: s => s.Text, // 给底层的：直接返原文
                binder: (el, dp, s) => el.SetValue(dp, s.Text) // 给 UI 的：静态赋值
            );

            // 资源文本只登记到 WPF 宿主自己的解析表(不进全局表):同进程的 Avalonia cell 查的是 Avalonia 的资源
            WpfHostRegistration.Services.RegisterString<HevoResourceString>(
                s => Application.Current.TryFindResource(s.ResourceKey) as string ?? s.ResourceKey); // 给底层的：去资源字典里捞翻译
            _universalBinders[typeof(HevoResourceString)] = (el, dp, s) => el.SetResourceReference(dp, ((HevoResourceString)s).ResourceKey); // 给 UI 的：挂载动态资源
        }

        // --- 泛型注册入口 (供业务扩展) ---

        public static void RegisterBrush<TBrush>(Func<TBrush, Brush?> factory, Action<FrameworkElement, DependencyProperty, TBrush> binder)
            where TBrush : IHevoBrush
        {
            _brushFactories[typeof(TBrush)] = b => factory((TBrush)b);
            _universalBinders[typeof(TBrush)] = (el, dp, b) => binder(el, dp, (TBrush)b);
        }

        public static void RegisterString<TString>(Func<TString, string> resolver, Action<FrameworkElement, DependencyProperty, TString> binder)
            where TString : IHevoString
        {
            // 解析表在核心(HevoStrings):核心里的 Feature 也要把多态文本解析成显示文本
            HevoStrings.Register(resolver);
            _universalBinders[typeof(TString)] = (el, dp, s) => binder(el, dp, (TString)s);
        }

        // --- 极速提取 API ---

        /// <summary>
        /// [底层专用] 查表获取 WPF 画刷实例
        /// </summary>
        public static Brush? CreateBrush(IHevoBrush brush)
        {
            return _brushFactories.TryGetValue(brush.GetType(), out var factory) ? factory(brush) : null;
        }

        /// <summary>
        /// [底层专用] 查表解析多态文本
        /// </summary>
        public static string ResolveString(IHevoString text)
        {
            return HevoStrings.Resolve(text, WpfHostRegistration.Services);
        }

        /// <summary>
        /// 💥 [UI 层专用] 终极全能绑定方法。一键处理所有多态！
        /// 开发者只需调 dot.BindHevo(Shape.FillProperty, f.Brush);
        /// </summary>
        public static void BindHevo(this FrameworkElement element, DependencyProperty property, object? abstraction)
        {
            if (abstraction == null) return;

            var type = abstraction.GetType();
            if (_universalBinders.TryGetValue(type, out var binder))
                binder(element, property, abstraction);
            else
                throw new NotSupportedException($"[Hevo 架构] 尚未注册对类型 {type.Name} 的 WPF 绑定策略。");
        }
    }
}
