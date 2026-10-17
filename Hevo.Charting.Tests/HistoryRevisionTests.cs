using System;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// BufferedDataSource.HistoryRevision:只有"只改最后一根"(PublishLastOnly 且条数不变)时不递增。
    /// </summary>
    public sealed class HistoryRevisionTests
    {
        public readonly record struct Bar(int Index, double Close);

        private sealed class TickSource : BufferedDataSource<TickSource, Bar>
        {
            public void Load(int n)
            {
                lock (_lock)
                {
                    _buffer.Clear();
                    for (int i = 0; i < n; i++) _buffer.Add(new Bar(i, 100 + i));
                    Publish();
                }
            }

            public void TickLast(double close)
            {
                lock (_lock)
                {
                    _buffer[_buffer.Count - 1] = _buffer[_buffer.Count - 1] with { Close = close };
                    PublishLastOnly();
                }
            }

            public void AppendLastOnly()
            {
                lock (_lock)
                {
                    _buffer.Add(new Bar(_buffer.Count, 1));
                    PublishLastOnly(); // 误用:条数变了也声明"只改最后一根"
                }
            }

            public void Append()
            {
                lock (_lock)
                {
                    _buffer.Add(new Bar(_buffer.Count, 1));
                    Publish();
                }
            }
        }

        [Fact]
        public void TickOnLastBar_KeepsHistoryRevision()
        {
            var src = new TickSource();
            src.Load(10);
            long afterLoad = src.HistoryRevision;

            src.TickLast(1.5);
            src.TickLast(2.5);
            Assert.Equal(afterLoad, src.HistoryRevision);
        }

        [Fact]
        public void AppendAndReload_BumpHistoryRevision()
        {
            var src = new TickSource();
            src.Load(10);
            long r0 = src.HistoryRevision;

            src.Append();                 // 上一根进入历史段
            long r1 = src.HistoryRevision;
            Assert.True(r1 > r0);

            src.AppendLastOnly();         // 条数变了,声明"只改最后一根"也不算数
            long r2 = src.HistoryRevision;
            Assert.True(r2 > r1);

            src.Load(10);                 // 全量重载
            Assert.True(src.HistoryRevision > r2);
        }
    }
}
