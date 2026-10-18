using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 测试程序集启动时抬高线程池最小线程数。
    /// <para>
    /// 很多测试会在线程池上阻塞:Python 回调 sleep 到超时、LatestOnlyRunner 故意卡住第一次回调、
    /// 列一致性竞争测试的读写线程。xUnit 又按测试类并行跑。CI 的托管机器只有 2 核,线程池最小线程数 = 2,
    /// 超出后大约每 0.5 秒才补一个线程,于是别的测试里一个普通的 Task.Run 可能排队好几秒,
    /// 撞上 5 秒的等待或超时(WatchAsync / PythonResultBuffer / PerCallTimeout 都这样偶发红过)。
    /// 这是测试并发造成的排队,不是产品代码的问题,所以在测试进程里抬高下限,不改产品代码。
    /// </para>
    /// </summary>
    internal static class TestAssemblyInit
    {
        // 64:同时阻塞的测试线程(按测试类并行 + 每个测试里的工作线程)在几十个量级,留足余量;
        // 线程按需创建,下限高不代表会真的建这么多
        private const int MinThreads = 64;

        [ModuleInitializer]
        internal static void RaiseThreadPoolFloor()
        {
            ThreadPool.GetMinThreads(out int worker, out int io);
            ThreadPool.SetMinThreads(Math.Max(worker, MinThreads), Math.Max(io, MinThreads));
        }
    }
}
