using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.5 Python handler 超时硬切。<see cref="PythonSandboxOptions.PerCallTimeoutMs"/> &gt; 0
    /// 时,<see cref="PythonNetModule.Invoke"/> 走本类包裹一次:超时把调用方放出来抛
    /// <see cref="TimeoutException"/>,超时的那次调用变成孤儿。
    ///
    /// <list type="bullet">
    ///   <item>主调用方不阻塞 —— 超时后立刻拿到 TimeoutException,UI 不会卡。</item>
    ///   <item>孤儿调用由 <c>onTimeout</c> 尝试中断(<see cref="PythonNetModule"/> 传入 <c>PythonEngine.Interrupt</c>,
    ///         向那个 Python 线程投递 KeyboardInterrupt)。纯 Python 循环下一个字节码检查点就停;
    ///         numpy 等原生调用要等它返回 Python 才生效,期间孤儿继续占着线程。</item>
    ///   <item>熔断(<see cref="PythonCallBreaker"/>):孤儿还没结束时同一函数的新调用直接拒绝,不再开新任务 ——
    ///         否则一个每次都超时的函数每帧派生一个孤儿,线程池和 GIL 被无上限占满;
    ///         连续超时 <see cref="PythonCallBreaker.TripAfterConsecutiveTimeouts"/> 次后一直拒绝,重载模块才恢复。</item>
    /// </list>
    /// </summary>
    internal static class PerCallTimeoutWatchdog
    {
        /// <param name="timeoutMs"><=0 = 关超时(直接 inline 调 <paramref name="action"/>);&gt;0 = 启 Task.Run watchdog。</param>
        /// <param name="action">实际执行的工作单元。<b>调用方不应已持 GIL</b> —— GIL acquire 必须在 <paramref name="action"/> 内做(否则跨线程 nested acquire 死锁)。</param>
        /// <param name="breaker">同一函数共用一个;null = 不熔断。</param>
        /// <param name="onTimeout">超时后调用一次(在调用方线程上,应只投递不阻塞)。</param>
        public static T RunWithTimeout<T>(int timeoutMs, Func<T> action, PythonCallBreaker? breaker = null, Action? onTimeout = null)
        {
            if (timeoutMs <= 0) return action();
            breaker?.ThrowIfRejected(timeoutMs);

            // Task.Run 把 action 调度到线程池,内部 acquire GIL 跑 Python 调用。
            var task = Task.Run(action);

            // ⚠️ Task.Wait(timeoutMs) 在 task **异常完成**时直接抛 AggregateException(不是返 false),
            // 必须 try/catch unwrap inner exception。否则 PythonDiagnosticsException 会被 Aggregate 包裹,
            // 调用方 `catch (PythonDiagnosticsException)` 拿不到。
            bool completed;
            try
            {
                completed = task.Wait(timeoutMs);
            }
            catch (AggregateException agg)
            {
                breaker?.OnCompleted();
                throw agg.InnerException ?? agg;
            }
            if (completed)
            {
                breaker?.OnCompleted();
                return task.Result;
            }

            OnTimedOut(task, breaker, onTimeout);
            throw new TimeoutException(
                $"Python handler 超时 ({timeoutMs}ms)。已请求中断该调用;numpy 等原生运算要等它返回 Python 才会停下。");
        }

        /// <summary>void 重载 —— 给 HandlerFeature 这种纯副作用路径用。</summary>
        public static void RunWithTimeout(int timeoutMs, Action action, PythonCallBreaker? breaker = null, Action? onTimeout = null)
            => RunWithTimeout<object?>(timeoutMs, () => { action(); return null; }, breaker, onTimeout);

        private static void OnTimedOut(Task task, PythonCallBreaker? breaker, Action? onTimeout)
        {
            breaker?.OnTimedOut(task);
            // 孤儿最终抛的异常(多半是被中断的 KeyboardInterrupt)没人等,在这里观察掉,不进 UnobservedTaskException
            task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            onTimeout?.Invoke();
        }
    }

    /// <summary>
    /// 单个 Python 函数的超时熔断状态。见 <see cref="PerCallTimeoutWatchdog"/>。
    /// </summary>
    internal sealed class PythonCallBreaker
    {
        public const int TripAfterConsecutiveTimeouts = 3;

        private readonly object _lock = new();
        private Task? _orphan;
        private int _consecutiveTimeouts;

        public bool HasOrphan
        {
            get { lock (_lock) return _orphan != null && !_orphan.IsCompleted; }
        }

        public bool IsTripped
        {
            get { lock (_lock) return _consecutiveTimeouts >= TripAfterConsecutiveTimeouts; }
        }

        internal void ThrowIfRejected(int timeoutMs)
        {
            lock (_lock)
            {
                if (_consecutiveTimeouts >= TripAfterConsecutiveTimeouts)
                    throw new PythonCallRejectedException(
                        $"Python handler 已连续 {_consecutiveTimeouts} 次超时({timeoutMs}ms),已熔断;修改并重载该 .py 后恢复。");
                if (_orphan != null && !_orphan.IsCompleted)
                    throw new PythonCallRejectedException(
                        "Python handler 上一次超时的调用仍在运行,本次调用被拒绝(避免孤儿调用累积)。");
                _orphan = null;
            }
        }

        internal void OnCompleted()
        {
            lock (_lock) _consecutiveTimeouts = 0;
        }

        internal void OnTimedOut(Task orphan)
        {
            lock (_lock)
            {
                _orphan = orphan;
                _consecutiveTimeouts++;
            }
        }
    }

    /// <summary>
    /// 熔断拒绝:没有执行 Python 代码就直接返回。继承 <see cref="TimeoutException"/>,按超时处理的调用方不用改。
    /// </summary>
    public sealed class PythonCallRejectedException : TimeoutException
    {
        public PythonCallRejectedException(string message) : base(message) { }
    }
}
