using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Hevo.Charting
{
    /// <summary>
    /// 数据流与 WPF UI 框架(依赖属性、调度器)的桥接。图表交互不走这里,见指针抽象(PointerRouter / WatchPointer)。退订时自动拔钩子。
    /// </summary>
    public static class WpfWorkflow
    {
        /// <summary>
        /// 💥 WPF 依赖属性 (DependencyProperty) 终极监听器。
        /// 解决了原生 WPF 中监听 DP 极其繁琐且极易导致内存泄漏的痛点。
        /// </summary>
        public static IWorkflow<object?> OnProperty(this DependencyObject element, DependencyProperty dp)
        {
            return new WorkflowEngine<object?>((next, error) =>
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(dp, element.GetType());
                EventHandler handler = (s, e) => next(element.GetValue(dp));
                descriptor.AddValueChanged(element, handler);

                // 初始状态推送
                try { next(element.GetValue(dp)); } catch (Exception ex) { error(ex); }

                // 💥 避坑指南：DependencyPropertyDescriptor 是极其容易导致泄漏的根源，
                // 这里利用 DisposableAction 确保了与图表生命周期同生死，绝对安全。
                return new DisposableAction(() => descriptor.RemoveValueChanged(element, handler));
            });
        }

        // =================================================================
        // 💥 5. 线程调度 (Schedulers)
        // =================================================================

        /// <summary>
        /// 💥 精确制导的 UI 线程调度器。
        /// 强行将上游（可能来自后台 Task 或 WebSocket）的数据流调度到指定的 UI 线程。
        /// </summary>
        /// <param name="dispatcher">目标 UI 线程调度器 (通常是 ChartCell.Dispatcher)</param>
        public static IWorkflow<T> ObserveOnUI<T>(this IWorkflow<T> source, Dispatcher dispatcher)
        {
            return new WorkflowEngine<T>((next, error) =>
            {
                return source.Subscribe(
                    val =>
                    {
                        // 💥 性能极客优化：如果是同线程调用，直接放行 (Fast Path)；
                        // 只有发生跨线程时，才引发 BeginInvoke 调度开销。
                        if (dispatcher.CheckAccess()) next(val);
                        else dispatcher.BeginInvoke(() => next(val), DispatcherPriority.Normal);
                    },
                    err =>
                    {
                        if (dispatcher.CheckAccess()) error(err);
                        else dispatcher.BeginInvoke(() => error(err), DispatcherPriority.Normal);
                    }
                );
            });
        }
    }
}
