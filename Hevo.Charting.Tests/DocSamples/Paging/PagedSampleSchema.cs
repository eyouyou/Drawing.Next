using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

// 开发规范 §12.1 按键分页:数据源发布 DataExtent,分页模块问"这个键之前还要几根",视口按键找回位置。
namespace Hevo.Charting.DocSamples.Paging
{
    /// <summary>
    /// 示例分页数据源:服务端是一段完整历史(内存里模拟),本地缓存一段连续、始终含最新,只往前翻。
    /// 真实数据源的到底判据、页大小自适应、失败冷却都在这一层,框架不管。
    /// </summary>
    public sealed class PagedSampleSource : BufferedDataSource<PagedSampleSource, double>
    {
        private readonly long[] _serverKeys;
        private readonly double[] _serverCloses;
        // 整段替换(复权 / 换月)时换一个;本示例不换
        private readonly object _epoch = new object();
        private int _first;
        private Task _fill = Task.CompletedTask;

        public PagedSampleSource(long[] serverKeys, double[] serverCloses, int initialCount)
        {
            _serverKeys = serverKeys;
            _serverCloses = serverCloses;
            lock (_lock)
            {
                _first = Math.Max(0, serverKeys.Length - initialCount);
                PublishWindow();
            }
        }

        /// <summary>在途的补拉(测试等它)。</summary>
        public Task PendingFill { get { lock (_lock) return _fill; } }

        /// <summary>
        /// 接 <see cref="DataPagingFeature.OnRequireBefore"/>:键 key 之前至少还要 count 根。
        /// 在黑板锁内被调,只判断够不够、起后台任务,不在这里 Publish。
        /// </summary>
        public void EnsureBefore(long key, int count)
        {
            lock (_lock)
            {
                if (!_fill.IsCompleted || _first == 0) return;   // 单飞;已到底
                int at = Array.BinarySearch(_serverKeys, key);
                if (at < 0) at = ~at;
                int target = Math.Max(0, at - count);           // 跳日期时目标早于缓存首根:一次补齐整段空档
                if (target >= _first) return;
                _fill = Task.Run(() => Prepend(target));
            }
        }

        private void Prepend(int target)
        {
            lock (_lock)
            {
                _first = Math.Min(_first, target);
                PublishWindow();
            }
        }

        private void PublishWindow()
        {
            int n = _serverKeys.Length - _first;
            _buffer.Clear();
            for (int i = 0; i < n; i++) _buffer.Add(_serverCloses[_first + i]);
            // 每次发布一块新的键数组:已发布的不原地改
            var keys = new long[n];
            Array.Copy(_serverKeys, _first, keys, 0, n);
            Publish(new DataExtent(_epoch, keys, olderExhausted: _first == 0));
        }
    }

    /// <summary>视口 + 按键分页的最小装配。跳日期调 <see cref="JumpTo"/>。</summary>
    public sealed class PagedSampleSchema : ChartReactiveSchema
    {
        private readonly DataPort<ReadOnlyMemory<double>> _close = new DataPort<ReadOnlyMemory<double>>("PS_Close");

        public PagedSampleSchema(PagedSampleSource source)
        {
            Source = source;
            Viewport = new ViewportManagerFeature
            {
                Alignment = ViewportAlignment.RightEdge,
                DefaultVisibleCount = 60,
                MinVisibleCount = 5,
                OverscrollMin = OverscrollPolicy.Overscroll,   // 往左拖进空白 = 想看更早的,留白根数并进分页要的根数
            };
        }

        public PagedSampleSource Source { get; }
        public ViewportManagerFeature Viewport { get; }

        /// <summary>跳到某个键:视口换锚点,分页模块据此让数据源补到目标,补到后视口落位。</summary>
        public void JumpTo(long key) => Viewport.ScrollToKey(key);

        protected override void DefineDataFlow(IChartCell chart)
        {
            Source.Pipe()
                .ProjectExtent(ViewportPorts.RequireAttached(chart))   // 长度 + DataExtent 写进视口端口
                .LinkStream(cfg => cfg.Map(_close, x => x))
                .BindTo(chart);
        }

        protected override void DefineFeatures(IFeatureContext canvas)
        {
            canvas.Add(Viewport);
            canvas.Add(new DataPagingFeature { OnRequireBefore = Source.EnsureBefore });
        }
    }
}
