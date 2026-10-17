using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 切面生命周期契约(Compose 装配 / Decompose 卸载)。
    /// 【开发关注:否 · 框架内部】业务代码不直接实现它,写 Feature 即可。
    /// </summary>
    public interface IChartFeature
    {
        void Compose(ChartCell chart, RenderContext ctx);
        void Decompose(ChartCell chart, RenderContext ctx);
    }

    /// <summary>
    /// 图表切面基类(切片设计):把"贯穿整张图的横切关注点"从 schema 里切出来,每个切面只管两件事 ——
    /// <list type="bullet">
    ///   <item>编排:<see cref="Compose"/> / <see cref="Decompose"/> 跟随图表生命周期挂上 / 摘掉;</item>
    ///   <item>装饰:<see cref="Decorate"/> 把 WPF 视觉树包一层外壳(图例条、边框、标题栏等)。</item>
    /// </list>
    /// 切面用 <c>+</c> 组合成一条链:<c>a + b</c> 里 a 在外层、b 在内层;Compose 先内后外,Decompose 先外后内。
    /// <para>
    /// 【开发关注】
    /// <list type="bullet">
    ///   <item>业务开发:不直接继承本类。写图表功能继承 <see cref="Feature"/>(或 ChartFeature),
    ///   需要包 WPF 外壳时重写 <see cref="Decorate"/>(范例:DynamicHeaderFeature)。可绑定属性用 <see cref="SetProperty{T}"/>。</item>
    ///   <item>不用关注:<c>+</c> 运算符、<see cref="Empty"/>、CombinedAspect —— ReactiveSchema.ComposeVisualLayers
    ///   每次装配按 Phase 顺序把 feature 串成链,ChartCell 用它做一次 Decorate;卸载由 ReactiveSchema 自己逐个 Decompose。</item>
    ///   <item>预留扩展点:不依赖数据流的纯外壳切面可直接继承本类,通过 <c>new ChartSchema(aspect)</c> 挂上。
    ///   目前只有 Feature 一个子类,ReactiveSchema 装配时会用 feature 链覆盖构造时传入的 aspect —— 要支持两者并存需先改 ComposeVisualLayers。</item>
    /// </list>
    /// </para>
    /// </summary>
    public abstract class ChartAspect : IChartFeature, IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // 内部私有类，外部不可直接创建
        private class EmptyAspect : ChartAspect
        {
            // 它什么都不重写，所以默认执行基类的行为：
            // Decorate 返回 ContentIdentity (表示不装饰)
            // Compose 什么都不做
        }

        // 静态单例，全局只存一份，节省内存
        public static readonly ChartAspect Empty = new EmptyAspect();

        // 这就是你要的：对内装饰和对外编排
        public virtual UIElement Decorate(UIElement inner)
        {
            return inner;
        }
        public virtual void Compose(ChartCell chart, RenderContext ctx) { }
        public virtual void Decompose(ChartCell chart, RenderContext ctx) { }
        public virtual void Dispose() { }

        // ==========================================
        // 💥 魔法契约：允许 Aspect 像字符串一样被拼接组合！
        // ==========================================
        public static ChartAspect operator +(ChartAspect left, ChartAspect right)
        {
            if (left == null || left == Empty) return right;
            if (right == null || right == Empty) return left;

            return new CombinedAspect(left, right);
        }

        // 内部类：负责将多个 WPF 装饰器嵌套包裹
        private class CombinedAspect : ChartAspect
        {
            private readonly ChartAspect _outer;
            private readonly ChartAspect _inner;

            public CombinedAspect(ChartAspect outer, ChartAspect inner)
            {
                _outer = outer;
                _inner = inner;
            }

            public override UIElement Decorate(UIElement inner)
            {
                return _outer.Decorate(_inner.Decorate(inner));
            }

            public override void Compose(ChartCell chart, RenderContext ctx)
            {
                _inner.Compose(chart, ctx);
                _outer.Compose(chart, ctx);
            }

            public override void Decompose(ChartCell chart, RenderContext ctx)
            {
                _outer.Decompose(chart, ctx);
                _inner.Decompose(chart, ctx);
            }
        }
    }
}
