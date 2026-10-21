using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DataExtent 随快照发布的约定:判等只看引用(纪元 / 键数组 + 起点 + 长度),不比内容;黑板写入不装箱;
    /// 订阅首帧 / Resume 补推的快照也带着它;无参 Publish 照旧发 default。
    /// </summary>
    public sealed class DataExtentTests
    {
        private sealed class KeyedSource : BufferedDataSource<KeyedSource, long>
        {
            public void Set(long[] keys, DataExtent extent)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    _buffer.AddRange(keys);
                    Publish(extent);
                }
            }

            public void SetUntracked(long[] keys)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    _buffer.AddRange(keys);
                    Publish();
                }
            }
        }

        [Fact]
        public void Equals_ComparesReferences_NotContent()
        {
            var epoch = new object();
            var keys = new long[] { 1, 2, 3 };
            var a = new DataExtent(epoch, keys, false);

            Assert.True(a.Equals(new DataExtent(epoch, keys, false)));
            // 同一数组原地改末根:引用 / 起点 / 长度都没变,不算变化(tick 不通知)
            keys[2] = 4;
            Assert.True(a.Equals(new DataExtent(epoch, keys, false)));
            // 内容相同的新数组、新纪元、到底标记变了:都算变化
            Assert.False(a.Equals(new DataExtent(epoch, new long[] { 1, 2, 4 }, false)));
            Assert.False(a.Equals(new DataExtent(new object(), keys, false)));
            Assert.False(a.Equals(new DataExtent(epoch, keys, true)));
            Assert.False(a.Equals(new DataExtent(epoch, keys.AsMemory(0, 2), false)));
        }

        [Fact]
        public void Default_IsNotTracked()
        {
            Assert.False(default(DataExtent).IsTracked);
            Assert.True(new DataExtent(new object(), ReadOnlyMemory<long>.Empty, false).IsTracked);
        }

        [Fact]
        public void BoardWrite_SameExtent_AllocatesNothing()
        {
            using var board = new DataBlackboard();
            var port = new DataPort<DataExtent>("test_extent");
            var extent = new DataExtent(new object(), new long[] { 1, 2, 3 }, false);
            using (board.WriteScope()) board.Write(port, extent); // 预热

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                using (board.WriteScope()) board.Write(port, extent);
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void Publish_CarriesExtent_AndGetSnapshotKeepsIt()
        {
            var ds = new KeyedSource();
            var extent = new DataExtent(new object(), new long[] { 10, 20 }, true);
            DataSnapshot<long> pushed = default;
            using var sub = ds.Stream.Subscribe(s => pushed = s);

            ds.Set(new long[] { 10, 20 }, extent);
            Assert.True(pushed.Extent.Equals(extent));
            Assert.True(ds.GetSnapshot().Extent.Equals(extent));

            // 无参 Publish 照旧:不跟踪
            ds.SetUntracked(new long[] { 10, 20, 30 });
            Assert.False(pushed.Extent.IsTracked);
            Assert.False(ds.GetSnapshot().Extent.IsTracked);
        }

        [Fact]
        public void Resume_RepublishCarriesExtent()
        {
            var ds = new KeyedSource();
            var extent = new DataExtent(new object(), new long[] { 1 }, false);
            ds.Set(new long[] { 1 }, extent);
            var seen = new List<DataSnapshot<long>>();
            using var sub = ds.Stream.Subscribe(seen.Add);

            ds.Suspend();
            ds.Resume();

            Assert.Single(seen);
            Assert.True(seen[0].Extent.Equals(extent));
        }

        // ── ReactiveDataSource.OnContextSwitching ────────────────────────────

        private sealed class SwitchSource : ReactiveDataSource<SwitchSource, string, int>
        {
            public readonly List<(string Next, string? Current)> Calls = new();
            protected override void OnContextSwitching(string context) => Calls.Add((context, Context));
            protected override Task<int> OnFetchAsync(string context, CancellationToken token) => Task.FromResult(0);
        }

        [Fact]
        public async Task OnContextSwitching_CalledOnBothPaths_BeforeContextChanges()
        {
            var ds = new SwitchSource();
            ds.SwitchContext("A");
            await ds.SwitchContextAsync("B");
            await ds.SwitchContextErasedAsync("C");

            Assert.Equal(new (string, string?)[] { ("A", null), ("B", "A"), ("C", "B") }, ds.Calls);
        }
    }
}
