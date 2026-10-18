using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// RequestBus 的每个 RequestAsync 都必须有结局:成功、异常或取消。
    /// 被新请求顶掉、Suspend 期间发出、Suspend / Dispose 时还在途的请求,以前 Task 永远不完成,调用方 await 挂死。
    /// </summary>
    public sealed class RequestBusCompletionTests
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

        private static async Task AssertCanceledSoon(Task task)
        {
            var winner = await Task.WhenAny(task, Task.Delay(Deadline));
            Assert.True(ReferenceEquals(winner, task), "请求的 Task 没有完成");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        [Fact]
        public async Task Normal_Request_ReturnsResult()
        {
            using var bus = new RequestBus<int, int>((x, _) => Task.FromResult(x * 2));
            Assert.Equal(42, await bus.RequestAsync(21).WaitAsync(Deadline));
        }

        [Fact]
        public async Task SupersededRequest_IsCanceled()
        {
            using var bus = new RequestBus<int, int>(async (x, token) =>
            {
                if (x == 1) await Task.Delay(Timeout.Infinite, token);
                return x;
            });

            var first = bus.RequestAsync(1);
            var second = bus.RequestAsync(2);

            Assert.Equal(2, await second.WaitAsync(Deadline));
            await AssertCanceledSoon(first);
        }

        [Fact]
        public async Task SupersededRequest_HandlerIgnoresToken_IsStillCanceled()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var bus = new RequestBus<int, int>(async (x, _) =>
            {
                if (x == 1) await release.Task;
                return x;
            });

            var first = bus.RequestAsync(1);
            Assert.Equal(2, await bus.RequestAsync(2).WaitAsync(Deadline));
            release.SetResult(true);
            await AssertCanceledSoon(first);
        }

        [Fact]
        public async Task RequestWhileSuspended_IsCanceled()
        {
            using var bus = new RequestBus<int, int>((x, _) => Task.FromResult(x));
            bus.Suspend();
            await AssertCanceledSoon(bus.RequestAsync(1));

            bus.Resume();
            Assert.Equal(3, await bus.RequestAsync(3).WaitAsync(Deadline));
        }

        [Fact]
        public async Task InFlightRequest_IsCanceled_OnSuspend()
        {
            var never = new TaskCompletionSource<int>();
            using var bus = new RequestBus<int, int>((_, _) => never.Task);
            var pending = bus.RequestAsync(1);
            bus.Suspend();
            await AssertCanceledSoon(pending);
        }

        [Fact]
        public async Task InFlightRequest_IsCanceled_OnDispose()
        {
            var never = new TaskCompletionSource<int>();
            var bus = new RequestBus<int, int>((_, _) => never.Task);
            var pending = bus.RequestAsync(1);
            bus.Dispose();
            await AssertCanceledSoon(pending);
        }
    }
}
