using System.Collections.Concurrent;
using Hevo.Charting.Core;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 分代请求数据源:切换让代号加一,旧代在途请求作废 / 回包丢弃;新一代首包整段替换,之后同代回包按 demand 合并。
    /// 用手动完成的请求控制回包先后,复现"切合约时旧合约的迟到回包并进新合约"。
    /// </summary>
    public sealed class DemandDataSourceTests
    {
        private interface IDemand { }
        private sealed record Latest(int Count) : IDemand;
        private sealed record History(int Before, int Count) : IDemand;

        // 数据项 = (上下文, 值);上下文用来检查有没有串台
        private sealed class Source : DemandDataSource<Source, string, IDemand, int[], (string Ctx, int V)>
        {
            public readonly ConcurrentQueue<(string Ctx, IDemand Demand, TaskCompletionSource<int[]> Tcs, CancellationToken Token)> Pending = new();
            public readonly List<DataChange?> Changes = new();

            public Source() => Stream.Subscribe(s => { lock (Changes) Changes.Add(s.Change); });

            protected override IDemand CreateInitialDemand(string context) => new Latest(3);
            protected override IDemand CreateRefreshDemand(string context) => new Latest(1);

            protected override Task<int[]> OnFetchAsync(string context, IDemand demand, CancellationToken token)
            {
                var tcs = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => tcs.TrySetCanceled(token));
                Pending.Enqueue((context, demand, tcs, token));
                return tcs.Task;
            }

            protected override DataChange ApplyReset(string context, IDemand demand, int[] response)
            {
                _buffer.Clear();
                foreach (var v in response) _buffer.Add((context, v));
                return DataChange.Reset;
            }

            protected override DataChange ApplyMerge(string context, IDemand demand, int[] response)
            {
                switch (demand)
                {
                    case History:
                        for (int i = response.Length - 1; i >= 0; i--) _buffer.Insert(0, (context, response[i]));
                        return DataChange.Prepend(response.Length);
                    default:
                        foreach (var v in response) _buffer.Add((context, v));
                        return DataChange.Append(response.Length);
                }
            }

            protected override int CountOf(int[] response) => response.Length;

            public (string Ctx, IDemand Demand, TaskCompletionSource<int[]> Tcs, CancellationToken Token) Next()
            {
                for (int i = 0; i < 200; i++)
                {
                    if (Pending.TryDequeue(out var p)) return p;
                    Thread.Sleep(5);
                }
                throw new TimeoutException("没有等到请求");
            }

            public (string Ctx, int V)[] Items => GetSnapshot().AsSpan().ToArray();
        }

        [Fact]
        public async Task Switch_FirstPacketReplaces_ThenSameGenerationMerges()
        {
            using var src = new Source();
            var t = src.SwitchAsync("A");
            var first = src.Next();
            Assert.IsType<Latest>(first.Demand);
            first.Tcs.SetResult(new[] { 1, 2, 3 });
            Assert.Equal(3, await t);

            var page = src.RequestAsync(new History(1, 2));
            src.Next().Tcs.SetResult(new[] { -1, 0 });
            var r = await page;

            Assert.True(r.Applied);
            Assert.Equal(DataChange.Prepend(2), r.Change);
            Assert.Equal(new[] { -1, 0, 1, 2, 3 }, src.Items.Select(x => x.V));
            Assert.Equal(DataChange.Prepend(2), src.Changes.Last());
        }

        [Fact]
        public async Task LateResponseOfOldGeneration_IsDropped()
        {
            using var src = new Source();
            var t = src.SwitchAsync("A");
            src.Next().Tcs.SetResult(new[] { 1, 2, 3 });
            await t;

            var stale = src.RequestAsync(new Latest(1));       // A 的刷新在途
            var staleReq = src.Next();

            var tb = src.SwitchAsync("B", ResetPolicy.KeepUntilLoaded);
            Assert.True(staleReq.Token.IsCancellationRequested);   // 切换取消了上一代的令牌
            staleReq.Tcs.TrySetResult(new[] { 99 });               // 服务端照样回包(不理会取消)

            var r = await stale;
            Assert.False(r.Applied);

            src.Next().Tcs.SetResult(new[] { 10, 20 });
            await tb;
            Assert.All(src.Items, x => Assert.Equal("B", x.Ctx));
            Assert.Equal(2, src.Items.Length);
        }

        [Fact]
        public async Task RequestBeforeFirstPacket_IsDropped_OldDataUntouched()
        {
            using var src = new Source();
            var t = src.SwitchAsync("A");
            src.Next().Tcs.SetResult(new[] { 1, 2, 3 });
            await t;

            var tb = src.SwitchAsync("B", ResetPolicy.KeepUntilLoaded);
            var r = await src.RequestAsync(new Latest(1));     // B 的首包还没到:不发、不合并
            Assert.False(r.Applied);
            Assert.All(src.Items, x => Assert.Equal("A", x.Ctx));   // 旧数据留着

            src.Next().Tcs.SetResult(new[] { 7 });
            await tb;
            Assert.Equal(new[] { ("B", 7) }, src.Items);
            Assert.Equal(DataChange.Reset, src.Changes.Last());
        }

        [Fact]
        public async Task ClearImmediately_PublishesEmptyReset_ThenFirstPacketReset()
        {
            using var src = new Source();
            var t = src.SwitchAsync("A");
            src.Next().Tcs.SetResult(new[] { 1, 2, 3 });
            await t;
            src.Changes.Clear();

            var tb = src.SwitchAsync("B");
            Assert.Empty(src.Items);
            src.Next().Tcs.SetResult(new[] { 5 });
            await tb;

            Assert.Equal(new DataChange?[] { DataChange.Reset, DataChange.Reset }, src.Changes);
            Assert.Equal(2, src.Generation);
        }

        [Fact]
        public async Task SupersededSwitch_ReturnsZero_AndOnlyLatestApplies()
        {
            using var src = new Source();
            var ta = src.SwitchAsync("A");
            var reqA = src.Next();
            var tb = src.SwitchAsync("B");
            reqA.Tcs.TrySetResult(new[] { 1, 2, 3 });            // A 的首包晚到
            Assert.Equal(0, await ta);

            src.Next().Tcs.SetResult(new[] { 9 });
            Assert.Equal(1, await tb);
            Assert.Equal(new[] { ("B", 9) }, src.Items);
        }

        [Fact]
        public async Task FirstPacketFails_DropsOldData_AndUnblocksRequests()
        {
            using var src = new Source();
            var t = src.SwitchAsync("A");
            src.Next().Tcs.SetResult(new[] { 1, 2, 3 });
            await t;

            var tb = src.SwitchAsync("B", ResetPolicy.KeepUntilLoaded);
            var first = src.Next();
            Assert.Equal(0, await src.RefreshAsync());                 // 切换中刷新不发请求
            Assert.True(src.Pending.IsEmpty);
            first.Tcs.SetException(new InvalidOperationException("网络断了"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => tb);

            Assert.Empty(src.Items);                                    // 旧合约的数据撤掉了
            var refresh = src.RefreshAsync();                           // 之后的同代请求照常合并
            src.Next().Tcs.SetResult(new[] { 5 });
            Assert.Equal(1, await refresh);
            Assert.Equal(new[] { ("B", 5) }, src.Items);
        }

        [Fact]
        public async Task Refresh_MergesWithRefreshDemand()
        {
            using var src = new Source();
            var t = src.SwitchAsync("A");
            src.Next().Tcs.SetResult(new[] { 1, 2, 3 });
            await t;

            var refresh = src.RefreshAsync();
            var req = src.Next();
            Assert.Equal(new Latest(1), req.Demand);
            req.Tcs.SetResult(new[] { 4 });
            Assert.Equal(1, await refresh);
            Assert.Equal(new[] { 1, 2, 3, 4 }, src.Items.Select(x => x.V));
            Assert.Equal(DataChange.Append(1), src.Changes.Last());
        }

        [Fact]
        public async Task LegacySwitchContext_AlsoStartsNewGeneration()
        {
            using var src = new Source();
            src.SwitchContext("A");
            src.Next().Tcs.SetResult(new[] { 1 });
            for (int i = 0; i < 200 && src.Items.Length == 0; i++) await Task.Delay(5);

            var stale = src.RequestAsync(new Latest(1));
            var staleReq = src.Next();
            src.SwitchContext("B");
            staleReq.Tcs.TrySetResult(new[] { 2 });
            Assert.False((await stale).Applied);
            Assert.Equal(2, src.Generation);
        }

        [Fact]
        public void DataChange_Merge_FollowsKLineChangeRules()
        {
            var c = DataChange.Append(1).Merge(DataChange.UpdateTail).Merge(DataChange.Append(2));
            Assert.Equal(3, c.Appended);
            Assert.True(c.TailUpdated);
            Assert.False(c.IsTailOnly);
            Assert.True(c.Merge(DataChange.Reset).IsReset);
            Assert.True(DataChange.None.IsEmpty);
            Assert.True(DataChange.UpdateTail.IsTailOnly);
        }

        [Fact]
        public void DataChangeLog_Since_MergesSkippedRecords()
        {
            var a = default(DataChangeLog).Record(DataChange.Reset, 100);
            var b = a.Record(DataChange.Prepend(30), 130).Record(DataChange.UpdateTail, 130).Record(DataChange.Append(1), 131);

            var since = b.Since(a);
            Assert.Equal(30, since!.Value.Prepended);
            Assert.Equal(1, since.Value.Appended);
            Assert.True(since.Value.TailUpdated);
            Assert.Equal(DataChange.Reset, b.Since(default));
            Assert.Null(b.Record(null, 131).Since(a));      // 中间有不描述变化的发布:说不清
            Assert.Equal(DataChange.None, b.Since(b));
        }
    }
}
