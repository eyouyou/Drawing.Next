using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Hevo.Charting.Core
{
    /// <summary>回 WPF UI 线程的 await 切换(切到后台见核心的 <see cref="HevoThread.SwitchToBackground"/>)。</summary>
    public static class WpfThread
    {
        // 💥 瞬移回主线程！(传入图表所在的 Dispatcher)
        public static DispatcherSwitchAwaiter SwitchToUI(Dispatcher dispatcher) => new DispatcherSwitchAwaiter(dispatcher);

        public struct DispatcherSwitchAwaiter : INotifyCompletion
        {
            private readonly Dispatcher _dispatcher;
            public DispatcherSwitchAwaiter(Dispatcher dispatcher) => _dispatcher = dispatcher;
            public DispatcherSwitchAwaiter GetAwaiter() => this;

            // 如果已经在主线程了，直接继续执行，0 损耗！
            public bool IsCompleted => _dispatcher.CheckAccess();

            public void GetResult() { }

            // 如果在后台线程，则把剩下的代码扔回主线程执行！
            public void OnCompleted(Action continuation) => _dispatcher.InvokeAsync(continuation);
        }
    }
}
