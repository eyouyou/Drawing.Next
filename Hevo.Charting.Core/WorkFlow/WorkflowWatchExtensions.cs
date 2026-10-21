using Hevo.Charting.Core;
using System.ComponentModel;
using System.Linq.Expressions;

namespace Hevo.Charting
{
    /// <summary>
    /// 💥 响应式工作流适配与桥接扩展库
    /// 提供数据流与图表生命周期、模型属性以及异步任务体系的无缝集成(WPF 路由事件 / 依赖属性 / 调度器桥接见 WPF 宿主的 WpfWorkflow)。
    /// 核心设计准则：绝对的生命周期自清理（0 内存泄漏）与严格的线程安全控制。
    /// </summary>
    public static partial class Workflow
    {
        // =================================================================
        // 💥 1. 生命周期与 UI 绑定 (Lifecycle & UI Binding)
        // =================================================================

        /// <summary>
        /// 💥 终极图表绑定算子：将无主的数据流强行锚定到指定的图表生命周期上。
        /// </summary>
        /// <typeparam name="T">数据流类型</typeparam>
        /// <param name="source">上游的纯净数据流</param>
        /// <param name="chart">作为宿主的图表控件</param>
        /// <returns>拥有图表上下文（Chart Context）的渲染流</returns>
        /// <remarks>
        /// 架构意义：这道算子是引擎的“海关”。未经 BindTo 的流在内存中是游离的；
        /// 一旦通过 BindTo，其销毁动作（Dispose）将直接被托管给图表的 <see cref="ChartSession"/>，
        /// 确保图表关闭时，所有挂载的网络请求、定时器、事件监听被瞬间全部拔除！
        /// </remarks>
        public static IRenderFlow<T> BindTo<T>(this IWorkflow<T> source, IChartCell chart)
        {
            var flow = new BoundWorkflow<T>(source, chart);

            // 💥 Ambient 自动登记主数据流：若当前正在 ReactiveSchema.DefineDataFlow 执行栈内，
            // 且产物是 IRenderFlow<DataBlackboard>，自动把它作为该 Schema 的主数据流登记。
            // 业务层无需任何显式的 register / SetDataFlow 调用——只写 `.BindTo(chart)` 即可。
            if (flow is IRenderFlow<LowCode.DataBlackboard> dataFlow
                && Core.ReactiveSchema.CurrentComposing is { } schema)
            {
                schema.RegisterMainFlow(dataFlow);
            }

            return flow;
        }

        private class BoundWorkflow<T> : IRenderFlow<T>
        {
            private readonly IWorkflow<T> _source;
            public IChartCell Chart { get; }

            public BoundWorkflow(IWorkflow<T> source, IChartCell chart)
            {
                _source = source;
                Chart = chart;
            }

            public IDisposable Subscribe(Action<T> onNext, Action<Exception>? onError = null)
            {
                // 1. 调用上游原始的订阅链
                var sub = _source.Subscribe(onNext, onError);

                // 2. 💥 绝杀拦截：只要你经过了 BindTo，你的命就是 ChartSession 的了！
                if (ChartLifecycle.GetActiveSession(Chart) is ChartSession session)
                {
                    session.Own(sub);
                }

                return sub;
            }

            public IRenderFlow<TOut> Wrap<TOut>(IWorkflow<TOut> nextWorkflow)
            {
                // 传递图表上下文，继续保持生命周期向下游的渗透能力
                return new BoundWorkflow<TOut>(nextWorkflow, Chart);
            }
        }

        /// <summary>
        /// 💥 创建一个基于内存的极简事件总线 (Event Bus / Subject 模式)。
        /// </summary>
        /// <returns>
        /// Flow: 供下游订阅的只读响应式流。
        /// Trigger: 供业务端主动推送数据的触发委托。
        /// </returns>
        public static (IWorkflow<T> Flow, Action<T> Trigger) Create<T>()
        {
            Action<T>? bus = null;
            var flow = new WorkflowEngine<T>((next, err) =>
            {
                // 挂载订阅者
                bus += next;
                return new DisposableAction(() => bus -= next);
            });
            // Invoke 时 null 传播确保了 0 订阅者时也不会空引用报错
            return (flow, val => bus?.Invoke(val));
        }

        // =================================================================
        // 💥 3. 属性监听与模型桥接 (Property Observers)
        // =================================================================

        /// <summary>
        /// 💥 MVVM 模型属性监听器。
        /// 通过表达式树强类型提取属性，彻底告别写死字符串 (Magic String) 带来的重构风险。
        /// </summary>
        /// <param name="propertyExpression">形如 `x => x.MyProperty` 的表达式</param>
        public static IWorkflow<TProp> On<TSchema, TProp>(
            this TSchema source,
            Expression<Func<TSchema, TProp>> propertyExpression)
            where TSchema : INotifyPropertyChanged
        {
            if (propertyExpression.Body is not MemberExpression member)
                throw new ArgumentException("必须指向一个有效的属性");

            // 提取属性名用于比对
            string propName = member.Member.Name;

            // 💥 高性能反射替代方案：将表达式直接编译成 IL 委托 (Getter)，速度是反射的千倍！
            var getter = propertyExpression.Compile();

            return new WorkflowEngine<TProp>((next, error) =>
            {
                // 💥 立即推送当前状态（作为流的初始种子）
                try { next(getter(source)); } catch (Exception ex) { error(ex); }

                void handler(object? s, PropertyChangedEventArgs e)
                {
                    // 高速字符串比对，匹配命中则抓取最新值下发
                    if (e.PropertyName == propName)
                    {
                        try { next(getter(source)); } catch (Exception ex) { error(ex); }
                    }
                }

                source.PropertyChanged += handler;
                return new DisposableAction(() => source.PropertyChanged -= handler);
            });
        }

        // =================================================================
        // 💥 4. 标准事件与异步任务桥接 (Events & Tasks)
        // =================================================================

        /// <summary>
        /// 标准 .NET 事件转响应式流 (针对 EventHandler<TArgs> 泛型事件)
        /// </summary>
        public static IWorkflow<TArgs> FromEvent<TArgs>(Action<EventHandler<TArgs>> add, Action<EventHandler<TArgs>> remove)
        {
            return FromGeneric<EventHandler<TArgs>, TArgs>(add, remove, next => (s, e) => next(e));
        }

        /// <summary>
        /// 万能事件适配器：可适配任意签名的 Delegate 事件
        /// </summary>
        public static IWorkflow<TArgs> FromGeneric<TDelegate, TArgs>(Action<TDelegate> add, Action<TDelegate> remove, Func<Action<TArgs>, TDelegate> converter)
        {
            return new WorkflowEngine<TArgs>((next, error) =>
            {
                var handler = converter(next);
                add(handler);
                return new DisposableAction(() => remove(handler));
            });
        }

        /// <summary>
        /// 💥 创建一个基于时间的脉冲流 (定时器 Trigger)。
        /// 架构级防御：彻底废弃裸露的 long 递增，使用引擎底层的 StateClock 提供无限纪元心跳。
        /// 生命周期与流绑定，流被释放时后台 Task 自动摧毁，0 线程泄漏！
        /// </summary>
        public static IWorkflow<VersionToken> Interval(TimeSpan period)
        {
            return new WorkflowEngine<VersionToken>((next, error) =>
            {
                // Phase 11 / §H.10：返回可暂停的 IntervalSubscription（IDisposable + IPausable）。
                // WorkflowEngine.Subscribe 会自动识别 IPausable 并透传到下游订阅，整条
                // `.Interval(...).Select(...).FetchExclusive(...).Subscribe(...)` 链
                // 在 Schema.Suspend 时自动冻结到此源头。
                return new IntervalSubscription(period, next, error);
            });
        }

        /// <summary>
        /// 💥 Interval 的可暂停订阅载体（Phase 11 / §H.10 方案 A）
        ///
        /// 暂停实现：用 `TaskCompletionSource` 做异步闸门。
        ///   - Suspend：新建未完成 tcs 替换 `_resumeTcs`，设置 `_isActive=false`
        ///   - Loop：Task.Delay 完成后检查 `_isActive`，若 false 则 `await _resumeTcs.Task.WaitAsync(token)`
        ///     ——此时 thread-pool 线程完全归还，**0 CPU 0 唤醒**
        ///   - Resume：设 `_isActive=true` + `TrySetResult()` → loop 立即唤醒，首帧 tick 无延迟
        ///   - Dispose：`_cts.Cancel()` 同时打断 Task.Delay 和 tcs.WaitAsync（抛 OperationCanceledException）
        ///
        /// RunContinuationsAsynchronously：避免 Resume 调用者（通常是 UI 线程）同步跑 tick 逻辑。
        /// </summary>
        internal sealed class IntervalSubscription : IDisposable, Abstractions.IPausable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly StateClock _clock = new();
            private volatile bool _isActive = true;
            private volatile TaskCompletionSource _resumeTcs;
            private bool _disposed;

            public bool IsActive => _isActive;

            public void Suspend()
            {
                if (!_isActive) return;   // 幂等
                // 先换新的未完成 tcs，再翻 flag——保证 loop 下一次检查 _isActive=false 时
                // 读到的 _resumeTcs 是新创建的未完成实例。
                _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _isActive = false;
            }

            public void Resume()
            {
                if (_isActive) return;    // 幂等
                _isActive = true;
                _resumeTcs.TrySetResult();  // 唤醒 loop（若正在 await）
            }

            public IntervalSubscription(TimeSpan period, Action<VersionToken> next, Action<Exception> error)
            {
                // 初始 tcs 置为已完成——首次 _isActive=true 时 loop 永远不会 await 它，
                // 但以防万一（比如外部在构造瞬间就调用 Suspend）保持一个可用的 signaled tcs。
                _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _resumeTcs.TrySetResult();

                Task.Run(async () =>
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            await Task.Delay(period, _cts.Token);
                            if (_cts.IsCancellationRequested) break;

                            if (!_isActive)
                            {
                                // 💥 真·暂停：thread-pool 线程完全归还，Resume 时 TrySetResult 即刻唤醒
                                // .NET 5 没有 Task.WaitAsync(CancellationToken)，手写一段等价逻辑
                                using (_cts.Token.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), _resumeTcs))
                                {
                                    await _resumeTcs.Task.ConfigureAwait(false);
                                }
                                if (_cts.IsCancellationRequested) break;
                            }

                            // 融合 Advance+Snapshot,单次 Interlocked.Increment 取新值
                            next(_clock.AdvanceAndSnapshot());
                        }
                    }
                    catch (OperationCanceledException) { /* Dispose 触发的 Cancel 正常退出（含 TaskCanceledException）*/ }
                    catch (Exception ex) { error(ex); }
                });
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { _cts.Cancel(); _cts.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 将异步任务 (无返回值) 包装为单发信号流。
        /// 任务完成后发出一个 bool(true) 的成功信号。
        /// </summary>
        public static IWorkflow<bool> FromTask(Func<CancellationToken, Task> taskFactory)
        {
            return new WorkflowEngine<bool>((next, error) =>
            {
                var cts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await taskFactory(cts.Token);
                        // 任务成功跑完后，发出一颗“信号弹”，通知下游执行后续动作
                        if (!cts.IsCancellationRequested) next(true);
                    }
                    catch (OperationCanceledException) { /* 正常取消 */ }
                    catch (Exception ex) { error(ex); }
                }, cts.Token);

                // 流被切断时，立即向内部发起 Cancel 协同取消
                return new DisposableAction(() =>
                {
                    cts.Cancel();
                    cts.Dispose();
                });
            });
        }

        /// <summary>
        /// 将异步任务 (有返回值) 包装为单发数据流。
        /// 任务完成后将结果 T 下发给管线。
        /// </summary>
        public static IWorkflow<T> FromTask<T>(Func<CancellationToken, Task<T>> taskFactory)
        {
            return new WorkflowEngine<T>((next, error) =>
            {
                var cts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await taskFactory(cts.Token);
                        if (!cts.IsCancellationRequested) next(result);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { error(ex); }
                }, cts.Token);

                return new DisposableAction(() =>
                {
                    cts.Cancel();
                    cts.Dispose();
                });
            });
        }
    }
}
