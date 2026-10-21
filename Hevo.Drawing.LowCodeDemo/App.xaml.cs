using System;
using System.Windows;
using System.Windows.Threading;
using Hevo.Charting.Samples;

namespace Hevo.Drawing.LowCodeDemo
{
    public partial class App : Application
    {
        public App()
        {
            // 示例数据源不引 UI 框架:推送定时器由宿主给,跑在 WPF UI 线程
            MockKLineDataSource.StartTimer = StartUiTimer;
        }

        private static IDisposable? StartUiTimer(TimeSpan interval, Action tick)
        {
            // 数据源可能在后台线程首次拉取,定时器显式绑 UI Dispatcher(当前线程可能没有 Dispatcher,timer 永不 Tick)
            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null) return null;
            var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, __) => tick(), dispatcher);
            timer.Start();
            return new TimerStopper(timer);
        }

        private sealed class TimerStopper : IDisposable
        {
            private readonly DispatcherTimer _timer;
            public TimerStopper(DispatcherTimer timer) => _timer = timer;
            public void Dispose() => _timer.Stop();
        }
    }
}
