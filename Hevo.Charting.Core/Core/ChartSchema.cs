namespace Hevo.Charting.Core
{
    /// <summary>
    /// c# 基础template类型。宿主无关:WPF 宿主把它挂在 ChartCell.Template 上(内部再换成 WPF 的 ControlTemplate)。
    /// </summary>
    public class ChartSchema
    {
        /// <summary>
        /// 组合编排和装饰
        /// 编排可以多个
        /// 装饰只能是一个
        /// </summary>
        public ChartAspect Aspect { get; protected set; } = ChartAspect.Empty;
        public ChartSchema(ChartAspect? aspect = null)
        {
            if (aspect != null)
            {
                Aspect = aspect;
            }
        }

        // ==========================================
        // 💥 引擎生命周期钩子：替代原先单一的 Aspect.Compose
        // ==========================================

        /// <summary>
        /// 编排可以多个：宿主应用模板时调用的核心入口。
        /// 子类 (如 ReactiveSchema) 将在此处遍历挂载所有的 Feature 组件。
        /// </summary>
        public virtual void ComposeAll(IChartCell chart, RenderContext ctx)
        {
            // 基类默认只挂载装饰器的图层（如果有的话）
            Aspect.Compose(chart, ctx);
        }

        public virtual void DecomposeAll(IChartCell chart, RenderContext ctx)
        {
            // 💥 架构对称：既然 ComposeAll 调了 Aspect.Compose，卸载时也必须通知 Aspect 进行相应的资源销毁！
            Aspect?.Decompose(chart, ctx);
        }
    }
}
